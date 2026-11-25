using System.Collections.Generic;
using TMPro;
using UnityEngine;

// Stage 3 (open the castle gate): a memory puzzle. Lamps above the gate light up in a
// sequence; the player repeats it by landing on the matching coloured plates. Each round the
// sequence gets one longer. After the last round the gate rises and walking in through it
// finishes the run (or moves on, if a later stage is added to the stage list).
// - A wrong plate: the round's sequence is shown again (Hard: a new sequence).
// - Control can change hands any time; after a handoff the sequence is shown again so the
//   new player can see it.
[DefaultExecutionOrder(-100)]
public class GateStageController : MonoBehaviour, IStageFlow
{
    [Header("Connections (auto-found when empty)")]
    [SerializeField] private ClimbActorController actor;
    [SerializeField] private InputRouter inputRouter;
    [SerializeField] private RemoteInputGateway gateway;
    [SerializeField] private TMP_Text statusText;

    [Header("Web display")]
    [SerializeField] private string stageId = "gate";
    [SerializeField] private string stageName = "3 · 성문 열기";

    [Header("Puzzle (difficulty overrides these)")]
    [SerializeField, Min(1)] private int rounds = 4;
    [SerializeField, Min(1)] private int startLength = 3;
    [SerializeField, Min(0.2f)] private float showStep = 0.7f;   // seconds per lamp
    [SerializeField] private bool newSequenceOnMistake;

    [Header("Rules")]
    [SerializeField, Min(0f)] private float hostResumeDelay = 1.5f;
    [SerializeField] private bool playIntro = true;
    [SerializeField, Min(0f)] private float nextStageDelay = 3f;

    [Header("After the run")]
    [SerializeField] private string newGameSceneName = "Stage_Dodge";
    [SerializeField] private string lobbySceneName = "Lobby";

    private const string StageControls = "move,jump,look";

    private enum State { Intro, Showing, Input, Mistake, RoundClear, Opening, Open, Finished, Advancing }

    private State state = State.Intro;
    private StageHandoff handoff;
    private StageIntroCamera intro;
    private RemoteInputSource stageRemoteInputSource;
    private readonly List<MemoryPlate> plates = new List<MemoryPlate>();
    private readonly Dictionary<int, MemoryLamp> lamps = new Dictionary<int, MemoryLamp>();
    private readonly List<int> sequence = new List<int>();
    private readonly List<GateDoor> doors = new List<GateDoor>();
    private System.Random random;
    private int round;          // 0-based
    private int inputIndex;
    private int showIndex;
    private float timer;
    private bool wasPaused;
    private bool initialized, usesPersistentGateway;
    private string nextScene = "";
    private string[] resultLines;
    private string statusLabel = "";
    private GUIStyle titleStyle, lineStyle, buttonStyle, labelStyle;

    private bool IsSinglePlayer => GameSession.Mode == GameMode.SinglePlayer;
    private bool IntroPlaying => intro != null && intro.IsPlaying;
    private int RoundLength => startLength + round;

    private void Awake()
    {
        if (actor == null) actor = FindInScene<ClimbActorController>();
        if (inputRouter == null) inputRouter = FindInScene<InputRouter>();
        if (gateway == null) gateway = FindInScene<RemoteInputGateway>();
        if (actor == null || inputRouter == null)
        {
            Debug.LogError("GateStageController: ClimbActorController와 InputRouter가 필요합니다.", this);
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
                Debug.LogError("GateStageController: 멀티플레이 세션을 찾을 수 없습니다. 로비에서 MULTI PLAY로 시작하세요.", this);
                usesPersistentGateway = false;
                gateway = null;
            }
        }

        NaWaBaDifficulty difficulty = GameSession.Difficulty;
        if (difficulty != null)
        {
            rounds = difficulty.MemoryRounds;
            startLength = difficulty.MemoryStartLength;
            showStep = difficulty.MemoryShowStep;
            newSequenceOnMistake = difficulty.MemoryNewSequenceOnMistake;
            if (!string.IsNullOrEmpty(difficulty.WebLabel) && !stageName.Contains("("))
                stageName = $"{stageName} ({difficulty.WebLabel})";
        }

        foreach (MemoryPlate plate in FindObjectsByType<MemoryPlate>(FindObjectsSortMode.None))
            if (plate.gameObject.scene == gameObject.scene) plates.Add(plate);
        plates.Sort((a, b) => a.Index.CompareTo(b.Index));
        foreach (MemoryLamp lamp in FindObjectsByType<MemoryLamp>(FindObjectsSortMode.None))
            if (lamp.gameObject.scene == gameObject.scene) lamps[lamp.Index] = lamp;
        foreach (GateDoor d in FindObjectsByType<GateDoor>(FindObjectsSortMode.None))
            if (d.gameObject.scene == gameObject.scene) doors.Add(d);
        if (plates.Count < 2)
            Debug.LogError("GateStageController: 발판(MemoryPlate)이 없습니다. " +
                "'NaWaBa > 성문 퍼즐 만들기 (Stage_Gate)'를 실행하고 씬을 저장하세요.", this);

