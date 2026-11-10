using System;
using System.Collections;
using System.IO;
using System.Text;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;

// Standalone video experiment using the existing server HTTP namespace.
// Keep this in BrowserVideoLab until the latency comparison is complete.
public sealed class NaWaBaLocalVideoSender : MonoBehaviour
{
    [SerializeField] private Camera sourceCamera;
    [SerializeField] private Transform rotatingCube;

    private string baseUrl;
    private string hostKey;
    private string clientId;
    private string inviteUrl = "";
    private const int Width = 1280;
    private const int Height = 720;
    private RTCPeerConnection peer;
    private RTCRtpSender sender;
    private VideoStreamTrack track;
    private MediaStream stream;
    private RenderTexture previousTarget;
    private RenderTexture capturedTexture;
    private Coroutine negotiation;
    private UnityWebRequest currentRequest;
    private ApiMessage response;
    private string requestError;
    private string status = "Starting...";
    private string session = "";
    private bool initialized;
    private bool previousRunInBackground;
    private int previousFrameRate;
    private int previousVSync;
    private GUIStyle labelStyle;
    private Coroutine videoUpdate;
    private bool captureAttempted;
    private bool showPreview;
    private bool captureReady;
    private enum Action { None, Capture, Pump, Connect }
    private Action pendingAction;

    private void Mark(string message)
    {
        status = message;
        Debug.Log("[NaWaBa RTC STEP] " + message, this);
    }

    [Serializable]
    private class ApiMessage
    {
        public string session;
        public string sdp;
        public string error;
        public string inviteUrl;
    }

    [Serializable]
    private class HostConfig
    {
        public int schemaVersion;
        public string serverUrl;
        public string hostKey;
    }

    private bool ReadHostConfig()
    {
        string filename = Path.Combine(Path.GetDirectoryName(Application.dataPath), "nawaba-host.local.json");
        try
        {
            var config = JsonUtility.FromJson<HostConfig>(File.ReadAllText(filename));
            if (config == null || config.schemaVersion != 1 || string.IsNullOrEmpty(config.hostKey) ||
                config.hostKey.Length != 64) throw new InvalidOperationException();
            foreach (char c in config.hostKey)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) throw new InvalidOperationException();
            if (!Uri.TryCreate(config.serverUrl, UriKind.Absolute, out Uri address) ||
                (address.Scheme != "wss" && !(address.Scheme == "ws" && address.IsLoopback)) ||
                address.AbsolutePath != "/host" || !string.IsNullOrEmpty(address.UserInfo) ||
                !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
                throw new InvalidOperationException();
            var httpAddress = new UriBuilder(address)
            {
                Scheme = address.Scheme == "wss" ? "https" : "http",
                Port = address.IsDefaultPort ? -1 : address.Port,
                Path = "/webrtc-experiment", Query = "", Fragment = ""
            };
            baseUrl = httpAddress.Uri.AbsoluteUri.TrimEnd('/');
            hostKey = config.hostKey;
            return true;
        }
        catch
        {
            status = "HOST CONFIG ERROR - see Console";
            Debug.LogError("Missing or invalid nawaba-host.local.json. Use your existing host configuration. " +
                "Editor: project root. Windows build: beside the .exe. Expected path: " + filename, this);
            return false;
        }
    }

