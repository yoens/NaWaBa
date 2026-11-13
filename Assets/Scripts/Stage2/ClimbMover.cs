using UnityEngine;

// Moving platform for stage 2: glides back and forth along "travel" (world metres, from the
// start point to one end; it swings to the other end too). The player rides along
// (ClimbActorController adds Velocity while standing on it). The course builder adds this
// for steps with a move distance; it can also be added by hand to any platform.
public class ClimbMover : MonoBehaviour
{
    [SerializeField] private Vector3 travel = new Vector3(2f, 0f, 0f);
    [SerializeField, Min(0.5f)] private float period = 4f;     // seconds for a full back-and-forth
    [SerializeField, Range(0f, 1f)] private float phase;

    // Set by the stage while the game is paused (handoff, intro): platforms hold still.
    public static bool Paused { get; set; }

    // Difficulty: 1 = as built, 1.3 = 30% faster swings.
    public static float SpeedScale { get; set; } = 1f;

    public Vector3 Velocity { get; private set; }

    private Rigidbody body;
    private Vector3 origin;
    private float time;

    public void Configure(Vector3 worldTravel, float seconds, float startPhase)
    {
        travel = worldTravel;
        period = Mathf.Max(0.5f, seconds);
        phase = Mathf.Repeat(startPhase, 1f);
    }

    private void Awake()
    {
        origin = transform.position;
        body = GetComponent<Rigidbody>();
        if (body == null) body = gameObject.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        body.interpolation = RigidbodyInterpolation.Interpolate;
    }

    private void FixedUpdate()
    {
        if (Paused)
        {
            Velocity = Vector3.zero;
            return;
        }
        float dt = Time.fixedDeltaTime;
        time += dt * Mathf.Max(0.1f, SpeedScale);
        Vector3 target = origin + travel * Mathf.Sin((time / period + phase) * Mathf.PI * 2f);
        Velocity = (target - body.position) / dt;
        body.MovePosition(target);
    }

    private void OnDisable()
    {
        Velocity = Vector3.zero;
    }

    private void OnDrawGizmosSelected()
    {
        Vector3 from = Application.isPlaying ? origin : transform.position;
        Gizmos.color = new Color(1f, 0.6f, 0.1f);
        Gizmos.DrawLine(from - travel, from + travel);
    }
}
