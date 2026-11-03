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
        if (gateway == null || inputRouter == null ||
            statusText == null || remoteButton == null ||
            reclaimButton == null)
        {
            Debug.LogError("ConnectionHUD의 Inspector 연결을 확인해주세요.", this);
            enabled = false;
        }
    }

    private void Update()
    {
        bool connected = gateway.IsConnected;
        bool remote = inputRouter.IsRemoteControl;

        statusText.text =
            $"Server: {(connected ? "CONNECTED" : "DISCONNECTED")}\n" +
            $"Mode: {(remote ? "REMOTE" : "LOCAL")}";

        remoteButton.interactable =
            connected && inputRouter.isActiveAndEnabled && !remote;

        reclaimButton.interactable = remote;
    }

    public void EnableRemoteMode()
    {
        if (!gateway.IsConnected || !inputRouter.isActiveAndEnabled)
            return;

        inputRouter.UseRemoteInput();
    }

    public void ReclaimControl()
    {
        inputRouter.UseLocalInput();
    }
}