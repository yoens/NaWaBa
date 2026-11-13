using System;
using System.Collections.Generic;
using UnityEngine;

// Stage 1 (3D): a snowy slope from the start pad (around the origin) up to a summit plateau.
// Snowballs roll down the play lane between two walls; the player climbs up with the
// stage 2 controls. Everything is built when the scene starts, so the numbers below are
// the whole level. Gizmos show the layout in the Scene view.
//
//   start pad (z < slope start, y = 0) -> slope (run x angle) -> summit plateau (goal)
//
// Editable map: menu "NaWaBa > 눈 비탈 만들기 (Stage_Dodge)" builds everything as real scene
// objects under "SnowSlope (built)". Move, delete or duplicate them freely (an obstacle needs
// its SnowObstacle component to stop snowballs). With no built objects the level is built
// when the scene starts, as before.
//
// With the Kenney prefabs assigned, the ground is covered with snow blocks, the walls are
// block walls, rocks and crates on the slope stop snowballs (hide behind them!), and trees,
// a flag and signs dress the scene. Without prefabs simple shapes are used instead.
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

    [Header("Obstacles on the slope (block snowballs, can be climbed)")]
    [SerializeField] private GameObject[] obstaclePrefabs;          // rocks, crates, barrels ...
    [SerializeField, Min(3f)] private float obstacleSpacing = 9f;   // metres between rows (along the slope)
    [SerializeField] private Vector2 obstacleSize = new Vector2(1.6f, 2.6f);
    [SerializeField, Range(0, 3)] private int maxObstaclesPerRow = 2;
    [SerializeField] private int layoutSeed = 7;                    // same layout every time

    [Header("Kenney look (optional)")]
    [SerializeField] private GameObject groundTilePrefab;           // e.g. block-snow-large
    [SerializeField, Min(1f)] private float groundTileSize = 2.5f;
    [SerializeField] private GameObject wallBlockPrefab;            // e.g. block-snow
    [SerializeField] private GameObject[] treePrefabs;              // side trees
    [SerializeField] private GameObject[] sideRockPrefabs;          // side rocks / stones
    [SerializeField] private GameObject flagPrefab;                 // summit flag
    [SerializeField] private GameObject signPrefab;                 // start sign
    [SerializeField] private GameObject[] startPropPrefabs;         // barrels, crates at the start
    [SerializeField] private Vector2 treeScale = new Vector2(2.5f, 4f);

    [Header("Colours (used for the simple shapes)")]
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
    private float SlopeLength => run / Mathf.Cos(angle * Mathf.Deg2Rad);

    private readonly List<Ball> balls = new List<Ball>();
    private readonly List<Blocker> blockers = new List<Blocker>();
    private Vector3 up, down, normal;   // along the slope (up/down) and its surface normal
    private Quaternion tilt;            // rotates "up" onto the slope normal
    private float spawnTimer;
    private Material ballMaterial;
    private bool built;
    private System.Random layoutRandom;
    private Transform buildRoot, currentGroup;
    private bool editMode;                     // building in the editor (undo, prefab links)
    private readonly Dictionary<Color, Material> paints = new Dictionary<Color, Material>();

    public const string BuiltName = "SnowSlope (built)";

    private class Ball
    {
        public Transform transform;
        public Rigidbody body;
        public float radius, travelled, spin, x;
    }

    // An obstacle in slope coordinates: lane x range and distance range from the top edge.
    private struct Blocker
    {
        public float xMin, xMax, fromTop, toTop;
    }

    [Header("Snowball patterns (difficulty overrides these)")]
    [SerializeField, Range(0f, 1f)] private float rowChance;
    [SerializeField, Range(0f, 1f)] private float aimedChance;

    // Aimed snowballs roll toward this (the player). Set by the stage controller.
    public Transform Target { get; set; }

    public void ConfigurePatterns(float doubleChance, float row, float aimed)
    {
        doubleSpawnChance = Mathf.Clamp01(doubleChance);
        rowChance = Mathf.Clamp01(row);
        aimedChance = Mathf.Clamp01(aimed);
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
        return new List<Vector3>
        {
            Origin + new Vector3(0f, Rise, SummitZ + summitDepth * 0.5f),
            Origin + new Vector3(0f, Rise, SummitZ),
            Origin + new Vector3(0f, Rise * 0.5f, SlopeStartZ + run * 0.5f),
            Origin + new Vector3(0f, 0f, SlopeStartZ),
            player
        };
    }

    private Vector3 Origin => transform.position;

    private void Awake()
    {
        InitShape();
        Transform existing = FindBuilt();
        if (existing != null)
        {
            buildRoot = existing;   // made in the editor: use it exactly as it is
            Debug.Log($"[SnowSlope] 에디터에서 만든 맵 사용: {existing.name} " +
                $"(장애물 {existing.GetComponentsInChildren<SnowObstacle>().Length}개)", existing);
        }
        else
        {
            Debug.LogWarning("[SnowSlope] 에디터에서 만든 맵(\"" + BuiltName + "\")이 씬에 없어서 " +
                "코드로 맵을 새로 만듭니다. 'NaWaBa > 눈 비탈 만들기'를 실행하고 씬을 저장하세요.", this);
            Build();
        }
        Physics.SyncTransforms();
        CollectBlockers();
        foreach (SnowSlopeTrigger trigger in buildRoot.GetComponentsInChildren<SnowSlopeTrigger>(true))
            trigger.Init(this, trigger.TriggerKind);
        ApplyFog();
        built = true;
    }

    // The editor-built map: normally a child of this object, but also found if it was dragged
    // elsewhere in the scene or renamed with a suffix (e.g. "SnowSlope (built) (1)").
    private Transform FindBuilt()
    {
        foreach (Transform child in transform)
            if (child.name.StartsWith(BuiltName)) return child;
        foreach (GameObject root in gameObject.scene.GetRootGameObjects())
        {
            if (root.name.StartsWith(BuiltName)) return root.transform;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith(BuiltName)) return t;
        }
        return null;
    }

    private void InitShape()
    {
        float a = angle * Mathf.Deg2Rad;
        up = new Vector3(0f, Mathf.Sin(a), Mathf.Cos(a));
        down = -up;
        normal = new Vector3(0f, Mathf.Cos(a), -Mathf.Sin(a));
        tilt = Quaternion.FromToRotation(Vector3.up, normal);
        layoutRandom = new System.Random(layoutSeed);
    }

    private void ApplyFog()
    {
        if (!useFog) return;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = fogColor;
        RenderSettings.fogStartDistance = 50f;
        RenderSettings.fogEndDistance = 180f;
    }

    // Every SnowObstacle (built or added by hand) stops snowballs in its lane.
    private void CollectBlockers()
    {
        blockers.Clear();
        Vector3 slopeBottom = Origin + new Vector3(0f, 0f, SlopeStartZ);
        var obstacles = new HashSet<SnowObstacle>(GetComponentsInChildren<SnowObstacle>());
        if (buildRoot != null) obstacles.UnionWith(buildRoot.GetComponentsInChildren<SnowObstacle>());
        foreach (SnowObstacle obstacle in obstacles)
        {
            Collider area = obstacle.GetComponentInChildren<Collider>();
            if (area == null || !obstacle.isActiveAndEnabled) continue;
            Bounds b = area.bounds;
            Vector3 local = b.center - Origin;
            float fromBottom = Vector3.Dot(b.center - slopeBottom, up);
            float fromTop = SlopeLength - fromBottom;
            float depth = Mathf.Max(b.extents.z, 0.3f);
            blockers.Add(new Blocker
            {
                xMin = local.x - b.extents.x, xMax = local.x + b.extents.x,
                fromTop = fromTop - depth, toTop = fromTop + depth
            });
        }
    }

    private Transform Group(string name)
    {
        var go = new GameObject(name);
        Register(go);
        go.transform.SetParent(buildRoot, false);
        return go.transform;
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
            if (UnityEngine.Random.value < rowChance) SpawnRow();
            else
            {
                Spawn(UnityEngine.Random.value < aimedChance);
                if (UnityEngine.Random.value < doubleSpawnChance) Spawn(false);
            }
        }

        for (int i = balls.Count - 1; i >= 0; i--)
        {
            Ball ball = balls[i];
            if (ball.transform == null) { balls.RemoveAt(i); continue; }
            float before = ball.travelled;
            ball.travelled += snowballSpeed * dt;
            if (ball.travelled > SlopeLength + ball.radius || HitsBlocker(ball, before))
            {
                // Reached the start pad (a safe area) or smashed into an obstacle.
                Shatter(ball);
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

    private bool HitsBlocker(Ball ball, float before)
    {
        float r = CurrentRadius(ball);
        foreach (Blocker b in blockers)
        {
            if (ball.x + r * 0.6f < b.xMin || ball.x - r * 0.6f > b.xMax) continue;
            // The front of the ball reached the obstacle's uphill face this step.
            float front = ball.travelled + r * 0.5f;
            if (front >= b.fromTop && before + r * 0.5f < b.toTop) return true;
        }
        return false;
    }

    private void Shatter(Ball ball)
    {
        Vector3 at = ball.transform.position;
        float r = CurrentRadius(ball);
        Destroy(ball.transform.gameObject);
        for (int i = 0; i < 6; i++)
        {
            GameObject chunk = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            chunk.name = "SnowChunk";
            Destroy(chunk.GetComponent<Collider>());
            chunk.transform.SetParent(transform, true);
            chunk.transform.position = at + UnityEngine.Random.insideUnitSphere * r * 0.5f;
            chunk.transform.localScale = Vector3.one * (r * UnityEngine.Random.Range(0.25f, 0.45f));
            Renderer renderer = chunk.GetComponent<Renderer>();
            if (renderer != null) renderer.sharedMaterial = BallMaterial();
            Rigidbody body = chunk.AddComponent<Rigidbody>();
            body.linearVelocity = (normal * 2.5f + UnityEngine.Random.insideUnitSphere * 2.5f);
            Destroy(chunk, 0.7f);
        }
    }

    // One snowball: random across the spread, or aimed at the player's lane position.
    private void Spawn(bool aimed)
    {
        float radius = UnityEngine.Random.Range(snowballRadius.x, snowballRadius.y);
        float limit = Mathf.Max(0f, laneWidth * 0.5f - radius - 0.1f);
        float half = Mathf.Min(spawnHalfWidth, limit);
        float x = UnityEngine.Random.Range(-half, half);
        if (aimed && Target != null)
            x = Mathf.Clamp(Target.position.x - Origin.x + UnityEngine.Random.Range(-0.8f, 0.8f), -limit, limit);
        SpawnAt(x, radius);
    }

    // A row across the whole lane with one gap (about 3 m) to slip through.
    private void SpawnRow()
    {
        float radius = Mathf.Lerp(snowballRadius.x, snowballRadius.y, 0.5f);
        float limit = Mathf.Max(0f, laneWidth * 0.5f - radius - 0.1f);
        float gapCenter = UnityEngine.Random.Range(-limit + 1.5f, limit - 1.5f);
        float step = radius * 2f + 0.5f;
        for (float x = -limit; x <= limit + 0.01f; x += step)
            if (Mathf.Abs(x - gapCenter) > 1.5f) SpawnAt(x, radius);
    }

    private void SpawnAt(float x, float radius)
    {
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
            x = x, travelled = 0f
        };
        balls.Add(ball);
        Place(ball, 0f);
        go.transform.position = body.position;
    }

    // Grows a little while rolling, like a real snowball.
    private float CurrentRadius(Ball ball) =>
        ball.radius * (1f + 0.25f * Mathf.Clamp01(ball.travelled / SlopeLength));

    private void Place(Ball ball, float dt)
    {
        float r = CurrentRadius(ball);
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
        var root = new GameObject(BuiltName);
        root.transform.SetParent(transform, false);
        Register(root);
        buildRoot = root.transform;
        currentGroup = Group("Ground");

        float totalWidth = laneWidth + 2f * sideWidth;
        float wallX = laneWidth * 0.5f + 0.25f;
        float padBack = -padDepth * 0.5f;
        float rise = Rise;
        bool tiles = groundTilePrefab != null;

        // Start pad (y 0) and summit plateau (y = rise): flat boxes.
        GameObject pad = Box("StartPad", new Vector3(0f, -1.5f, (padBack + SlopeStartZ) * 0.5f),
            new Vector3(totalWidth, 3f, padDepth), Quaternion.identity, padColor, groundLayer);
        GameObject summit = Box("Summit", new Vector3(0f, rise - 1.5f, SummitZ + summitDepth * 0.5f),
            new Vector3(totalWidth, 3f, summitDepth), Quaternion.identity, snowColor, groundLayer);

        // The slope: its top surface runs from the pad edge to the summit edge.
        // It starts 1 m early (under the pad) so there is no gap at the bottom.
        Vector3 bottom = new Vector3(0f, 0f, SlopeStartZ) - up * 1f;
        Vector3 topEdge = new Vector3(0f, rise, SummitZ);
        float length = Vector3.Distance(bottom, topEdge);
        Quaternion slopeRotation = Quaternion.LookRotation(up, normal);
        Vector3 mid = (bottom + topEdge) * 0.5f;
        GameObject slope = Box("Slope", mid - normal * 0.5f, new Vector3(totalWidth, 1f, length),
            slopeRotation, snowColor, groundLayer);

        if (tiles)
        {
            // Snow blocks over the lane and a strip beside it; the plain box stays underneath.
            float tiledHalf = laneWidth * 0.5f + 4f;
            TileFlat(padBack, SlopeStartZ, 0f, tiledHalf);
            TileSlope(tiledHalf);
            TileFlat(SummitZ, SummitZ + summitDepth, rise, tiledHalf);
        }

        // Walls along the lane (not "ground", so they cannot be jumped off).
        currentGroup = Group("Walls");
        foreach (float side in new[] { -1f, 1f })
        {
            GameObject a = Box("WallPad", new Vector3(side * wallX, wallHeight * 0.5f - 0.5f, (padBack + SlopeStartZ) * 0.5f),
                new Vector3(0.5f, wallHeight + 1f, padDepth), Quaternion.identity, wallColor, 0);
            GameObject b = Box("WallSlope", new Vector3(side * wallX, 0f, 0f) + mid + normal * (wallHeight * 0.5f - 0.5f),
                new Vector3(0.5f, wallHeight + 1f, length), slopeRotation, wallColor, 0);
            GameObject c = Box("WallSummit", new Vector3(side * wallX, rise + wallHeight * 0.5f - 0.5f, SummitZ + summitDepth * 0.5f),
                new Vector3(0.5f, wallHeight + 1f, summitDepth), Quaternion.identity, wallColor, 0);
            if (wallBlockPrefab != null)
            {
                Hide(a); Hide(b); Hide(c);
                WallBlocks(new Vector3(side * wallX, 0f, padBack), new Vector3(side * wallX, 0f, SlopeStartZ), Vector3.up);
                WallBlocks(new Vector3(side * wallX, 0f, SlopeStartZ), new Vector3(side * wallX, rise, SummitZ), normal);
                WallBlocks(new Vector3(side * wallX, rise, SummitZ), new Vector3(side * wallX, rise, SummitZ + summitDepth), Vector3.up);
            }
        }
        // Back walls behind the start and the summit.
        GameObject back = Box("WallBack", new Vector3(0f, wallHeight * 0.5f - 0.5f, padBack - 0.25f),
            new Vector3(laneWidth + 1f, wallHeight + 1f, 0.5f), Quaternion.identity, wallColor, 0);
        GameObject end = Box("WallTop", new Vector3(0f, rise + wallHeight * 0.5f - 0.5f, SummitZ + summitDepth + 0.25f),
            new Vector3(laneWidth + 1f, wallHeight + 1f, 0.5f), Quaternion.identity, wallColor, 0);
        if (wallBlockPrefab != null)
        {
            Hide(back); Hide(end);
            WallBlocks(new Vector3(-laneWidth * 0.5f, 0f, padBack - 0.25f), new Vector3(laneWidth * 0.5f, 0f, padBack - 0.25f), Vector3.up);
            WallBlocks(new Vector3(-laneWidth * 0.5f, rise, SummitZ + summitDepth + 0.25f),
                new Vector3(laneWidth * 0.5f, rise, SummitZ + summitDepth + 0.25f), Vector3.up);
        }

        // Summit goal: land anywhere on the plateau.
        currentGroup = Group("Goal");
        var goal = new GameObject("SummitGoal");
        Register(goal);
        goal.layer = 2;
        goal.transform.SetParent(currentGroup, false);
        goal.transform.localPosition = new Vector3(0f, rise + 1.2f, SummitZ + summitDepth * 0.5f + 0.5f);
        BoxCollider goalArea = goal.AddComponent<BoxCollider>();
        goalArea.isTrigger = true;
        goalArea.size = new Vector3(laneWidth, 2.4f, summitDepth - 1f);
        goal.AddComponent<SnowSlopeTrigger>().Init(this, SnowSlopeTrigger.Kind.Summit);

        BuildSummitProps(new Vector3(0f, rise, SummitZ + summitDepth - 2.5f));
        currentGroup = Group("Obstacles");
        BuildObstacles();
        currentGroup = Group("Scenery");
        BuildStartProps(padBack);
        BuildTrees(totalWidth);
    }

    // Rows of rocks / crates on the slope. A row never closes the lane.
    private void BuildObstacles()
    {
        if (maxObstaclesPerRow <= 0) return;
        float slopeLength = SlopeLength;
        float half = laneWidth * 0.5f;
        for (float fromBottom = 7f; fromBottom < slopeLength - 6f; fromBottom += obstacleSpacing)
        {
            int count = 1 + layoutRandom.Next(maxObstaclesPerRow);
            var used = new List<Vector2>();
            for (int i = 0; i < count; i++)
            {
                float size = Mathf.Lerp(obstacleSize.x, obstacleSize.y, (float)layoutRandom.NextDouble());
                float x = 0f;
                bool placed = false;
                for (int attempt = 0; attempt < 8 && !placed; attempt++)
                {
                    x = Mathf.Lerp(-half + size * 0.5f + 0.3f, half - size * 0.5f - 0.3f, (float)layoutRandom.NextDouble());
                    placed = true;
                    foreach (Vector2 other in used)
                        if (Mathf.Abs(other.x - x) < (other.y + size) * 0.5f + 2.5f) placed = false;
                }
                if (!placed) continue;
                used.Add(new Vector2(x, size));
                float s = fromBottom + (float)layoutRandom.NextDouble() * 2f;
                PlaceObstacle(x, s, size);
            }
        }
    }

    private void PlaceObstacle(float x, float fromBottom, float size)
    {
        Vector3 surface = new Vector3(x, 0f, SlopeStartZ) + up * fromBottom;
        GameObject prefab = Pick(obstaclePrefabs);
        GameObject obstacle;
        if (prefab != null)
        {
            obstacle = Model(prefab, size, true, out _);
            obstacle.transform.localRotation = tilt * Quaternion.Euler(0f, (float)layoutRandom.NextDouble() * 360f, 0f);
        }
        else
        {
            obstacle = Box("Rock", Vector3.zero, new Vector3(size, size * 0.6f, size * 0.8f), Quaternion.identity, padColor, groundLayer);
            obstacle.transform.localRotation = tilt;
            surface += normal * (size * 0.3f);
        }
        obstacle.name = "Obstacle";
        SetLayer(obstacle, groundLayer);
        obstacle.transform.localPosition = surface - normal * 0.05f;
        obstacle.AddComponent<SnowObstacle>();
    }

    private static void SetLayer(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform) SetLayer(child.gameObject, layer);
    }

    private void BuildSummitProps(Vector3 basePoint)
    {
        if (flagPrefab != null)
        {
            GameObject flag = Model(flagPrefab, 0f, false, out _, 4.5f);
            flag.transform.localPosition = basePoint;
        }
        else
        {
            Box("FlagPole", basePoint + Vector3.up * 2f, new Vector3(0.15f, 4f, 0.15f), Quaternion.identity,
                new Color(0.4f, 0.3f, 0.22f), 0, collider: false);
            Box("Flag", basePoint + new Vector3(0.75f, 3.5f, 0f), new Vector3(1.4f, 0.8f, 0.05f), Quaternion.identity,
                new Color(0.9f, 0.2f, 0.2f), 0, collider: false);
        }
        // Rocks and trees around the flag.
        Vector3[] spots = { new Vector3(-4f, 0f, 0.8f), new Vector3(4.2f, 0f, -0.6f), new Vector3(-6.5f, 0f, -1.5f), new Vector3(6.8f, 0f, 1f) };
        for (int i = 0; i < spots.Length; i++)
        {
            GameObject prefab = i < 2 ? Pick(sideRockPrefabs) : Pick(treePrefabs);
            if (prefab == null) continue;
            GameObject prop = Model(prefab, 0f, false, out _, i < 2 ? 2.5f : 3.2f);
            prop.transform.localPosition = basePoint + spots[i];
            prop.transform.localRotation = Quaternion.Euler(0f, i * 70f, 0f);
        }
    }

    private void BuildStartProps(float padBack)
    {
        float half = laneWidth * 0.5f;
        if (signPrefab != null)
        {
            GameObject sign = Model(signPrefab, 0f, false, out _, 2f);
            sign.transform.localPosition = new Vector3(-half + 2f, 0f, SlopeStartZ - 1.2f);
            sign.transform.localRotation = Quaternion.Euler(0f, 160f, 0f);
        }
        if (startPropPrefabs == null) return;
        Vector3[] spots = { new Vector3(half - 1.4f, 0f, padBack + 1.2f), new Vector3(half - 2.6f, 0f, padBack + 1f),
                            new Vector3(-half + 1.3f, 0f, padBack + 1.3f) };
        for (int i = 0; i < spots.Length; i++)
        {
            GameObject prefab = Pick(startPropPrefabs);
            if (prefab == null) continue;
            GameObject prop = Model(prefab, 0f, false, out _, 1.6f);
            prop.transform.localPosition = spots[i];
            prop.transform.localRotation = Quaternion.Euler(0f, i * 37f, 0f);
        }
    }

    private void BuildTrees(float totalWidth)
    {
        float start = -padDepth * 0.5f;
        float end = SummitZ + summitDepth;
        float inner = laneWidth * 0.5f + 1.5f;
        float outer = Mathf.Max(inner + 0.5f, totalWidth * 0.5f - 1f);
        for (float z = start; z < end; z += 2.8f)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                if (Rand(0f, 1f) < 0.2f) continue;
                float x = side * Rand(inner, outer);
                float zz = z + Rand(-1f, 1f);
                bool rock = Rand(0f, 1f) < 0.25f && sideRockPrefabs != null && sideRockPrefabs.Length > 0;
                GameObject prop = rock
                    ? Model(Pick(sideRockPrefabs), 0f, false, out _, Rand(1.2f, 2.4f))
                    : CreateTree();
                foreach (Collider c in prop.GetComponentsInChildren<Collider>()) Strip(c);
                prop.transform.SetParent(currentGroup, false);
                prop.transform.localPosition = new Vector3(x, SurfaceHeight(zz) - 0.05f, zz);
                prop.transform.localRotation = Quaternion.Euler(0f, Rand(0f, 360f), 0f);
                if (!rock) prop.transform.localScale *= Rand(treeScale.x, treeScale.y);
            }
        }
    }

    // ---------- tiles and walls ----------

    private void TileFlat(float zFrom, float zTo, float height, float halfWidth)
    {
        int countX = Mathf.CeilToInt(halfWidth * 2f / groundTileSize);
        int countZ = Mathf.CeilToInt((zTo - zFrom) / groundTileSize);
        float sizeX = halfWidth * 2f / countX, sizeZ = (zTo - zFrom) / countZ;
        for (int ix = 0; ix < countX; ix++)
        for (int iz = 0; iz < countZ; iz++)
        {
            Vector3 top = new Vector3(-halfWidth + sizeX * (ix + 0.5f), height, zFrom + sizeZ * (iz + 0.5f));
            Tile(top, Vector3.up, Quaternion.identity, sizeX, sizeZ);
        }
    }

    private void TileSlope(float halfWidth)
    {
        float length = SlopeLength;
        int countX = Mathf.CeilToInt(halfWidth * 2f / groundTileSize);
        int countS = Mathf.CeilToInt(length / groundTileSize);
        float sizeX = halfWidth * 2f / countX, sizeS = length / countS;
        Vector3 start = new Vector3(0f, 0f, SlopeStartZ);
        for (int ix = 0; ix < countX; ix++)
        for (int i = 0; i < countS; i++)
        {
            Vector3 top = start + new Vector3(-halfWidth + sizeX * (ix + 0.5f), 0f, 0f) + up * (sizeS * (i + 0.5f));
            Tile(top, normal, tilt, sizeX, sizeS);
        }
    }

    // One ground block whose top face is centred on "top".
    private void Tile(Vector3 top, Vector3 surfaceNormal, Quaternion rotation, float sizeX, float sizeZ)
    {
        GameObject tile = Fit(groundTilePrefab, new Vector3(sizeX + 0.02f, 1f, sizeZ + 0.02f));
        tile.name = "SnowTile";
        tile.transform.localRotation = rotation;
        tile.transform.localPosition = top - surfaceNormal * 1f;
    }

    // A row of blocks from "from" to "to" (ground level), standing up along "standUp".
    private void WallBlocks(Vector3 from, Vector3 to, Vector3 standUp)
    {
        Vector3 along = to - from;
        float length = along.magnitude;
        if (length < 0.1f) return;
        int count = Mathf.Max(1, Mathf.CeilToInt(length / 2f));
        float segment = length / count;
        Quaternion rotation = Quaternion.LookRotation(along.normalized, standUp);
        for (int i = 0; i < count; i++)
        {
            Vector3 groundPoint = from + along * ((i + 0.5f) / count);
            GameObject block = Fit(wallBlockPrefab, new Vector3(0.6f, wallHeight + 0.6f, segment + 0.02f));
            block.name = "WallBlock";
            block.transform.localRotation = rotation;
            block.transform.localPosition = groundPoint - standUp * 0.6f;
        }
    }

    // Instance a prefab (no colliders) scaled to exactly this size; pivot = bottom centre.
    private GameObject Fit(GameObject prefab, Vector3 size)
    {
        GameObject go = NewInstance(prefab);
        foreach (Collider c in go.GetComponentsInChildren<Collider>()) Strip(c);
        Bounds b = LocalBounds(go);
        Vector3 scale = go.transform.localScale;
        go.transform.localScale = new Vector3(
            scale.x * size.x / Mathf.Max(0.01f, b.size.x),
            scale.y * size.y / Mathf.Max(0.01f, b.size.y),
            scale.z * size.z / Mathf.Max(0.01f, b.size.z));
        go.transform.SetParent(currentGroup, false);
        return go;
    }

    // Instance a prefab scaled uniformly: to a footprint (size > 0) or a height (height > 0).
    // With "solid" a box collider is fitted around it.
    private GameObject Model(GameObject prefab, float footprint, bool solid, out Vector3 dimensions, float height = 0f)
    {
        GameObject go = NewInstance(prefab);
        foreach (Collider c in go.GetComponentsInChildren<Collider>()) Strip(c);
        Bounds b = LocalBounds(go);
        float factor = footprint > 0f
            ? footprint / Mathf.Max(0.01f, Mathf.Max(b.size.x, b.size.z))
            : height / Mathf.Max(0.01f, b.size.y);
        Vector3 baseScale = go.transform.localScale;
        go.transform.localScale = baseScale * factor;
        dimensions = b.size * factor;
        if (solid)
        {
            BoxCollider box = go.AddComponent<BoxCollider>();
            // Bounds were measured at the prefab's own scale; the collider works in local units.
            box.center = Divide(b.center, baseScale);
            box.size = Divide(b.size, baseScale);
        }
        go.transform.SetParent(currentGroup, false);
        return go;
    }

    private static Vector3 Divide(Vector3 a, Vector3 b) =>
        new Vector3(a.x / Mathf.Max(0.0001f, b.x), a.y / Mathf.Max(0.0001f, b.y), a.z / Mathf.Max(0.0001f, b.z));

    // Renderer bounds of a fresh instance standing at the origin (= its local bounds x scale).
    private static Bounds LocalBounds(GameObject go)
    {
        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return new Bounds(Vector3.up * 0.5f, Vector3.one);
        Bounds b = renderers[0].bounds;
        foreach (Renderer r in renderers) b.Encapsulate(r.bounds);
        return b;
    }

    private GameObject Pick(GameObject[] prefabs)
    {
        if (prefabs == null || prefabs.Length == 0) return null;
        return prefabs[layoutRandom.Next(prefabs.Length)];
    }

    private static void Hide(GameObject go)
    {
        Renderer r = go.GetComponent<Renderer>();
        if (r != null) r.enabled = false;
    }

    private float SurfaceHeight(float z)
    {
        if (z <= SlopeStartZ) return 0f;
        if (z >= SummitZ) return Rise;
        return (z - SlopeStartZ) * Mathf.Tan(angle * Mathf.Deg2Rad);
    }

    private GameObject CreateTree()
    {
        GameObject prefab = Pick(treePrefabs);
        if (prefab != null) return NewInstance(prefab);
        var root = new GameObject("Tree");
        Register(root);
        Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.15f, 0f), new Vector3(0.1f, 0.15f, 0.1f), new Color(0.45f, 0.3f, 0.2f));
        Part(root, PrimitiveType.Sphere, new Vector3(0f, 0.45f, 0f), new Vector3(0.45f, 0.35f, 0.45f), new Color(0.2f, 0.42f, 0.3f));
        Part(root, PrimitiveType.Sphere, new Vector3(0f, 0.7f, 0f), new Vector3(0.3f, 0.28f, 0.3f), new Color(0.85f, 0.9f, 0.95f));
        return root;
    }

    private void Part(GameObject root, PrimitiveType type, Vector3 position, Vector3 scale, Color color)
    {
        GameObject part = NewPrimitive(type);
        Strip(part.GetComponent<Collider>());
        part.transform.SetParent(root.transform, false);
        part.transform.localPosition = position;
        part.transform.localScale = scale;
        Renderer r = part.GetComponent<Renderer>();
        if (r != null) r.sharedMaterial = Paint(color);
    }

    private GameObject Box(string name, Vector3 localCenter, Vector3 size, Quaternion rotation, Color color,
        int layer, bool collider = true)
    {
        GameObject box = NewPrimitive(PrimitiveType.Cube);
        box.name = name;
        box.layer = layer;
        if (!collider) Strip(box.GetComponent<Collider>());
        box.transform.SetParent(currentGroup, false);
        box.transform.localPosition = localCenter;
        box.transform.localRotation = rotation;
        box.transform.localScale = size;
        Renderer r = box.GetComponent<Renderer>();
        if (r != null) r.sharedMaterial = Paint(color);
        return box;
    }

    // ---------- object creation (works at runtime and in the editor) ----------

    private float Rand(float a, float b) => Mathf.Lerp(a, b, (float)layoutRandom.NextDouble());

    private GameObject NewInstance(GameObject prefab)
    {
        GameObject go = null;
#if UNITY_EDITOR
        if (editMode) go = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, gameObject.scene);
