using UnityEngine;

// Floor plate in a MirrorRoom: stepping on it puts the room's mirrors back where they started
// (a way out when a mirror is pushed into a corner).
[RequireComponent(typeof(Collider))]
public class MirrorResetPlate : MonoBehaviour
{
    private MirrorRoom room;
    private MirrorStageController stage;

    private void Awake()
    {
        GetComponent<Collider>().isTrigger = true;
        room = GetComponentInParent<MirrorRoom>();
        stage = FindFirstObjectByType<MirrorStageController>();
    }

    private void OnTriggerEnter(Collider other)
    {
        ClimbActorController actor = other.GetComponentInParent<ClimbActorController>();
        if (actor == null || actor.Frozen || room == null) return;
        if (room.ResetPieces() && stage != null) stage.OnRoomReset(room);
    }
}
