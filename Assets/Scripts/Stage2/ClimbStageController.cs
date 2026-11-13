using System.Collections.Generic;
using TMPro;
using UnityEngine;

// Stage 2 (to the castle gate). Control can change hands at any time: the player freezes
// in place during the handoff and continues from there. A participant who cleared stage 1
// keeps playing here after a fresh video connection. Landing on the ClimbGoal ends the run
// (until stage 3 exists) and shows the result for everyone.
[DefaultExecutionOrder(-100)]
public class ClimbStageController : MonoBehaviour, IStageFlow
{
    // Leave empty to find them in this scene automatically.
    [Header("Connections (auto-found when empty)")]
    [SerializeField] private ClimbActorController actor;
    [SerializeField] private InputRouter inputRouter;
    [SerializeField] private RemoteInputGateway gateway;
    // Optional: shows handoff countdowns and CLEAR. Without it a small on-screen label is used.
    [SerializeField] private TMP_Text statusText;

    [Header("Web display")]
    [SerializeField] private string stageId = "climb";
    [SerializeField] private string stageName = "2 · 성문 앞으로";

    [Header("Rules")]
    [SerializeField, Min(0f)] private float hostResumeDelay = 1.5f;
    // Opening shot from the castle gate back to the start (host can skip with Space).
    [SerializeField] private bool playIntro = true;

    [Header("Next stage (from Resources/NaWaBaStages; the run ends here if this is the last)")]
    [SerializeField, Min(0f)] private float nextStageDelay = 3f;

    [Header("After the run")]
    [SerializeField] private string newGameSceneName = "Stage_Dodge";
    [SerializeField] private string lobbySceneName = "Lobby";

    private const string StageControls = "move,jump,look";

    private StageHandoff handoff;
    private bool initialized;
    private bool usesPersistentGateway;
    private bool cleared;
    private bool advancing;          // cleared, and a next stage follows (stage 3 ...)
    private float advanceLeft;
    private string nextScene = "";
    private string[] resultLines;
    private RemoteInputSource stageRemoteInputSource;
    private bool useCheckpoints = true;
    private StageIntroCamera intro;
    private GameObject courseRoot;
    private const string CourseRootPrefix = "ClimbCourse (built)";
    private bool IntroPlaying => intro != null && intro.IsPlaying;
    private int checkpointOrder;
    private GUIStyle titleStyle, lineStyle, buttonStyle, labelStyle;

    private bool IsSinglePlayer => GameSession.Mode == GameMode.SinglePlayer;

    private void Awake()
    {
        if (actor == null) actor = FindInScene<ClimbActorController>();
        if (inputRouter == null) inputRouter = FindInScene<InputRouter>();
        if (gateway == null) gateway = FindInScene<RemoteInputGateway>();

        if (actor == null || inputRouter == null)
        {
            Debug.LogError("ClimbStageController: ClimbActorController와 InputRouter가 필요합니다.", this);
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
                Debug.LogError("ClimbStageController: 멀티플레이 세션 또는 RemoteInputSource를 찾을 수 없습니다. " +
                    "로비에서 MULTI PLAY로 시작했는지 확인하세요.", this);
                usesPersistentGateway = false;
                gateway = null;
            }
        }

        // Difficulty chosen in the lobby: label on the web page, checkpoints on or off.
        NaWaBaDifficulty difficulty = GameSession.Difficulty;
        SelectCourse(difficulty != null ? difficulty.CourseName : "");
        ClimbMover.SpeedScale = difficulty != null ? difficulty.MoverSpeed : 1f;
        if (difficulty != null)
        {
            useCheckpoints = difficulty.UseCheckpoints;
            if (!string.IsNullOrEmpty(difficulty.WebLabel) && !stageName.Contains("("))
                stageName = $"{stageName} ({difficulty.WebLabel})";
        }

