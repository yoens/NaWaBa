using System.Collections.Generic;
using UnityEngine;

// Stage 1 "climbing up the mountain" feel. Snowballs roll down (−Z) and the world slides
// down past the player too, so the player seems to climb: trees and rocks at both sides of
// the arena and snow streaks on the slope scroll toward the bottom of the screen and are
// recycled at the top. A height gauge right of the arena fills up as the round goes on.
// Purely visual: nothing here has a collider. DodgeStageController drives it.
public class DodgeScenery : MonoBehaviour
{
    [Header("Side props (trees, rocks)")]
    // Prefabs placed beside the arena. Leave empty to use simple built-in trees.
    [SerializeField] private GameObject[] propPrefabs;
    [SerializeField, Min(0)] private int propsPerSide = 7;
    [SerializeField] private Vector2 propSideRange = new Vector2(13f, 22f); // |x| from the centre
    [SerializeField] private Vector2 propScaleRange = new Vector2(3f, 5f);

    [Header("Snow streaks on the slope")]
    [SerializeField, Min(0)] private int streakCount = 14;
    [SerializeField] private float streakHalfWidth = 9.5f;
    [SerializeField] private Color streakColor = new Color(1f, 1f, 1f, 0.35f);

    [Header("Mountain side (shown where no floor is drawn)")]
    [SerializeField] private bool buildSlope = true;
    [SerializeField] private Color slopeColor = new Color(0.86f, 0.9f, 0.96f);

    [Header("Scroll")]
    [SerializeField, Min(0f)] private float scrollSpeed = 3f;
    [SerializeField] private float topZ = 17f;
    [SerializeField] private float bottomZ = -17f;

    [Header("Height gauge (right of the arena)")]
    [SerializeField] private bool showGauge = true;
    [SerializeField] private float gaugeX = 10.7f;
    [SerializeField, Min(1f)] private float gaugeLength = 22f;

    private readonly List<Transform> props = new List<Transform>();
    private readonly List<Transform> streaks = new List<Transform>();
    private Material streakMaterial;
    private bool running;
    private float progress;     // 0..1 of the round
    private float altitude;     // metres shown
    private Transform gaugeRoot, gaugeFill, gaugeMarker;

    public bool Running { get => running; set => running = value; }
    public float ScrollSpeed { get => scrollSpeed; set => scrollSpeed = Mathf.Max(0f, value); }
    public float Altitude => altitude;

    private void Awake()
    {
        if (buildSlope) BuildSlope();
        BuildStreaks();
        BuildProps();
        if (showGauge) BuildGauge();
    }

    private void Update()
    {
        if (!running || scrollSpeed <= 0f) return;
        float step = scrollSpeed * Time.deltaTime;
        Scroll(props, step, true);
        // Streaks are closer to the player, so they move a little faster (depth feel).
        Scroll(streaks, step * 1.6f, false);
    }

    // progress01: how far through the round (0 at start, 1 at clear). metres: height label.
    public void SetProgress(float progress01, float metres)
    {
        progress = Mathf.Clamp01(progress01);
        altitude = metres;
        UpdateGauge();
    }

    private void Scroll(List<Transform> items, float step, bool isProp)
    {
        float span = topZ - bottomZ;
        foreach (Transform item in items)
        {
            if (item == null) continue;
            Vector3 p = item.localPosition;
            p.z -= step;
            if (p.z < bottomZ)
            {
                p.z += span;
                if (isProp) p.x = RandomSideX(p.x >= 0f ? 1f : -1f);
                else p.x = Random.Range(-streakHalfWidth, streakHalfWidth);
            }
            item.localPosition = p;
        }
    }

    private float RandomSideX(float side) => side * Random.Range(propSideRange.x, propSideRange.y);

