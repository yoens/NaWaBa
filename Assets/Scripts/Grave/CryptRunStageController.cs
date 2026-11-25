using System.Collections.Generic;
using TMPro;
using UnityEngine;

// Region 2 (무덤), game 2: the crypt descent. A row of left/right arrows is shown for a few
// seconds (at the start and before every retry), then the player runs down through the crypt
// on their own: at every fork only left/right can be chosen. The right way drops one level
// deeper; the wrong way is shut by a gate (fail), no choice runs into the wall (fail).
// A fail restarts from the top with the same sequence, shown again.
// Handoff rule (user decision): whoever starts a try keeps it until it fails or succeeds, so
// new approvals are only allowed while the arrows are shown or after a fail. Take Back /
// leaving still works at any time (the run pauses and continues as it was).
[DefaultExecutionOrder(-100)]
public class CryptRunStageController : MonoBehaviour, IStageFlow
{
    [Header("Connections (auto-found when empty)")]
    [SerializeField] private ClimbActorController actor;
    [SerializeField] private InputRouter inputRouter;
    [SerializeField] private RemoteInputGateway gateway;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private CryptCourse course;

    [Header("Web display")]
    [SerializeField] private string stageId = "crypt";
    [SerializeField] private string stageName = "무덤 2 · 지하 갈림길";

    [Header("Rules (difficulty overrides show time and speed)")]
    [SerializeField, Range(2f, 8f)] private float showTime = 4f;
    [SerializeField, Range(4f, 20f)] private float runSpeed = 10f;
    [SerializeField, Range(0f, 0.2f)] private float speedGainPerFork = 0.05f;
    [SerializeField, Min(1f)] private float fallGravity = 25f;
    [SerializeField, Min(0.2f)] private float failPause = 1.8f;
    [SerializeField, Min(0f)] private float hostResumeDelay = 1.5f;
    [SerializeField, Min(0f)] private float nextStageDelay = 3f;

    public enum CameraView { FirstPerson, LowChase }

    [Header("Camera (first person = the player's eyes, the body is hidden)")]
    [SerializeField] private CameraView cameraView = CameraView.FirstPerson;
    [SerializeField] private float eyeHeight = 1.55f;
    [SerializeField, Range(-10f, 40f)] private float lookDownRunning = 6f;   // degrees
    [SerializeField, Range(0f, 80f)] private float lookDownFalling = 45f;    // looks down while dropping
    [Tooltip("LowChase only: close behind the shoulder, below the wall tops.")]
    [SerializeField] private float chaseBack = 2f;
    [SerializeField] private float chaseUp = 1.8f;
    [SerializeField] private float chaseSmooth = 0.06f;
    [SerializeField] private float fovRunning = 74f;

    [Header("After the run")]
    [SerializeField] private string newGameSceneName = "Stage_Dodge";
    [SerializeField] private string lobbySceneName = "Lobby";

    private const string StageControls = "move";

    private enum State { Memory, Running, Failed, Advancing, Finished }
    private enum Leg { ToFork, ToGoal, ToFail }

    private State state = State.Memory;
    private StageHandoff handoff;
    private RemoteInputSource stageRemoteInputSource;
    private bool initialized, usesPersistentGateway;

    private int[] sequence = new int[0];      // -1 left, +1 right
    private int[] picks = new int[0];         // what was chosen at each fork this try
    private int forkIndex, choice;
    private bool failedAtWall;
    private readonly List<CryptCourse.Segment> path = new List<CryptCourse.Segment>();
    private int segment;
    private float segmentDistance, fallTime, timer, advanceLeft;
    private Leg leg;
    private Vector3 runnerLocal, headingLocal = Vector3.forward;
    private bool falling;

    private Camera view;
    private ClimbCameraController followCamera;
    private float baseFov = 60f;
    private Vector3 cameraPosition, cameraVelocity;
    private float shake, pitch;
    private readonly List<(Renderer renderer, UnityEngine.Rendering.ShadowCastingMode mode)> hiddenBody =
        new List<(Renderer, UnityEngine.Rendering.ShadowCastingMode)>();
    private Rigidbody actorBody;
    private RigidbodyInterpolation savedInterpolation;

    private string nextScene = "";
    private string[] resultLines;
    private string statusLabel = "";
    private GUIStyle titleStyle, lineStyle, buttonStyle, labelStyle, smallStyle;
    private Texture2D arrowTexture, whiteTexture;

