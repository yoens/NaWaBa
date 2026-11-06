using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public class MultiplayerHostSession : MonoBehaviour
{
    public static MultiplayerHostSession Instance { get; private set; }
    public RemoteInputGateway Gateway { get; private set; }

    public static MultiplayerHostSession EnsureSession()
    {
        if (!Application.isPlaying || GameSession.Mode != GameMode.Multiplayer)
            return null;
        if (Instance != null) return Instance;

        // Configure before OnEnable can open a socket.
        GameObject root = new GameObject("MultiplayerHostSession");
        root.SetActive(false);
        RemoteInputGateway gateway = root.AddComponent<RemoteInputGateway>();
        gateway.ConfigureManagedLobbySession();
        MultiplayerHostSession owner = root.AddComponent<MultiplayerHostSession>();
        owner.Gateway = gateway;
        Instance = owner;
        DontDestroyOnLoad(root);
        root.SetActive(true);
        return owner;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Instance = null;
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name == "MainMenu" || GameSession.Mode != GameMode.Multiplayer)
            Shutdown();
    }

    public void Shutdown()
    {
        if (Instance == this) Instance = null;
        // Stops networking immediately; Destroy itself is deferred by Unity.
        gameObject.SetActive(false);
        Destroy(gameObject);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
