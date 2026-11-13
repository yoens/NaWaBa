using TMPro;
using UnityEngine;
using UnityEngine.UI;


[DefaultExecutionOrder(-100)]
public class SnowClimbStageController : MonoBehaviour, IStageFlow
{
    // Leave empty to find them in this scene automatically.
    [Header("Connections (auto-found when empty)")]
    [SerializeField] private ClimbActorController actor;
    [SerializeField] private InputRouter inputRouter;
    [SerializeField] private RemoteInputGateway gateway;
    [SerializeField] private SnowSlope slope;
    [SerializeField] private ClimbCameraController followCamera;
    // Optional: status line and the host's START / RETRY button.
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private Button retryButton;

    [Header("Rules")]
    [SerializeField, Min(0f)] private float retryDelay = 2f;
    [SerializeField, Min(0f)] private float hostResumeDelay = 1.5f;
    [SerializeField] private bool playIntro = true;

    [Header("Flow")]
    [SerializeField] private string nextStageSceneName = "Stage_Climb";
    [SerializeField, Min(0f)] private float nextStageDelay = 3f;

    private const string StageId = "dodge";
    private const string BaseStageName = "1 · 눈덩이 피하며 오르기";
    private const string StageControls = "move,jump,look";

    private enum StageState { Intro, Ready, Playing, Failed, Cleared, Advancing }

    private StageState state;
    private StageHandoff handoff;
    private StageIntroCamera intro;
    private TMP_Text retryLabel;
    private RemoteInputSource stageRemoteInputSource;
    private string stageName = BaseStageName;
    private float summitMeters = 300f;
    private float retryLeft, advanceLeft;
    private string nextScene = "";
    private bool initialized, usesPersistentGateway;
    private string statusLabel = "";
    private GUIStyle labelStyle;

    private bool IsSinglePlayer => GameSession.Mode == GameMode.SinglePlayer;
    private bool Multiplayer => usesPersistentGateway;
    private bool IntroPlaying => intro != null && intro.IsPlaying;

    private void Awake()
    {
        if (actor == null) actor = FindInScene<ClimbActorController>();
        if (inputRouter == null) inputRouter = FindInScene<InputRouter>();
        if (gateway == null) gateway = FindInScene<RemoteInputGateway>();
        if (slope == null) slope = FindInScene<SnowSlope>();
        if (followCamera == null) followCamera = FindInScene<ClimbCameraController>();

        if (actor == null || inputRouter == null || slope == null)
        {
            Debug.LogError("SnowClimbStageController: ClimbActorController, InputRouter, SnowSlope가 필요합니다.", this);
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
                Debug.LogError("SnowClimbStageController: 멀티플레이 세션 또는 RemoteInputSource를 찾을 수 없습니다. " +
                    "로비에서 MULTI PLAY로 시작했는지 확인하세요.", this);
                usesPersistentGateway = false;
                gateway = null;
            }
        }
        if (gateway != null) gateway.AllowRemoteApproval = false;

        if (retryButton != null) retryLabel = retryButton.GetComponentInChildren<TMP_Text>();
        handoff = new StageHandoff(usesPersistentGateway ? gateway : null, hostResumeDelay);
        slope.Target = actor.transform;
        ApplyDifficulty();