        if (gateway != null) gateway.AllowRemoteApproval = false;
        handoff = new StageHandoff(usesPersistentGateway ? gateway : null, hostResumeDelay);
        initialized = true;
    }

    private void OnEnable()
    {
        if (!initialized) return;

        cleared = false;
        advancing = false;
        resultLines = null;
        checkpointOrder = 0;
        actor.ClearRespawnPoint();
        foreach (ClimbCheckpoint checkpoint in
                 FindObjectsByType<ClimbCheckpoint>(FindObjectsSortMode.None))
            checkpoint.MarkReached(false);
        handoff.Reset();
        actor.InputLocked = false;
        actor.Fell += OnFell;

        if (!usesPersistentGateway)
        {
            inputRouter.UseLocalInput();
            StartIntro();
            return;
        }

        // Read before binding: a participant carried over from stage 1 keeps their turn.
        bool carried = gateway.IsCarryingControl;
        if (!gateway.BindStageInputs(inputRouter, stageRemoteInputSource,
                stageId, stageName, StageControls))
        {
            Debug.LogError("ClimbStageController: 스테이지 입력을 멀티플레이 세션에 연결하지 못했습니다.", this);
            usesPersistentGateway = false;
            return;
        }
        if (!carried) inputRouter.UseLocalInput();

        gateway.RemoteControlPreparing += OnRemotePreparing;
        gateway.RemoteControlStarted += OnRemoteStarted;
        gateway.RemoteControlEnded += OnRemoteEnded;

        // Freeze at the start point until the carried player's video and countdown are ready.
        if (carried) handoff.OnPreparing();
        actor.Frozen = handoff.Paused;
        StartIntro();
    }

    // Several built courses can be in the scene ("ClimbCourse (built) <asset name>"): turn on
    // the one the difficulty names (or the first) and turn the others off.
    private void SelectCourse(string wanted)
    {
        var courses = new List<GameObject>();
        foreach (GameObject root in gameObject.scene.GetRootGameObjects())
            if (root.name.StartsWith(CourseRootPrefix)) courses.Add(root);
        if (courses.Count == 0) return;

        courseRoot = null;
        if (!string.IsNullOrEmpty(wanted))
            courseRoot = courses.Find(c => c.name.EndsWith(" " + wanted) || c.name == wanted);
        if (courseRoot == null) courseRoot = courses.Find(c => c.activeSelf) ?? courses[0];
        foreach (GameObject course in courses) course.SetActive(course == courseRoot);
        Debug.Log($"[Climb] course: {courseRoot.name}" +
            (!string.IsNullOrEmpty(wanted) && !courseRoot.name.EndsWith(wanted)
                ? $" ('{wanted}' is not built in this scene)" : ""), this);
    }

    private void StartIntro()
    {
        if (!playIntro) return;
        ClimbCameraController followCamera = FindInScene<ClimbCameraController>();
        intro = StageIntroCamera.For(followCamera);
        if (intro == null) return;
        actor.Frozen = true;
        intro.Play(BuildIntroRoute(), followCamera, null);
    }

    // Gate → built course platforms (last to first) → player.
    private List<Vector3> BuildIntroRoute()
    {
        var route = new List<Vector3>();
        ClimbGoal goal = courseRoot != null ? courseRoot.GetComponentInChildren<ClimbGoal>() : null;
        if (goal == null) goal = FindInScene<ClimbGoal>();
        if (goal != null) route.Add(goal.transform.position);

        GameObject built = courseRoot;
        if (built != null && built.scene == gameObject.scene)
        {
            var platforms = new List<Transform>();
            foreach (Transform child in built.transform)
                if (child.name.Length > 2 && char.IsDigit(child.name[0]) && !child.name.StartsWith("00_"))
                    platforms.Add(child);
            platforms.Sort((a, b) => string.CompareOrdinal(b.name, a.name));
            foreach (Transform platform in platforms)
            {
                Collider area = platform.GetComponentInChildren<Collider>();
                route.Add(area != null
                    ? new Vector3(area.bounds.center.x, area.bounds.max.y, area.bounds.center.z)
                    : platform.position);
            }
        }
        route.Add(actor.transform.position);
        return route;
    }

    private void OnDisable()
    {
        if (actor != null) actor.Fell -= OnFell;
        if (usesPersistentGateway && gateway != null)
        {
            gateway.RemoteControlPreparing -= OnRemotePreparing;
            gateway.RemoteControlStarted -= OnRemoteStarted;
            gateway.RemoteControlEnded -= OnRemoteEnded;
            gateway.AllowRemoteApproval = false;
            if (gateway.HasAuthority) gateway.ReclaimControl();
            gateway.UnbindStageInputs(inputRouter);
        }

        if (actor != null)
        {
            actor.Frozen = false;
            actor.InputLocked = false;
        }
        if (intro != null) intro.Skip();
        ClimbMover.Paused = false;
        handoff?.Reset();
    }

    public void PrepareToLeave()
    {
        enabled = false;
    }

    private void Update()
    {
        if (!initialized) return;
        float deltaTime = Time.deltaTime;
        handoff.Tick(deltaTime);

        if (usesPersistentGateway)
            gateway.AllowRemoteApproval = gateway.isActiveAndEnabled && !cleared &&
                inputRouter.isActiveAndEnabled;

        if (advancing)
        {
            actor.Frozen = true;
            advanceLeft -= deltaTime;
            SetStatus(advanceLeft > 0f ? $"CLEAR!\nNEXT STAGE IN {Mathf.CeilToInt(advanceLeft)}" : "LOADING...");
            if (advanceLeft <= 0f && !StageSceneLoader.IsLoading &&
                !StageSceneLoader.Load(nextScene, this, this, carryControl: true))
            {
                advancing = false;   // could not load: end the run here instead
                RunRecord.Finish();
                resultLines = RunRecord.SummaryLines();
            }
            return;
        }

        actor.Frozen = handoff.Paused || cleared || IntroPlaying;
        ClimbMover.Paused = handoff.Paused || IntroPlaying;
        if (!cleared && !handoff.Paused && !IntroPlaying)
            RunRecord.AddControlTime(gateway, deltaTime);

        SetStatus(cleared ? "ALL CLEAR!" : handoff.Paused ? handoff.Label :
            IntroPlaying ? "REACH THE CASTLE GATE!\n(SPACE: SKIP)" : stageName);
    }

    private void OnRemotePreparing()
    {
        if (cleared)
        {
            gateway.ReclaimControl();
            return;
        }
        handoff.OnPreparing();
        actor.Frozen = true;
    }

    private void OnRemoteStarted()
    {
        if (!handoff.Paused || !gateway.IsControlling || !inputRouter.IsRemoteControl)
        {
            gateway.ReclaimControl();
            return;
        }
        handoff.OnStarted();
        actor.Frozen = false;
        Debug.Log("[Climb] 원격 조작 시작: " + gateway.PlayerName, this);
    }

    private void OnRemoteEnded()
    {
        if (cleared) { handoff.Reset(); return; }
        handoff.OnEnded();
        inputRouter.UseLocalInput();
        Debug.Log("[Climb] 호스트 조작으로 복귀", this);
    }

    private void OnFell()
    {
        if (cleared) return;
        RunRecord.Player player = RunRecord.NoteFall(gateway);
        if (usesPersistentGateway)
            gateway.ReportRunEvent("fell", actor.HasCheckpoint
                ? $"{player.Name} 추락! 체크포인트 {checkpointOrder}에서 다시 시작"
                : $"{player.Name} 추락! 2스테이지 시작점으로 돌아갑니다");
    }

    // Called by ClimbCheckpoint when the player lands on it. Returns true when it became
    // the new respawn point (checkpoints on, and further along than the current one).
    public bool ReachCheckpoint(ClimbCheckpoint checkpoint, ClimbActorController who)
    {
        if (!initialized || cleared || !useCheckpoints || who != actor) return false;
        if (checkpoint.Order <= checkpointOrder) return false;
        checkpointOrder = checkpoint.Order;
        actor.SetRespawnPoint(checkpoint.RespawnPosition, actor.transform.rotation);
        string who_ = RunRecord.Current(gateway).Name;
        Debug.Log($"[Climb] checkpoint {checkpointOrder} by {who_}", this);
        if (usesPersistentGateway)
            gateway.ReportRunEvent("checkpoint", $"{who_} 체크포인트 {checkpointOrder} 도착!");
        return true;
    }

    // Called by ClimbGoal while the player stands in the goal area. Counts once.
    public void ReachGoal()
    {
        if (!initialized || cleared || handoff.Paused || IntroPlaying) return;
        cleared = true;
        RunRecord.Player player = RunRecord.NoteClear(gateway, stageName);

        nextScene = StageSceneLoader.NextStage(gameObject.scene.name, "");
        if (StageSceneLoader.CanLoad(nextScene))
        {
            // Not the last stage: move on, the controller keeps their turn.
            advancing = true;
            advanceLeft = nextStageDelay;
            actor.Frozen = true;
            Debug.Log("[Climb] CLEAR by " + player.Name + " -> " + nextScene, this);
            if (usesPersistentGateway)
                gateway.ReportRunEvent("stage_cleared", $"{player.Name}님이 {stageName} 클리어! 다음 스테이지로 이동합니다");
            return;
        }

        RunRecord.Finish();
        resultLines = RunRecord.SummaryLines();
        actor.Frozen = true;
        Debug.Log("[Climb] GOAL by " + player.Name, this);

        if (usesPersistentGateway)
        {
            // End the remote turn; the request list stays for the next game.
            if (gateway.HasAuthority) gateway.EndRunControl();
            gateway.ReportRunEvent("run_result", $"전체 클리어! 마지막 골인: {player.Name}", resultLines);
        }
    }

    private void StartNewGame()
    {
        // Stage 1 begins a new run (RunRecord resets there).
        StageSceneLoader.Load(StageSceneLoader.FirstStage(newGameSceneName), this, this);
    }

    private void ReturnToLobby()
    {
        StageSceneLoader.Load(lobbySceneName, this, this);
    }

    private void SetStatus(string text)
    {
        if (statusText != null && statusText.text != text) statusText.text = text;
        statusLabel = text;
    }

    private string statusLabel = "";

    private void OnGUI()
    {
        if (!initialized) return;
        EnsureStyles();

        if (statusText == null && handoff.Paused)
            GUI.Label(new Rect(0, 20, Screen.width, 60), statusLabel, labelStyle);

        if (!cleared || resultLines == null) return;

        float width = Mathf.Min(720f, Screen.width - 40f);
        float height = 150f + resultLines.Length * 30f;
        var panel = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
        GUI.Box(panel, GUIContent.none);
        GUI.Box(panel, GUIContent.none); // darker backdrop
        GUI.Label(new Rect(panel.x, panel.y + 14, width, 44), "ALL CLEAR!", titleStyle);
        float y = panel.y + 66;
        foreach (string line in resultLines)
        {
            GUI.Label(new Rect(panel.x + 24, y, width - 48, 28), line, lineStyle);
            y += 30f;
        }
        float buttonWidth = (width - 72f) / 2f;
        if (GUI.Button(new Rect(panel.x + 24, panel.yMax - 62, buttonWidth, 44), "NEW GAME (1스테이지)", buttonStyle))
            StartNewGame();
        if (GUI.Button(new Rect(panel.x + 48 + buttonWidth, panel.yMax - 62, buttonWidth, 44), "LOBBY", buttonStyle))
            ReturnToLobby();
    }

    private void EnsureStyles()
    {
        if (titleStyle != null) return;
        titleStyle = new GUIStyle(GUI.skin.label)
            { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        titleStyle.normal.textColor = new Color(1f, 0.85f, 0.2f);
        lineStyle = new GUIStyle(GUI.skin.label) { fontSize = 19 };
        lineStyle.normal.textColor = Color.white;
        buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = FontStyle.Bold };
        labelStyle = new GUIStyle(GUI.skin.label)
            { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        labelStyle.normal.textColor = Color.white;
    }

    private T FindInScene<T>() where T : Component
    {
        foreach (T candidate in FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (candidate.gameObject.scene == gameObject.scene)
                return candidate;
        }
        return null;
    }
}
