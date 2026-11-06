using UnityEngine;
using UnityEngine.SceneManagement;

public class MainMenuController : MonoBehaviour
{
    private const string LobbyScene = "Lobby";
    private bool loading;

    public void SelectSinglePlayer()
    {
        OpenLobby(GameMode.SinglePlayer);
    }

    public void SelectMultiplayer()
    {
        OpenLobby(GameMode.Multiplayer);
    }

    private void OpenLobby(GameMode mode)
    {
        if (loading)
            return;

        if (!Application.CanStreamedLevelBeLoaded(LobbyScene))
        {
            Debug.LogError(
                "Build Profiles의 Scene List에 Lobby를 추가해주세요.",
                this);
            return;
        }

        loading = true;
        GameSession.SelectMode(mode);
        SceneManager.LoadScene(LobbyScene);
    }
}