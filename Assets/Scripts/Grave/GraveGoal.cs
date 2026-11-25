using UnityEngine;

// The crypt door at the end of the graveyard path. Standing in it clears the stage.
[RequireComponent(typeof(Collider))]
public class GraveGoal : MonoBehaviour
{
    private GraveStageController stage;

    private void Awake()
    {
        GetComponent<Collider>().isTrigger = true;
        stage = FindFirstObjectByType<GraveStageController>();
    }

    private void OnTriggerStay(Collider other)
    {
        ClimbActorController actor = other.GetComponentInParent<ClimbActorController>();
        if (actor != null && stage != null && actor.IsGrounded && !actor.Frozen) stage.OnReachedGoal();
    }
}
