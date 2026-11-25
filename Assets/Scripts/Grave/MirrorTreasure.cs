using UnityEngine;

// The treasure chest at the top of the mirror cave. Walking up to it opens it (TreasureChestFx
// on the same object: coins, light) and clears the stage.
[RequireComponent(typeof(Collider))]
public class MirrorTreasure : MonoBehaviour
{
    private MirrorStageController stage;
    private TreasureChestFx fx;
    private bool opened;

    public bool IsOpen => opened;

    private void Awake()
    {
        GetComponent<Collider>().isTrigger = true;
        stage = FindFirstObjectByType<MirrorStageController>();
        fx = GetComponent<TreasureChestFx>();
    }

    private void OnTriggerStay(Collider other)
    {
        if (opened) return;
        ClimbActorController actor = other.GetComponentInParent<ClimbActorController>();
        if (actor != null && stage != null && !actor.Frozen) stage.OnTreasureReached(this);
    }

    public void Open()
    {
        if (opened) return;
        opened = true;
        if (fx != null) fx.Open();
    }
}
