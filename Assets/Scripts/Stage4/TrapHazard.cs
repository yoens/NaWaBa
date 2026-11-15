using UnityEngine;

// Anything that hurts in stage 4 (axe blade, saw, raised spikes, arrow): touching its
// trigger sends the player back to the last checkpoint.
[RequireComponent(typeof(Collider))]
public class TrapHazard : MonoBehaviour
{
    // Turned off by traps while they are harmless (spikes down).
    public bool Armed { get; set; } = true;

    private KeepStageController stage;

    private void Awake()
    {
        GetComponent<Collider>().isTrigger = true;
        stage = FindFirstObjectByType<KeepStageController>();
    }

    private void OnTriggerEnter(Collider other) => Touch(other);
    private void OnTriggerStay(Collider other) => Touch(other);

    private void Touch(Collider other)
    {
        if (!Armed || stage == null) return;
        ClimbActorController actor = other.GetComponentInParent<ClimbActorController>();
        if (actor != null && !actor.Frozen) stage.OnTrapHit(this);
    }
}
