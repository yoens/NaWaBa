using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class LobbyController : MonoBehaviour
{
    [SerializeField] private TMP_Text modeText;
    [SerializeField] private Button startButton;
    [SerializeField] private string stageSceneName = "Stage_Dodge";

    private const string MainMenuScene = "MainMenu";
    private bool loading;

    private bool CanStartSinglePlayer => !loading &&
        GameSession.Mode == GameMode.SinglePlayer &&
        GameSession.SelectedCharacter != null &&
        GameSession.SelectedCharacter.VisualPrefab != null;

    private void Start()
    {
        if (modeText == null || startButton == null)
        {
            Debug.LogError("LobbyController: assign Mode Text and Start Button.", this);
            if (startButton != null) startButton.interactable = false;
            enabled = false;
            return;
        }
        modeText.text = GameSession.Mode == GameMode.SinglePlayer
            ? "SINGLE PLAYER" : "MULTIPLAYER";
        startButton.interactable = CanStartSinglePlayer;
    }

    private void Update()
    {
        // Character selection may initialize after this component's Start.
        startButton.interactable = CanStartSinglePlayer;
    }

    public void StartStage()
    {
        if (!isActiveAndEnabled || !CanStartSinglePlayer) return;
        OpenScene(stageSceneName);
    }

    public void BackToMainMenu()
    {
        OpenScene(MainMenuScene);
    }

    private void OpenScene(string sceneName)
    {
        if (loading) return;
        if (string.IsNullOrWhiteSpace(sceneName) ||
            !Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Debug.LogError($"Add scene to Build Profiles Scene List: {sceneName}", this);
            return;
        }
        loading = true;
        if (startButton != null) startButton.interactable = false;
        SceneManager.LoadScene(sceneName);
    }
}
