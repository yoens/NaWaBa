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
            failure = "Video setup timed out.";
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
        track = new VideoStreamTrack(videoTexture);
        stream = new MediaStream();
        stream.AddTrack(track);
        yield return null;
        StartCoroutine(Guarded(WebRTC.Update()));
        // Keep initialization split across frames, as in the successful video lab.
        for (int i = 0; i < 3; i++) yield return null;

        Debug.Log("[NaWaBa Room Video] 2 PEER", this);
        var configuration = new RTCConfiguration {
            iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } }
        };
        peer = new RTCPeerConnection(ref configuration);
        var currentPeer = peer;
        string currentGrant = grant;
        peer.OnConnectionStateChange = state => {
            if (peer != currentPeer || grant != currentGrant) return;
            Status = "VIDEO: " + state;
            gateway.SetLocalVideoConnected(grant, state == RTCPeerConnectionState.Connected);
            if (state == RTCPeerConnectionState.Failed || state == RTCPeerConnectionState.Disconnected ||
                state == RTCPeerConnectionState.Closed)
                failure = "Video connection " + state;
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
        double deadline = Time.realtimeSinceStartupAsDouble + 15;
        while (peer.GatheringState != RTCIceGatheringState.Complete)
        {
            if (Time.realtimeSinceStartupAsDouble >= deadline)
                throw new InvalidOperationException("ICE gathering timed out.");
            yield return null;
        }
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
