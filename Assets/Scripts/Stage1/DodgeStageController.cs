using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Stage 1: survive the falling snowballs for surviveDuration seconds.
// Control can change hands at any time: the round freezes (time, bullets, player) during the
// handoff and resumes where it was. A failure restarts stage 1 with the same controller.
// Clearing moves to stage 2 and a remote controller keeps their turn.
[DefaultExecutionOrder(-100)]
public class DodgeStageController : MonoBehaviour, IStageFlow
{
    [Header("Connections")]
    [SerializeField] private ActorController actorController;
    [SerializeField] private DodgePlayerHit playerHit;
    [SerializeField] private DodgeBulletSpawner bulletSpawner;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private Button retryButton;
    [SerializeField] private RemoteInputGateway gateway;
    [SerializeField] private InputRouter inputRouter;

    [Header("Rules")]
    [SerializeField, Min(1f)] private float surviveDuration = 20f;
    // After a hit the round restarts automatically (RETRY restarts it at once for the host).
    [SerializeField, Min(0f)] private float retryDelay = 2f;
    // Short countdown before the host's keys take over again after a remote turn.
    [SerializeField, Min(0f)] private float hostResumeDelay = 1.5f;

    [Header("Flow")]
    // 클리어하면 이 씬으로 자동 이동합니다. 비워 두면 RETRY만 가능합니다.
    [SerializeField] private string nextStageSceneName = "Stage_Climb";
    [SerializeField, Min(0f)] private float nextStageDelay = 3f;

    private const string StageId = "dodge";
    private const string StageName = "1 · 눈덩이 피하기";
    private const string StageControls = "move";

    private enum StageState
    {
        Ready,      // no round yet (multiplayer waits for START or an approved participant)
        Playing,    // round running (may be paused for a handoff)
        Failed,     // hit; restarting after retryDelay
        Cleared,    // survived, but there is no next stage to load
        Advancing   // survived; loading the next stage
    }

    private StageState state;
    private StageHandoff handoff;
    private Rigidbody playerBody;
    private TMP_Text retryLabel;
    private Vector3 startPosition;
    private Quaternion startRotation;
    private float roundTime;
    private float retryLeft;
    private float advanceLeft;
    private bool simulationRunning;
    private bool initialized;
    private bool usesPersistentGateway;
    private RemoteInputSource stageRemoteInputSource;

    private bool IsSinglePlayer => gateway != null && gateway.UsesGameSessionMode &&
        GameSession.Mode == GameMode.SinglePlayer;

    private bool Multiplayer => usesPersistentGateway;

    private void Awake()
    {
        StopSimulation();
        if (retryButton != null) retryButton.interactable = false;
        RemoteInputGateway configuredGateway = gateway;
        usesPersistentGateway = configuredGateway != null &&
            configuredGateway.UsesGameSessionMode && GameSession.Mode == GameMode.Multiplayer;
        if (usesPersistentGateway)
        {
            stageRemoteInputSource = configuredGateway.RemoteInputTarget;
            configuredGateway.enabled = false;
            gateway = RemoteInputGateway.ResolveForScene(configuredGateway);
        }
        if (gateway != null) gateway.AllowRemoteApproval = false;

        if (actorController == null || playerHit == null || bulletSpawner == null ||
            statusText == null || retryButton == null || gateway == null || inputRouter == null)
        {
            Debug.LogError("DodgeStageController: assign all seven Inspector references.", this);
            enabled = false;
            return;
        }

        if (usesPersistentGateway && stageRemoteInputSource == null)
        {
            Debug.LogError("DodgeStageController: assign RemoteInputSource on the scene gateway.", this);
            enabled = false;
            return;
        }

        playerBody = actorController.GetComponent<Rigidbody>();
        if (playerBody == null)
        {
            Debug.LogError("DodgeStageController: the player needs a Rigidbody.", this);
            enabled = false;
            return;
        }

        startPosition = playerBody.position;
        startRotation = playerBody.rotation;
        retryLabel = retryButton.GetComponentInChildren<TMP_Text>();
        handoff = new StageHandoff(usesPersistentGateway ? gateway : null, hostResumeDelay);

        // Stage 1 is always the start of a new run (한 판).
        RunRecord.BeginRun();
        initialized = true;
    }

