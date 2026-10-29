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
    [SerializeField] private string serverUrl =
        "ws://127.0.0.1:3000/host";

    [SerializeField] private RemoteInputSource remoteInputSource;
    [SerializeField] private InputRouter inputRouter;

    private const int MaxPendingMessages = 32;
    private const double MaxMessageAge = 0.5;

    private Session session;
    private long lastSequence = -1;
    private bool connectionReported;

    [Serializable]
    private class InputMessage
    {
        public int protocolVersion;
        public string type;
        public long sequence = -1;
        public float x = float.NaN;
        public float y = float.NaN;
    }

    private class ReceivedPacket
    {
        public string json;
        public double receivedAt;
    }

    // 연결마다 별도 상태를 사용합니다.
    private class Session
    {
        public readonly CancellationTokenSource cancellation =
            new CancellationTokenSource();

        public readonly ConcurrentQueue<ReceivedPacket> messages =
            new ConcurrentQueue<ReceivedPacket>();

        public Task worker;
        public volatile bool connected;
        public volatile bool finished;
        public string error;
    }

    private static double ClockNow()
    {
        return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    }

    private void OnEnable()
    {
        if (remoteInputSource == null || inputRouter == null)
        {
            Debug.LogError(
                "Gateway에 RemoteInputSource와 InputRouter를 연결해주세요.",
                this);

            enabled = false;
            return;
        }

        inputRouter.UseLocalInput();
        lastSequence = -1;
        connectionReported = false;

        Session current = new Session();
        session = current;

        // 백그라운드 작업에서 MonoBehaviour의 필드를 읽지 않도록 복사합니다.
        string address = serverUrl;
        CancellationToken token = current.cancellation.Token;

        current.worker = Task.Run(
            () => ReceiveLoopAsync(current, address, token));
    }

    // 이 메서드는 Unity 오브젝트를 직접 변경하지 않습니다.
    private static async Task ReceiveLoopAsync(
        Session current,
        string address,
        CancellationToken token)
    {
        try
        {
            using (ClientWebSocket socket = new ClientWebSocket())
            {
                await socket.ConnectAsync(new Uri(address), token)
                    .ConfigureAwait(false);

                current.connected = true;
                byte[] buffer = new byte[1024];

                while (!token.IsCancellationRequested &&
                       socket.State == WebSocketState.Open)
                {
                    int length = 0;
                    WebSocketReceiveResult result;

                    // 하나의 메시지가 여러 조각으로 도착할 수 있습니다.
                    do
                    {
                        if (length >= buffer.Length)
                            throw new InvalidOperationException(
                                "수신 메시지가 너무 큽니다.");

                        result = await socket.ReceiveAsync(
                            new ArraySegment<byte>(
                                buffer, length, buffer.Length - length),
                            token).ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close)
                            return;

                        if (result.MessageType != WebSocketMessageType.Text)
                            throw new InvalidOperationException(
                                "텍스트 메시지만 받을 수 있습니다.");

                        length += result.Count;
                    }
                    while (!result.EndOfMessage);

                    if (current.messages.Count >= MaxPendingMessages)
                        throw new InvalidOperationException(
                            "입력 수신 대기열이 초과됐습니다.");

                    current.messages.Enqueue(new ReceivedPacket
                    {
                        json = Encoding.UTF8.GetString(buffer, 0, length),
                        receivedAt = ClockNow()
                    });
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Play 종료나 컴포넌트 비활성화에 따른 정상 취소입니다.
        }
        catch (Exception exception)
        {
            current.error = exception.Message;
        }
        finally
        {
            current.finished = true;
        }
    }

    private void Update()
    {
        Session current = session;

        if (current == null)
            return;

        if (current.finished)
        {
            string reason = current.error ?? "서버 연결이 종료됐습니다.";
            Debug.LogWarning(reason + " 로컬 모드로 복귀합니다.", this);
            enabled = false;
            return;
        }

        if (current.connected && !connectionReported)
        {
            connectionReported = true;
            Debug.Log("WebSocket 연결 완료: " + serverUrl, this);
        }

        InputMessage latestInput = null;
        double latestReceivedAt = 0;

        try
        {
            for (int i = 0;
                 i < MaxPendingMessages &&
                 current.messages.TryDequeue(out ReceivedPacket packet);
                 i++)
            {
                if (ClockNow() - packet.receivedAt >= MaxMessageAge)
                    throw new InvalidOperationException(
                        "처리가 지연된 입력을 감지했습니다.");

                InputMessage message =
                    JsonUtility.FromJson<InputMessage>(packet.json);

                if (message == null || message.protocolVersion != 1)
                    throw new InvalidOperationException(
                        "지원하지 않는 메시지입니다.");

                if (message.type == "reset")
                {
                    remoteInputSource.ClearInput();
                    lastSequence = -1;
                    latestInput = null;
                    continue;
                }

                if (message.type != "input" ||
                    message.sequence < 0 ||
                    float.IsNaN(message.x) || float.IsNaN(message.y) ||
                    float.IsInfinity(message.x) ||
                    float.IsInfinity(message.y) ||
                    Mathf.Abs(message.x) > 1f ||
                    Mathf.Abs(message.y) > 1f)
                {
                    throw new InvalidOperationException(
                        "입력 메시지 형식이 올바르지 않습니다.");
                }

                if (message.sequence <= lastSequence)
                    continue;

                lastSequence = message.sequence;

                // 여러 입력이 도착했다면 마지막 상태만 이동에 사용합니다.
                latestInput = message;
                latestReceivedAt = packet.receivedAt;
            }

            if (latestInput != null &&
                inputRouter.isActiveAndEnabled &&
                inputRouter.IsRemoteControl)
            {
                remoteInputSource.SetMoveInput(
                    new Vector2(latestInput.x, latestInput.y),
                    ClockNow() - latestReceivedAt);
            }
        }
        catch (Exception exception)
        {
            Debug.LogError("원격 입력 오류: " + exception.Message, this);
            enabled = false;
        }
    }

    private void OnDisable()
    {
        Session previous = session;
        session = null;

        if (remoteInputSource != null)
            remoteInputSource.ClearInput();

        if (inputRouter != null)
            inputRouter.UseLocalInput();

        if (previous == null)
            return;

        previous.cancellation.Cancel();

        // 수신 작업이 끝난 뒤 취소 자원을 정리합니다.
        _ = previous.worker.ContinueWith(
            _ => previous.cancellation.Dispose(),
            TaskScheduler.Default);
    }
}