#endif
        if (go == null) go = Instantiate(prefab);
        Register(go);
        return go;
    }

    private GameObject NewPrimitive(PrimitiveType type)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        Register(go);
        return go;
    }

    private void Register(GameObject go)
    {
#if UNITY_EDITOR
        if (editMode) UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Build Snow Slope");
#endif
    }

    private void Strip(Component component)
    {
        if (component == null) return;
        if (editMode) DestroyImmediate(component);
        else Destroy(component);
    }

    // One material per colour. In the editor they are saved as assets so the scene keeps them.
    private Material Paint(Color color)
    {
        if (paints.TryGetValue(color, out Material cached) && cached != null) return cached;
        Material material = null;
#if UNITY_EDITOR
        if (editMode)
        {
            const string folder = "Assets/Materials/NaWaBa";
            string path = $"{folder}/Snow_{ColorUtility.ToHtmlStringRGB(color)}.mat";
            material = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(DefaultMaterial()) { name = "Snow_" + ColorUtility.ToHtmlStringRGB(color) };
                Tint(material, color);
                if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/Materials"))
                    UnityEditor.AssetDatabase.CreateFolder("Assets", "Materials");
                if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
                    UnityEditor.AssetDatabase.CreateFolder("Assets/Materials", "NaWaBa");
                UnityEditor.AssetDatabase.CreateAsset(material, path);
            }
        }
