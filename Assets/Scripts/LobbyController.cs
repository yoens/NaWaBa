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

    private bool CanStartStage
    {
        get
        {
            if (loading || GameSession.SelectedCharacter == null ||
                GameSession.SelectedCharacter.VisualPrefab == null) return false;
            if (GameSession.Mode == GameMode.SinglePlayer) return true;
            MultiplayerHostSession owner = MultiplayerHostSession.Instance;
            RemoteInputGateway gateway = owner != null ? owner.Gateway : null;
            return gateway != null && gateway.IsConnected &&
                !string.IsNullOrEmpty(gateway.InviteUrl);
        }
    }

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
        startButton.interactable = CanStartStage;
    }

    private void Update()
    {
        // Character selection may initialize after this component's Start.
        startButton.interactable = CanStartStage;
    }

    public void StartStage()
    {
        if (!isActiveAndEnabled || !CanStartStage) return;
        // A new run always starts at the first stage of the stage list.
        OpenScene(StageSceneLoader.FirstStage(stageSceneName));
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
        SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
    }
}
