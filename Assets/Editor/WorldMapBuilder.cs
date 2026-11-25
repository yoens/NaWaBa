using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Menu: NaWaBa > 지도 만들기 (WorldMap)
// Creates (or opens) Assets/Scenes/WorldMap.unity, adds it to the Build Profile scene list
// after the Lobby, and builds the world map diorama as editable scene objects:
//   "WorldMap (built)"      - floating islands on a sea of clouds, one per region:
//                             0 구름성 (castle kit), 1 무덤 (graveyard kit), 2 숲 (nature kit),
//                             3 도시 (city kit), 4 우주 (space kit), 5 ??? (teaser).
//                             Regions 2-4 are built from the KitIslands table below: adding a
//                             region = one more Spot + one more KitIsland (+ NaWaBaStages.regions).
//                             Each region has a WorldMapRegion, a click box, a LockCover
//                             (clouds while locked) and a Path of stones from the previous one.
//   "WorldMap (controller)" - WorldMapController (kept when rebuilding: its Inspector values stay).
// Everything can be moved afterwards. Running it again rebuilds "WorldMap (built)" only.
public static class WorldMapBuilder
{
    private const string ScenePath = "Assets/Scenes/WorldMap.unity";
    private const string RootName = "WorldMap (built)";
    private const string ControllerName = "WorldMap (controller)";
    private const string GraveyardFolder = "Assets/Prefabs/graveyard";
    private const string UndoName = "Build World Map";

    private struct RegionSpot
    {
        public string name;
        public Vector3 center;
        public float radius;
    }

    // Index = region index in NaWaBaStages.regions (an index past that list stays a locked teaser).
    private static readonly RegionSpot[] Spots =
    {
        new RegionSpot { name = "구름성", center = new Vector3(0f, 0f, 0f), radius = 5f },
        new RegionSpot { name = "무덤", center = new Vector3(14f, 0f, 5f), radius = 5f },
        new RegionSpot { name = "숲", center = new Vector3(27f, 0f, 0f), radius = 5f },
        new RegionSpot { name = "도시", center = new Vector3(40f, 0f, 6f), radius = 5f },
        new RegionSpot { name = "우주", center = new Vector3(53f, 2.5f, 1f), radius = 5f },
        new RegionSpot { name = "???", center = new Vector3(65f, 0f, 6f), radius = 3.5f },
    };

    // A kit model on an island: name in the kit folder, bottom position on the island, yaw, size
    // relative to the island's kit scale.
    private struct KitProp
    {
        public string name;
        public Vector3 at;
        public float yaw, size;

        public KitProp(string name, float x, float z, float yaw = 0f, float size = 1f, float y = 0f)
        {
            this.name = name; at = new Vector3(x, y, z); this.yaw = yaw; this.size = size;
        }
    }

    // One kit scale per island (from a reference model's height) so pieces stay in proportion.
    private struct KitIsland
    {
        public int region;
        public string folder, reference;
        public float referenceHeight;
        public string topMaterial;
        public Color topColor;
        public KitProp[] props;
    }

