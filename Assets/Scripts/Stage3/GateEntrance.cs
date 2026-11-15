using UnityEngine;

// Trigger just inside the gate: walking in after the gate opens finishes stage 3.
[RequireComponent(typeof(Collider))]
public class GateEntrance : MonoBehaviour
{
    private GateStageController stage;

    private void Awake()
    {
        GetComponent<Collider>().isTrigger = true;
        stage = FindFirstObjectByType<GateStageController>();
    }

    private void OnTriggerStay(Collider other)
    {
        ClimbActorController actor = other.GetComponentInParent<ClimbActorController>();
        if (actor != null && stage != null && !actor.Frozen) stage.OnEnteredCastle();
    }
}
