using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ConnectionHUD : MonoBehaviour
{
    [SerializeField] private RemoteInputGateway gateway;
    [SerializeField] private InputRouter inputRouter;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private Button remoteButton;
    [SerializeField] private Button reclaimButton;

    private void Awake()
    {
        if (gateway == null || inputRouter == null || statusText == null ||
            remoteButton == null || reclaimButton == null)
        {
            Debug.LogError("ConnectionHUD: check the five Inspector references.", this);
            enabled = false;
            return;
        }
        statusText.richText = false;
        TMP_Text label = remoteButton.GetComponentInChildren<TMP_Text>();
        if (label != null) label.text = "Approve";
        label = reclaimButton.GetComponentInChildren<TMP_Text>();
        if (label != null) label.text = "Take Back";
    }

    private void Update()
    {
        string player = string.IsNullOrEmpty(gateway.PlayerName) ? "-" : gateway.PlayerName;
        statusText.text = $"Server: {(gateway.IsConnected ? "CONNECTED" : "DISCONNECTED")}\n" +
            $"Mode: {gateway.ControlState}\nPlayer: {player}";
        remoteButton.interactable = gateway.HasPendingRequest && inputRouter.isActiveAndEnabled;
        reclaimButton.interactable = gateway.HasAuthority;
    }

    // Keep existing button OnClick method names; the Inspector wiring need not change.
    public void EnableRemoteMode() => gateway.ApprovePendingRequest();
    public void ReclaimControl() => gateway.ReclaimControl();
}
