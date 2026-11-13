using UnityEngine;
using UnityEngine.SceneManagement;

// Implemented by each stage controller so navigation can leave any stage safely.
public interface IStageFlow
{
    // Revoke remote control, unbind stage inputs and stop the simulation.
    void PrepareToLeave();
}

public static class StageSceneLoader
{
    private static bool loading;
    public static bool IsLoading => loading;

    public static bool CanLoad(string sceneName) =>
        !string.IsNullOrWhiteSpace(sceneName) &&
        Application.CanStreamedLevelBeLoaded(sceneName);

    // carryControl: moving to the next stage of the same run. A participant who is playing
    // keeps their turn (new video + countdown in the next stage). Returning to the lobby or
    // starting a new game never carries control.
    public static bool Load(string sceneName, IStageFlow leaving, Object context, bool carryControl = false)
    {
        if (loading) return false;
        if (!CanLoad(sceneName))
        {
            Debug.LogError($"Build Profiles의 Scene List에 '{sceneName}' 씬을 추가해주세요.", context);
            return false;
        }

        loading = true;
        if (carryControl && GameSession.Mode == GameMode.Multiplayer)
        {
            RemoteInputGateway gateway = MultiplayerHostSession.Instance != null
                ? MultiplayerHostSession.Instance.Gateway : null;
            if (gateway != null) gateway.BeginStageCarry();
        }
        leaving?.PrepareToLeave();

        // The persistent gateway accepts one video sender. Release this stage's
        // sender before the next stage registers its own.
        foreach (NaWaBaRoomVideoSender sender in
                 Object.FindObjectsByType<NaWaBaRoomVideoSender>(FindObjectsSortMode.None))
            sender.enabled = false;

        AsyncOperation operation = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
        if (operation == null)
        {
            loading = false;
            return false;
        }
        operation.completed += _ => loading = false;
        return true;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        loading = false;
    }
}
