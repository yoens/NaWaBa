using UnityEngine;

// A ghost that patrols a fixed route (points in the parent's space). Touching it sends the
// player back to the last checkpoint. The position is a function of GraveClock.Time, so the
// route is the same every time and readable for the people watching.
// Loop = goes round (circle); otherwise it walks to the last point and back (ping-pong).
[RequireComponent(typeof(Collider))]
public class GraveGhost : MonoBehaviour
{
    [SerializeField] private Vector3[] points = { Vector3.zero, Vector3.right * 4f };
    [SerializeField] private bool loop;
    [SerializeField, Min(0.1f)] private float speed = 2.5f;     // m/s at ghost speed 1
    [SerializeField, Range(0f, 1f)] private float phase;          // start offset along the route
    [Tooltip("Model child that bobs up and down (optional).")]
    [SerializeField] private Transform visual;

    private GraveStageController stage;
    private float[] distances;
    private float length;
    private Vector3 visualBase;

    public void Configure(Vector3[] route, bool isLoop, float metresPerSecond, float startPhase, Transform model)
    {
        points = route;
        loop = isLoop;
        speed = metresPerSecond;
        phase = startPhase;
        visual = model;
    }

    private void Awake()
    {
        GetComponent<Collider>().isTrigger = true;
        Rigidbody body = GetComponent<Rigidbody>();
        if (body == null) body = gameObject.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        stage = FindFirstObjectByType<GraveStageController>();
        if (visual != null) visualBase = visual.localPosition;
        Measure();
        Place(GraveClock.Time);
    }

    private void Measure()
    {
        if (points == null || points.Length == 0) points = new[] { transform.localPosition };
        int segments = loop ? points.Length : points.Length - 1;
        distances = new float[Mathf.Max(1, segments + 1)];
        length = 0f;
        for (int i = 0; i < segments; i++)
        {
            length += Vector3.Distance(points[i], points[(i + 1) % points.Length]);
            distances[i + 1] = length;
        }
    }

    private void Update()
    {
        Place(GraveClock.Time);
    }

    private void Place(float time)
    {
        if (points.Length == 1 || length <= 0.001f)
        {
            transform.localPosition = points[0];
            return;
        }
        float cycle = loop ? length : length * 2f;
        float d = Mathf.Repeat(time * speed + phase * cycle, cycle);
        bool back = !loop && d > length;
        if (back) d = cycle - d;

        int i = 0;
        while (i < distances.Length - 2 && d > distances[i + 1]) i++;
        Vector3 a = points[i], b = points[(i + 1) % points.Length];
        float span = Mathf.Max(0.001f, distances[i + 1] - distances[i]);
        transform.localPosition = Vector3.Lerp(a, b, (d - distances[i]) / span);

        Vector3 heading = back ? a - b : b - a;
        heading.y = 0f;
        if (heading.sqrMagnitude > 0.0001f)
        {
            Quaternion face = Quaternion.LookRotation(transform.parent != null
                ? transform.parent.TransformDirection(heading) : heading, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, face, 0.25f);
        }
        if (visual != null)
            visual.localPosition = visualBase + Vector3.up * (Mathf.Sin(time * 3f + phase * 6.28f) * 0.15f);
    }

    private void OnTriggerEnter(Collider other) => Touch(other);
    private void OnTriggerStay(Collider other) => Touch(other);

    private void Touch(Collider other)
    {
        if (stage == null) return;
        ClimbActorController actor = other.GetComponentInParent<ClimbActorController>();
        if (actor != null && !actor.Frozen) stage.OnGhostHit(this);
    }

    private void OnDrawGizmosSelected()
    {
        if (points == null || points.Length < 2) return;
        Gizmos.color = new Color(0.6f, 0.9f, 1f);
        Transform parent = transform.parent;
        int segments = loop ? points.Length : points.Length - 1;
        for (int i = 0; i < segments; i++)
        {
            Vector3 a = points[i], b = points[(i + 1) % points.Length];
            if (parent != null) { a = parent.TransformPoint(a); b = parent.TransformPoint(b); }
            Gizmos.DrawLine(a, b);
        }
    }
}
