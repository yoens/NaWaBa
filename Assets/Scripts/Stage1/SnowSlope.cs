using System;
using System.Collections.Generic;
using UnityEngine;

// Stage 1 (3D): a snowy slope from the start pad (around the origin) up to a summit plateau.
// Snowballs roll down the play lane between two walls; the player climbs up with the
// stage 2 controls. Everything is built when the scene starts, so the numbers below are
// the whole level. Gizmos show the layout in the Scene view.
//
//   start pad (z < 4, y = 0) → slope (run × angle) → summit plateau (goal)
public class SnowSlope : MonoBehaviour
{
    [Header("Shape (metres / degrees)")]
    [SerializeField, Min(4f)] private float laneWidth = 14f;        // between the walls
    [SerializeField, Min(10f)] private float run = 55f;             // horizontal length of the slope
    [SerializeField, Range(5f, 30f)] private float angle = 15f;
    [SerializeField, Min(4f)] private float padDepth = 9f;          // start area (safe)
    [SerializeField, Min(6f)] private float summitDepth = 12f;
    [SerializeField, Min(0f)] private float sideWidth = 16f;        // snowfield outside the walls
    [SerializeField, Min(1.6f)] private float wallHeight = 2.4f;    // higher than a jump
    [SerializeField] private int groundLayer = 6;                   // must be in the player's Ground Mask

    [Header("Snowballs (difficulty overrides these)")]
    [SerializeField, Min(0.1f)] private float spawnInterval = 1f;
    [SerializeField, Min(0f)] private float spawnHalfWidth = 6f;
    [SerializeField, Min(0.5f)] private float snowballSpeed = 7f;
    [SerializeField] private Vector2 snowballRadius = new Vector2(0.55f, 0.85f);
    [SerializeField, Range(0f, 1f)] private float doubleSpawnChance = 0.3f;

    [Header("Look")]
    [SerializeField] private GameObject[] treePrefabs;
    [SerializeField] private Vector2 treeScale = new Vector2(2.5f, 4f);
    [SerializeField] private Color snowColor = new Color(0.93f, 0.96f, 1f);
    [SerializeField] private Color padColor = new Color(0.55f, 0.57f, 0.6f);
    [SerializeField] private Color wallColor = new Color(0.62f, 0.72f, 0.82f);
    [SerializeField] private Color snowballColor = Color.white;
    [SerializeField] private bool useFog = true;
    [SerializeField] private Color fogColor = new Color(0.78f, 0.86f, 0.95f);

    // Fired by snowballs and the summit area. The stage controller decides what happens.
    public event Action PlayerHit;
    public event Action ReachedSummit;

    public bool Running { get; set; }
    public float Rise => run * Mathf.Tan(angle * Mathf.Deg2Rad);
    public float SlopeStartZ => padDepth * 0.5f;
    public float SummitZ => SlopeStartZ + run;

    private readonly List<Ball> balls = new List<Ball>();
    private Vector3 up, down, normal;   // along the slope (up/down) and its surface normal
    private float spawnTimer;
    private Material ballMaterial;
    private bool built;

    private class Ball
    {
        public Transform transform;
        public Rigidbody body;
        public float radius, travelled, spin;
        public float x;
    }

    // Difficulty for this run.
    public void Configure(float interval, float halfWidth, float speed)
    {
        spawnInterval = Mathf.Max(0.1f, interval);
        spawnHalfWidth = Mathf.Max(0f, halfWidth);
        snowballSpeed = Mathf.Max(0.5f, speed);
    }

    // 0 at the bottom of the slope, 1 at the summit.
    public float Progress(Vector3 position) => Mathf.Clamp01((position.z - SlopeStartZ) / run);

    // Points from the summit down to the start (for the intro camera).
    public List<Vector3> IntroRoute(Vector3 player)
    {
        var route = new List<Vector3>
        {
            Origin + new Vector3(0f, Rise, SummitZ + summitDepth * 0.5f),
            Origin + new Vector3(0f, Rise, SummitZ),
            Origin + new Vector3(0f, Rise * 0.5f, SlopeStartZ + run * 0.5f),
            Origin + new Vector3(0f, 0f, SlopeStartZ),
            player
        };
        return route;
    }

    private Vector3 Origin => transform.position;

    private void Awake()
    {
        float a = angle * Mathf.Deg2Rad;
        up = new Vector3(0f, Mathf.Sin(a), Mathf.Cos(a));
        down = -up;
        normal = new Vector3(0f, Mathf.Cos(a), -Mathf.Sin(a));
        Build();
    }