    private bool IsSinglePlayer => GameSession.Mode == GameMode.SinglePlayer;

    private void Awake()
    {
        if (actor == null) actor = FindInScene<ClimbActorController>();
        if (inputRouter == null) inputRouter = FindInScene<InputRouter>();
        if (gateway == null) gateway = FindInScene<RemoteInputGateway>();
        if (course == null) course = FindInScene<CryptCourse>();
        if (actor == null || inputRouter == null || course == null)
        {
            Debug.LogError("CryptRunStageController: ClimbActorController, InputRouter, CryptCourse가 필요합니다. " +
                "'NaWaBa > 무덤 지하 갈림길 만들기 (Stage_Crypt)'를 실행하세요.", this);
            enabled = false;
            return;
        }

        RemoteInputGateway configuredGateway = gateway;
        usesPersistentGateway = !IsSinglePlayer && configuredGateway != null &&
            configuredGateway.UsesGameSessionMode;
        if (usesPersistentGateway)
        {
            stageRemoteInputSource = configuredGateway.RemoteInputTarget;
            configuredGateway.enabled = false;
            gateway = RemoteInputGateway.ResolveForScene(configuredGateway);
            if (gateway == null || stageRemoteInputSource == null)
            {
                Debug.LogError("CryptRunStageController: 멀티플레이 세션을 찾을 수 없습니다. 로비에서 MULTI PLAY로 시작하세요.", this);
                usesPersistentGateway = false;
                gateway = null;
            }
        }

        NaWaBaDifficulty difficulty = GameSession.Difficulty;
        if (difficulty != null)
        {
            showTime = difficulty.CryptShowTime;
            runSpeed = difficulty.CryptRunSpeed;
            if (!string.IsNullOrEmpty(difficulty.WebLabel) && !stageName.Contains("("))
                stageName = $"{stageName} ({difficulty.WebLabel})";
        }

        actorBody = actor.GetComponent<Rigidbody>();
        followCamera = FindInScene<ClimbCameraController>();
        view = followCamera != null ? followCamera.GetComponent<Camera>() : null;
        if (view == null) view = Camera.main;
        if (view != null) baseFov = view.fieldOfView;

        // The run normally begins on the world map; this covers starting the scene directly.
        RunRecord.BeginRunIfIdle();
        if (gateway != null) gateway.AllowRemoteApproval = false;
        handoff = new StageHandoff(usesPersistentGateway ? gateway : null, hostResumeDelay);
        initialized = true;
    }

    private void OnEnable()
    {
        if (!initialized) return;
        resultLines = null;
        handoff.Reset();
        actor.InputLocked = false;

        if (usesPersistentGateway)
        {
            bool carried = gateway.IsCarryingControl;
            if (gateway.BindStageInputs(inputRouter, stageRemoteInputSource, stageId, stageName, StageControls))
            {
                if (!carried) inputRouter.UseLocalInput();
                gateway.RemoteControlPreparing += OnRemotePreparing;
                gateway.RemoteControlStarted += OnRemoteStarted;
                gateway.RemoteControlEnded += OnRemoteEnded;
                if (carried) handoff.OnPreparing();
            }
            else
            {
                Debug.LogError("CryptRunStageController: 스테이지 입력을 멀티플레이 세션에 연결하지 못했습니다.", this);
                usesPersistentGateway = false;
            }
        }
        if (!usesPersistentGateway) inputRouter.UseLocalInput();

        // The player is moved along the path by this script: physics off, own chase camera.
        actor.Frozen = true;
        if (actorBody != null)
        {
            savedInterpolation = actorBody.interpolation;
            actorBody.interpolation = RigidbodyInterpolation.None;
        }
        if (followCamera != null) followCamera.enabled = false;
        HideBody(cameraView == CameraView.FirstPerson);

        // A new order every time the stage is entered; the same order for every retry.
        var random = new System.Random(System.Environment.TickCount);
        sequence = new int[course.ForkCount];
        for (int i = 0; i < sequence.Length; i++)
        {
            int side = random.Next(2) == 0 ? -1 : 1;
            // At most three of the same in a row.
            if (i >= 3 && sequence[i - 1] == side && sequence[i - 2] == side && sequence[i - 3] == side) side = -side;
            sequence[i] = side;
        }
        picks = new int[sequence.Length];
        BeginTry();
    }