    private bool AcceptInvite(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri address) ||
            address.GetLeftPart(UriPartial.Authority) != new Uri(baseUrl).GetLeftPart(UriPartial.Authority) ||
            address.AbsolutePath != "/webrtc-experiment/" || !string.IsNullOrEmpty(address.UserInfo) ||
            !string.IsNullOrEmpty(address.Query)) return false;
        string expectedPrefix = "#session=" + session + "&view=";
        if (!address.Fragment.StartsWith(expectedPrefix, StringComparison.Ordinal)) return false;
        string token = address.Fragment.Substring(expectedPrefix.Length);
        if (token.Length != 64) return false;
        foreach (char c in token)
            if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
        inviteUrl = value;
        return true;
    }

    private void OnEnable()
    {
        if (sourceCamera == null)
        {
            Debug.LogError("NaWaBaLocalVideoSender: assign Source Camera.", this);
            enabled = false;
            return;
        }
        previousTarget = sourceCamera.targetTexture;
        previousRunInBackground = Application.runInBackground;
        previousFrameRate = Application.targetFrameRate;
        previousVSync = QualitySettings.vSyncCount;
        initialized = true;
        clientId = Guid.NewGuid().ToString("D");
        session = "";
        inviteUrl = "";
        captureAttempted = false;
        captureReady = false;
        pendingAction = Action.None;
        showPreview = false;
        Mark("0 READY - no WebRTC started. Click 1 Capture.");
    }

    private void CaptureOnly()
    {
        if (captureAttempted) return;
        captureAttempted = true;
        // Called from Update, not from an IMGUI layout/repaint event.
        Mark("1A BEFORE CaptureStreamTrack");
        try
        {
            track = sourceCamera.CaptureStreamTrack(Width, Height);
            capturedTexture = sourceCamera.targetTexture;
            Mark("1B AFTER CaptureStreamTrack");
            stream = new MediaStream();
            Mark("1C AFTER MediaStream constructor");
            stream.AddTrack(track);
            captureReady = true;
            Mark("1D CAPTURE READY - video update and network are still stopped");
        }
        catch (Exception error)
        {
            capturedTexture = sourceCamera.targetTexture;
            status = "CAPTURE ERROR - stop Play and check Console";
            Debug.LogException(error, this);
        }
    }

    private void StartVideoUpdate()
    {
        if (!captureReady || videoUpdate != null) return;
        Mark("2A BEFORE starting WebRTC.Update");
        videoUpdate = StartCoroutine(WebRTC.Update());
        Mark("2B VIDEO UPDATE STARTED - wait a few seconds before connecting");
        StartCoroutine(LogVideoUpdateFrames());
    }

    private IEnumerator LogVideoUpdateFrames()
    {
        for (int i = 0; i < 10; i++) yield return null;
        Mark("2C VIDEO UPDATE survived 10 frames - network still stopped");
    }

    private void Update()
    {
        if (rotatingCube != null)
            rotatingCube.Rotate(new Vector3(25f, 55f, 15f) * Time.deltaTime);
        var action = pendingAction;
        pendingAction = Action.None;
        if (action == Action.Capture) CaptureOnly();
        else if (action == Action.Pump) StartVideoUpdate();
        else if (action == Action.Connect) NewSession();
    }

    public void NewSession()
    {
        if (!initialized || !captureReady || videoUpdate == null) return;
        if (!ReadHostConfig()) return;
        inviteUrl = "";
        session = "";
        Application.runInBackground = true;
        Mark("3A START authenticated signaling");
        CancelNegotiation();
        ClosePeer();
        negotiation = StartCoroutine(Negotiate());
    }

    private IEnumerator Negotiate()
    {
        status = "Connecting to local server...";
        yield return Exchange("/api/start", new ApiMessage());
        if (requestError != null) { Fail(requestError); yield break; }
        session = response.session;
        if (!Guid.TryParseExact(session, "D", out _) || !AcceptInvite(response.inviteUrl))
        {
            Fail("Invalid video session response");
            yield break;
        }

        var configuration = new RTCConfiguration
        {
            iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } }
        };
        Mark("3B BEFORE RTCPeerConnection constructor");
        peer = new RTCPeerConnection(ref configuration);
        Mark("3C AFTER RTCPeerConnection constructor");
        var currentPeer = peer;
        peer.OnConnectionStateChange = state =>
        {
            if (peer != currentPeer) return;
            status = "WebRTC: " + state;
            Debug.Log("[NaWaBa WebRTC] " + state, this);
        };
        Mark("3D BEFORE AddTrack");
        sender = peer.AddTrack(track, stream);
        Mark("3E AFTER AddTrack");
        foreach (var transceiver in peer.GetTransceivers())
            transceiver.Direction = RTCRtpTransceiverDirection.SendOnly;

        Mark("3F BEFORE CreateOffer");
        var offerOperation = peer.CreateOffer();
        yield return offerOperation;
        if (offerOperation.IsError) { Fail(offerOperation.Error.message); yield break; }
        Mark("3G AFTER CreateOffer");
        var description = offerOperation.Desc;
        var localOperation = peer.SetLocalDescription(ref description);
        yield return localOperation;
        if (localOperation.IsError) { Fail(localOperation.Error.message); yield break; }

        status = "Gathering local ICE candidates...";
        float deadline = Time.realtimeSinceStartup + 15f;
        while (peer.GatheringState != RTCIceGatheringState.Complete)
        {
            if (Time.realtimeSinceStartup >= deadline) { Fail("ICE gathering timed out"); yield break; }
            yield return null;
        }
        string localSdp = peer.LocalDescription.sdp;
        Debug.Log("[NaWaBa RTC ICE] " + (localSdp.Contains(" typ srflx ")
            ? "STUN public candidate collected" : "No srflx candidate in gathered SDP"), this);
        yield return Exchange("/api/offer", new ApiMessage { session = session, sdp = localSdp });
        if (requestError != null) { Fail(requestError); yield break; }

        Mark("3H OFFER READY - Copy Video Link, then open it in the browser");
        while (true)
        {
            yield return Exchange("/api/answer?session=" + Uri.EscapeDataString(session));
            if (requestError != null) { Fail(requestError); yield break; }
            if (response.session != session) { Fail("Session mismatch"); yield break; }
            if (!string.IsNullOrEmpty(response.sdp)) break;
            yield return new WaitForSecondsRealtime(0.3f);
        }
        description = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = response.sdp };
        Mark("3I BEFORE SetRemoteDescription(answer)");
        var remoteOperation = peer.SetRemoteDescription(ref description);
        yield return remoteOperation;
        if (remoteOperation.IsError) { Fail(remoteOperation.Error.message); yield break; }

        Mark("3J AFTER SetRemoteDescription(answer)");
        var parameters = sender.GetParameters();
        if (parameters.encodings == null || parameters.encodings.Length == 0)
        {
            Fail("No video encoding parameters available");
            yield break;
        }
        foreach (var encoding in parameters.encodings)
        {
            encoding.maxFramerate = 30;
            encoding.maxBitrate = 3000000;
            encoding.scaleResolutionDownBy = 1;
        }
        var parameterError = sender.SetParameters(parameters);
        if (parameterError.errorType != RTCErrorType.None)
        {
            Fail("Could not set 30fps limit: " + parameterError.errorType);
            yield break;
        }
        status = "WebRTC: " + peer.ConnectionState + " / video capped at 30fps";
        negotiation = null;
    }

    private IEnumerator Exchange(string route, ApiMessage payload = null)
    {
        response = null;
        requestError = null;
        using (var request = new UnityWebRequest(baseUrl + route, payload == null ? "GET" : "POST"))
        {
            currentRequest = request;
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = 60;
            request.redirectLimit = 0;
            request.SetRequestHeader("X-NaWaBa-Host-Key", hostKey);
            request.SetRequestHeader("X-NaWaBa-Video-Client", clientId);
            if (!string.IsNullOrEmpty(session)) request.SetRequestHeader("X-NaWaBa-Video-Session", session);
            if (payload != null)
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload)));
                request.SetRequestHeader("Content-Type", "application/json");
            }
            UnityWebRequestAsyncOperation operation = null;
            try { operation = request.SendWebRequest(); }
            catch (Exception error) { requestError = error.Message; }
            if (operation != null)
            {
                yield return operation;
                if (request.result != UnityWebRequest.Result.Success)
                    requestError = request.error + " " + request.downloadHandler.text;
                else
                {
                    try { response = JsonUtility.FromJson<ApiMessage>(request.downloadHandler.text); }
                    catch (Exception error) { requestError = "Invalid server response: " + error.Message; }
                    if (response == null && requestError == null) requestError = "Empty server response";
                }
            }
            currentRequest = null;
        }
    }

    private void Fail(string message)
    {
        inviteUrl = "";
        status = "ERROR: " + message + " - press New Session to retry";
        Debug.LogError("[NaWaBa WebRTC] " + message, this);
        ClosePeer();
        negotiation = null;
    }

    private void CancelNegotiation()
    {
        // Abort before stopping the iterator so an in-flight HTTP poll is released.
        if (currentRequest != null) currentRequest.Abort();
        if (negotiation != null) StopCoroutine(negotiation);
        negotiation = null;
        if (currentRequest != null) currentRequest.Dispose();
        currentRequest = null;
    }

    private void ClosePeer()
    {
        sender = null;
        if (peer == null) return;
        peer.OnConnectionStateChange = null;
        peer.Close();
        peer.Dispose();
        peer = null;
    }

    private void OnGUI()
    {
        if (showPreview && capturedTexture != null)
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), capturedTexture, ScaleMode.ScaleToFit);
        if (labelStyle == null)
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
        GUI.Box(new Rect(8, 8, Mathf.Max(320, Screen.width - 16), 206), GUIContent.none);
        GUI.Label(new Rect(20, 14, Screen.width - 40, 56), status, labelStyle);
        GUI.enabled = !captureAttempted && pendingAction == Action.None;
        if (GUI.Button(new Rect(20, 78, 130, 34), "1 Capture")) pendingAction = Action.Capture;
        GUI.enabled = captureReady && videoUpdate == null && pendingAction == Action.None;
        if (GUI.Button(new Rect(160, 78, 150, 34), "2 Video Update")) pendingAction = Action.Pump;
        GUI.enabled = captureReady && videoUpdate != null && pendingAction == Action.None;
        if (GUI.Button(new Rect(20, 120, 180, 34), "3 Connect / New Session")) pendingAction = Action.Connect;
        GUI.enabled = !string.IsNullOrEmpty(inviteUrl);
        if (GUI.Button(new Rect(210, 120, 140, 34), "Copy Video Link"))
        {
            GUIUtility.systemCopyBuffer = inviteUrl;
            status = "VIDEO LINK COPIED - use this new link on the receiver";
        }
        GUI.enabled = captureReady;
        bool nextPreview = GUI.Toggle(new Rect(20, 166, 220, 28), showPreview, "Optional local preview");
        if (nextPreview != showPreview)
        {
            Mark(nextPreview ? "P1 BEFORE enabling local preview" : "P0 Local preview disabled");
            showPreview = nextPreview;
        }
        GUI.enabled = true;
    }

    private void OnDisable()
    {
        if (initialized) Mark("STOP 1 - begin cleanup");
        CancelNegotiation();
        StopAllCoroutines();
        videoUpdate = null;
        ClosePeer();
        if (stream != null && track != null) stream.RemoveTrack(track);
        track?.Dispose();
        track = null;
        stream?.Dispose();
        stream = null;
        if (initialized)
        {
            if (sourceCamera != null) sourceCamera.targetTexture = previousTarget;
            if (capturedTexture != null && capturedTexture != previousTarget) Destroy(capturedTexture);
            Application.runInBackground = previousRunInBackground;
            Application.targetFrameRate = previousFrameRate;
            QualitySettings.vSyncCount = previousVSync;
        }
        capturedTexture = null;
        if (initialized) Mark("STOP 2 - cleanup completed");
        initialized = false;
        inviteUrl = "";
        hostKey = null;
        captureReady = false;
        pendingAction = Action.None;
    }
}
