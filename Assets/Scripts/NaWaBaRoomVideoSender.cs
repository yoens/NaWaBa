using System;
using System.Collections;
using Unity.WebRTC;
using UnityEngine;

// One component in the gameplay scene. The persistent gateway owns the room/socket.
// No native capture or WebRTC work in OnEnable; approval schedules work for Update.
public sealed class NaWaBaRoomVideoSender : MonoBehaviour
{
    [SerializeField] private RemoteInputGateway gateway;
    private const int Width = 1280;
    private const int Height = 720;
    private RTCPeerConnection peer;
    private RTCRtpSender sender;
    private VideoStreamTrack track;
    private MediaStream stream;
    private RenderTexture screenTexture;
    private RenderTexture videoTexture;
    private string grant = "";
    private string pendingGrant = "";
    private string answer;
    private string failure;
    private bool stopRequested;
    private bool registered;
    private bool firstCapture;
    private bool savedBackground;
    private bool previousBackground;
    private double startedAt;
    public string Status { get; private set; } = "IDLE";

    private void OnEnable()
    {
        if (gateway != null && gateway.UsesGameSessionMode && GameSession.Mode == GameMode.SinglePlayer)
            return;
        gateway = RemoteInputGateway.ResolveForScene(gateway);
        if (gateway == null || !gateway.RegisterVideoSender(this))
        {
            Debug.LogError("NaWaBaRoomVideoSender: assign the stage RemoteInputGateway; use one sender per stage.", this);
            enabled = false;
            return;
        }
        registered = true;
        gateway.RemoteVideoRequested += OnVideoRequested;
        gateway.RemoteVideoAnswer += OnVideoAnswer;
        gateway.RemoteControlEnded += OnControlEnded;
    }

    private void OnVideoRequested(string id) { pendingGrant = id; }
    private void OnVideoAnswer(string id, string sdp)
    {
        if (id == grant && answer == null) answer = sdp;
    }
    private void OnControlEnded() { pendingGrant = ""; stopRequested = true; }

    private void Update()
    {
        if (!registered) return;
        if (failure != null)
        {
            string reason = failure;
            string failedGrant = grant;
            Cleanup();
            Debug.LogWarning("[NaWaBa Room Video] " + reason, this);
            gateway.ReportVideoFailure(failedGrant);
            Status = "VIDEO FAILED - request again";
        }
        if (stopRequested || (!string.IsNullOrEmpty(grant) && gateway.CurrentGrantId != grant))
        {
            Cleanup();
            Status = "IDLE";
        }
        if (!string.IsNullOrEmpty(pendingGrant))
        {
            string next = pendingGrant;
            pendingGrant = "";
            Cleanup();
            if (gateway.CurrentGrantId != next) return;
            grant = next;
            startedAt = Time.realtimeSinceStartupAsDouble;
            previousBackground = Application.runInBackground;
            savedBackground = true;
            Application.runInBackground = true;
            StartCoroutine(Guarded(Negotiate()));
        }
        if (!string.IsNullOrEmpty(grant) && !gateway.IsControlling &&
            Time.realtimeSinceStartupAsDouble - startedAt > 65)
            failure = "Video setup timed out. " + candidateSummary;
        if (disconnectedAt > 0 && string.IsNullOrEmpty(failure) &&
            Time.realtimeSinceStartupAsDouble - disconnectedAt > 4)
        {
            disconnectedAt = 0;
            failure = "Video connection Disconnected for 4 s. " + candidateSummary;
        }
    }

    // Fail closed on C# exceptions as well as WebRTC operation errors.
    private IEnumerator Guarded(IEnumerator routine)
    {
        while (true)
        {
            bool more = false;
            object current = null;
            try { more = routine.MoveNext(); if (more) current = routine.Current; }
            catch (Exception error) { failure = error.Message; }
            if (!more) yield break;
            yield return current;
        }
    }