    private void BuildProps()
    {
        float span = topZ - bottomZ;
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            float side = sideIndex == 0 ? -1f : 1f;
            for (int i = 0; i < propsPerSide; i++)
            {
                GameObject prop = CreateProp();
                prop.name = "ScenerySideProp";
                foreach (Collider c in prop.GetComponentsInChildren<Collider>()) Destroy(c);
                Transform t = prop.transform;
                t.SetParent(transform, false);
                float z = bottomZ + span * (i + Random.Range(0.1f, 0.9f)) / propsPerSide;
                t.localPosition = new Vector3(RandomSideX(side), 0f, z);
                t.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                t.localScale *= Random.Range(propScaleRange.x, propScaleRange.y);
                props.Add(t);
            }
        }
    }

    private GameObject CreateProp()
    {
        if (propPrefabs != null && propPrefabs.Length > 0)
        {
            GameObject prefab = propPrefabs[Random.Range(0, propPrefabs.Length)];
            if (prefab != null) return Instantiate(prefab);
        }
        return BuildSimpleTree();
    }

    // Fallback tree from primitives: a trunk and two stacked "cones" (squashed spheres).
    private GameObject BuildSimpleTree()
    {
        var root = new GameObject("SimpleTree");
        Color trunk = new Color(0.45f, 0.3f, 0.2f);
        Color leaves = new Color(0.2f, 0.45f, 0.3f);
        AddPart(root, PrimitiveType.Cylinder, new Vector3(0f, 0.15f, 0f), new Vector3(0.1f, 0.15f, 0.1f), trunk);
        AddPart(root, PrimitiveType.Sphere, new Vector3(0f, 0.4f, 0f), new Vector3(0.45f, 0.3f, 0.45f), leaves);
        AddPart(root, PrimitiveType.Sphere, new Vector3(0f, 0.62f, 0f), new Vector3(0.3f, 0.25f, 0.3f), leaves);
        return root;
    }

    private static void AddPart(GameObject root, PrimitiveType type, Vector3 position, Vector3 scale, Color color)
    {
        GameObject part = GameObject.CreatePrimitive(type);
        Destroy(part.GetComponent<Collider>());
        part.transform.SetParent(root.transform, false);
        part.transform.localPosition = position;
        part.transform.localScale = scale;
        Renderer r = part.GetComponent<Renderer>();
        if (r != null) r.material.color = color;
    }

    private void BuildStreaks()
    {
        float span = topZ - bottomZ;
        for (int i = 0; i < streakCount; i++)
        {
            GameObject streak = GameObject.CreatePrimitive(PrimitiveType.Quad);
            streak.name = "SnowStreak";
            Destroy(streak.GetComponent<Collider>());
            Transform t = streak.transform;
            t.SetParent(transform, false);
            // Lying flat just above the floor, long along the slope.
            t.localRotation = Quaternion.Euler(90f, 0f, 0f);
            t.localScale = new Vector3(Random.Range(0.06f, 0.12f), Random.Range(1.2f, 2.6f), 1f);
            t.localPosition = new Vector3(Random.Range(-streakHalfWidth, streakHalfWidth), 0.03f,
                bottomZ + span * (i + Random.value) / Mathf.Max(1, streakCount));
            ApplyStreakMaterial(streak.GetComponent<Renderer>());
            streaks.Add(t);
        }
    }

    private void ApplyStreakMaterial(Renderer target)
    {
        if (target == null) return;
        if (streakMaterial == null)
        {
            // URP unlit, transparent. Falls back to the default material if the shader is missing.
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                target.material.color = streakColor;
                return;
            }
            streakMaterial = new Material(shader) { name = "SnowStreak" };
            streakMaterial.SetFloat("_Surface", 1f);   // transparent
            streakMaterial.SetFloat("_Blend", 0f);     // alpha
            streakMaterial.SetFloat("_ZWrite", 0f);
            streakMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            streakMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            streakMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            streakMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            streakMaterial.SetColor("_BaseColor", streakColor);
        }
        target.sharedMaterial = streakMaterial;
    }

    private void BuildSlope()
    {
        GameObject slope = GameObject.CreatePrimitive(PrimitiveType.Quad);
        slope.name = "MountainSlope";
        Destroy(slope.GetComponent<Collider>());
        slope.transform.SetParent(transform, false);
        slope.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        slope.transform.localPosition = new Vector3(0f, -0.4f, 0f);
        slope.transform.localScale = new Vector3(80f, 60f, 1f);
        Renderer r = slope.GetComponent<Renderer>();
        if (r != null) r.material.color = slopeColor;
    }

    private void OnDestroy()
    {
        if (streakMaterial != null) Destroy(streakMaterial);
    }

    // World-space gauge beside the arena (so it is also in the video the web players see).
    private void BuildGauge()
    {
        gaugeRoot = new GameObject("HeightGauge").transform;
        gaugeRoot.SetParent(transform, false);
        gaugeRoot.localPosition = new Vector3(gaugeX, 0.05f, 0f);
        GaugePart("Back", new Vector3(0f, 0f, 0f), new Vector3(0.7f, gaugeLength + 0.4f, 1f), new Color(0.1f, 0.12f, 0.18f));
        gaugeFill = GaugePart("Fill", Vector3.zero, new Vector3(0.45f, 0.01f, 1f), new Color(0.55f, 0.85f, 1f));
        gaugeMarker = GaugePart("Climber", Vector3.zero, new Vector3(1.1f, 0.35f, 1f), new Color(1f, 0.85f, 0.2f));
        GaugePart("Summit", new Vector3(0f, 0.02f, gaugeLength * 0.5f), new Vector3(1.3f, 0.25f, 1f), Color.white);
        UpdateGauge();
    }

    private Transform GaugePart(string name, Vector3 position, Vector3 scale, Color color)
    {
        GameObject part = GameObject.CreatePrimitive(PrimitiveType.Quad);
        part.name = name;
        Destroy(part.GetComponent<Collider>());
        Transform t = part.transform;
        t.SetParent(gaugeRoot, false);
        t.localRotation = Quaternion.Euler(90f, 0f, 0f);
        t.localPosition = position;
        t.localScale = scale;
        Renderer r = part.GetComponent<Renderer>();
        if (r != null) r.material.color = color;
        return t;
    }

    private void UpdateGauge()
    {
        if (gaugeRoot == null) return;
        float bottom = -gaugeLength * 0.5f;
        float filled = Mathf.Max(0.01f, gaugeLength * progress);
        gaugeFill.localScale = new Vector3(0.45f, filled, 1f);
        gaugeFill.localPosition = new Vector3(0f, 0.01f, bottom + filled * 0.5f);
        gaugeMarker.localPosition = new Vector3(0f, 0.03f, bottom + gaugeLength * progress);
    }
}