    private void OnDisable()
    {
        if (usesPersistentGateway && gateway != null)
        {
            gateway.RemoteControlPreparing -= OnRemotePreparing;
            gateway.RemoteControlStarted -= OnRemoteStarted;
            gateway.RemoteControlEnded -= OnRemoteEnded;
            gateway.AllowRemoteApproval = false;
            if (gateway.HasAuthority && !gateway.IsCarryingControl) gateway.ReclaimControl();
            gateway.UnbindStageInputs(inputRouter);
        }
        if (actor != null)
        {
            actor.ScriptedVelocity = null;
            actor.ScriptedGrounded = true;
            actor.Frozen = false;
            actor.InputLocked = false;
        }
        if (actorBody != null) actorBody.interpolation = savedInterpolation;
        if (view != null) view.fieldOfView = baseFov;
        if (followCamera != null) followCamera.enabled = true;
        HideBody(false);
        handoff?.Reset();
    }

    private void OnDestroy()
    {
        if (arrowTexture != null) Destroy(arrowTexture);
        if (whiteTexture != null) Destroy(whiteTexture);
    }

    public void PrepareToLeave() => enabled = false;

    // ---------- tries ----------

    // Back to the top: show the arrows again, then run.
    private void BeginTry()
    {
        state = State.Memory;
        timer = showTime;
        forkIndex = 0;
        choice = 0;
        failedAtWall = false;
        System.Array.Clear(picks, 0, picks.Length);
        course.ResetGates();
        course.FirstLeg(path);
        leg = Leg.ToFork;
        segment = 0;
        segmentDistance = 0f;
        fallTime = 0f;
        falling = false;
        runnerLocal = course.StartPoint;
        headingLocal = Vector3.forward;
        ApplyRunner(Vector3.zero, true);
        SnapCamera();
    }

    private void Update()
    {
        if (!initialized) return;
        float dt = Time.deltaTime;
        handoff.Tick(dt);

        bool done = state == State.Finished || state == State.Advancing;
        // One player per try: approvals only while the arrows are shown or after a fail.
        if (usesPersistentGateway)
            gateway.AllowRemoteApproval = gateway.isActiveAndEnabled && inputRouter.isActiveAndEnabled &&
                (state == State.Memory || state == State.Failed);

        actor.Frozen = true;
        bool paused = handoff.Paused && !done;
        if (paused)
        {
            actor.ScriptedVelocity = Vector3.zero;
            SetStatus(handoff.Label);
            return;
        }
        if (!done) RunRecord.AddControlTime(gateway, dt);
        if (shake > 0f) shake = Mathf.Max(0f, shake - dt * 2.5f);

        switch (state)
        {
            case State.Memory:
                timer -= dt;
                actor.ScriptedVelocity = Vector3.zero;
                SetStatus("REMEMBER!");
                if (timer <= 0f) state = State.Running;
                break;
            case State.Running:
                ReadChoice();
                Advance(dt);
                if (state == State.Running) SetStatus($"FORK {Mathf.Min(forkIndex + 1, sequence.Length)}/{sequence.Length}");
                break;
            case State.Failed:
                timer -= dt;
                actor.ScriptedVelocity = Vector3.zero;
                SetStatus(failedAtWall ? "NO TURN!" : "BLOCKED!");
                if (timer <= 0f) BeginTry();
                break;
            case State.Advancing:
                advanceLeft -= dt;
                actor.ScriptedVelocity = Vector3.zero;
                SetStatus(advanceLeft > 0f ? $"CLEAR!\nNEXT STAGE IN {Mathf.CeilToInt(advanceLeft)}" : "LOADING...");
                if (advanceLeft <= 0f && !StageSceneLoader.IsLoading &&
                    !StageSceneLoader.Load(nextScene, this, this, carryControl: true))
                    FinishRun(RunRecord.Current(gateway));
                break;
            case State.Finished:
                actor.ScriptedVelocity = Vector3.zero;
                SetStatus("REGION CLEAR!");
                break;
        }
    }

    // The last left/right held before the turn counts; it can be changed until then.
    private void ReadChoice()
    {
        if (leg == Leg.ToFail) return;
        float x = inputRouter.ReadMove().x;
        if (x > 0.5f) choice = 1;
        else if (x < -0.5f) choice = -1;
    }