    private static readonly KitIsland[] KitIslands =
    {
        new KitIsland
        {
            region = 2, folder = "Assets/Prefabs/nature", reference = "tree_pineTallA", referenceHeight = 3.6f,
            topMaterial = "MapForestGround", topColor = new Color(0.33f, 0.55f, 0.3f),
            props = new[]
            {
                new KitProp("tree_pineTallA", -2.8f, 2.2f), new KitProp("tree_pineTallB", -1.2f, 3.2f, 40f),
                new KitProp("tree_oak", 2.6f, 2.4f, 10f), new KitProp("tree_default", 3.4f, 0.2f, 70f, 0.9f),
                new KitProp("tree_pineRoundA", -3.6f, -0.6f, 0f, 0.8f), new KitProp("tree_fat", 0.6f, 3.4f, 120f, 0.8f),
                new KitProp("tent_detailedOpen", -0.6f, 0.6f, 160f), new KitProp("campfire_stones", 0.9f, -0.6f),
                new KitProp("log_stack", 2.2f, -1.6f, 30f), new KitProp("rock_largeA", -2.6f, -2.6f, 20f),
                new KitProp("mushroom_redGroup", 1.6f, -3.0f), new KitProp("stump_round", -1.4f, -1.8f),
                new KitProp("flower_yellowA", 0.2f, -2.6f), new KitProp("flower_redA", -0.8f, -3.2f),
                new KitProp("plant_bushLarge", 3.6f, -2.2f),
            },
        },
        new KitIsland
        {
            region = 3, folder = "Assets/Prefabs/city", reference = "building-a", referenceHeight = 2.2f,
            topMaterial = "MapCityGround", topColor = new Color(0.55f, 0.56f, 0.6f),
            props = new[]
            {
                new KitProp("building-skyscraper-a", -1.6f, 2.4f, 180f), new KitProp("building-skyscraper-c", 1.0f, 2.8f, 180f),
                new KitProp("building-skyscraper-b", 3.0f, 1.2f, 210f, 0.9f), new KitProp("building-a", -3.4f, 0.6f, 150f),
                new KitProp("building-d", -1.4f, -0.2f, 180f), new KitProp("building-f", 1.2f, -0.4f, 180f),
                new KitProp("building-h", 3.4f, -1.6f, 240f), new KitProp("building-c", -3.0f, -2.2f, 120f),
                new KitProp("detail-parasol-a", -0.2f, -2.6f), new KitProp("detail-parasol-b", 1.0f, -2.9f),
            },
        },
        new KitIsland
        {
            region = 4, folder = "Assets/Prefabs/space", reference = "hangar_roundGlass", referenceHeight = 1.8f,
            topMaterial = "MapSpaceGround", topColor = new Color(0.36f, 0.32f, 0.45f),
            props = new[]
            {
                new KitProp("hangar_roundGlass", -1.6f, 1.6f, 180f), new KitProp("rocket_baseA", 2.2f, 2.0f),
                new KitProp("rocket_fuelA", 2.2f, 2.0f, 0f, 1f, -1f), new KitProp("rocket_topA", 2.2f, 2.0f, 0f, 1f, -2f),
                new KitProp("satelliteDish_large", -3.4f, -0.8f, 60f), new KitProp("rover", 0.6f, -1.2f, 210f),
                new KitProp("crater", -0.8f, -2.8f), new KitProp("rock_crystalsLargeA", 3.4f, -1.8f, 30f),
                new KitProp("rock_crystals", -3.0f, 2.8f), new KitProp("astronautA", 1.6f, -2.6f, 200f),
                new KitProp("alien", -2.0f, -2.0f, 160f), new KitProp("machine_wireless", 3.6f, 0.4f, 270f),
            },
        },
    };

    [MenuItem("NaWaBa/지도 만들기 (WorldMap)")]
    private static void Build()
    {
        if (EditorApplication.isPlaying) return;
        if (!OpenOrCreateScene()) return;
        var scene = EditorSceneManager.GetActiveScene();

        GameObject oldRoot = Find(scene, RootName);
        if (oldRoot != null &&
            !EditorUtility.DisplayDialog("NaWaBa · 지도",
                "이미 만들어진 지도가 있습니다. 다시 만들면 \"" + RootName + "\" 안에서 직접 수정한 내용이 사라집니다.\n" +
                "(Ctrl+Z로 되돌릴 수 있음)", "다시 만들기", "취소"))
            return;

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(UndoName);
        if (oldRoot != null) Undo.DestroyObjectImmediate(oldRoot);

        Materials m = Materials.Load();
        var root = New(RootName, null);
        Transform t = root.transform;

        CloudSea(t, m);
        var regions = new List<WorldMapRegion>();
        for (int i = 0; i < Spots.Length; i++)
        {
            WorldMapRegion region = BuildRegion(t, i, m);
            regions.Add(region);
        }
        GameObject marker = Prim(PrimitiveType.Cube, "SelectMarker", t, Vector3.up * 6f, Vector3.one * 0.6f, m.marker);
        marker.transform.localRotation = Quaternion.Euler(0f, 0f, 45f);

        Camera camera = SetUpCameraAndLight(scene, m);
        SetUpController(scene, camera, marker.transform);

        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Selection.activeGameObject = root;
        Debug.Log($"[WorldMap] built {regions.Count} regions in {ScenePath} (saved).");
        EditorUtility.DisplayDialog("NaWaBa · 지도",
            "지도 씬을 만들고 저장했습니다.\n" + ScenePath + "\n\n" +
            "· 지역 이름/설명: Resources/NaWaBaStages 의 Regions\n" +
            "· 섬·모델은 자유롭게 옮겨도 됩니다(다시 만들기 전까지 유지).", "OK");
    }

