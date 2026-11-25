using UnityEngine;

// Checkpoint on the graveyard path. "Progress" grows toward the crypt; reaching one with a
// higher progress than before makes it the respawn point. The lamp orb turns green.
[RequireComponent(typeof(Collider))]
public class GraveCheckpoint : MonoBehaviour
{
    [SerializeField] private int progress = 1;
    [Tooltip("Lamp orb that changes colour when reached (created when empty).")]
    [SerializeField] private Renderer orb;

    private GraveStageController stage;
    private bool reached;

    public int Progress => progress;
    public Vector3 RespawnPosition => transform.position;

    public void Configure(int value) => progress = value;

    private void Awake()
    {
        GetComponent<Collider>().isTrigger = true;
        stage = FindFirstObjectByType<GraveStageController>();
        if (orb == null)
        {
            GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(ball.GetComponent<Collider>());
            ball.name = "CheckpointOrb";
            ball.transform.SetParent(transform, false);
            ball.transform.localPosition = new Vector3(0f, 2.6f, 0f);
            ball.transform.localScale = Vector3.one * 0.45f;
            orb = ball.GetComponent<Renderer>();
        }
        MarkReached(false);
    }

    public void MarkReached(bool value)
    {
        reached = value;
        if (orb != null)
            RuntimeMaterials.Paint(orb, value ? new Color(0.3f, 1f, 0.45f) : new Color(0.55f, 0.75f, 1f));
    }

    private void OnTriggerStay(Collider other)
    {
        if (reached || stage == null) return;
        ClimbActorController actor = other.GetComponentInParent<ClimbActorController>();
        if (actor != null && actor.IsGrounded && !actor.Frozen && stage.ReachCheckpoint(this)) MarkReached(true);
    }
}