    private IEnumerator Negotiate()
    {
        Status = "CAPTURING GAME";
        Debug.Log("[NaWaBa Room Video] 1 CAPTURE", this);
        // Capture the rendered Game view, including Screen Space Overlay UI.
        // Desktop windows/Editor panels are not captured. No camera targetTexture changes.
        var format = WebRTC.GetSupportedRenderTextureFormat(SystemInfo.graphicsDeviceType);
        videoTexture = new RenderTexture(Width, Height, 0, format) { name = "NaWaBa Video 720p" };
        if (!videoTexture.Create()) throw new InvalidOperationException("Could not create video texture.");
        StartCoroutine(Guarded(CaptureFrames()));
        while (!firstCapture) yield return null;
        track = new VideoStreamTrack(videoTexture, Graphics.Blit);
        stream = new MediaStream();
        stream.AddTrack(track);
        yield return null;
        StartCoroutine(Guarded(WebRTC.Update()));
        // Keep initialization split across frames, as in the successful video lab.
        for (int i = 0; i < 3; i++) yield return null;

        Debug.Log("[NaWaBa Room Video] 2 PEER", this);
        var configuration = new RTCConfiguration { iceServers = BuildIceServers() };
        LogHostVideoInfo();
        peer = new RTCPeerConnection(ref configuration);
        var currentPeer = peer;
        string currentGrant = grant;
        peer.OnConnectionStateChange = state => {
            if (peer != currentPeer || grant != currentGrant) return;
            Status = "VIDEO: " + state;
            gateway.SetLocalVideoConnected(grant, state == RTCPeerConnectionState.Connected);
            if (state == RTCPeerConnectionState.Failed || state == RTCPeerConnectionState.Closed)
                failure = "Video connection " + state + " " + candidateSummary;
            // A short drop often recovers by itself; Update() fails it only after 4 seconds.
            disconnectedAt = state == RTCPeerConnectionState.Disconnected ? Time.realtimeSinceStartupAsDouble : 0;
        };
        sender = peer.AddTrack(track, stream);
        foreach (var transceiver in peer.GetTransceivers())
            transceiver.Direction = RTCRtpTransceiverDirection.SendOnly;
        var offer = peer.CreateOffer();
        yield return offer;
        if (offer.IsError) throw new InvalidOperationException(offer.Error.message);
        var description = offer.Desc;
        var local = peer.SetLocalDescription(ref description);
        yield return local;
        if (local.IsError) throw new InvalidOperationException(local.Error.message);
        // A blocked STUN server or extra network adapters (VPN, virtual machines) can make
        // gathering take 10 s or more: after 4 s send the offer with what was found so far.
        double gatherStart = Time.realtimeSinceStartupAsDouble;
        while (peer.GatheringState != RTCIceGatheringState.Complete)
        {
            double waited = Time.realtimeSinceStartupAsDouble - gatherStart;
            bool found = peer.LocalDescription.sdp != null && peer.LocalDescription.sdp.Contains("a=candidate");
            if (waited >= 4 && found) break;
            if (waited >= 15) throw new InvalidOperationException("ICE gathering found no candidates (network blocked?).");
            yield return null;
        }
        candidateSummary = SummarizeCandidates(peer.LocalDescription.sdp,
            Time.realtimeSinceStartupAsDouble - gatherStart, peer.GatheringState == RTCIceGatheringState.Complete);
        Debug.Log("[NaWaBa Room Video] " + candidateSummary, this);
        if (!gateway.SendVideoOffer(grant, peer.LocalDescription.sdp))
            throw new InvalidOperationException("Could not send room video offer.");
        Debug.Log("[NaWaBa Room Video] 3 OFFER SENT", this);
        Status = "WAITING FOR BROWSER VIDEO";
        while (answer == null) yield return null;
        description = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = answer };
        var remote = peer.SetRemoteDescription(ref description);
        yield return remote;
        if (remote.IsError) throw new InvalidOperationException(remote.Error.message);
        var parameters = sender.GetParameters();
        if (parameters.encodings == null || parameters.encodings.Length == 0)
            throw new InvalidOperationException("No video encoding parameters.");
        foreach (var encoding in parameters.encodings)
        {
            encoding.maxFramerate = 30;
            encoding.maxBitrate = 3000000;
            encoding.scaleResolutionDownBy = 1;
        }
        var result = sender.SetParameters(parameters);
        if (result.errorType != RTCErrorType.None)
            throw new InvalidOperationException("Video settings failed: " + result.errorType);
        gateway.SetLocalVideoConnected(grant, peer.ConnectionState == RTCPeerConnectionState.Connected);
        Debug.Log("[NaWaBa Room Video] 4 ANSWER APPLIED - 720p / max 30fps", this);
    }

    private string candidateSummary = "";
    private double disconnectedAt;

    // "ICE candidates host 2 / srflx 1 / relay 0 (3.1 s)". srflx 0 = this PC could not reach
    // any STUN server (UDP blocked by a VPN, security software or the network).
    private static string SummarizeCandidates(string sdp, double seconds, bool complete)
    {
        int host = Count(sdp, " typ host"), srflx = Count(sdp, " typ srflx"), relay = Count(sdp, " typ relay");
        string text = $"ICE candidates host {host} / srflx {srflx} / relay {relay} " +
            $"({seconds:0.0} s{(complete ? "" : ", gathering cut short")})";
        if (srflx == 0 && relay == 0)
            text += " - NO STUN RESULT: UDP looks blocked on this PC (VPN / firewall / security software). " +
                "Only players on the same local network can connect.";
        return text;
    }

    private static int Count(string text, string part)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // Same STUN/TURN list as the browsers (from the server); Google STUN when none was sent.
    private RTCIceServer[] BuildIceServers()
    {
        var list = new System.Collections.Generic.List<RTCIceServer>();
        RemoteInputGateway.IceServer[] fromServer = gateway != null ? gateway.IceServers : null;
        if (fromServer != null)
        {
            foreach (RemoteInputGateway.IceServer server in fromServer)
            {
                if (server == null || server.urls == null || server.urls.Length == 0) continue;
                var ice = new RTCIceServer { urls = server.urls };
                if (!string.IsNullOrEmpty(server.username))
                {
                    ice.username = server.username;
                    ice.credential = server.credential;
                    ice.credentialType = RTCIceCredentialType.Password;
                }
                list.Add(ice);
            }
        }
        if (list.Count == 0) list.Add(new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } });
        return list.ToArray();
    }

    private static bool loggedVideoInfo;

    // Step 7 check on another PC: what this machine uses to capture and encode the video.
    private void LogHostVideoInfo()
    {
        if (loggedVideoInfo) return;
        loggedVideoInfo = true;
        bool relay = false;
        if (gateway != null && gateway.IceServers != null)
            foreach (RemoteInputGateway.IceServer server in gateway.IceServers)
                if (server?.urls != null)
                    foreach (string url in server.urls)
                        if (url != null && url.StartsWith("turn")) relay = true;
        Debug.Log("[NaWaBa Room Video] HOST INFO\n" +
            $"GPU: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceVendor}, {SystemInfo.graphicsMemorySize} MB)\n" +
            $"Graphics API: {SystemInfo.graphicsDeviceType} {SystemInfo.graphicsDeviceVersion}\n" +
            $"Video texture: {(videoTexture != null ? videoTexture.format.ToString() : "?")} {Width}x{Height}\n" +
            $"OS: {SystemInfo.operatingSystem} | CPU: {SystemInfo.processorType}\n" +
            $"ICE: {(relay ? "STUN + TURN relay" : "STUN only")}", this);
    }

    private IEnumerator CaptureFrames()
    {
        var endOfFrame = new WaitForEndOfFrame();
        while (true)
        {
            yield return endOfFrame;
            int width = Mathf.Max(2, Screen.width);
            int height = Mathf.Max(2, Screen.height);
            if (screenTexture == null || screenTexture.width != width || screenTexture.height != height)
            {
                if (screenTexture != null) Destroy(screenTexture);
                screenTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32) {
                    name = "NaWaBa Game Capture"
                };
                if (!screenTexture.Create()) throw new InvalidOperationException("Could not create screen texture.");
            }
            ScreenCapture.CaptureScreenshotIntoRenderTexture(screenTexture);
            // Preserve aspect ratio with black bars when the host resizes the Game view.
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = videoTexture;
            GL.PushMatrix();
            try
            {
                GL.Clear(true, true, Color.black);
                GL.LoadPixelMatrix(0, Width, Height, 0);
                float scale = Mathf.Min((float)Width / width, (float)Height / height);
                float w = width * scale;
                float h = height * scale;
                Graphics.DrawTexture(new Rect((Width - w) * 0.5f, (Height - h) * 0.5f, w, h), screenTexture);
            }
            finally { GL.PopMatrix(); RenderTexture.active = previous; }
            firstCapture = true;
        }
    }

    private void Cleanup()
    {
        StopAllCoroutines();
        disconnectedAt = 0;
        candidateSummary = "";
        if (peer != null)
        {
            peer.OnConnectionStateChange = null;
            peer.Close();
            peer.Dispose();
            peer = null;
        }
        sender = null;
        if (stream != null && track != null) stream.RemoveTrack(track);
        track?.Dispose();
        track = null;
        stream?.Dispose();
        stream = null;
        if (screenTexture != null) Destroy(screenTexture);
        if (videoTexture != null) Destroy(videoTexture);
        screenTexture = null;
        videoTexture = null;
        if (savedBackground) Application.runInBackground = previousBackground;
        savedBackground = false;
        grant = "";
        answer = null;
        failure = null;
        stopRequested = false;
        firstCapture = false;
    }

    private void OnDisable()
    {
        if (registered && gateway != null)
        {
            gateway.RemoteVideoRequested -= OnVideoRequested;
            gateway.RemoteVideoAnswer -= OnVideoAnswer;
            gateway.RemoteControlEnded -= OnControlEnded;
            gateway.UnregisterVideoSender(this);
        }
        registered = false;
        pendingGrant = "";
        Cleanup();
    }
}
