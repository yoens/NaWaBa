using UnityEngine;

// Shared pause rule for handing control over in the middle of a stage.
// The game freezes while the next player's video connects and counts down,
// and briefly when control returns to the host, then resumes exactly where it was.
public sealed class StageHandoff
{
    public enum Phase
    {
        None,
        PreparingRemote,   // approved participant: video + countdown
        ReturningToHost    // short countdown before the host's keys take over
    }

    private readonly RemoteInputGateway gateway;
    private readonly float hostResumeDelay;
    private float hostResumeLeft;

    public StageHandoff(RemoteInputGateway gateway, float hostResumeDelay)
    {
        this.gateway = gateway;
        this.hostResumeDelay = Mathf.Max(0f, hostResumeDelay);
    }

    public Phase Current { get; private set; }
    public bool Paused => Current != Phase.None;

    public string Label
    {
        get
        {
            if (Current == Phase.ReturningToHost)
                return $"HOST IN {Mathf.Max(1, Mathf.CeilToInt(hostResumeLeft))}";
            if (Current == Phase.PreparingRemote && gateway != null)
            {
                string who = string.IsNullOrEmpty(gateway.PlayerName) ? "" : "\n" + gateway.PlayerName;
                return "HANDOFF · " + gateway.ControlState + who;
            }
            return "";
        }
    }

    public void OnPreparing()
    {
        Current = Phase.PreparingRemote;
        hostResumeLeft = 0f;
    }

    public void OnStarted()
    {
        Current = Phase.None;
    }

    // Control came back to the host (Take Back, timeout, participant left, video failed).
    public void OnEnded()
    {
        Current = hostResumeDelay > 0f ? Phase.ReturningToHost : Phase.None;
        hostResumeLeft = hostResumeDelay;
    }

    public void Reset()
    {
        Current = Phase.None;
        hostResumeLeft = 0f;
    }

    public void Tick(float deltaTime)
    {
        if (Current != Phase.ReturningToHost) return;
        hostResumeLeft -= deltaTime;
        if (hostResumeLeft <= 0f) Reset();
    }
}