#endif
        if (material == null)
        {
            material = new Material(DefaultMaterial());
            Tint(material, color);
        }
        paints[color] = material;
        return material;
    }

    private static Material defaultMaterial;

    private static Material DefaultMaterial()
    {
        if (defaultMaterial != null) return defaultMaterial;
        GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
        defaultMaterial = probe.GetComponent<Renderer>().sharedMaterial;
        DestroyImmediate(probe);
        return defaultMaterial;
    }

    private static void Tint(Material material, Color color)
    {
        material.color = color;
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
    }

#if UNITY_EDITOR
    // Menu "NaWaBa > 눈 비탈 만들기" calls this: builds the level as editable scene objects.
    public GameObject BuildInEditor()
    {
        Transform existing = FindBuilt();
        if (existing != null) UnityEditor.Undo.DestroyObjectImmediate(existing.gameObject);
        InitShape();
        editMode = true;
        try { Build(); }
        finally { editMode = false; }
        return buildRoot.gameObject;
    }

    // Inspector: ⋮ > "Kenney 프리팹 자동 연결" fills the prefab slots by name (when empty or missing).
    [ContextMenu("Kenney 프리팹 자동 연결")]
    private void AutoLinkPrefabs()
    {
        UnityEditor.Undo.RecordObject(this, "Link Kenney prefabs");
        obstaclePrefabs = FindPrefabs("rocks", "crate", "crate-strong", "barrel");
        groundTilePrefab = FindPrefab("block-snow-large");
        wallBlockPrefab = FindPrefab("block-snow");
        treePrefabs = FindPrefabs("tree-pine-snow", "tree-snow", "tree-pine-snow-small");
        sideRockPrefabs = FindPrefabs("rocks", "stones");
        flagPrefab = FindPrefab("flag");
        signPrefab = FindPrefab("sign");
        startPropPrefabs = FindPrefabs("barrel", "crate", "crate-item");
        UnityEditor.EditorUtility.SetDirty(this);
        Debug.Log("[SnowSlope] Kenney prefabs linked. Save the scene.", this);
    }

    private static GameObject[] FindPrefabs(params string[] names)
    {
        var found = new List<GameObject>();
        foreach (string n in names)
        {
            GameObject prefab = FindPrefab(n);
            if (prefab != null) found.Add(prefab);
        }
        return found.ToArray();
    }

    private static GameObject FindPrefab(string exactName)
    {
        foreach (string guid in UnityEditor.AssetDatabase.FindAssets(exactName + " t:Prefab"))
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            if (path.EndsWith(".prefab") && System.IO.Path.GetFileNameWithoutExtension(path) == exactName)
                return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }
        Debug.LogWarning("[SnowSlope] prefab not found: " + exactName);
        return null;
    }
#endif

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
        Gizmos.DrawWireCube(o, new Vector3(laneWidth, 0.05f, padDepth));
        Gizmos.color = new Color(1f, 0.8f, 0.2f);
        Gizmos.DrawWireCube(o + new Vector3(0f, rise, s + run + summitDepth * 0.5f),
            new Vector3(laneWidth, 0.05f, summitDepth));
    }
}
