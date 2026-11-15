using UnityEngine;

// Exit of the trap corridors (stage 4). Standing in it clears the stage.
[RequireComponent(typeof(Collider))]
public class KeepGoal : MonoBehaviour
{
    private KeepStageController stage;

    private void Awake()
    {
        GetComponent<Collider>().isTrigger = true;
        stage = FindFirstObjectByType<KeepStageController>();
    }

    private void OnTriggerStay(Collider other)
    {
        ClimbActorController actor = other.GetComponentInParent<ClimbActorController>();
        if (actor != null && stage != null && actor.IsGrounded && !actor.Frozen) stage.OnReachedGoal();
    }
}
