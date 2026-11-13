using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

public class RemoteInputGateway : MonoBehaviour
{
    // Keep these names: existing Inspector references are preserved when overwriting this file.
    [SerializeField] private string serverUrl = "ws://127.0.0.1:3000/host";
    [SerializeField] private RemoteInputSource remoteInputSource;
    [SerializeField] private InputRouter inputRouter;

    [SerializeField] private bool useGameSessionMode;
    public bool UsesGameSessionMode => useGameSessionMode;
    public RemoteInputSource RemoteInputTarget => remoteInputSource;

    public static RemoteInputGateway ResolveForScene(RemoteInputGateway configured)
    {
        if (configured == null || !configured.UsesGameSessionMode ||
            GameSession.Mode != GameMode.Multiplayer) return configured;
        MultiplayerHostSession owner = MultiplayerHostSession.Instance;
        return owner != null ? owner.Gateway : null;
    }

    public bool BindStageInputs(InputRouter router, RemoteInputSource source,
        string stageId = "stage", string stageName = "", string controls = "move")
    {
        if (!managedLobbySession || router == null || source == null) return false;
        if (inputRouter != null && inputRouter != router)
        {
            Debug.LogError("NaWaBa: another stage already owns the input targets.", this);
            return false;
        }
        AllowRemoteApproval = false;
        // A controller carried over from the previous stage keeps their turn; resume is
        // requested from Update once this stage's video sender has registered.
        if (carrying) resumePending = true;
        else ReclaimControl();
        inputRouter = router;
        remoteInputSource = source;
        remoteInputSource.ClearInput();
        inputRouter.UseLocalInput();
        ReportStage(stageId, stageName, controls);
        return true;
    }

    public void UnbindStageInputs(InputRouter expectedRouter)
    {
        if (!managedLobbySession || inputRouter != expectedRouter) return;
        AllowRemoteApproval = false;
        if (!carrying) ReclaimControl();
        // The request list belongs to the room, not the scene: it survives stage changes.
        if (remoteInputSource != null) remoteInputSource.ClearInput();
        if (inputRouter != null) inputRouter.UseLocalInput();
        remoteInputSource = null;
        inputRouter = null;
        ReportStage("loading", "이동 중", "");
    }

    // What the web page shows (current stage and which buttons to enable).
    // Remembered and re-sent after every (re)connection.
    private string reportedStageId = "lobby";
    private string reportedStageName = "대기실";
    private string reportedControls = "";

    public void ReportStage(string stageId, string stageName, string controls)
    {
        reportedStageId = string.IsNullOrEmpty(stageId) ? "stage" : stageId;
        reportedStageName = stageName ?? "";
        reportedControls = controls ?? "";
        SendStageStatus();
    }

    private void SendStageStatus()
    {
        if (!IsConnected) return;
        Queue(new Message {
            type = "stage_status", stageId = reportedStageId,
            stageName = reportedStageName, controls = reportedControls
        });
    }

    // A persistent lobby connection can exist before a stage provides input targets.
    private bool managedLobbySession;
    private string inviteUrl = "";
    private bool guestStatusSupported;
    private bool guestConnected;
    private int participantCount;
    public bool SupportsGuestStatus => IsConnected && guestStatusSupported;
    public bool IsGuestConnected => SupportsGuestStatus && guestConnected;
    // Web participants currently in this room (watchers + at most one controller).
    public int ParticipantCount => SupportsGuestStatus ? participantCount : 0;
    private double connectionStartedAt;
    private static Task pendingShutdown = Task.CompletedTask;
    public string InviteUrl => IsConnected ? inviteUrl : "";
    public string RoomId { get; private set; } = "";
    public string RoomLabel => IsConnected && RoomId.Length >= 8 ? RoomId.Substring(0, 8) : "-";
    public string ConnectionIssue { get; private set; } = "";
    public bool IsConnecting => isActiveAndEnabled && session != null &&
        !session.finished && !helloReceived;

