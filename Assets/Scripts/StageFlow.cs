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

    public static bool Load(string sceneName, IStageFlow leaving, Object context)
    {
        if (loading) return false;
        if (!CanLoad(sceneName))
        {
            Debug.LogError($"Build Profiles의 Scene List에 '{sceneName}' 씬을 추가해주세요.", context);
            return false;
        }

        loading = true;
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
