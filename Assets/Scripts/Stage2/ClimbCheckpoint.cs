using UnityEngine;

// Checkpoint flag on a platform. Landing inside the trigger makes it the respawn point for
// falls (when the difficulty uses checkpoints). The builder menu places these automatically;
// you can also add one by hand: a GameObject with a trigger collider resting on a platform.
[RequireComponent(typeof(Collider))]
public class ClimbCheckpoint : MonoBehaviour
{
    [SerializeField] private ClimbStageController stage;
    // Order along the course (1, 2, ...). A lower checkpoint never replaces a higher one.
    [SerializeField, Min(1)] private int order = 1;
    [SerializeField] private bool showMarker = true;
    [SerializeField] private Color idleColor = new Color(0.35f, 0.7f, 1f);
    [SerializeField] private Color reachedColor = new Color(0.3f, 1f, 0.45f);

    private Renderer flagRenderer;
    private bool reached;

    public int Order => order;

    // Where the player body is placed after a fall: the centre of the trigger's floor.
    public Vector3 RespawnPosition
    {
        get
        {
            Bounds b = GetComponent<Collider>().bounds;
            return new Vector3(b.center.x, b.min.y + 0.1f, b.center.z);
        }
    }

    public void Configure(int newOrder) => order = Mathf.Max(1, newOrder);

    private void Awake()
    {
        Collider area = GetComponent<Collider>();
        area.isTrigger = true;
        if (stage == null)
        {
            foreach (ClimbStageController candidate in
                     FindObjectsByType<ClimbStageController>(FindObjectsSortMode.None))
            {
                if (candidate.gameObject.scene == gameObject.scene) { stage = candidate; break; }
            }
        }
        if (showMarker) BuildMarker(area);
    }

    private void OnTriggerStay(Collider other)
    {
        if (reached || stage == null) return;
        ClimbActorController actor = other.GetComponentInParent<ClimbActorController>();
        if (actor == null || !actor.IsGrounded || actor.Frozen) return;
        if (stage.ReachCheckpoint(this, actor)) MarkReached(true);
    }

    public void MarkReached(bool value)
    {
        reached = value;
        if (flagRenderer != null) flagRenderer.material.color = value ? reachedColor : idleColor;
    }

    private void BuildMarker(Collider area)
    {
        Bounds bounds = area.bounds;
        Vector3 basePoint = new Vector3(bounds.max.x - 0.15f, bounds.min.y, bounds.max.z - 0.15f);

        GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pole.name = "CheckpointPole";
        Destroy(pole.GetComponent<Collider>());
        pole.transform.SetParent(transform, true);
        pole.transform.position = basePoint + Vector3.up * 0.6f;
        pole.transform.localScale = new Vector3(0.06f, 0.6f, 0.06f);

        GameObject flag = GameObject.CreatePrimitive(PrimitiveType.Cube);
        flag.name = "CheckpointFlag";
        Destroy(flag.GetComponent<Collider>());
        flag.transform.SetParent(transform, true);
        flag.transform.position = basePoint + new Vector3(0f, 1.05f, -0.22f);
        flag.transform.localScale = new Vector3(0.03f, 0.25f, 0.4f);
        flagRenderer = flag.GetComponent<Renderer>();
        if (flagRenderer != null) flagRenderer.material.color = idleColor;
    }
}