    // ---------- scene ----------

    private static bool OpenOrCreateScene()
    {
        var active = EditorSceneManager.GetActiveScene();
        if (active.path != ScenePath)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;
            if (System.IO.File.Exists(ScenePath))
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            else
            {
                var created = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                if (!EditorSceneManager.SaveScene(created, ScenePath))
                {
                    EditorUtility.DisplayDialog("NaWaBa", ScenePath + " 저장에 실패했습니다.", "OK");
                    return false;
                }
            }
        }
        AddToBuildProfile();
        return true;
    }

    // Right after the Lobby (the lobby's START opens the map).
    private static void AddToBuildProfile()
    {
        var scenes = EditorBuildSettings.scenes.ToList();
        if (scenes.Any(s => s.path == ScenePath)) return;
        int lobby = scenes.FindIndex(s => s.path.EndsWith("/Lobby.unity"));
        var entry = new EditorBuildSettingsScene(ScenePath, true);
        if (lobby >= 0) scenes.Insert(lobby + 1, entry);
        else scenes.Add(entry);
        EditorBuildSettings.scenes = scenes.ToArray();
        Debug.Log("[WorldMap] added to the Build Profile scene list: " + ScenePath);
    }

    private static Camera SetUpCameraAndLight(UnityEngine.SceneManagement.Scene scene, Materials m)
    {
        Camera camera = null;
        Light sun = null;
        foreach (GameObject go in scene.GetRootGameObjects())
        {
            if (camera == null) camera = go.GetComponentInChildren<Camera>();
            if (sun == null)
            {
                Light light = go.GetComponentInChildren<Light>();
                if (light != null && light.type == LightType.Directional) sun = light;
            }
        }
        if (camera == null)
        {
            var go = New("Main Camera", null);
            go.tag = "MainCamera";
            camera = Undo.AddComponent<Camera>(go);
        }
        Undo.RecordObject(camera, UndoName);
        Undo.RecordObject(camera.transform, UndoName);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.55f, 0.75f, 0.95f);
        camera.farClipPlane = 300f;
        camera.transform.position = new Vector3(0f, 9f, -12f);
        camera.transform.LookAt(new Vector3(0f, 1.2f, 0f));

        if (sun == null)
        {
            var go = New("Directional Light", null);
            sun = Undo.AddComponent<Light>(go);
            sun.type = LightType.Directional;
        }
        Undo.RecordObject(sun.transform, UndoName);
        sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        return camera;
    }

    private static void SetUpController(UnityEngine.SceneManagement.Scene scene, Camera camera, Transform marker)
    {
        WorldMapController controller = null;
        foreach (GameObject go in scene.GetRootGameObjects())
            if (controller == null) controller = go.GetComponentInChildren<WorldMapController>();
        if (controller == null)
            controller = Undo.AddComponent<WorldMapController>(New(ControllerName, null));
        var so = new SerializedObject(controller);
        so.FindProperty("mapCamera").objectReferenceValue = camera;
        so.FindProperty("selectMarker").objectReferenceValue = marker;
        so.ApplyModifiedProperties();
    }

    // ---------- diorama ----------

    private static void CloudSea(Transform parent, Materials m)
    {
        var sea = Prim(PrimitiveType.Quad, "CloudSea (visual)", parent, new Vector3(32f, -2.2f, 3f),
            new Vector3(200f, 160f, 1f), m.cloud);
        sea.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        var random = new System.Random(7);
        var puffs = New("CloudPuffs (visual)", parent);
        for (int i = 0; i < 46; i++)
        {
            float x = -14f + (float)random.NextDouble() * 92f;
            float z = -10f + (float)random.NextDouble() * 30f;
            float s = 1.5f + (float)random.NextDouble() * 2.5f;
            Prim(PrimitiveType.Sphere, "Puff", puffs.transform, new Vector3(x, -1.8f, z),
                new Vector3(s * 1.6f, s * 0.5f, s), m.cloud);
        }
    }

    private static WorldMapRegion BuildRegion(Transform parent, int index, Materials m)
    {
        RegionSpot spot = Spots[index];
        var go = New($"Region {index} - {spot.name}", parent);
        go.transform.localPosition = spot.center;
        Transform t = go.transform;

        int kitIsland = System.Array.FindIndex(KitIslands, k => k.region == index);
        Material top = index == 0 ? m.snow : index == 1 ? m.graveGround
            : kitIsland >= 0 ? ClimbCourseBuilder.MaterialAsset(KitIslands[kitIsland].topMaterial, KitIslands[kitIsland].topColor)
            : m.rock;
        Island(t, spot.radius, top, m.rock);
        if (index == 0) CastleModels(t, m);
        else if (index == 1) GraveyardModels(t, m);
        else if (kitIsland >= 0) KitModels(t, KitIslands[kitIsland], m);

        // Click box (Default layer so Physics.Raycast hits it).
        BoxCollider box = Undo.AddComponent<BoxCollider>(go);
        box.center = new Vector3(0f, 2f, 0f);
        box.size = new Vector3(spot.radius * 2f, 5f, spot.radius * 2f);

        GameObject cover = LockCover(t, spot.radius, m);
        GameObject path = index > 0 ? PathStones(t, Spots[index - 1], spot, m) : null;

        WorldMapRegion region = Undo.AddComponent<WorldMapRegion>(go);
        var so = new SerializedObject(region);
        so.FindProperty("regionIndex").intValue = index;
        so.FindProperty("fallbackTitle").stringValue = spot.name;
        so.FindProperty("lockCover").objectReferenceValue = cover;
        so.FindProperty("path").objectReferenceValue = path;
        so.ApplyModifiedProperties();
        return region;
    }

    // Floating island: flat top disc + a narrower rock body under it.
    private static void Island(Transform parent, float radius, Material top, Material rock)
    {
        Prim(PrimitiveType.Cylinder, "IslandTop", parent, new Vector3(0f, -0.25f, 0f),
            new Vector3(radius * 2f, 0.25f, radius * 2f), top);
        Prim(PrimitiveType.Cylinder, "IslandRock", parent, new Vector3(0f, -1.3f, 0f),
            new Vector3(radius * 1.6f, 0.9f, radius * 1.6f), rock);
        Prim(PrimitiveType.Sphere, "IslandTip", parent, new Vector3(0f, -2.4f, 0f),
            new Vector3(radius * 1.1f, 1.6f, radius * 1.1f), rock);
    }

    private static GameObject LockCover(Transform parent, float radius, Materials m)
    {
        var cover = New("LockCover", parent);
        Vector3[] spots =
        {
            new Vector3(0f, 1.6f, 0f), new Vector3(-0.55f, 1.1f, -0.4f), new Vector3(0.6f, 1.2f, -0.3f),
            new Vector3(-0.4f, 1.3f, 0.55f), new Vector3(0.5f, 1.0f, 0.5f), new Vector3(0f, 2.6f, 0.1f),
            new Vector3(0f, 0.9f, -0.75f),
        };
        foreach (Vector3 p in spots)
        {
            float s = radius * (p.y > 2f ? 0.9f : 1.15f);
            Prim(PrimitiveType.Sphere, "Fog", cover.transform, new Vector3(p.x * radius, p.y, p.z * radius),
                new Vector3(s, s * 0.7f, s), m.fog);
        }
        return cover;
    }

    // Stones from the edge of the previous island to the edge of this one (parent = this region).
    private static GameObject PathStones(Transform region, RegionSpot from, RegionSpot to, Materials m)
    {
        var path = New("Path", region);
        Vector3 a = from.center, b = to.center;
        Vector3 dir = (b - a).normalized;
        Vector3 start = a + dir * (from.radius - 0.6f), end = b - dir * (to.radius - 0.6f);
        float length = Vector3.Distance(start, end);
        int count = Mathf.Max(2, Mathf.RoundToInt(length / 1.1f));
        Vector3 side = Vector3.Cross(Vector3.up, dir);
        for (int i = 0; i <= count; i++)
        {
            float k = i / (float)count;
            Vector3 world = Vector3.Lerp(start, end, k) + side * Mathf.Sin(k * Mathf.PI) * 1.2f;
            world.y = Mathf.Lerp(a.y, b.y, k) - 0.15f - Mathf.Sin(k * Mathf.PI) * 0.4f;
            Prim(PrimitiveType.Cylinder, $"Stone {i}", path.transform, world - to.center,
                new Vector3(0.7f, 0.06f, 0.7f), m.path);
        }
        return path;
    }

    private static void CastleModels(Transform t, Materials m)
    {
        if (!ClimbCourseBuilder.HasCastleKit)
        {
            Prim(PrimitiveType.Cube, "Keep (placeholder)", t, new Vector3(0f, 1.5f, 1f), new Vector3(2f, 3f, 2f), m.rock);
            Debug.LogWarning("[WorldMap] Castle kit not found: placeholder box used.");
            return;
        }
        // Scale so the main tower is about 4.5 m (measured, not hard-coded).
        string[] tower = { "tower-hexagon-base", "tower-hexagon-mid", "tower-hexagon-roof" };
        float towerHeight = tower.Sum(n => ClimbCourseBuilder.KitSize(n).y);
        float scale = 4.5f / Mathf.Max(0.01f, towerHeight);
        var castle = New("Castle", t);
        Transform c = castle.transform;
        ClimbCourseBuilder.PlaceKitStack(c, new Vector3(0f, 0f, 1.2f), 0f, scale, tower);

        // Front wall across X (castle kit pieces run along local Z, so yaw 90).
        float cell = ClimbCourseBuilder.KitSize("wall").z * scale;
        for (int i = -2; i <= 2; i++)
        {
            string piece = i == 0 ? "wall-doorway" : "wall";
            ClimbCourseBuilder.PlaceKit(c, piece, new Vector3(i * cell, 0f, -1.2f), 90f, scale);
        }
        ClimbCourseBuilder.PlaceKit(c, "gate", new Vector3(0f, 0f, -1.2f), 90f, scale);
        float side = 2.5f * cell + 0.2f;
        foreach (float x in new[] { -side, side })
            ClimbCourseBuilder.PlaceKitStack(c, new Vector3(x, 0f, -1.2f), 0f, scale * 0.8f,
                "tower-square-base", "tower-square-mid", "tower-square-roof");

        ClimbCourseBuilder.PlaceKit(t, "tree-large", new Vector3(-3.3f, 0f, 1.8f), 20f, scale);
        ClimbCourseBuilder.PlaceKit(t, "tree-small", new Vector3(3.4f, 0f, 2.2f), 70f, scale);
        ClimbCourseBuilder.PlaceKit(t, "tree-small", new Vector3(-3.6f, 0f, -2.6f), 140f, scale);
        // Snowy hills behind the castle (the climb up from stage 1).
        Prim(PrimitiveType.Sphere, "SnowHill", t, new Vector3(-2.2f, 0f, 3.6f), new Vector3(4.5f, 3.2f, 3f), m.snow);
        Prim(PrimitiveType.Sphere, "SnowHill", t, new Vector3(2.4f, 0f, 3.8f), new Vector3(3.5f, 2.2f, 2.6f), m.snow);
    }

    private static void GraveyardModels(Transform t, Materials m)
    {
        GameObject crypt = Grave("crypt");
        if (crypt == null)
        {
            Prim(PrimitiveType.Cube, "Crypt (placeholder)", t, new Vector3(0f, 1f, 1.5f), new Vector3(2.4f, 2f, 2f), m.rock);
            Debug.LogWarning("[WorldMap] graveyard kit not found in " + GraveyardFolder + ": placeholder box used.");
            return;
        }
        // One scale for the whole kit so pieces stay in proportion: crypt about 2.6 m tall.
        float scale = 2.6f / Mathf.Max(0.01f, Measure(crypt).size.y);
        var g = New("Graveyard", t).transform;
        Place(g, "crypt", new Vector3(0f, 0f, 1.8f), 180f, scale);

        string[] stones = { "gravestone-cross", "gravestone-round", "gravestone-broken", "gravestone-wide", "gravestone-decorative" };
        var random = new System.Random(3);
        int n = 0;
        for (int row = 0; row < 2; row++)
            for (int col = -2; col <= 2; col++)
            {
                if (row == 0 && col == 0) continue;   // keep the walkway to the crypt open
                var p = new Vector3(col * 1.3f, 0f, -0.4f - row * 1.4f);
                Place(g, stones[n++ % stones.Length], p, 180f + (float)(random.NextDouble() * 20 - 10), scale);
            }

        // Iron fence along the front edge with a gate in the middle.
        GameObject fence = Grave("iron-fence");
        float fenceLength = fence != null ? Mathf.Max(Measure(fence).size.x, Measure(fence).size.z) * scale : 1f;
        for (int i = -2; i <= 2; i++)
            Place(g, i == 0 ? "iron-fence-border-gate" : "iron-fence", new Vector3(i * fenceLength, 0f, -3.6f), 0f, scale);

        Place(g, "pine", new Vector3(-3.4f, 0f, 2.2f), 0f, scale);
        Place(g, "pine-crooked", new Vector3(3.3f, 0f, 1.6f), 40f, scale);
        Place(g, "pine", new Vector3(3.0f, 0f, -1.8f), 90f, scale * 0.8f);
        Place(g, "lantern-candle", new Vector3(-1.0f, 0f, 0.6f), 0f, scale);
        Place(g, "fire-basket", new Vector3(1.0f, 0f, 0.6f), 0f, scale);
        Place(g, "pumpkin-carved", new Vector3(-2.8f, 0f, -2.8f), 200f, scale);
        GameObject ghost = Place(g, "character-ghost", new Vector3(1.9f, 1.2f, -1.2f), 210f, scale);
        if (ghost != null) ghost.name = "Ghost (hint)";
    }

    // Models of a kit island (regions 2+). A prop with y < 0 in the table is stacked on top of the
    // previous prop at the same x/z (e.g. the rocket: base, fuel, top).
    private static void KitModels(Transform t, KitIsland island, Materials m)
    {
        GameObject reference = Kit(island.folder, island.reference);
        if (reference == null)
        {
            Prim(PrimitiveType.Cube, "Region model (placeholder)", t, new Vector3(0f, 1f, 1f), new Vector3(2f, 2f, 2f), m.rock);
            Debug.LogWarning($"[WorldMap] {island.folder}/{island.reference} not found: placeholder box used.");
            return;
        }
        float scale = island.referenceHeight / Mathf.Max(0.01f, Measure(reference).size.y);
        var group = New(Spots[island.region].name, t).transform;
        var stackTop = new Dictionary<Vector2, float>();
        foreach (KitProp prop in island.props)
        {
            GameObject prefab = Kit(island.folder, prop.name);
            if (prefab == null) continue;
            var key = new Vector2(prop.at.x, prop.at.z);
            float y = prop.at.y < 0f && stackTop.TryGetValue(key, out float h) ? h : 0f;
            float size = scale * prop.size;
            PlaceKit(group, prefab, new Vector3(prop.at.x, y, prop.at.z), prop.yaw, size);
            stackTop[key] = y + Measure(prefab).size.y * size;
        }
    }

    // ---------- graveyard kit ----------

    private static readonly Dictionary<string, GameObject> kitCache = new Dictionary<string, GameObject>();

    private static GameObject Grave(string exactName) => Kit(GraveyardFolder, exactName);

    // Prefab by exact file name in a kit folder (cached; warns once when missing).
    private static GameObject Kit(string folder, string exactName)
    {
        string key = folder + "/" + exactName;
        if (kitCache.TryGetValue(key, out GameObject cached) && cached != null) return cached;
        GameObject found = null;
        if (AssetDatabase.IsValidFolder(folder))
            foreach (string guid in AssetDatabase.FindAssets(exactName + " t:Prefab", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) != exactName) continue;
                found = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                break;
            }
        if (found == null && !kitCache.ContainsKey(key)) Debug.LogWarning("[WorldMap] prefab not found: " + key);
        kitCache[key] = found;
        return found;
    }

    // Bottom-centre of the model's meshes at "bottom" (parent space). Decoration only: no collider.
    private static GameObject Place(Transform parent, string name, Vector3 bottom, float yaw, float scale)
    {
        GameObject prefab = Grave(name);
        return prefab != null ? PlaceKit(parent, prefab, bottom, yaw, scale) : null;
    }

    private static GameObject PlaceKit(Transform parent, GameObject prefab, Vector3 bottom, float yaw, float scale)
    {
        Bounds b = Measure(prefab);
        var piece = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.gameObject.scene);
        Undo.RegisterCreatedObjectUndo(piece, UndoName);
        piece.transform.SetParent(parent, false);
        piece.transform.localScale = piece.transform.localScale * scale;
        Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
        piece.transform.localRotation = rotation;
        piece.transform.localPosition = bottom - rotation * (new Vector3(b.center.x, b.min.y, b.center.z) * scale);
        return piece;
    }

    private static Bounds Measure(GameObject prefab)
    {
        var probe = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        probe.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        Renderer[] renderers = probe.GetComponentsInChildren<Renderer>();
        Bounds b = renderers.Length > 0 ? renderers[0].bounds : new Bounds(Vector3.up * 0.5f, Vector3.one);
        foreach (Renderer r in renderers) b.Encapsulate(r.bounds);
        Object.DestroyImmediate(probe);
        return b;
    }

    // ---------- helpers ----------

    private sealed class Materials
    {
        public Material cloud, fog, snow, rock, graveGround, path, marker;

        public static Materials Load() => new Materials
        {
            cloud = ClimbCourseBuilder.MaterialAsset("MapCloud", new Color(0.97f, 0.98f, 1f)),
            fog = ClimbCourseBuilder.MaterialAsset("MapFog", new Color(0.78f, 0.82f, 0.9f)),
            snow = ClimbCourseBuilder.MaterialAsset("MapSnow", new Color(0.9f, 0.94f, 1f)),
            rock = ClimbCourseBuilder.MaterialAsset("MapRock", new Color(0.45f, 0.43f, 0.42f)),
            graveGround = ClimbCourseBuilder.MaterialAsset("MapGraveGround", new Color(0.28f, 0.33f, 0.27f)),
            path = ClimbCourseBuilder.MaterialAsset("MapPath", new Color(0.72f, 0.62f, 0.45f)),
            marker = ClimbCourseBuilder.MaterialAsset("MapMarker", new Color(1f, 0.82f, 0.2f)),
        };
    }

    // Primitive with a material asset (the default primitive material turns purple in URP builds).
    private static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 localPosition,
        Vector3 localScale, Material material)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        Undo.RegisterCreatedObjectUndo(go, UndoName);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localScale = localScale;
        if (material != null) go.GetComponent<Renderer>().sharedMaterial = material;
        return go;
    }

    private static GameObject New(string name, Transform parent)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, UndoName);
        if (parent != null) go.transform.SetParent(parent, false);
        return go;
    }

    private static GameObject Find(UnityEngine.SceneManagement.Scene scene, string name)
    {
        foreach (GameObject go in scene.GetRootGameObjects())
            if (go.name == name) return go;
        return null;
    }
}
