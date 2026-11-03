using System;
using System.Collections.Concurrent;
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

    private const int ProtocolVersion = 2;
    private const int MaxPendingMessages = 32;
    private const double InputTimeout = 0.5;
    private Session session;
    private bool helloReceived;
    private Message authority;
    private bool controlling;
    private long lastSequence = -1;
    private double lastInputAt;

    public bool IsConnected => session != null && session.connected &&
        !session.finished && helloReceived;
    public string PendingControllerId { get; private set; } = "";
    public string PendingRequestId { get; private set; } = "";
    public string PendingNickname { get; private set; } = "";
    public bool HasPendingRequest => IsConnected && authority == null && PendingRequestId.Length > 0;
    public bool HasAuthority => authority != null;
    public string PlayerName => authority != null ? authority.nickname : PendingNickname;
    public string ControlState => controlling ? "REMOTE" : authority != null ? "WAITING FOR WEB" : "LOCAL";

    [Serializable]
    private class Message
    {
        public int protocolVersion = ProtocolVersion;
        public string type;
        public string controllerId;
        public string requestId;
        public string grantId;
        public string nickname;
        public string reason;
        public long sequence = -1;
        public float x;
        public float y;
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
    }

    private void ClearAuthority()
    {
        authority = null;
        controlling = false;
        lastSequence = -1;
        if (remoteInputSource != null) remoteInputSource.ClearInput();
        if (inputRouter != null) inputRouter.UseLocalInput();
    }

    private void OnEnable()
    {
        if (remoteInputSource == null || inputRouter == null)
        {
            Debug.LogError("RemoteInputGateway: assign RemoteInputSource and InputRouter.", this);
            enabled = false;
            return;
        }
        helloReceived = false;
        ClearPendingRequest();
        ClearAuthority();
        var current = new Session();
        session = current;
        string address = serverUrl;
        current.worker = Task.Run(() => RunSessionAsync(current, address));
    }

    private static async Task RunSessionAsync(Session current, string address)
    {
        try
        {
            using (var socket = new ClientWebSocket())
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(current.cancellation.Token))
            {
                await socket.ConnectAsync(new Uri(address), linked.Token).ConfigureAwait(false);
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
            current.error = error.Message;
        }
        finally
        {
            current.connected = false;
            current.finished = true;
        }
    }

    private static async Task ReceiveAsync(ClientWebSocket socket, Session current, CancellationToken token)
    {
        byte[] buffer = new byte[2048];
        while (!token.IsCancellationRequested)
        {
            int length = 0;
            WebSocketReceiveResult result;
            do
            {
                if (length >= buffer.Length) throw new InvalidOperationException("Message too large.");
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer, length, buffer.Length - length), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
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
        current.outgoing.Enqueue(JsonUtility.ToJson(message));
        current.sendSignal.Release();
        return true;
    }

    public void ApprovePendingRequest()
    {
        if (!HasPendingRequest || !inputRouter.isActiveAndEnabled ||
            !remoteInputSource.isActiveAndEnabled) return;
        authority = new Message {
            controllerId = PendingControllerId, requestId = PendingRequestId,
            nickname = PendingNickname
        };
        var approval = new Message {
            type = "approve_control", controllerId = authority.controllerId,
            requestId = authority.requestId
        };
        ClearPendingRequest();
        if (!Queue(approval)) ClearAuthority();
    }

    public void ReclaimControl()
    {
        Message previous = authority;
        ClearAuthority(); // Immediate local recovery, even if the network is stalled.
        if (previous != null)
        {
            Queue(new Message {
                type = "revoke_control", controllerId = previous.controllerId,
                requestId = previous.requestId, grantId = previous.grantId
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
            Debug.LogWarning("NaWaBa disconnected. " + current.error + " Restart Play to reconnect.", this);
            enabled = false;
            return;
        }
        Message latest = null;
        double receivedAt = 0;
        try
        {
            for (int i = 0; i < MaxPendingMessages && current.incoming.TryDequeue(out Packet packet); i++)
            {
                if (ClockNow() - packet.receivedAt >= InputTimeout)
                    throw new InvalidOperationException("Stale network message.");
                Message message = JsonUtility.FromJson<Message>(packet.json);
                if (message == null || message.protocolVersion != ProtocolVersion)
                    throw new InvalidOperationException("Protocol mismatch. Update all three parts.");
                if (message.type == "host_hello")
                {
                    helloReceived = true;
                    Debug.Log("NaWaBa server connected (protocol 2).", this);
                    continue;
                }
                if (!helloReceived) throw new InvalidOperationException("Missing server hello.");
                if (message.type == "control_request")
                {
                    if (string.IsNullOrEmpty(message.controllerId) || string.IsNullOrEmpty(message.requestId) ||
                        string.IsNullOrWhiteSpace(message.nickname) || message.nickname.Length > 20)
                        throw new InvalidOperationException("Invalid control request.");
                    if (authority != null) continue;
                    PendingControllerId = message.controllerId;
                    PendingRequestId = message.requestId;
                    PendingNickname = message.nickname;
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
                    if (string.IsNullOrEmpty(message.grantId))
                        throw new InvalidOperationException("Missing grant ID.");
                    authority.grantId = message.grantId;
                    continue;
                }
                if (message.type == "control_started")
                {
                    if (!CurrentGrant(message) || controlling) continue;
                    if (!inputRouter.isActiveAndEnabled || !remoteInputSource.isActiveAndEnabled)
                    {
                        ReclaimControl();
                        continue;
                    }
                    remoteInputSource.ClearInput();
                    lastSequence = -1;
                    lastInputAt = packet.receivedAt;
                    controlling = true;
                    inputRouter.UseRemoteInput();
                    continue;
                }
                if (message.type == "control_revoked")
                {
                    if (SameRequest(message, authority)) ClearAuthority();
                    continue;
                }
                if (message.type != "input") throw new InvalidOperationException("Unknown server message.");
                if (!controlling || !CurrentGrant(message)) continue;
                if (message.sequence < 0 || float.IsNaN(message.x) || float.IsNaN(message.y) ||
                    float.IsInfinity(message.x) || float.IsInfinity(message.y) ||
                    Mathf.Abs(message.x) > 1 || Mathf.Abs(message.y) > 1)
                    throw new InvalidOperationException("Invalid input.");
                if (message.sequence <= lastSequence) continue;
                lastSequence = message.sequence;
                lastInputAt = packet.receivedAt;
                latest = message;
                receivedAt = packet.receivedAt;
            }
            if (controlling && (!inputRouter.isActiveAndEnabled || !inputRouter.IsRemoteControl ||
                !remoteInputSource.isActiveAndEnabled || ClockNow() - lastInputAt >= InputTimeout))
                ReclaimControl();
            if (latest != null && controlling && CurrentGrant(latest))
                remoteInputSource.SetMoveInput(new Vector2(latest.x, latest.y), ClockNow() - receivedAt);
        }
        catch (Exception error)
        {
            Debug.LogError("NaWaBa: " + error.Message, this);
            enabled = false;
        }
    }

    private void OnDisable()
    {
        helloReceived = false;
        ClearPendingRequest();
        ClearAuthority();
        var previous = session;
        session = null;
        if (previous == null) return;
        previous.cancellation.Cancel();
        _ = previous.worker.ContinueWith(_ => {
            previous.sendSignal.Dispose();
            previous.cancellation.Dispose();
        }, TaskScheduler.Default);
    }
}