    private float Speed => runSpeed * (1f + speedGainPerFork * Mathf.Min(forkIndex, sequence.Length));

    private void Advance(float dt)
    {
        Vector3 before = runnerLocal;
        float remaining = Speed * dt;
        while (state == State.Running && remaining > 0f)
        {
            if (segment >= path.Count)
            {
                EndOfLeg();
                continue;
            }
            CryptCourse.Segment seg = path[segment];
            if (seg.fall)
            {
                // Gravity drop: the time is used, not the distance.
                falling = true;
                fallTime += dt;
                float height = Mathf.Max(0.1f, seg.a.y - seg.b.y);
                float duration = Mathf.Sqrt(2f * height / fallGravity);
                float u = Mathf.Clamp01(fallTime / duration);
                Vector3 flat = Vector3.Lerp(seg.a, seg.b, u);
                float fallen = 0.5f * fallGravity * (u * duration) * (u * duration);
                runnerLocal = new Vector3(flat.x, seg.a.y - Mathf.Min(height, fallen), flat.z);
                Vector3 across = seg.b - seg.a;
                across.y = 0f;
                if (across.sqrMagnitude > 0.0001f) headingLocal = across.normalized;
                if (u >= 1f)
                {
                    falling = false;
                    fallTime = 0f;
                    segment++;
                    shake = 0.35f;
                }
                break;
            }

            float length = Vector3.Distance(seg.a, seg.b);
            Vector3 direction = (seg.b - seg.a) / Mathf.Max(0.0001f, length);
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.0001f) headingLocal = direction.normalized;
            if (segmentDistance + remaining < length)
            {
                segmentDistance += remaining;
                runnerLocal = Vector3.Lerp(seg.a, seg.b, segmentDistance / length);
                remaining = 0f;
            }
            else
            {
                remaining -= length - segmentDistance;
                runnerLocal = seg.b;
                segment++;
                segmentDistance = 0f;
            }
        }
        Vector3 velocity = dt > 0f ? course.transform.TransformVector(runnerLocal - before) / dt : Vector3.zero;
        ApplyRunner(velocity, false);
    }

    private void EndOfLeg()
    {
        segment = 0;
        segmentDistance = 0f;
        switch (leg)
        {
            case Leg.ToFork: Decide(forkIndex); break;
            case Leg.ToGoal: Clear(); break;
            default: Fail(); break;
        }
    }

    private void Decide(int k)
    {
        int picked = choice;
        choice = 0;
        picks[k] = picked;
        if (picked == 0)
        {
            failedAtWall = true;
            course.WallLeg(k, path);
            leg = Leg.ToFail;
            return;
        }
        bool correct = picked == sequence[k];
        course.ForkLeg(k, picked, correct, path);
        if (!correct)
        {
            course.CloseGate(k, picked);
            leg = Leg.ToFail;
            return;
        }
        forkIndex = k + 1;
        leg = k >= sequence.Length - 1 ? Leg.ToGoal : Leg.ToFork;
    }

    private void Fail()
    {
        state = State.Failed;
        timer = failPause;
        shake = 0.6f;
        actor.ScriptedVelocity = Vector3.zero;
        RunRecord.Player player = RunRecord.NoteFail(gateway);
        int fork = Mathf.Min(forkIndex + 1, sequence.Length);
        Debug.Log($"[Crypt] {player.Name} failed at fork {fork} ({(failedAtWall ? "no turn" : "wrong way")})", this);
        if (usesPersistentGateway)
            gateway.ReportRunEvent("failed", failedAtWall
                ? $"{player.Name} {fork}번째 갈림길에서 방향을 못 골랐다! 처음부터 다시"
                : $"{player.Name} {fork}번째 갈림길에서 막혔다! 처음부터 다시");
    }

    private void Clear()
    {
        RunRecord.Player player = RunRecord.NoteClear(gateway, stageName);
        actor.ScriptedVelocity = Vector3.zero;
        nextScene = StageSceneLoader.NextStage(gameObject.scene.name, "");
        if (StageSceneLoader.CanLoad(nextScene))
        {
            state = State.Advancing;
            advanceLeft = nextStageDelay;
            if (usesPersistentGateway)
                gateway.ReportRunEvent("stage_cleared", $"{player.Name}님이 {stageName} 클리어! 다음 스테이지로 이동합니다");
            return;
        }
        StageSceneLoader.CompleteRegion(gameObject.scene.name, nextScene);
        FinishRun(player);
    }

    private void FinishRun(RunRecord.Player player)
    {
        state = State.Finished;
        RunRecord.Finish();
        resultLines = RunRecord.SummaryLines();
        Debug.Log("[Crypt] REGION CLEAR, last: " + player.Name, this);
        if (usesPersistentGateway)
        {
            if (gateway.HasAuthority) gateway.EndRunControl();
            gateway.ReportRunEvent("run_result", $"무덤 아래에 도착했다! 지역 클리어 · 마지막: {player.Name}", resultLines);
        }
    }

    // ---------- player and camera ----------

    private void ApplyRunner(Vector3 worldVelocity, bool snap)
    {
        Transform t = course.transform;
        Vector3 position = t.TransformPoint(runnerLocal);
        Vector3 forward = t.TransformDirection(headingLocal);
        forward.y = 0f;
        Quaternion face = forward.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(forward.normalized, Vector3.up) : actor.transform.rotation;
        Quaternion rotation = snap ? face : Quaternion.Slerp(actor.transform.rotation, face, 0.35f);
        actor.transform.SetPositionAndRotation(position, rotation);
        if (actorBody != null)
        {
            actorBody.position = position;
            actorBody.rotation = rotation;
        }
        actor.ScriptedVelocity = worldVelocity;
        actor.ScriptedGrounded = !falling;
    }

    // First person: the body still casts its shadow but is not drawn (it would fill the view).
    private void HideBody(bool hide)
    {
        if (hide && hiddenBody.Count == 0 && actor != null)
        {
            foreach (Renderer r in actor.GetComponentsInChildren<Renderer>(true))
            {
                hiddenBody.Add((r, r.shadowCastingMode));
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            }
        }
        else if (!hide)
        {
            foreach (var (r, mode) in hiddenBody)
                if (r != null) r.shadowCastingMode = mode;
            hiddenBody.Clear();
        }
    }

    private float WantedPitch => falling ? lookDownFalling : state == State.Running ? lookDownRunning : 2f;

    // Where the camera wants to be: the player's eyes, or just behind the shoulder.
    private void CameraPose(out Vector3 position, out Quaternion rotation)
    {
        Transform t = actor.transform;
        Vector3 forward = t.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        forward.Normalize();
        rotation = Quaternion.LookRotation(forward, Vector3.up) * Quaternion.Euler(pitch, 0f, 0f);
        position = cameraView == CameraView.FirstPerson
            ? t.position + Vector3.up * eyeHeight + forward * 0.15f
            : t.position - forward * chaseBack + Vector3.up * chaseUp;
    }

    private void SnapCamera()
    {
        if (view == null) return;
        pitch = WantedPitch;
        CameraPose(out cameraPosition, out Quaternion rotation);
        cameraVelocity = Vector3.zero;
        view.transform.SetPositionAndRotation(cameraPosition, rotation);
    }

    private void LateUpdate()
    {
        if (!initialized || view == null) return;
        pitch = Mathf.Lerp(pitch, WantedPitch, Time.deltaTime * (falling ? 9f : 5f));
        CameraPose(out Vector3 target, out Quaternion rotation);
        // First person sits on the eyes (no lag, or it would clip the walls in the turns).
        cameraPosition = cameraView == CameraView.FirstPerson
            ? target
            : Vector3.SmoothDamp(cameraPosition, target, ref cameraVelocity, chaseSmooth);
        Vector3 jitter = shake > 0f ? Random.insideUnitSphere * shake * 0.2f : Vector3.zero;
        view.transform.SetPositionAndRotation(cameraPosition + jitter, rotation);
        bool fast = state == State.Running && !handoff.Paused;
        float speedFactor = Mathf.InverseLerp(4f, 20f, Speed);
        float wanted = fast ? Mathf.Lerp(baseFov, fovRunning + speedFactor * 6f, 0.85f) : baseFov;
        view.fieldOfView = Mathf.Lerp(view.fieldOfView, wanted, Time.deltaTime * 4f);
    }

    // ---------- handoff ----------

    private void OnRemotePreparing()
    {
        if (state == State.Finished || state == State.Advancing)
        {
            gateway.ReclaimControl();
            return;
        }
        handoff.OnPreparing();
    }

    private void OnRemoteStarted()
    {
        if (!handoff.Paused || !gateway.IsControlling || !inputRouter.IsRemoteControl)
        {
            gateway.ReclaimControl();
            return;
        }
        handoff.OnStarted();
        // The new player sees the whole row of arrows from the beginning.
        if (state == State.Memory) timer = showTime;
        Debug.Log("[Crypt] 원격 조작 시작: " + gateway.PlayerName, this);
    }

    private void OnRemoteEnded()
    {
        if (state == State.Finished || state == State.Advancing) handoff.Reset();
        else handoff.OnEnded();
        if (state == State.Memory) timer = showTime;
        inputRouter.UseLocalInput();
    }

    // ---------- HUD ----------

    private void SetStatus(string text)
    {
        statusLabel = text;
        if (statusText != null && statusText.text != text) statusText.text = text;
    }

    private void OnGUI()
    {
        if (!initialized) return;
        EnsureStyles();
        if (statusText == null && !string.IsNullOrEmpty(statusLabel))
            GUI.Label(new Rect(0, 20, Screen.width, 70), statusLabel, labelStyle);

        bool paused = handoff.Paused && state != State.Finished && state != State.Advancing;
        if (!paused && state == State.Memory) DrawSequence();
        else if (state == State.Running || state == State.Failed || paused) DrawProgress();
        if (state == State.Failed) DrawFailBanner();
        if (state == State.Finished && resultLines != null) DrawResult();
    }

    // The whole row of arrows with a countdown bar.
    private void DrawSequence()
    {
        int n = sequence.Length;
        float size = Mathf.Clamp((Screen.width - 80f) / Mathf.Max(1, n) - 10f, 36f, 96f);
        float rowWidth = n * (size + 10f) - 10f;
        float x = (Screen.width - rowWidth) / 2f;
        float y = Screen.height * 0.38f;
        var panel = new Rect(x - 24f, y - 70f, rowWidth + 48f, size + 130f);
        GUI.Box(panel, GUIContent.none);
        GUI.Box(panel, GUIContent.none);
        GUI.Label(new Rect(panel.x, panel.y + 8f, panel.width, 50f), "REMEMBER!  (기억하세요)", titleStyle);
        for (int i = 0; i < n; i++)
        {
            var cell = new Rect(x + i * (size + 10f), y, size, size);
            DrawCell(cell, new Color(0.12f, 0.12f, 0.16f, 0.95f));
            DrawArrow(cell, sequence[i], new Color(1f, 0.85f, 0.3f));
            GUI.Label(new Rect(cell.x, cell.yMax + 2f, size, 22f), (i + 1).ToString(), smallStyle);
        }
        float left = Mathf.Clamp01(timer / Mathf.Max(0.01f, showTime));
        var bar = new Rect(x, y + size + 30f, rowWidth, 10f);
        DrawCell(bar, new Color(0.2f, 0.2f, 0.25f, 0.9f));
        DrawCell(new Rect(bar.x, bar.y, bar.width * left, bar.height), new Color(1f, 0.85f, 0.3f));
    }

    // Small row at the top: passed forks green, the current one shows the held choice.
    private void DrawProgress()
    {
        int n = sequence.Length;
        const float size = 34f;
        float rowWidth = n * (size + 6f) - 6f;
        float x = (Screen.width - rowWidth) / 2f, y = 96f;
        for (int i = 0; i < n; i++)
        {
            var cell = new Rect(x + i * (size + 6f), y, size, size);
            bool passed = i < forkIndex;
            bool current = i == forkIndex && state == State.Running && leg == Leg.ToFork;
            bool failedHere = state == State.Failed && i == forkIndex;
            Color back = passed ? new Color(0.15f, 0.55f, 0.25f, 0.95f)
                : failedHere ? new Color(0.7f, 0.15f, 0.15f, 0.95f)
                : current ? new Color(0.35f, 0.35f, 0.45f, 0.95f)
                : new Color(0.1f, 0.1f, 0.13f, 0.85f);
            DrawCell(cell, back);
            if (passed) DrawArrow(cell, picks[i], Color.white);
            else if (failedHere && picks[i] != 0) DrawArrow(cell, picks[i], Color.white);
            else GUI.Label(cell, "?", smallStyle);
        }

        // Big indicator of the held direction for the next fork.
        if (state != State.Running || leg == Leg.ToFail) return;
        float big = 70f;
        var spot = new Rect((Screen.width - big) / 2f, Screen.height - big - 40f, big, big);
        DrawCell(spot, new Color(0f, 0f, 0f, 0.45f));
        if (choice != 0) DrawArrow(spot, choice, new Color(1f, 0.85f, 0.3f));
        else GUI.Label(spot, "?", titleStyle);
    }

    private void DrawFailBanner()
    {
        var banner = new Rect(0f, Screen.height * 0.42f, Screen.width, 60f);
        DrawCell(banner, new Color(0.35f, 0f, 0f, 0.7f));
        GUI.Label(banner, failedAtWall ? "NO TURN! · 처음부터 다시" : "BLOCKED! · 처음부터 다시", titleStyle);
    }

    private void DrawResult()
    {
        float width = Mathf.Min(720f, Screen.width - 40f);
        float height = 150f + resultLines.Length * 30f;
        var panel = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
        GUI.Box(panel, GUIContent.none);
        GUI.Box(panel, GUIContent.none);
        GUI.Label(new Rect(panel.x, panel.y + 14, width, 44), "REGION CLEAR!", titleStyle);
        float y = panel.y + 66;
        foreach (string line in resultLines)
        {
            GUI.Label(new Rect(panel.x + 24, y, width - 48, 28), line, lineStyle);
            y += 30f;
        }
        float buttonWidth = (width - 72f) / 2f;
        if (GUI.Button(new Rect(panel.x + 24, panel.yMax - 62, buttonWidth, 44), "MAP (지도)", buttonStyle))
            StageSceneLoader.LoadMap(this, this, newGameSceneName);
        if (GUI.Button(new Rect(panel.x + 48 + buttonWidth, panel.yMax - 62, buttonWidth, 44), "LOBBY", buttonStyle))
            StageSceneLoader.Load(lobbySceneName, this, this);
    }

    private void DrawCell(Rect rect, Color color)
    {
        Color old = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, whiteTexture);
        GUI.color = old;
    }

    private void DrawArrow(Rect cell, int side, Color color)
    {
        Rect inner = new Rect(cell.x + cell.width * 0.15f, cell.y + cell.height * 0.15f, cell.width * 0.7f, cell.height * 0.7f);
        Color old = GUI.color;
        GUI.color = color;
        // The texture points right; flip it for left.
        GUI.DrawTextureWithTexCoords(inner, arrowTexture, side < 0 ? new Rect(1f, 0f, -1f, 1f) : new Rect(0f, 0f, 1f, 1f));
        GUI.color = old;
    }

    private void EnsureStyles()
    {
        if (titleStyle != null) return;
        titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 32, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        titleStyle.normal.textColor = new Color(1f, 0.85f, 0.2f);
        lineStyle = new GUIStyle(GUI.skin.label) { fontSize = 19 };
        lineStyle.normal.textColor = Color.white;
        buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = FontStyle.Bold };
        labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        labelStyle.normal.textColor = Color.white;
        smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        smallStyle.normal.textColor = new Color(0.85f, 0.85f, 0.9f);

        whiteTexture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        whiteTexture.SetPixel(0, 0, Color.white);
        whiteTexture.Apply();

        // Arrow pointing right: a shaft and a head, white on transparent.
        const int s = 64;
        arrowTexture = new Texture2D(s, s, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color32[s * s];
        for (int py = 0; py < s; py++)
            for (int px = 0; px < s; px++)
            {
                float u = (px + 0.5f) / s, v = (py + 0.5f) / s;
                bool shaft = u >= 0.08f && u <= 0.55f && Mathf.Abs(v - 0.5f) <= 0.13f;
                bool head = u > 0.5f && u <= 0.95f && Mathf.Abs(v - 0.5f) <= (0.95f - u) * 0.95f;
                pixels[py * s + px] = shaft || head ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
            }
        arrowTexture.SetPixels32(pixels);
        arrowTexture.Apply();
    }

    private T FindInScene<T>() where T : Component
    {
        foreach (T candidate in FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (candidate.gameObject.scene == gameObject.scene) return candidate;
        return null;
    }
}
