using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Disable simulation before ordinary actors/spawners get their first Update.
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

    [Header("Flow")]
    // 클리어하면 이 씬으로 자동 이동합니다. 비워 두면 RETRY만 가능합니다.
    [SerializeField] private string nextStageSceneName = "Stage_Climb";
    [SerializeField, Min(0f)] private float nextStageDelay = 3f;

    private const string StageId = "dodge";
    private const string StageName = "1 · 눈덩이 피하기";
    private const string StageControls = "move";

    private enum StageState
    {
        Ready,
        PreparingRemote,
        PlayingLocal,
        PlayingRemote,
        Cleared,
        Failed,
        Cancelled,
        Advancing
    }

    private StageState state;
    private Rigidbody playerBody;
    private TMP_Text retryLabel;
    private Vector3 startPosition;
    private Quaternion startRotation;
    private float remainingTime;
    private float roundStartedAt;
    private float advanceAt;
    private bool initialized;
    private bool usesPersistentGateway;
    private RemoteInputSource stageRemoteInputSource;

    private bool IsSinglePlayer => gateway != null && gateway.UsesGameSessionMode &&
        GameSession.Mode == GameMode.SinglePlayer;

    private bool IsPlaying => state == StageState.PlayingLocal ||
        state == StageState.PlayingRemote;

    private bool CanStartAttempt => initialized && isActiveAndEnabled &&
        (state == StageState.Ready || state == StageState.Cleared ||
         state == StageState.Failed || state == StageState.Cancelled);

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
        initialized = true;
    }

    private void Start()
    {
        if (initialized && IsSinglePlayer)
            StartLocalAttempt();
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

        gateway.RemoteControlPreparing += OnRemotePreparing;
        gateway.RemoteControlStarted += OnRemoteStarted;
        gateway.RemoteControlEnded += OnRemoteEnded;
        retryButton.onClick.AddListener(StartLocalAttempt);

        // Re-enabling a stage never restores a previous grant or running attempt.
        state = StageState.Ready;
        if (gateway.HasAuthority) gateway.ReclaimControl();
        inputRouter.UseLocalInput();
        ResetAttempt();
        statusText.text = "READY";
        RefreshAvailability();
    }

    private void OnDisable()
    {
        if (gateway != null)
        {
            gateway.RemoteControlPreparing -= OnRemotePreparing;
            gateway.RemoteControlStarted -= OnRemoteStarted;
            gateway.RemoteControlEnded -= OnRemoteEnded;
            gateway.AllowRemoteApproval = false;
            if (gateway.HasAuthority) gateway.ReclaimControl();
            if (usesPersistentGateway) gateway.UnbindStageInputs(inputRouter);
        }

        if (retryButton != null)
        {
            retryButton.onClick.RemoveListener(StartLocalAttempt);
            retryButton.interactable = false;
        }

        StopSimulation();
        if (initialized) ClearBullets();
    }

    public void PrepareToLeave()
    {
        // OnDisable removes event subscriptions, revokes control and clears bullets.
        enabled = false;
    }

    private void Update()
    {
        if (!initialized) return;

        // OnEnable can run before InputRouter is active due to execution order.
        // Refresh while READY too, so the initial disabled button can recover.
        RefreshAvailability();

        if (state == StageState.PreparingRemote)
            statusText.text = gateway.ControlState;
        if (state == StageState.Advancing)
        {
            UpdateAdvance();
            return;
        }
        if (!IsPlaying) return;

        if (state == StageState.PlayingRemote &&
            (!gateway.IsConnected || !gateway.IsControlling ||
             !inputRouter.isActiveAndEnabled || !inputRouter.IsRemoteControl))
        {
            EndAttempt(StageState.Cancelled);
            return;
        }

        if (playerHit.IsHit)
        {
            EndAttempt(StageState.Failed);
            return;
        }

        remainingTime = Mathf.Max(0f, surviveDuration - (Time.time - roundStartedAt));
        if (remainingTime <= 0f)
        {
            EndAttempt(StageState.Cleared);
            return;
        }

        RefreshTimeText();
    }

    private void StartLocalAttempt()
    {
        if (!CanStartAttempt || gateway.HasAuthority || !inputRouter.isActiveAndEnabled)
            return;

        inputRouter.UseLocalInput();
        ResetAttempt();
        BeginPlaying(false);
    }

    private void OnRemotePreparing()
    {
        if (!CanStartAttempt)
        {
            gateway.ReclaimControl();
            return;
        }

        state = StageState.PreparingRemote;
        ResetAttempt();
        statusText.text = "CONNECTING VIDEO";
        RefreshAvailability();
    }

    private void OnRemoteStarted()
    {
        if (state != StageState.PreparingRemote || !gateway.IsControlling ||
            !inputRouter.IsRemoteControl)
        {
            gateway.ReclaimControl();
            return;
        }

        // The existing server sends control_started after its countdown.
        BeginPlaying(true);
    }

    private void OnRemoteEnded()
    {
        if (state == StageState.PreparingRemote || state == StageState.PlayingRemote)
            EndAttempt(StageState.Cancelled);
    }

    private void BeginPlaying(bool remote)
    {
        state = remote ? StageState.PlayingRemote : StageState.PlayingLocal;
        roundStartedAt = Time.time;
        remainingTime = surviveDuration;
        actorController.enabled = true;
        bulletSpawner.enabled = true;
        RefreshTimeText();
        RefreshAvailability();
    }

    private void ResetAttempt()
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
        remainingTime = surviveDuration;
    }

    private void EndAttempt(StageState result)
    {
        if (!IsPlaying && state != StageState.PreparingRemote) return;

        bool wasRemoteRound = state == StageState.PlayingRemote;
        // Set the terminal state before revocation emits RemoteControlEnded.
        // That callback must not overwrite CLEAR/FAILED with CANCELLED.
        state = result;
        StopSimulation();
        ClearBullets();

        if (gateway.HasAuthority)
        {
            if (wasRemoteRound && (result == StageState.Cleared || result == StageState.Failed))
                gateway.CompleteRemoteRound(result == StageState.Cleared);
            else
                gateway.ReclaimControl();
        }

        statusText.text = result == StageState.Cleared ? "CLEAR!" :
            result == StageState.Failed ? "FAILED" : "CANCELLED";
        Debug.Log("[Dodge] " + statusText.text, this);

        // 클리어하면 잠시 결과를 보여준 뒤 다음 스테이지로 이동합니다.
        // Advancing 동안은 새 참가 승인과 RETRY를 막습니다.
        if (result == StageState.Cleared && StageSceneLoader.CanLoad(nextStageSceneName))
        {
            state = StageState.Advancing;
            advanceAt = Time.time + nextStageDelay;
            UpdateAdvance();
        }
        RefreshAvailability();
    }

    private void UpdateAdvance()
    {
        float remaining = advanceAt - Time.time;
        if (remaining > 0f)
        {
            statusText.text = $"CLEAR!\nNEXT STAGE IN {Mathf.CeilToInt(remaining)}";
            return;
        }

        statusText.text = "LOADING...";
        if (!StageSceneLoader.Load(nextStageSceneName, this, this))
        {
            // 이동에 실패하면 이 스테이지에서 다시 도전할 수 있게 둡니다.
            state = StageState.Cleared;
            statusText.text = "CLEAR!";
            RefreshAvailability();
        }
    }

    private void RefreshTimeText()
    {
        statusText.text = $"TIME: {Mathf.CeilToInt(remainingTime)}";
    }

    private void RefreshAvailability()
    {
        gateway.AllowRemoteApproval = !IsSinglePlayer && gateway.isActiveAndEnabled &&
            CanStartAttempt && !gateway.HasAuthority;
        retryButton.interactable = CanStartAttempt && !gateway.HasAuthority &&
            inputRouter.isActiveAndEnabled;
        if (retryLabel != null)
            retryLabel.text = IsSinglePlayer
                ? (state == StageState.Ready ? "START" : "RETRY")
                : (state == StageState.Ready ? "START LOCAL" : "RETRY LOCAL");
    }

    private void StopSimulation()
    {
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