    internal void ConfigureManagedLobbySession()
    {
        if (gameObject.activeInHierarchy)
            throw new InvalidOperationException("Configure the session while inactive.");
        managedLobbySession = true;
        useGameSessionMode = true;
        AllowRemoteApproval = false;
    }

    public void Reconnect()
    {
        if (!Application.isPlaying || !gameObject.activeInHierarchy ||
            IsConnected || IsConnecting) return;
        enabled = false;
        enabled = true;
    }

    private bool IsValidInviteUrl(string value, string roomId)
    {
        if (string.IsNullOrEmpty(roomId) || !Guid.TryParseExact(roomId, "D", out Guid parsedRoom) ||
            parsedRoom.ToString("D") != roomId) return false;
        string prefix = "#room=" + roomId + "&join=";
        if (string.IsNullOrEmpty(value) || value.Length > 1024 ||
            !Uri.TryCreate(value, UriKind.Absolute, out Uri invite) ||
            !Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri server)) return false;
        string expectedScheme = server.Scheme == "wss" ? "https" : "http";
        if (invite.Scheme != expectedScheme || invite.Host != server.Host ||
            invite.Port != server.Port || !string.IsNullOrEmpty(invite.UserInfo) ||
            invite.AbsolutePath != "/" || !string.IsNullOrEmpty(invite.Query) ||
            !invite.Fragment.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string key = invite.Fragment.Substring(prefix.Length);
        if (key.Length != 64) return false;
        foreach (char c in key)
            if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
        return true;
    }

    private const int ProtocolVersion = 8;
    private const int MaxPendingMessages = 32;
    private const double InputTimeout = 0.5;
    private Session session;
    private bool helloReceived;
    private Message authority;
    private bool controlling;
    private long lastSequence = -1;
    private double lastInputAt;
    private double lastVideoAt;
    private double countdownEndsAt;
    private bool videoReady;
    private string localVideoGrant = "";
    private bool localVideoConnected;
    private NaWaBaRoomVideoSender videoSender;
    // Stage change with a remote controller: authority is kept, video/input are not.
    private bool carrying;
    private bool resumePending;
    public bool IsCarryingControl => carrying;
    public string CurrentGrantId => authority != null ? authority.grantId ?? "" : "";
    public int RemoteCountdown => countdownEndsAt > 0
        ? Math.Max(0, (int)Math.Ceiling(countdownEndsAt - ClockNow())) : 0;
    public event Action<string> RemoteVideoRequested;
    public event Action<string, string> RemoteVideoAnswer;

    public bool RegisterVideoSender(NaWaBaRoomVideoSender sender)
    {
        if (sender == null || (videoSender != null && videoSender != sender)) return false;
        videoSender = sender;
        return true;
    }

    public void UnregisterVideoSender(NaWaBaRoomVideoSender sender)
    {
        if (videoSender != sender) return;
        videoSender = null;
        if (HasAuthority && !carrying) ReclaimControl();
    }

    public bool SendVideoOffer(string grant, string sdp)
    {
        if (string.IsNullOrEmpty(grant) || CurrentGrantId != grant || string.IsNullOrEmpty(sdp) ||
            Encoding.UTF8.GetByteCount(sdp) > 60000) return false;
        return Queue(new Message { type = "video_offer", controllerId = authority.controllerId,
            requestId = authority.requestId, grantId = grant, sdp = sdp });
    }

    public void SetLocalVideoConnected(string grant, bool connected)
    {
        if (string.IsNullOrEmpty(grant) || CurrentGrantId != grant) return;
        localVideoGrant = grant;
        localVideoConnected = connected;
    }

    public void ReportVideoFailure(string grant)
    {
        if (string.IsNullOrEmpty(grant) || CurrentGrantId != grant) return;
        Queue(new Message { type = "video_failed", controllerId = authority.controllerId,
            requestId = authority.requestId, grantId = grant });
        ClearAuthority();
    }