        random = new System.Random(System.Environment.TickCount);
        if (gateway != null) gateway.AllowRemoteApproval = false;
        handoff = new StageHandoff(usesPersistentGateway ? gateway : null, hostResumeDelay);
        initialized = true;
    }

    private void OnEnable()
    {
        if (!initialized) return;
        state = State.Intro;
        resultLines = null;
        round = 0;
        NewSequence();
        handoff.Reset();
        actor.InputLocked = false;
        actor.Fell += OnFell;

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
                Debug.LogError("GateStageController: 스테이지 입력을 멀티플레이 세션에 연결하지 못했습니다.", this);
                usesPersistentGateway = false;
            }
        }
        if (!usesPersistentGateway) inputRouter.UseLocalInput();

        actor.Frozen = true;
        ClimbCameraController followCamera = FindInScene<ClimbCameraController>();
        intro = playIntro ? StageIntroCamera.For(followCamera) : null;
        if (intro != null) intro.Play(IntroRoute(), followCamera, OnIntroFinished);
        else OnIntroFinished();
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
            if (gateway.HasAuthority && !gateway.IsCarryingControl) gateway.ReclaimControl();
            gateway.UnbindStageInputs(inputRouter);
        }
        if (intro != null) intro.Skip();
        if (actor != null)
        {
            actor.Frozen = false;
            actor.InputLocked = false;
        }
        handoff?.Reset();
    }

    public void PrepareToLeave() => enabled = false;

    private List<Vector3> IntroRoute()
    {
        var route = new List<Vector3>();
        if (doors.Count > 0) route.Add(doors[0].transform.position + Vector3.up * 2f);
        Vector3 platesCenter = Vector3.zero;
        foreach (MemoryPlate plate in plates) platesCenter += plate.transform.position;
        if (plates.Count > 0) route.Add(platesCenter / plates.Count);
        route.Add(actor.transform.position);
        return route;
    }

    private void OnIntroFinished()
    {
        if (!isActiveAndEnabled || state != State.Intro) return;
        BeginShow(1f);
    }

    // ---------- puzzle flow ----------

    private void NewSequence()
    {
        sequence.Clear();
        int colours = Mathf.Max(2, plates.Count);
        int total = startLength + rounds - 1;
        for (int i = 0; i < total; i++)
        {
            int next = random.Next(colours);
            // Avoid three of the same in a row (hard to read, not more fun).
            if (i >= 2 && sequence[i - 1] == next && sequence[i - 2] == next) next = (next + 1) % colours;
            sequence.Add(next);
        }
    }

    private void BeginShow(float delay)
    {
        state = State.Showing;
        showIndex = -1;
        timer = -delay;
        inputIndex = 0;
    }

    private void Update()
    {
        if (!initialized) return;
        float dt = Time.deltaTime;
        handoff.Tick(dt);

        bool done = state == State.Finished || state == State.Advancing;
        if (usesPersistentGateway)
            gateway.AllowRemoteApproval = gateway.isActiveAndEnabled && !done && inputRouter.isActiveAndEnabled;

        bool paused = handoff.Paused && !done;
        actor.Frozen = paused || IntroPlaying || done || state == State.Opening;

        // After a handoff the new player sees the sequence again.
        if (wasPaused && !paused && (state == State.Showing || state == State.Input || state == State.Mistake))
            BeginShow(0.8f);
        wasPaused = paused;

        if (paused)
        {
            SetStatus(handoff.Label);
            return;
        }
        if (!done && !IntroPlaying) RunRecord.AddControlTime(gateway, dt);

        switch (state)
        {
            case State.Intro:
                SetStatus("OPEN THE GATE!\n(SPACE: SKIP)");
                break;
            case State.Showing:
                UpdateShow(dt);
                SetStatus($"ROUND {round + 1}/{rounds}\nWATCH THE LAMPS");
                break;
            case State.Input:
                SetStatus($"ROUND {round + 1}/{rounds}\nYOUR TURN {inputIndex}/{RoundLength}");
                break;
            case State.Mistake:
                timer -= dt;
                SetStatus("WRONG!\nWATCH AGAIN");
                if (timer <= 0f) BeginShow(0.5f);
                break;
            case State.RoundClear:
                timer -= dt;
                SetStatus($"ROUND {round + 1}/{rounds} CLEAR!");
                if (timer <= 0f) NextRound();
                break;
            case State.Opening:
                SetStatus("THE GATE OPENS!");
                if (doors.TrueForAll(d => d.IsFullyOpen)) { timer -= dt; if (timer <= 0f) state = State.Open; }
                break;
            case State.Open:
                SetStatus("WALK IN!");
                break;
            case State.Advancing:
                advanceLeft -= dt;
                SetStatus(advanceLeft > 0f ? $"CLEAR!\nNEXT STAGE IN {Mathf.CeilToInt(advanceLeft)}" : "LOADING...");
                if (advanceLeft <= 0f && !StageSceneLoader.IsLoading &&
                    !StageSceneLoader.Load(nextScene, this, this, carryControl: true))
                    FinishRun(RunRecord.Current(gateway));
                break;
            case State.Finished:
                SetStatus("ALL CLEAR!");
                break;
        }
    }

    private float advanceLeft;

    private void UpdateShow(float dt)
    {
        timer += dt;
        int step = Mathf.FloorToInt(timer / showStep);
        if (timer < 0f || step == showIndex) return;
        showIndex = step;
        if (step >= RoundLength)
        {
            state = State.Input;
            inputIndex = 0;
            return;
        }
        FlashColour(sequence[step], showStep * 0.75f);
    }

    private void FlashColour(int colour, float seconds)
    {
        if (lamps.TryGetValue(colour, out MemoryLamp lamp)) lamp.Flash(seconds);
        if (colour >= 0 && colour < plates.Count) plates[colour].Flash(seconds);
    }

    // Called by MemoryPlate when the player lands on it.
    public void OnPlatePressed(MemoryPlate plate)
    {
        if (handoff.Paused || state != State.Input) return;
        FlashColour(plate.Index, 0.35f);
        if (plate.Index != sequence[inputIndex])
        {
            RunRecord.Player player = RunRecord.NoteFail(gateway);
            state = State.Mistake;
            timer = 1.5f;
            if (newSequenceOnMistake) NewSequence();
            Debug.Log($"[Gate] wrong plate by {player.Name} (round {round + 1})", this);
            if (usesPersistentGateway)
                gateway.ReportRunEvent("failed", $"{player.Name} 틀렸다! 라운드 {round + 1} 순서를 다시 보여줍니다");
            return;
        }
        inputIndex++;
        if (inputIndex < RoundLength) return;

        RunRecord.Player who = RunRecord.Current(gateway);
        if (usesPersistentGateway)
            gateway.ReportRunEvent("checkpoint", $"{who.Name} 라운드 {round + 1}/{rounds} 성공!");
        state = State.RoundClear;
        timer = 1.2f;
        foreach (MemoryLamp lamp in lamps.Values) lamp.Flash(0.8f);
    }

    private void NextRound()
    {
        if (round + 1 < rounds)
        {
            round++;
            BeginShow(0.6f);
            return;
        }
        // All rounds done: open the gate.
        state = State.Opening;
        timer = 0.5f;
        foreach (MemoryLamp lamp in lamps.Values) lamp.SetOn(true);
        foreach (GateDoor d in doors) d.Open();
        Debug.Log("[Gate] puzzle solved, gate opening", this);
    }

    // Called by GateEntrance once the player walks through the open gate.
    public void OnEnteredCastle()
    {
        if (state != State.Open || handoff.Paused) return;
        RunRecord.Player player = RunRecord.NoteClear(gateway, stageName);
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
        Debug.Log("[Gate] ALL CLEAR, last: " + player.Name, this);
        if (usesPersistentGateway)
        {
            if (gateway.HasAuthority) gateway.EndRunControl();
            gateway.ReportRunEvent("run_result", $"성문 안으로! 전체 클리어 · 마지막: {player.Name}", resultLines);
        }
    }

    private void OnFell()
    {
        RunRecord.Player player = RunRecord.NoteFall(gateway);
        if (usesPersistentGateway) gateway.ReportRunEvent("fell", $"{player.Name} 추락! 성문 앞에서 다시 시작");
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
        Debug.Log("[Gate] 원격 조작 시작: " + gateway.PlayerName, this);
    }

    private void OnRemoteEnded()
    {
        if (state == State.Finished || state == State.Advancing) handoff.Reset();
        else handoff.OnEnded();
        inputRouter.UseLocalInput();
    }

    // ---------- status and result ----------

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
        if (state != State.Finished || resultLines == null) return;

        float width = Mathf.Min(720f, Screen.width - 40f);
        float height = 150f + resultLines.Length * 30f;
        var panel = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
        GUI.Box(panel, GUIContent.none);
        GUI.Box(panel, GUIContent.none);
        GUI.Label(new Rect(panel.x, panel.y + 14, width, 44), "ALL CLEAR!", titleStyle);
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

    private void EnsureStyles()
    {
        if (titleStyle != null) return;
        titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        titleStyle.normal.textColor = new Color(1f, 0.85f, 0.2f);
        lineStyle = new GUIStyle(GUI.skin.label) { fontSize = 19 };
        lineStyle.normal.textColor = Color.white;
        buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = FontStyle.Bold };
        labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        labelStyle.normal.textColor = Color.white;
    }

    private T FindInScene<T>() where T : Component
    {
        foreach (T candidate in FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (candidate.gameObject.scene == gameObject.scene) return candidate;
        return null;
    }
}
