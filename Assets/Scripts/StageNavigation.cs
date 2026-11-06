using UnityEngine;
using UnityEngine.SceneManagement;

public class StageNavigation : MonoBehaviour
{
    [SerializeField] private string lobbySceneName = "Lobby";
    [SerializeField] private DodgeStageController stageController;

    private bool loading;

    public void ReturnToLobby()
    {
        if (!Application.isPlaying || loading) return;

        if (string.IsNullOrWhiteSpace(lobbySceneName) ||
            !Application.CanStreamedLevelBeLoaded(lobbySceneName))
        {
            Debug.LogError("Build Profiles의 Scene List에 Lobby 씬을 추가해주세요.", this);
            return;
        }
        if (stageController == null)
        {
            Debug.LogError("StageNavigation: assign the stage's DodgeStageController.", this);
            return;
        }

        loading = true;
        stageController.PrepareToLeave();
        SceneManager.LoadSceneAsync(lobbySceneName, LoadSceneMode.Single);
    }
}