    public bool IsConnected => session != null && session.connected &&
        !session.finished && helloReceived;
    public string PendingControllerId { get; private set; } = "";
    public string PendingRequestId { get; private set; } = "";
    public string PendingNickname { get; private set; } = "";
    // Approval is possible when nobody plays remotely, or while a participant plays
    // (that hands control over). Not while a handoff or stage change is still being prepared.
    public bool HasPendingRequest => IsConnected && PendingRequestId.Length > 0 &&
        !carrying && (authority == null || controlling);
    public string ControllerId => authority != null ? authority.controllerId ?? "" : "";

    // Server-ordered request list (front = the one Approve will grant).
    private const int MaxQueueEntries = 64;
    private string[] queueNames = Array.Empty<string>();
    public int QueueCount => IsConnected ? queueNames.Length : 0;
    public string QueueSummary
    {
        get
        {
            if (QueueCount == 0) return "-";
            string front = queueNames[0];
            return QueueCount == 1 ? front : $"{front} (+{QueueCount - 1})";
        }
    }
    public bool HasAuthority => authority != null;
    public bool IsControlling => controlling;

    // A stage can temporarily disallow approvals without disconnecting the host.
    // A live video sender and valid stage input targets are also required.
    public bool AllowRemoteApproval { get; set; } = true;
    public bool CanApprovePendingRequest => AllowRemoteApproval && HasPendingRequest &&
        videoSender != null && videoSender.isActiveAndEnabled &&
        inputRouter != null && inputRouter.isActiveAndEnabled &&
        remoteInputSource != null && remoteInputSource.isActiveAndEnabled;

    // Raised only on Unity's main thread, after the corresponding state changes.
    public event Action RemoteControlPreparing;
    public event Action RemoteControlStarted;
    public event Action RemoteControlEnded;
    public string PlayerName => authority != null ? authority.nickname : PendingNickname;
    public string ControlState => controlling ? "REMOTE" : authority == null ? "LOCAL" :
        videoReady ? "START IN " + RemoteCountdown : "CONNECTING VIDEO";

    [Serializable]
    private class HostConfig
    {
        public int schemaVersion;
        public string serverUrl;
        public string hostKey;
    }