    private void OnDestroy()
    {
        if (ballMaterial != null) Destroy(ballMaterial);
    }

    // ---------- snowballs ----------

    private void FixedUpdate()
    {
        if (!built || !Running) return;
        float dt = Time.fixedDeltaTime;

        spawnTimer += dt;
        if (spawnTimer >= spawnInterval)
        {
            spawnTimer = 0f;
            Spawn();
            if (UnityEngine.Random.value < doubleSpawnChance) Spawn();
        }

        float slopeLength = run / Mathf.Cos(angle * Mathf.Deg2Rad);
        for (int i = balls.Count - 1; i >= 0; i--)
        {
            Ball ball = balls[i];
            if (ball.transform == null) { balls.RemoveAt(i); continue; }
            ball.travelled += snowballSpeed * dt;
            if (ball.travelled > slopeLength + ball.radius)
            {
                // Reached the start pad: crumble (the pad is a safe area).
                Destroy(ball.transform.gameObject);
                balls.RemoveAt(i);
                continue;
            }
            Place(ball, dt);
        }
    }

    public void ClearSnowballs()
    {
        foreach (Ball ball in balls)
            if (ball.transform != null) Destroy(ball.transform.gameObject);
        balls.Clear();
        spawnTimer = 0f;
    }

    private void Spawn()
    {
        float radius = UnityEngine.Random.Range(snowballRadius.x, snowballRadius.y);
        float limit = Mathf.Max(0f, laneWidth * 0.5f - radius - 0.1f);
        float half = Mathf.Min(spawnHalfWidth, limit);

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "Snowball";
        go.layer = 2; // Ignore Raycast: the follow camera does not bump into snowballs
        go.transform.SetParent(transform, false);
        SphereCollider sphere = go.GetComponent<SphereCollider>();
        if (sphere != null) sphere.isTrigger = true;
        Rigidbody body = go.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        go.AddComponent<SnowSlopeTrigger>().Init(this, SnowSlopeTrigger.Kind.Snowball);
        Renderer renderer = go.GetComponent<Renderer>();
        if (renderer != null) renderer.sharedMaterial = BallMaterial();

        var ball = new Ball
        {
            transform = go.transform, body = body, radius = radius,
            x = UnityEngine.Random.Range(-half, half), travelled = 0f
        };
        balls.Add(ball);
        Place(ball, 0f);
        go.transform.position = body.position;
    }

    private void Place(Ball ball, float dt)
    {
        // Grows a little while rolling, like a real snowball.
        float slopeLength = run / Mathf.Cos(angle * Mathf.Deg2Rad);
        float r = ball.radius * (1f + 0.25f * Mathf.Clamp01(ball.travelled / slopeLength));
        ball.transform.localScale = Vector3.one * (r * 2f);
        Vector3 top = Origin + new Vector3(ball.x, Rise, SummitZ);
        Vector3 position = top + down * ball.travelled + normal * r;
        ball.spin += snowballSpeed / Mathf.Max(0.1f, r) * Mathf.Rad2Deg * dt;
        Quaternion rotation = Quaternion.AngleAxis(-ball.spin, Vector3.right);
        if (dt > 0f) { ball.body.MovePosition(position); ball.body.MoveRotation(rotation); }
        else { ball.body.position = position; ball.body.rotation = rotation; }
    }

    private Material BallMaterial()
    {
        if (ballMaterial != null) return ballMaterial;
        GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ballMaterial = new Material(probe.GetComponent<Renderer>().sharedMaterial) { name = "Snowball" };
        Destroy(probe);
        ballMaterial.color = snowballColor;
        if (ballMaterial.HasProperty("_BaseColor")) ballMaterial.SetColor("_BaseColor", snowballColor);
        return ballMaterial;
    }

    internal void OnTrigger(SnowSlopeTrigger.Kind kind, ClimbActorController actor)
    {
        if (!Running) return;
        if (kind == SnowSlopeTrigger.Kind.Snowball) PlayerHit?.Invoke();
        else if (actor.IsGrounded) ReachedSummit?.Invoke();
    }

    // ---------- level geometry ----------