    private void Start()
    {
        if (initialized && IsSinglePlayer)
            StartRound();
    }

    private void OnEnable()
    {
        if (!initialized) return;
        if (usesPersistentGateway && !gateway.BindStageInputs(inputRouter, stageRemoteInputSource,
                StageId, StageName, StageControls))
        {
            Debug.LogError("DodgeStageController: could not bind the stage input targets.", this);
            enabled = false;
            return;
        }

        if (usesPersistentGateway)
        {
            gateway.RemoteControlPreparing += OnRemotePreparing;
            gateway.RemoteControlStarted += OnRemoteStarted;
            gateway.RemoteControlEnded += OnRemoteEnded;
        }
        retryButton.onClick.AddListener(OnRetryClicked);

        state = StageState.Ready;
        handoff.Reset();
        inputRouter.UseLocalInput();
        ResetRound();
        statusText.text = "READY";
        RefreshAvailability();
    }

    private void OnDisable()
    {
        if (gateway != null && usesPersistentGateway)
        {
            gateway.RemoteControlPreparing -= OnRemotePreparing;
            gateway.RemoteControlStarted -= OnRemoteStarted;
            gateway.RemoteControlEnded -= OnRemoteEnded;
            gateway.AllowRemoteApproval = false;
            // A controller carried into the next stage keeps their turn.
            if (gateway.HasAuthority && !gateway.IsCarryingControl) gateway.ReclaimControl();
            gateway.UnbindStageInputs(inputRouter);
        }

        if (retryButton != null)
        {
            retryButton.onClick.RemoveListener(OnRetryClicked);
            retryButton.interactable = false;
        }

        StopSimulation();
        DodgeBullet.Frozen = false;
        if (initialized) ClearBullets();
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
        RefreshAvailability();

        // Handoff: everything stands still and no time passes.
        if (handoff.Paused && state != StageState.Advancing)
        {
            SetSimulation(false);
            statusText.text = handoff.Label;
            return;
        }

        switch (state)
        {
            case StageState.Ready:
                statusText.text = "READY";
                break;

            case StageState.Playing:
                SetSimulation(true);
                if (playerHit.IsHit)
                {
                    FailRound();
                    break;
                }
                roundTime += deltaTime;
                RunRecord.AddControlTime(gateway, deltaTime);
                if (roundTime >= surviveDuration)
                {
                    ClearRound();
                    break;
                }
                statusText.text = $"TIME: {Mathf.CeilToInt(surviveDuration - roundTime)}";
                break;

            case StageState.Failed:
                retryLeft -= deltaTime;
                statusText.text = $"FAILED\nRETRY IN {Mathf.Max(1, Mathf.CeilToInt(retryLeft))}";
                if (retryLeft <= 0f) StartRound();
                break;

            case StageState.Advancing:
                UpdateAdvance(deltaTime);
                break;
        }
    }

    private void OnRetryClicked()
    {
        if (!CanHostStartRound) return;
        inputRouter.UseLocalInput();
        StartRound();
    }

    private bool CanHostStartRound => initialized && isActiveAndEnabled &&
        (state == StageState.Ready || state == StageState.Failed || state == StageState.Cleared) &&
        !handoff.Paused && !gateway.HasAuthority && inputRouter.isActiveAndEnabled;

    private void OnRemotePreparing()
    {
        // Allowed at any time except while the next stage is loading.
        if (state == StageState.Advancing)
        {
            gateway.ReclaimControl();
            return;
        }
        handoff.OnPreparing();
        SetSimulation(false);
    }

    private void OnRemoteStarted()
    {
        if (!handoff.Paused || !gateway.IsControlling || !inputRouter.IsRemoteControl)
        {
            gateway.ReclaimControl();
            return;
        }
        handoff.OnStarted();
        // Approved before any round ran: the participant starts stage 1.
        if (state == StageState.Ready || state == StageState.Cleared) StartRound();
    }