        // Stage 1 is always the start of a new run (한 판).
        RunRecord.BeginRun();
        initialized = true;
    }

    private void ApplyDifficulty()
    {
        NaWaBaDifficulty difficulty = GameSession.Difficulty;
        if (difficulty == null) return;
        slope.Configure(difficulty.SpawnInterval, difficulty.SpawnHalfWidth, difficulty.SnowballSpeed);
        slope.ConfigurePatterns(difficulty.DoubleChance, difficulty.RowChance, difficulty.AimedChance);
        summitMeters = difficulty.SummitMeters;
        if (!string.IsNullOrEmpty(difficulty.WebLabel))
            stageName = $"{BaseStageName} ({difficulty.WebLabel})";
        Debug.Log($"[Snow] difficulty {difficulty.DisplayName}: every {difficulty.SpawnInterval:0.##}s, " +
            $"speed {difficulty.SnowballSpeed:0.#}, double {difficulty.DoubleChance:0.##}, " +
            $"row {difficulty.RowChance:0.##}, aimed {difficulty.AimedChance:0.##}", this);
    }

    private void OnEnable()
    {
        if (!initialized) return;

        slope.PlayerHit += OnPlayerHit;
        slope.ReachedSummit += OnReachedSummit;
        actor.Fell += OnFell;
        if (retryButton != null) retryButton.onClick.AddListener(OnRetryClicked);

        if (usesPersistentGateway)
        {
            bool carried = gateway.IsCarryingControl;
            if (!gateway.BindStageInputs(inputRouter, stageRemoteInputSource, StageId, stageName, StageControls))
            {
                Debug.LogError("SnowClimbStageController: 스테이지 입력을 멀티플레이 세션에 연결하지 못했습니다.", this);
                usesPersistentGateway = false;
            }
            else
            {
                if (!carried) inputRouter.UseLocalInput();
                gateway.RemoteControlPreparing += OnRemotePreparing;
                gateway.RemoteControlStarted += OnRemoteStarted;
                gateway.RemoteControlEnded += OnRemoteEnded;
            }
        }
        if (!usesPersistentGateway) inputRouter.UseLocalInput();

        handoff.Reset();
        slope.ClearSnowballs();
        slope.Running = false;
        actor.InputLocked = false;
        actor.Frozen = true;
        state = StageState.Intro;

        intro = playIntro ? StageIntroCamera.For(followCamera) : null;
        if (intro != null) intro.Play(slope.IntroRoute(actor.transform.position), followCamera, OnIntroFinished);
        else OnIntroFinished();
        RefreshAvailability();
    }

    private void OnDisable()
    {
        if (slope != null)
        {
            slope.PlayerHit -= OnPlayerHit;
            slope.ReachedSummit -= OnReachedSummit;
            slope.Running = false;
        }
        if (actor != null) actor.Fell -= OnFell;
        if (retryButton != null)
        {
            retryButton.onClick.RemoveListener(OnRetryClicked);
            retryButton.interactable = false;
        }
        if (usesPersistentGateway && gateway != null)
        {
            gateway.RemoteControlPreparing -= OnRemotePreparing;
            gateway.RemoteControlStarted -= OnRemoteStarted;
            gateway.RemoteControlEnded -= OnRemoteEnded;
            gateway.AllowRemoteApproval = false;
            // A controller carried into the next stage keeps their turn.
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

    public void PrepareToLeave()
    {
        enabled = false;
    }

    private void OnIntroFinished()
    {
        if (!isActiveAndEnabled || state != StageState.Intro) return;
        // Single player starts at once. In multiplayer a participant approved during the
        // intro (or carried over) starts now; otherwise the host presses START or approves.
        if (IsSinglePlayer || !Multiplayer || gateway.HasAuthority) StartRound();
        else state = StageState.Ready;
    }

    private void Update()
    {
        if (!initialized) return;
        float deltaTime = Time.deltaTime;
        handoff.Tick(deltaTime);
        RefreshAvailability();

        bool paused = handoff.Paused && state != StageState.Advancing;
        bool running = state == StageState.Playing && !paused && !IntroPlaying;
        actor.Frozen = !running;
        slope.Running = running;

        if (paused && state != StageState.Intro)
        {
            SetStatus(handoff.Label);
            return;
        }

        switch (state)
        {
            case StageState.Intro:
                SetStatus("REACH THE SUMMIT!\n(SPACE: SKIP)");
                break;
            case StageState.Ready:
                SetStatus("READY");
                break;
            case StageState.Playing:
                RunRecord.AddControlTime(gateway, deltaTime);
                SetStatus($"ALT {Mathf.RoundToInt(slope.Progress(actor.transform.position) * summitMeters)}m");
                break;
            case StageState.Failed:
                retryLeft -= deltaTime;
                SetStatus($"HIT!\nRESTART IN {Mathf.Max(1, Mathf.CeilToInt(retryLeft))}");
                if (retryLeft <= 0f) StartRound();
                break;
            case StageState.Cleared:
                SetStatus("SUMMIT!");
                break;
            case StageState.Advancing:
                UpdateAdvance(deltaTime);
                break;
        }
    }

    private void StartRound()
    {
        slope.ClearSnowballs();
        actor.RespawnAtStart();
        if (followCamera != null) followCamera.ResetBehindTarget();
        state = StageState.Playing;
    }

    private void OnPlayerHit()
    {
        if (state != StageState.Playing || handoff.Paused) return;
        RunRecord.Player player = RunRecord.NoteFail(gateway);
        state = StageState.Failed;
        retryLeft = retryDelay;
        slope.Running = false;
        actor.Frozen = true;
        Debug.Log("[Snow] HIT by snowball: " + player.Name, this);
        if (Multiplayer)
            gateway.ReportRunEvent("failed", $"{player.Name} 눈덩이에 맞음! {retryDelay:0}초 후 아래에서 다시 시작");
    }

    private void OnFell()
    {
        if (state != StageState.Playing) return;
        RunRecord.Player player = RunRecord.NoteFall(gateway);
        if (Multiplayer) gateway.ReportRunEvent("fell", $"{player.Name} 추락! 아래에서 다시 시작합니다");
    }

    private void OnReachedSummit()
    {
        if (state != StageState.Playing || handoff.Paused) return;
        RunRecord.Player player = RunRecord.NoteClear(gateway, stageName);
        slope.ClearSnowballs();
        Debug.Log("[Snow] SUMMIT by " + player.Name, this);
        if (Multiplayer)
            gateway.ReportRunEvent("stage_cleared", $"{player.Name}님이 {stageName} 클리어! 다음 스테이지로 이동합니다");

        // Next stage from the stage list (Resources/NaWaBaStages); the Inspector name is the fallback.
        nextScene = StageSceneLoader.NextStage(gameObject.scene.name, nextStageSceneName);
        if (StageSceneLoader.CanLoad(nextScene))
        {
            state = StageState.Advancing;
            advanceLeft = nextStageDelay;
        }
        else state = StageState.Cleared;
    }

    private void UpdateAdvance(float deltaTime)
    {
        advanceLeft -= deltaTime;
        if (advanceLeft > 0f)
        {
            SetStatus($"SUMMIT! {Mathf.RoundToInt(summitMeters)}m\nNEXT STAGE IN {Mathf.CeilToInt(advanceLeft)}");
            return;
        }
        SetStatus("LOADING...");
        if (!StageSceneLoader.Load(nextScene, this, this, carryControl: true))
            state = StageState.Cleared;
    }

    // ---------- handoff ----------

    private void OnRemotePreparing()
    {
        if (state == StageState.Advancing || state == StageState.Cleared)
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
        if (intro != null && intro.IsPlaying) intro.Skip();
        if (state == StageState.Ready) StartRound();
        Debug.Log("[Snow] 원격 조작 시작: " + gateway.PlayerName, this);
    }

    private void OnRemoteEnded()
    {
        if (state == StageState.Ready || state == StageState.Intro ||
            state == StageState.Advancing || state == StageState.Cleared)
            handoff.Reset();
        else
            handoff.OnEnded();
        inputRouter.UseLocalInput();
    }

    // ---------- host button ----------

    private bool CanHostStartRound => initialized && isActiveAndEnabled &&
        (state == StageState.Ready || state == StageState.Failed) && !handoff.Paused &&
        (gateway == null || !gateway.HasAuthority) && inputRouter.isActiveAndEnabled;

    private void OnRetryClicked()
    {
        if (!CanHostStartRound) return;
        inputRouter.UseLocalInput();
        StartRound();
    }

    private void RefreshAvailability()
    {
        if (Multiplayer)
            gateway.AllowRemoteApproval = gateway.isActiveAndEnabled &&
                state != StageState.Advancing && state != StageState.Cleared;
        if (retryButton == null) return;
        retryButton.interactable = CanHostStartRound;
        if (retryLabel != null)
            retryLabel.text = IsSinglePlayer
                ? (state == StageState.Ready ? "START" : "RETRY")
                : (state == StageState.Ready ? "START LOCAL" : "RETRY LOCAL");
    }

    // ---------- status ----------

    private void SetStatus(string text)
    {
        statusLabel = text;
        if (statusText != null && statusText.text != text) statusText.text = text;
    }

    private void OnGUI()
    {
        if (statusText != null || string.IsNullOrEmpty(statusLabel)) return;
        if (labelStyle == null)
        {
            labelStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            labelStyle.normal.textColor = Color.white;
        }
        GUI.Label(new Rect(0, 20, Screen.width, 70), statusLabel, labelStyle);
    }

    private T FindInScene<T>() where T : Component
    {
        foreach (T candidate in FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (candidate.gameObject.scene == gameObject.scene) return candidate;
        return null;
    }
}