    private void Build()
    {
        float totalWidth = laneWidth + 2f * sideWidth;
        float wallX = laneWidth * 0.5f + 0.25f;
        float padBack = -padDepth * 0.5f;
        float rise = Rise;

        // Start pad (y 0) and summit plateau (y = rise): flat boxes, 1 m thick.
        Box("StartPad", new Vector3(0f, -1.5f, (padBack + SlopeStartZ) * 0.5f),
            new Vector3(totalWidth, 3f, padDepth), Quaternion.identity, padColor, groundLayer);
        Box("Summit", new Vector3(0f, rise - 1.5f, SummitZ + summitDepth * 0.5f),
            new Vector3(totalWidth, 3f, summitDepth), Quaternion.identity, snowColor, groundLayer);

        // The slope: its top surface runs from the pad edge to the summit edge.
        // It starts 1 m early (under the pad) so there is no gap at the bottom.
        Vector3 bottom = new Vector3(0f, 0f, SlopeStartZ) - up * 1f;
        Vector3 topEdge = new Vector3(0f, rise, SummitZ);
        float length = Vector3.Distance(bottom, topEdge);
        Quaternion tilt = Quaternion.LookRotation(up, normal);
        Vector3 mid = (bottom + topEdge) * 0.5f;
        Box("Slope", mid - normal * 0.5f, new Vector3(totalWidth, 1f, length), tilt, snowColor, groundLayer);

        // Walls along the lane (not "ground", so they cannot be jumped off).
        foreach (float side in new[] { -1f, 1f })
        {
            Box("WallPad", new Vector3(side * wallX, wallHeight * 0.5f - 0.5f, (padBack + SlopeStartZ) * 0.5f),
                new Vector3(0.5f, wallHeight + 1f, padDepth), Quaternion.identity, wallColor, 0);
            Box("WallSlope", new Vector3(side * wallX, 0f, 0f) + mid + normal * (wallHeight * 0.5f - 0.5f),
                new Vector3(0.5f, wallHeight + 1f, length), tilt, wallColor, 0);
            Box("WallSummit", new Vector3(side * wallX, rise + wallHeight * 0.5f - 0.5f, SummitZ + summitDepth * 0.5f),
                new Vector3(0.5f, wallHeight + 1f, summitDepth), Quaternion.identity, wallColor, 0);
        }
        // Back walls behind the start and the summit.
        Box("WallBack", new Vector3(0f, wallHeight * 0.5f - 0.5f, padBack - 0.25f),
            new Vector3(laneWidth + 1f, wallHeight + 1f, 0.5f), Quaternion.identity, wallColor, 0);
        Box("WallTop", new Vector3(0f, rise + wallHeight * 0.5f - 0.5f, SummitZ + summitDepth + 0.25f),
            new Vector3(laneWidth + 1f, wallHeight + 1f, 0.5f), Quaternion.identity, wallColor, 0);

        // Summit goal: land anywhere on the plateau.
        var goal = new GameObject("SummitGoal");
        goal.layer = 2;
        goal.transform.SetParent(transform, false);
        goal.transform.localPosition = new Vector3(0f, rise + 1.2f, SummitZ + summitDepth * 0.5f + 0.5f);
        BoxCollider goalArea = goal.AddComponent<BoxCollider>();
        goalArea.isTrigger = true;
        goalArea.size = new Vector3(laneWidth, 2.4f, summitDepth - 1f);
        goal.AddComponent<SnowSlopeTrigger>().Init(this, SnowSlopeTrigger.Kind.Summit);

        BuildSummitFlag(new Vector3(0f, rise, SummitZ + summitDepth - 2f));
        BuildTrees(totalWidth);

        if (useFog)
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = fogColor;
            RenderSettings.fogStartDistance = 45f;
            RenderSettings.fogEndDistance = 160f;
        }
        built = true;
    }

    private void BuildSummitFlag(Vector3 basePoint)
    {
        Box("FlagPole", basePoint + Vector3.up * 2f, new Vector3(0.15f, 4f, 0.15f), Quaternion.identity,
            new Color(0.4f, 0.3f, 0.22f), 0, collider: false);
        Box("Flag", basePoint + new Vector3(0.75f, 3.5f, 0f), new Vector3(1.4f, 0.8f, 0.05f), Quaternion.identity,
            new Color(0.9f, 0.2f, 0.2f), 0, collider: false);
        // A few rocks so the summit reads as a summit.
        Box("Rock", basePoint + new Vector3(-3f, 0.4f, 0.5f), new Vector3(1.6f, 0.8f, 1.2f),
            Quaternion.Euler(0f, 20f, 0f), padColor, 0, collider: false);
        Box("Rock", basePoint + new Vector3(3.2f, 0.3f, -0.4f), new Vector3(1.1f, 0.6f, 1f),
            Quaternion.Euler(0f, -35f, 0f), padColor, 0, collider: false);
    }

    private void BuildTrees(float totalWidth)
    {
        float start = -padDepth * 0.5f;
        float end = SummitZ + summitDepth;
        float inner = laneWidth * 0.5f + 1.5f;
        float outer = Mathf.Max(inner + 0.5f, totalWidth * 0.5f - 1f);
        for (float z = start; z < end; z += 3.2f)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                if (UnityEngine.Random.value < 0.25f) continue;
                float x = side * UnityEngine.Random.Range(inner, outer);
                float zz = z + UnityEngine.Random.Range(-1f, 1f);
                GameObject tree = CreateTree();
                foreach (Collider c in tree.GetComponentsInChildren<Collider>()) Destroy(c);
                tree.transform.SetParent(transform, false);
                tree.transform.localPosition = new Vector3(x, SurfaceHeight(zz) - 0.05f, zz);
                tree.transform.localRotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                tree.transform.localScale *= UnityEngine.Random.Range(treeScale.x, treeScale.y);
            }
        }
    }

    private float SurfaceHeight(float z)
    {
        if (z <= SlopeStartZ) return 0f;
        if (z >= SummitZ) return Rise;
        return (z - SlopeStartZ) * Mathf.Tan(angle * Mathf.Deg2Rad);
    }

    private GameObject CreateTree()
    {
        if (treePrefabs != null && treePrefabs.Length > 0)
        {
            GameObject prefab = treePrefabs[UnityEngine.Random.Range(0, treePrefabs.Length)];
            if (prefab != null) return Instantiate(prefab);
        }
        var root = new GameObject("Tree");
        Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.15f, 0f), new Vector3(0.1f, 0.15f, 0.1f), new Color(0.45f, 0.3f, 0.2f));
        Part(root, PrimitiveType.Sphere, new Vector3(0f, 0.45f, 0f), new Vector3(0.45f, 0.35f, 0.45f), new Color(0.2f, 0.42f, 0.3f));
        Part(root, PrimitiveType.Sphere, new Vector3(0f, 0.7f, 0f), new Vector3(0.3f, 0.28f, 0.3f), new Color(0.85f, 0.9f, 0.95f));
        return root;
    }

    private static void Part(GameObject root, PrimitiveType type, Vector3 position, Vector3 scale, Color color)
    {
        GameObject part = GameObject.CreatePrimitive(type);
        Destroy(part.GetComponent<Collider>());
        part.transform.SetParent(root.transform, false);
        part.transform.localPosition = position;
        part.transform.localScale = scale;
        Renderer r = part.GetComponent<Renderer>();
        if (r != null) r.material.color = color;
    }

    private GameObject Box(string name, Vector3 localCenter, Vector3 size, Quaternion rotation, Color color,
        int layer, bool collider = true)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.layer = layer;
        if (!collider) Destroy(box.GetComponent<Collider>());
        box.transform.SetParent(transform, false);
        box.transform.localPosition = localCenter;
        box.transform.localRotation = rotation;
        box.transform.localScale = size;
        Renderer r = box.GetComponent<Renderer>();
        if (r != null) r.material.color = color;
        return box;
    }

    private void OnDrawGizmos()
    {
        float a = angle * Mathf.Deg2Rad;
        float rise = run * Mathf.Tan(a);
        float s = padDepth * 0.5f, half = laneWidth * 0.5f;
        Vector3 o = transform.position;
        Gizmos.color = new Color(0.4f, 0.8f, 1f);
        Vector3 b0 = o + new Vector3(-half, 0f, s), b1 = o + new Vector3(half, 0f, s);
        Vector3 t0 = o + new Vector3(-half, rise, s + run), t1 = o + new Vector3(half, rise, s + run);
        Gizmos.DrawLine(b0, b1); Gizmos.DrawLine(t0, t1); Gizmos.DrawLine(b0, t0); Gizmos.DrawLine(b1, t1);
        Gizmos.DrawWireCube(o + new Vector3(0f, 0f, 0f), new Vector3(laneWidth, 0.05f, padDepth));
        Gizmos.color = new Color(1f, 0.8f, 0.2f);
        Gizmos.DrawWireCube(o + new Vector3(0f, rise, s + run + summitDepth * 0.5f),
            new Vector3(laneWidth, 0.05f, summitDepth));
    }
}
