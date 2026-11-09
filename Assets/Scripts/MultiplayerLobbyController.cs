using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class MultiplayerLobbyController : MonoBehaviour
{
    [SerializeField] private TMP_Text connectionStatusText;
    [SerializeField] private TMP_InputField inviteUrlInput;
    [SerializeField] private Button copyInviteButton;
    [SerializeField] private Button reconnectButton;

    private MultiplayerHostSession hostSession;
    private TMP_Text copyLabel;
    private bool listenersAdded;
    private float copiedUntil;

    private void Start()
    {
        if (connectionStatusText == null || inviteUrlInput == null ||
            copyInviteButton == null || reconnectButton == null)
        {
            Debug.LogError("MultiplayerLobbyController: assign all four UI references.", this);
            enabled = false;
            return;
        }

        connectionStatusText.richText = false;
        inviteUrlInput.readOnly = true;
        inviteUrlInput.richText = false;
        if (inviteUrlInput.textComponent != null)
            inviteUrlInput.textComponent.richText = false;
        inviteUrlInput.SetTextWithoutNotify("");
        copyInviteButton.interactable = false;
        reconnectButton.interactable = false;
        copyLabel = copyInviteButton.GetComponentInChildren<TMP_Text>(true);

        if (GameSession.Mode != GameMode.Multiplayer)
        {
            enabled = false;
            return;
        }

        copyInviteButton.onClick.AddListener(CopyInviteLink);
        reconnectButton.onClick.AddListener(Reconnect);
        listenersAdded = true;
        hostSession = MultiplayerHostSession.EnsureSession();
        RefreshView();
    }

    private void Update()
    {
        RefreshView();
    }

    private void RefreshView()
    {
        RemoteInputGateway gateway = hostSession != null ? hostSession.Gateway : null;
        bool connected = gateway != null && gateway.IsConnected;
        bool connecting = gateway != null && gateway.IsConnecting;
        string link = connected ? gateway.InviteUrl : "";
        bool canCopy = !string.IsNullOrEmpty(link);

        if (inviteUrlInput.text != link)
            inviteUrlInput.SetTextWithoutNotify(link);

        copyInviteButton.interactable = canCopy;
        reconnectButton.interactable = gateway != null && !connected && !connecting;
        if (!canCopy) copiedUntil = 0f;
        if (copyLabel != null)
            copyLabel.text = canCopy && Time.unscaledTime < copiedUntil ? "COPIED!" : "COPY LINK";

        if (connected)
        {
            // Two lines fit the existing status label; no extra Inspector reference is required.
            string state = !gateway.SupportsGuestStatus ? "UPDATE SERVER" :
                gateway.IsGuestConnected ? "GUEST CONNECTED" : "WAITING FOR GUEST";
            connectionStatusText.text = state + "\nROOM: " + gateway.RoomLabel;
        }
        else if (connecting)
            connectionStatusText.text = "CONNECTING...";
        else
            connectionStatusText.text = gateway != null && !string.IsNullOrEmpty(gateway.ConnectionIssue)
                ? gateway.ConnectionIssue : "DISCONNECTED";
    }

    private void CopyInviteLink()
    {
        RemoteInputGateway gateway = hostSession != null ? hostSession.Gateway : null;
        if (gateway == null || !gateway.IsConnected || string.IsNullOrEmpty(gateway.InviteUrl)) return;
        GUIUtility.systemCopyBuffer = gateway.InviteUrl;
        copiedUntil = Time.unscaledTime + 2f;
        RefreshView();
    }

    private void Reconnect()
    {
        if (hostSession == null || hostSession.Gateway == null) return;
        hostSession.Gateway.Reconnect();
        RefreshView();
    }

    private void OnDestroy()
    {
        if (!listenersAdded) return;
        if (copyInviteButton != null) copyInviteButton.onClick.RemoveListener(CopyInviteLink);
        if (reconnectButton != null) reconnectButton.onClick.RemoveListener(Reconnect);
    }
}