    private void OnRemoteEnded()
    {
        // Nothing is running: no need for a host countdown.
        if (state == StageState.Ready || state == StageState.Advancing || state == StageState.Cleared)
            handoff.Reset();
        else
            handoff.OnEnded();
    }

    private void StartRound()
    {
        ResetRound();
        state = StageState.Playing;
        SetSimulation(!handoff.Paused);
    }

    private void FailRound()
    {
        RunRecord.Player player = RunRecord.NoteFail(gateway);
        state = StageState.Failed;
        retryLeft = retryDelay;
        StopSimulation();
        ClearBullets();
        Debug.Log("[Dodge] FAILED by " + player.Name, this);
        if (Multiplayer)
            gateway.ReportRunEvent("failed", $"{player.Name} 피격! {retryDelay:0}초 후 1스테이지 다시 시작");
    }

    private void ClearRound()
    {
        RunRecord.Player player = RunRecord.NoteClear(gateway, StageName);
        StopSimulation();
        ClearBullets();
        Debug.Log("[Dodge] CLEAR by " + player.Name, this);
        if (Multiplayer)
            gateway.ReportRunEvent("stage_cleared", $"{player.Name}님이 {StageName} 클리어! 다음 스테이지로 이동합니다");

        if (StageSceneLoader.CanLoad(nextStageSceneName))
        {
            state = StageState.Advancing;
            advanceLeft = nextStageDelay;
            statusText.text = $"CLEAR!\nNEXT STAGE IN {Mathf.CeilToInt(advanceLeft)}";
        }
        else
        {
            state = StageState.Cleared;
            statusText.text = "CLEAR!";
        }
    }

    private void UpdateAdvance(float deltaTime)
    {
        // The current controller keeps playing in the next stage; no handoff now.
        advanceLeft -= deltaTime;
        if (advanceLeft > 0f)
        {
            statusText.text = $"CLEAR!\nNEXT STAGE IN {Mathf.CeilToInt(advanceLeft)}";
            return;
        }

        statusText.text = "LOADING...";
        if (!StageSceneLoader.Load(nextStageSceneName, this, this, carryControl: true))
        {
            state = StageState.Cleared;
            statusText.text = "CLEAR!";
        }
    }

    private void RefreshAvailability()
    {
        if (Multiplayer)
            gateway.AllowRemoteApproval = gateway.isActiveAndEnabled && state != StageState.Advancing;
        retryButton.interactable = CanHostStartRound;
        if (retryLabel != null)
            retryLabel.text = IsSinglePlayer
                ? (state == StageState.Ready ? "START" : "RETRY")
                : (state == StageState.Ready ? "START LOCAL" : "RETRY LOCAL");
    }

    private void ResetRound()
    {
        StopSimulation();
        ClearBullets();

        playerBody.position = startPosition;
        playerBody.rotation = startRotation;
        if (!playerBody.isKinematic)
        {
            playerBody.linearVelocity = Vector3.zero;
            playerBody.angularVelocity = Vector3.zero;
        }

        playerHit.ResetHitState();
        bulletSpawner.ResetSpawnTimer();
        roundTime = 0f;
    }

    // Runs or freezes the player, the spawner and every bullet together.
    private void SetSimulation(bool running)
    {
        if (simulationRunning == running && actorController.enabled == running) return;
        simulationRunning = running;
        actorController.enabled = running;
        bulletSpawner.enabled = running;
        DodgeBullet.Frozen = !running;
    }

    private void StopSimulation()
    {
        simulationRunning = false;
        if (actorController != null) actorController.enabled = false;
        if (bulletSpawner != null) bulletSpawner.enabled = false;
    }

    private void ClearBullets()
    {
        DodgeBullet[] bullets = FindObjectsByType<DodgeBullet>(FindObjectsSortMode.None);
        foreach (DodgeBullet bullet in bullets)
        {
            if (bullet.gameObject.scene != gameObject.scene) continue;
            bullet.gameObject.SetActive(false);
            Destroy(bullet.gameObject);
        }
    }
}
