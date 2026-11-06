using UnityEngine;
using UnityEngine.SceneManagement;

public class StageNavigation : MonoBehaviour
{
    [SerializeField] private string lobbySceneName = "Lobby";

    private bool loading;

    public void ReturnToLobby()
    {
        if (!Application.isPlaying || loading)
            return;

        if (string.IsNullOrWhiteSpace(lobbySceneName) ||
            !Application.CanStreamedLevelBeLoaded(lobbySceneName))
        {
            Debug.LogError(
                "Build Profiles의 Scene List에 Lobby 씬을 추가해주세요.",
                this);

            return;
        }

        loading = true;

        SceneManager.LoadScene(
            lobbySceneName,
            LoadSceneMode.Single);
    }
}