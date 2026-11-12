using UnityEngine;

public class StageNavigation : MonoBehaviour
{
    [SerializeField] private string lobbySceneName = "Lobby";

    // Any stage controller implementing IStageFlow (Dodge, Climb, ...).
    // The existing DodgeStageController reference is kept by this field name.
    // Left empty, the stage controller in this scene is found automatically.
    [SerializeField] private MonoBehaviour stageController;

    public void ReturnToLobby()
    {
        if (!Application.isPlaying) return;

        IStageFlow flow = stageController as IStageFlow;
        if (flow == null) flow = FindStageInScene();
        if (flow == null)
        {
            Debug.LogError("StageNavigation: 이 씬에 IStageFlow 스테이지 컨트롤러가 없습니다.", this);
            return;
        }

        StageSceneLoader.Load(lobbySceneName, flow, this);
    }

    private IStageFlow FindStageInScene()
    {
        foreach (MonoBehaviour behaviour in
                 FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (behaviour is IStageFlow flow && behaviour.gameObject.scene == gameObject.scene)
            {
                stageController = behaviour;
                return flow;
            }
        }
        return null;
    }
}
