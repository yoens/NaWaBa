using UnityEngine;

public class LobbyModePanels : MonoBehaviour
{
    [SerializeField] private GameObject multiplayerPanel;

    private void Start()
    {
        if (multiplayerPanel == null)
        {
            Debug.LogError(
                "LobbyModePanels에 MultiplayerPanel을 연결해주세요.",
                this);

            enabled = false;
            return;
        }

        bool isMultiplayer =
            GameSession.Mode == GameMode.Multiplayer;

        multiplayerPanel.SetActive(isMultiplayer);
    }
}