    private bool TryReadHostConfig(out HostConfig config)
    {
        config = null;
        // Editor: project root. Windows player: folder containing the .exe.
        string configPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "nawaba-host.local.json");
        try
        {
            config = JsonUtility.FromJson<HostConfig>(File.ReadAllText(configPath));
            if (config == null || config.schemaVersion != 1 || string.IsNullOrEmpty(config.hostKey) ||
                config.hostKey.Length != 64) throw new InvalidOperationException();
            foreach (char c in config.hostKey)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) throw new InvalidOperationException();
            if (!Uri.TryCreate(config.serverUrl, UriKind.Absolute, out Uri address) ||
                (address.Scheme != "ws" && address.Scheme != "wss") || address.AbsolutePath != "/host" ||
                !string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Query) ||
                !string.IsNullOrEmpty(address.Fragment)) throw new InvalidOperationException();
            return true;
        }
        catch
        {
            Debug.LogError("NaWaBa: missing or invalid host configuration. Run WebController/setup-local.cjs. " +
                "For a Windows build, copy nawaba-host.local.json beside the .exe. Expected file: " + configPath, this);
            return false;
        }
    }

    // STUN/TURN servers sent by the server (host_hello). The video sender uses them.
    [Serializable]
    public class IceServer
    {
        public string[] urls;
        public string username;
        public string credential;
    }

    public IceServer[] IceServers { get; private set; }

    [Serializable]
    private class Message
    {
        public IceServer[] iceServers;
        public int protocolVersion = ProtocolVersion;
        public string type;
        public string roomId;
        public string controllerId;
        public string requestId;
        public string grantId;
        public string nickname;
        public string reason;
        public string inviteUrl;
        public string sdp;
        public int countdownMs;
        public bool guestStatusSupported;
        public bool guestConnected;
        public int participantCount;
        public long sequence = -1;
        public float x;
        public float y;
        public long jump;      // cumulative jump presses in this grant
        public float look;     // camera yaw input, -1..1
        public float lookY;    // camera pitch input, -1..1 (+ = look up); 0 when not sent
        public string stageId;
        public string stageName;
        public string controls;
        public QueueEntry[] queue;   // queue_status: waiting requests, front first
        public string kind;          // run_event
        public string text;          // run_event
        public string[] lines;       // run_event (final result)
    }

    [Serializable]
    private class QueueEntry
    {
        public string controllerId;
        public string requestId;
        public string nickname;
    }

    private class Packet
    {
        public string json;
        public double receivedAt;
    }

    private class Session
    {
        public readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        public readonly ConcurrentQueue<Packet> incoming = new ConcurrentQueue<Packet>();
        public readonly ConcurrentQueue<string> outgoing = new ConcurrentQueue<string>();
        public readonly SemaphoreSlim sendSignal = new SemaphoreSlim(0);
        public Task worker;
        public volatile bool connected;
        public volatile bool finished;
        public string error;
    }

    private static double ClockNow() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    private void ClearPendingRequest()
    {
        PendingControllerId = "";
        PendingRequestId = "";
        PendingNickname = "";
        queueNames = Array.Empty<string>();
    }

    // Host-side "remove from list" for the front request (e.g. someone who is away).
    public void RemoveFrontRequest()
    {
        if (!IsConnected || string.IsNullOrEmpty(PendingRequestId)) return;
        Queue(new Message {
            type = "remove_request", controllerId = PendingControllerId, requestId = PendingRequestId
        });
    }

    private void ClearAuthority()
    {
        bool hadAuthority = authority != null;
        authority = null;
        carrying = false;
        resumePending = false;
        ResetControlState();
        if (hadAuthority) RemoteControlEnded?.Invoke();
    }

    // Stops video and input for the current grant without ending the participant's turn.
    private void ResetControlState()
    {
        controlling = false;
        videoReady = false;
        localVideoConnected = false;
        localVideoGrant = "";
        countdownEndsAt = 0;
        lastVideoAt = 0;
        lastSequence = -1;
        if (remoteInputSource != null) remoteInputSource.ClearInput();
        if (inputRouter != null) inputRouter.UseLocalInput();
        if (authority != null) authority.grantId = "";
    }

    // Called just before loading the next stage. A remote controller keeps their turn:
    // the server pauses the grant and the next stage asks for a fresh video + countdown.
    public bool BeginStageCarry()
    {
        if (!controlling || authority == null || string.IsNullOrEmpty(authority.grantId)) return false;
        if (!Queue(new Message {
                type = "stage_transition", controllerId = authority.controllerId,
                requestId = authority.requestId, grantId = authority.grantId }))
            return false;
        ResetControlState();
        carrying = true;
        resumePending = false;
        return true;
    }

    // The run is over (all clear): return control to the host; the request list stays.
    public void EndRunControl() => ReleaseControl("run_complete");

    // Room-wide game events for the web page (failures, clears, final result).
    public void ReportRunEvent(string kind, string text, string[] lines = null)
    {
        if (!IsConnected) return;
        Queue(new Message { type = "run_event", kind = kind, text = text ?? "",
            lines = lines ?? Array.Empty<string>() });
    }

    private void OnEnable()
    {
        // Before reading credentials or starting a network worker.
        if (useGameSessionMode && GameSession.Mode == GameMode.SinglePlayer)
        {
            enabled = false;
            return;
        }

        // Mode-aware scene components are configuration references, not extra sockets.
        // Multiplayer networking is owned by the persistent lobby session.
        if (useGameSessionMode && !managedLobbySession &&
            GameSession.Mode == GameMode.Multiplayer)
        {
            enabled = false;
            return;
        }

        if (!managedLobbySession && (remoteInputSource == null || inputRouter == null))
        {
            Debug.LogError("RemoteInputGateway: assign RemoteInputSource and InputRouter.", this);
            enabled = false;
            return;
        }
        helloReceived = false;
        inviteUrl = "";
        RoomId = "";
        guestStatusSupported = false;
        guestConnected = false;
        participantCount = 0;
        ConnectionIssue = "";
        ClearPendingRequest();
        ClearAuthority();
        if (!TryReadHostConfig(out HostConfig config))
        {
            ConnectionIssue = "HOST CONFIG ERROR";
            enabled = false;
            return;
        }
        var current = new Session();
        session = current;
        serverUrl = config.serverUrl;
        string address = config.serverUrl;
        string hostKey = config.hostKey;
        connectionStartedAt = ClockNow();
        Task previousShutdown = pendingShutdown;
        current.worker = Task.Run(async () => {
            try
            {
                // Avoid a new local socket while a previous worker is still closing.
                await previousShutdown.ConfigureAwait(false);
                current.cancellation.Token.ThrowIfCancellationRequested();
                await RunSessionAsync(current, address, hostKey).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { current.error = error.Message; }
            finally { current.finished = true; }
        });
    }

    private static async Task RunSessionAsync(Session current, string address, string hostKey)
    {
        try
        {
            using (var socket = new ClientWebSocket())
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(current.cancellation.Token))
            {
                socket.Options.SetRequestHeader("X-NaWaBa-Host-Key", hostKey);
                socket.Options.SetRequestHeader("X-NaWaBa-Protocol", "8");
                socket.Options.SetRequestHeader("X-NaWaBa-Guest-Status", "1");
                linked.CancelAfter(TimeSpan.FromSeconds(120));
                await socket.ConnectAsync(new Uri(address), linked.Token).ConfigureAwait(false);
                linked.CancelAfter(Timeout.Infinite);
                current.connected = true;
                Task receive = ReceiveAsync(socket, current, linked.Token);
                Task send = SendAsync(socket, current, linked.Token);
                await Task.WhenAny(receive, send).ConfigureAwait(false);
                linked.Cancel();
                socket.Abort();
                try
                {
                    await Task.WhenAll(receive, send).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (current.cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (string.IsNullOrEmpty(current.error)) current.error = error.Message;
        }
        finally
        {
            current.connected = false;
            current.finished = true;
        }
    }

    private static async Task ReceiveAsync(ClientWebSocket socket, Session current, CancellationToken token)
    {
        byte[] buffer = new byte[65536];
        while (!token.IsCancellationRequested)
        {
            int length = 0;
            WebSocketReceiveResult result;
            do
            {
                if (length >= buffer.Length) throw new InvalidOperationException("Message too large.");
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer, length, buffer.Length - length), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    current.error = result.CloseStatusDescription;
                    return;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidOperationException("Text messages only.");
                length += result.Count;
            } while (!result.EndOfMessage);
            if (current.incoming.Count >= MaxPendingMessages)
                throw new InvalidOperationException("Incoming queue overflow.");
            current.incoming.Enqueue(new Packet {
                json = Encoding.UTF8.GetString(buffer, 0, length), receivedAt = ClockNow()
            });
        }
    }

    // Exactly one sender per ClientWebSocket; Unity's main thread only enqueues JSON.
    private static async Task SendAsync(ClientWebSocket socket, Session current, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await current.sendSignal.WaitAsync(token).ConfigureAwait(false);
            if (!current.outgoing.TryDequeue(out string json)) continue;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text,
                true, token).ConfigureAwait(false);
        }
    }

    private bool Queue(Message message)
    {
        var current = session;
        if (!IsConnected || current == null) return false;
        if (current.outgoing.Count >= MaxPendingMessages)
        {
            Debug.LogError("Outgoing queue overflow; returning to local control.", this);
            enabled = false;
            return false;
        }
        message.roomId = RoomId;
        current.outgoing.Enqueue(JsonUtility.ToJson(message));
        current.sendSignal.Release();
        return true;
    }

    public void ApprovePendingRequest()
    {
        if (!CanApprovePendingRequest) return;
        // Approving while someone else plays: the server ends their grant ("replaced").
        // Locally we skip RemoteControlEnded so the stage stays paused for the newcomer.
        if (authority != null) ResetControlState();
        authority = new Message {
            controllerId = PendingControllerId, requestId = PendingRequestId,
            nickname = PendingNickname
        };
        var approval = new Message {
            type = "approve_control", controllerId = authority.controllerId,
            requestId = authority.requestId
        };
        PendingControllerId = "";
        PendingRequestId = "";
        PendingNickname = "";
        if (!Queue(approval))
        {
            ClearAuthority();
            return;
        }
        RemoteControlPreparing?.Invoke();
    }

    public void ReclaimControl() => ReleaseControl("host_reclaimed");

    // Called by the stage only after a remote round reaches its terminal state.
    public void CompleteRemoteRound(bool cleared)
    {
        if (!controlling || string.IsNullOrEmpty(CurrentGrantId))
        {
            ReclaimControl();
            return;
        }
        ReleaseControl(cleared ? "round_cleared" : "round_failed");
    }

    private void ReleaseControl(string reason)
    {
        Message previous = authority;
        ClearAuthority(); // Immediate local recovery, even if the network is stalled.
        if (previous != null)
        {
            Queue(new Message {
                type = "revoke_control", controllerId = previous.controllerId,
                requestId = previous.requestId, grantId = previous.grantId, reason = reason
            });
        }
    }

    private static bool SameRequest(Message a, Message b) => a != null && b != null &&
        a.controllerId == b.controllerId && a.requestId == b.requestId;

    private bool CurrentGrant(Message message) => SameRequest(message, authority) &&
        !string.IsNullOrEmpty(authority.grantId) && message.grantId == authority.grantId;

    private void Update()
    {
        var current = session;
        if (current == null) return;
        if (current.finished)
        {
            ConnectionIssue = "DISCONNECTED";
            Debug.LogWarning("NaWaBa disconnected. " + current.error +
                " Use RECONNECT in the multiplayer lobby, or restart Play.", this);
            enabled = false;
            return;
        }
        if (!helloReceived && ClockNow() - connectionStartedAt >= 120)
        {
            ConnectionIssue = "CONNECTION TIMEOUT";
            enabled = false;
            return;
        }
        if (carrying && resumePending && helloReceived && authority != null &&
            videoSender != null && videoSender.isActiveAndEnabled &&
            inputRouter != null && inputRouter.isActiveAndEnabled)
        {
            resumePending = false;
            carrying = false; // From here it is an ordinary approval waiting for its grant.
            if (Queue(new Message {
                    type = "resume_control", controllerId = authority.controllerId,
                    requestId = authority.requestId }))
                RemoteControlPreparing?.Invoke();
            else
                ClearAuthority();
        }
        Message latest = null;
        double receivedAt = 0;
        try
        {
            for (int i = 0; i < MaxPendingMessages && current.incoming.TryDequeue(out Packet packet); i++)
            {
                bool stale = ClockNow() - packet.receivedAt >= InputTimeout;
                Message message = JsonUtility.FromJson<Message>(packet.json);
                if (message == null || message.protocolVersion != ProtocolVersion)
                    throw new InvalidOperationException("Protocol mismatch. Update all three parts.");
                if (message.type == "host_hello")
                {
                    // Only accept a same-server participant URL. Never construct it from the host key.
                    if (helloReceived || !IsValidInviteUrl(message.inviteUrl, message.roomId))
                        throw new InvalidOperationException("Invalid participant URL from server.");
                    RoomId = message.roomId;
                    inviteUrl = message.inviteUrl;
                    IceServers = message.iceServers != null && message.iceServers.Length > 0
                        ? message.iceServers : null;
                    guestStatusSupported = message.guestStatusSupported;
                    guestConnected = guestStatusSupported && message.guestConnected;
                    participantCount = guestConnected ? Mathf.Max(0, message.participantCount) : 0;
                    helloReceived = true;
                    Debug.Log("NaWaBa connected (protocol 8), room: " + RoomId, this);
                    SendStageStatus();
                    continue;
                }
                if (!helloReceived) throw new InvalidOperationException("Missing server hello.");
                if (message.roomId != RoomId)
                    throw new InvalidOperationException("Server room mismatch.");
                if (message.type == "controller_status")
                {
                    if (!guestStatusSupported)
                        throw new InvalidOperationException("Unexpected guest status message.");
                    // This is a connection snapshot, not movement input: scene-loading age is irrelevant.
                    if (message.participantCount < 0 || message.participantCount > 1000)
                        throw new InvalidOperationException("Invalid participant count.");
                    guestConnected = message.guestConnected && message.participantCount > 0;
                    participantCount = guestConnected ? message.participantCount : 0;
                    // The server already revokes a request/grant whose owner left; this only
                    // covers the case where nobody is left in the room.
                    if (!guestConnected)
                    {
                        ClearPendingRequest();
                        ClearAuthority();
                    }
                    continue;
                }
                if (message.type == "queue_status")
                {
                    // A full snapshot of the waiting list; the newest one always wins, so a
                    // scene-loading delay cannot apply an outdated front entry.
                    QueueEntry[] entries = message.queue ?? Array.Empty<QueueEntry>();
                    if (entries.Length > MaxQueueEntries)
                        throw new InvalidOperationException("Invalid request list.");
                    var names = new string[entries.Length];
                    for (int e = 0; e < entries.Length; e++)
                    {
                        QueueEntry entry = entries[e];
                        if (entry == null || string.IsNullOrEmpty(entry.controllerId) ||
                            string.IsNullOrEmpty(entry.requestId) ||
                            string.IsNullOrWhiteSpace(entry.nickname) || entry.nickname.Length > 20)
                            throw new InvalidOperationException("Invalid request list entry.");
                        names[e] = entry.nickname;
                    }
                    queueNames = names;
                    PendingControllerId = entries.Length > 0 ? entries[0].controllerId : "";
                    PendingRequestId = entries.Length > 0 ? entries[0].requestId : "";
                    PendingNickname = entries.Length > 0 ? entries[0].nickname : "";
                    continue;
                }
                if (message.type == "request_rejected")
                {
                    if (message.requestId == PendingRequestId) ClearPendingRequest();
                    if (SameRequest(message, authority)) ClearAuthority();
                    continue;
                }
                if (message.type == "control_granted")
                {
                    if (!SameRequest(message, authority)) continue;
                    if (stale) { ReclaimControl(); continue; }
                    if (string.IsNullOrEmpty(message.grantId))
                        throw new InvalidOperationException("Missing grant ID.");
                    authority.grantId = message.grantId;
                    RemoteVideoRequested?.Invoke(message.grantId);
                    continue;
                }
                if (message.type == "video_answer")
                {
                    if (!CurrentGrant(message)) continue;
                    if (string.IsNullOrEmpty(message.sdp) || Encoding.UTF8.GetByteCount(message.sdp) > 60000)
                        throw new InvalidOperationException("Invalid video answer.");
                    RemoteVideoAnswer?.Invoke(message.grantId, message.sdp);
                    continue;
                }
                if (message.type == "countdown_started")
                {
                    if (!CurrentGrant(message)) continue;
                    if (stale || message.countdownMs < 0 || message.countdownMs > 10000)
                    { ReclaimControl(); continue; }
                    videoReady = true;
                    lastVideoAt = packet.receivedAt;
                    countdownEndsAt = packet.receivedAt + message.countdownMs / 1000.0;
                    continue;
                }
                if (message.type == "video_alive")
                {
                    if (CurrentGrant(message) && videoReady) lastVideoAt = packet.receivedAt;
                    continue;
                }
                if (message.type == "control_started")
                {
                    if (!CurrentGrant(message) || controlling) continue;
                    if (stale) { ReclaimControl(); continue; }
                    if (!videoReady || !localVideoConnected || localVideoGrant != CurrentGrantId ||
                        ClockNow() - lastVideoAt >= 1.5 ||
                        inputRouter == null || remoteInputSource == null ||
                        !inputRouter.isActiveAndEnabled || !remoteInputSource.isActiveAndEnabled)
                    {
                        ReclaimControl();
                        continue;
                    }
                    remoteInputSource.ClearInput();
                    lastSequence = -1;
                    lastInputAt = packet.receivedAt;
                    controlling = true;
                    inputRouter.UseRemoteInput();
                    if (!inputRouter.IsRemoteControl)
                    {
                        ReclaimControl();
                        continue;
                    }
                    RemoteControlStarted?.Invoke();
                    continue;
                }
                if (message.type == "control_revoked")
                {
                    if (SameRequest(message, authority)) ClearAuthority();
                    continue;
                }
                if (message.type != "input") throw new InvalidOperationException("Unknown server message.");
                if (!controlling || !CurrentGrant(message)) continue;
                if (stale) { ReleaseControl("input_timeout"); continue; }
                if (message.sequence < 0 || float.IsNaN(message.x) || float.IsNaN(message.y) ||
                    float.IsInfinity(message.x) || float.IsInfinity(message.y) ||
                    Mathf.Abs(message.x) > 1 || Mathf.Abs(message.y) > 1 ||
                    message.jump < 0 || float.IsNaN(message.look) || float.IsInfinity(message.look) ||
                    Mathf.Abs(message.look) > 1 || float.IsNaN(message.lookY) ||
                    float.IsInfinity(message.lookY) || Mathf.Abs(message.lookY) > 1)
                    throw new InvalidOperationException("Invalid input.");
                if (message.sequence <= lastSequence) continue;
                lastSequence = message.sequence;
                lastInputAt = packet.receivedAt;
                latest = message;
                receivedAt = packet.receivedAt;
            }
            if (authority != null && videoReady && ClockNow() - lastVideoAt >= 1.5)
                ReleaseControl("video_timeout");
            if (controlling && ClockNow() - lastInputAt >= InputTimeout)
                ReleaseControl("input_timeout");
            if (controlling && (inputRouter == null || remoteInputSource == null ||
                !inputRouter.isActiveAndEnabled || !inputRouter.IsRemoteControl ||
                !remoteInputSource.isActiveAndEnabled))
                ReclaimControl();
            if (latest != null && controlling && CurrentGrant(latest))
                remoteInputSource.SetInput(new Vector2(latest.x, latest.y), latest.look, latest.lookY, latest.jump,
                    ClockNow() - receivedAt);
        }
        catch (Exception error)
        {
            ConnectionIssue = "CONNECTION ERROR";
            Debug.LogError("NaWaBa: " + error.Message, this);
            enabled = false;
        }
    }

    private void OnDisable()
    {
        helloReceived = false;
        inviteUrl = "";
        RoomId = "";
        guestStatusSupported = false;
        guestConnected = false;
        participantCount = 0;
        ClearPendingRequest();
        ClearAuthority();
        var previous = session;
        session = null;
        if (previous == null) return;
        previous.cancellation.Cancel();
        pendingShutdown = pendingShutdown.IsCompleted ? previous.worker :
            Task.WhenAll(pendingShutdown, previous.worker);
        _ = previous.worker.ContinueWith(_ => {
            previous.sendSignal.Dispose();
            previous.cancellation.Dispose();
        }, TaskScheduler.Default);
    }
}
