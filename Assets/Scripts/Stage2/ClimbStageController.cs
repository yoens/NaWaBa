using UnityEngine;

// Stage 2 (climb). Binds this scene's input targets to the persistent multiplayer
// session so the web participant can be approved, and freezes the actor during handoff.
// The summit/goal check is intentionally not part of this version.
// Disable approvals before ordinary actors get their first Update.
[DefaultExecutionOrder(-100)]
public class ClimbStageController : MonoBehaviour, IStageFlow
{
    // Leave empty to find them in this scene automatically.
    [Header("Connections (auto-found when empty)")]
    [SerializeField] private ClimbActorController actor;
    [SerializeField] private InputRouter inputRouter;
    [SerializeField] private RemoteInputGateway gateway;

    [Header("Web display")]
    [SerializeField] private string stageId = "climb";
    [SerializeField] private string stageName = "2 · 등반";

    private const string StageControls = "move,jump,look";

    private enum StageState
    {
        Local,
        PreparingRemote,
        PlayingRemote
    }

    private StageState state;
    private bool initialized;
    private bool usesPersistentGateway;
    private RemoteInputSource stageRemoteInputSource;

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
            // The scene gateway is only a configuration reference; never a second socket.
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

        if (gateway != null) gateway.AllowRemoteApproval = false;
        initialized = true;
    }

    private void OnEnable()
    {
        if (!initialized) return;

        state = StageState.Local;
        actor.InputLocked = false;
        inputRouter.UseLocalInput();

        if (!usesPersistentGateway) return;

        if (!gateway.BindStageInputs(inputRouter, stageRemoteInputSource,
                stageId, stageName, StageControls))
        {
            Debug.LogError("ClimbStageController: 스테이지 입력을 멀티플레이 세션에 연결하지 못했습니다.", this);
            usesPersistentGateway = false;
            return;
        }

        gateway.RemoteControlPreparing += OnRemotePreparing;
        gateway.RemoteControlStarted += OnRemoteStarted;
        gateway.RemoteControlEnded += OnRemoteEnded;
    }

    private void OnDisable()
    {
        if (usesPersistentGateway && gateway != null)
        {
            gateway.RemoteControlPreparing -= OnRemotePreparing;
            gateway.RemoteControlStarted -= OnRemoteStarted;
            gateway.RemoteControlEnded -= OnRemoteEnded;
            gateway.AllowRemoteApproval = false;
            if (gateway.HasAuthority) gateway.ReclaimControl();
            gateway.UnbindStageInputs(inputRouter);
        }

        if (actor != null) actor.InputLocked = false;
        state = StageState.Local;
    }

    public void PrepareToLeave()
    {
        // OnDisable revokes control and unbinds the stage inputs.
        enabled = false;
    }

    private void Update()
    {
        if (!usesPersistentGateway) return;

        if (state == StageState.PlayingRemote &&
            (!gateway.IsConnected || !gateway.IsControlling ||
             !inputRouter.isActiveAndEnabled || !inputRouter.IsRemoteControl))
        {
            if (gateway.HasAuthority) gateway.ReclaimControl();
            ReturnToLocal();
        }

        gateway.AllowRemoteApproval = gateway.isActiveAndEnabled &&
            state == StageState.Local && !gateway.HasAuthority &&
            inputRouter.isActiveAndEnabled;
    }

    private void OnRemotePreparing()
    {
        if (state != StageState.Local)
        {
            gateway.ReclaimControl();
            return;
        }

        // Stop the host's movement while video connects and the countdown runs.
        state = StageState.PreparingRemote;
        actor.InputLocked = true;
        Debug.Log("[Climb] 원격 조작 준비", this);
    }

    private void OnRemoteStarted()
    {
        if (state != StageState.PreparingRemote || !gateway.IsControlling ||
            !inputRouter.IsRemoteControl)
        {
            gateway.ReclaimControl();
            return;
        }

        state = StageState.PlayingRemote;
        actor.InputLocked = false;
        Debug.Log("[Climb] 원격 조작 시작: " + gateway.PlayerName, this);
    }

    private void OnRemoteEnded()
    {
        ReturnToLocal();
    }

    private void ReturnToLocal()
    {
        if (state == StageState.Local) return;
        state = StageState.Local;
        actor.InputLocked = false;
        inputRouter.UseLocalInput();
        Debug.Log("[Climb] 호스트 조작으로 복귀", this);
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
