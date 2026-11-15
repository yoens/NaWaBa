using UnityEngine;

// Checkpoint in the castle (stage 4). "Progress" grows toward the exit; reaching one with
// a higher progress than before makes it the respawn point.
[RequireComponent(typeof(Collider))]
public class TrapCheckpoint : MonoBehaviour
{
    [SerializeField] private int progress = 1;
    private KeepStageController stage;
    private Renderer flag;
    private bool reached;

    public int Progress => progress;
    public Vector3 RespawnPosition => transform.position;

    public void Configure(int value) => progress = value;

    private void Awake()
    {
        GetComponent<Collider>().isTrigger = true;
        stage = FindFirstObjectByType<KeepStageController>();

        GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(pole.GetComponent<Collider>());
        pole.transform.SetParent(transform, false);
        pole.transform.localPosition = new Vector3(1.6f, 1.2f, 0f);
        pole.transform.localScale = new Vector3(0.08f, 1.2f, 0.08f);
        RuntimeMaterials.Paint(pole.GetComponent<Renderer>(), new Color(0.85f, 0.85f, 0.85f));
        GameObject cloth = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(cloth.GetComponent<Collider>());
        cloth.transform.SetParent(transform, false);
        cloth.transform.localPosition = new Vector3(1.6f, 2.05f, 0.4f);
        cloth.transform.localScale = new Vector3(0.04f, 0.5f, 0.8f);
        flag = cloth.GetComponent<Renderer>();
        MarkReached(false);
    }

    public void MarkReached(bool value)
    {
        reached = value;
        RuntimeMaterials.Paint(flag, value ? new Color(0.3f, 1f, 0.45f) : new Color(0.35f, 0.7f, 1f));
    }

    private void OnTriggerStay(Collider other)
    {
        if (reached || stage == null) return;
        ClimbActorController actor = other.GetComponentInParent<ClimbActorController>();
        if (actor != null && actor.IsGrounded && !actor.Frozen && stage.ReachCheckpoint(this)) MarkReached(true);
    }
}
