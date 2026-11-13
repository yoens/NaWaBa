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
    // Optional: wire a "Remove" button to RemoveFrontRequest() to drop the front request.
    [SerializeField] private Button removeRequestButton;

    private bool IsSinglePlayer => gateway.UsesGameSessionMode &&
        GameSession.Mode == GameMode.SinglePlayer;

    private void Awake()
    {
        gateway = RemoteInputGateway.ResolveForScene(gateway);
        if (gateway == null || inputRouter == null || statusText == null ||
            remoteButton == null || reclaimButton == null)
        {
            Debug.LogError("ConnectionHUD: check the five Inspector references.", this);
            enabled = false;
            return;
        }
        statusText.richText = false;
        remoteButton.gameObject.SetActive(!IsSinglePlayer);
        reclaimButton.gameObject.SetActive(!IsSinglePlayer);
        if (removeRequestButton != null) removeRequestButton.gameObject.SetActive(!IsSinglePlayer);
        TMP_Text label = remoteButton.GetComponentInChildren<TMP_Text>();
        if (label != null) label.text = "Approve";
        label = reclaimButton.GetComponentInChildren<TMP_Text>();
        if (label != null) label.text = "Take Back";
    }

    private void Update()
    {
        if (IsSinglePlayer)
        {
            CharacterDefinition character = GameSession.SelectedCharacter;
            string name = character != null ? character.DisplayName : "-";
            statusText.text = $"Mode: SINGLE PLAYER\nConnection: OFFLINE\nCharacter: {name}";
            return;
        }

        string player = string.IsNullOrEmpty(gateway.PlayerName) ? "-" : gateway.PlayerName;
        statusText.text = $"Server: {(gateway.IsConnected ? "CONNECTED" : "DISCONNECTED")} / Room: {gateway.RoomLabel}" +
            $" / Guests: {gateway.ParticipantCount}\n" +
            $"Mode: {gateway.ControlState}\nPlayer: {player} / Waiting: {gateway.QueueCount} / Next: {gateway.QueueSummary}";
        remoteButton.interactable = gateway.CanApprovePendingRequest;
        reclaimButton.interactable = gateway.HasAuthority;
        if (removeRequestButton != null)
            removeRequestButton.interactable = gateway.IsConnected && gateway.QueueCount > 0;
    }

    // Preserve the existing button OnClick names and Inspector connections.
    public void EnableRemoteMode() => gateway.ApprovePendingRequest();
    public void ReclaimControl() => gateway.ReclaimControl();
    // Optional "Remove" button: takes the front request off the list.
    public void RemoveFrontRequest() => gateway.RemoveFrontRequest();
}
