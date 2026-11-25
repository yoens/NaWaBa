using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Menu: NaWaBa > 무덤 지하 갈림길 만들기 (Stage_Crypt)
// Region 2 (무덤), game 2: the crypt descent (remember the arrows, pick left/right at every fork).
// First run: copies Stage_Grave.unity (or Stage_Gate.unity) to Stage_Crypt.unity, swaps the stage
// controller for CryptRunStageController, removes the old course, makes the scene dark (fog,
// ambient), adds it to the Build Profile (after Stage_Grave) and to NaWaBaStages (region 1,
// after Stage_Grave).
// Every run: (re)builds "Crypt (built)" from the cave kit (Assets/Prefabs/cave) so that the
// player stands in the start room. The running path comes from CryptCourse (same numbers);
// the kit pieces are turned by measuring which of their sides are open, not by guessed angles.
public static class CryptDescentBuilder
{
    private const string ScenePath = "Assets/Scenes/Stage_Crypt.unity";
    private static readonly string[] SourceScenes = { "Assets/Scenes/Stage_Grave.unity", "Assets/Scenes/Stage_Gate.unity" };
    private const string StageListPath = "Assets/Resources/NaWaBaStages.asset";
    private const string SceneName = "Stage_Crypt";
    private const string AfterScene = "Stage_Grave";
    private const string StageTitle = "무덤 2 · 지하 갈림길";
    private const int Region = 1;
    private const string RootName = "Crypt (built)";
    private const string CaveFolder = "Assets/Prefabs/cave";
    private const string GraveyardFolder = "Assets/Prefabs/graveyard";
    private const string UndoName = "Build Crypt";
    private const int ForkCount = 8;

    private static readonly Vector3[] Sides = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };

    private static float tile = 4f, drop = 6f, propScale = 1f;

    [MenuItem("NaWaBa/무덤 지하 갈림길 만들기 (Stage_Crypt)")]
    private static void Build()
    {
        if (EditorApplication.isPlaying) return;
        if (!OpenOrCreateScene()) return;
        var scene = EditorSceneManager.GetActiveScene();

        CryptRunStageController stage = Object.FindFirstObjectByType<CryptRunStageController>();
        ClimbActorController actor = Object.FindFirstObjectByType<ClimbActorController>();
        if (stage == null || actor == null)
        {
            EditorUtility.DisplayDialog("NaWaBa", "Stage_Crypt 씬에 CryptRunStageController와 플레이어가 필요합니다.", "OK");
            return;
        }
        string[] needed = { "corridor", "corridor-junction", "corridor-corner", "corridor-end", "room-small", "room-large", "gate-metal-bars" };
        string missing = string.Join(", ", needed.Where(n => Kit(CaveFolder, n) == null));
        if (missing.Length > 0)
        {
            EditorUtility.DisplayDialog("NaWaBa", CaveFolder + " 에서 cave 프리팹을 찾지 못했습니다: " + missing, "OK");
            return;
        }

        GameObject old = Find(scene, RootName);
        if (old != null && !EditorUtility.DisplayDialog("NaWaBa · 무덤 지하",
                "이미 만들어진 지하 갈림길이 있습니다. 다시 만들면 \"" + RootName + "\" 안에서 직접 수정한 내용이 사라집니다.\n" +
                "(Ctrl+Z로 되돌릴 수 있음)", "다시 만들기", "취소"))
            return;

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(UndoName);
        if (old != null) Undo.DestroyObjectImmediate(old);
        cache.Clear();
        openCache.Clear();
        boundsCache.Clear();

        Bounds corridor = Measure(Kit(CaveFolder, "corridor"));
        tile = Mathf.Max(1f, Mathf.Round(Mathf.Max(corridor.size.x, corridor.size.z) * 100f) / 100f);
        drop = tile * 1.5f;
        GameObject gravestone = Kit(GraveyardFolder, "gravestone-cross");
        propScale = gravestone != null ? 1.2f / Mathf.Max(0.01f, Measure(gravestone).size.y) : 1f;

        var root = New(RootName, null);
        Quaternion yaw = Quaternion.Euler(0f, actor.transform.eulerAngles.y, 0f);
        Vector3 start = new Vector3(0f, 0f, -3f * tile);
        root.transform.SetPositionAndRotation(actor.transform.position - yaw * start, yaw);
        Transform t = root.transform;
        Transform pieces = New("Cave", t).transform;
        Transform gates = New("Gates", t).transform;
        Transform deco = New("Decoration", t).transform;
        Transform lights = New("Lights", t).transform;

        var left = new GameObject[ForkCount];
        var right = new GameObject[ForkCount];
        float gateHalf = 0.7f;
        Vector3 X = Vector3.right, Z = Vector3.forward;

        // Start: a crypt room, then the first stem corridor.
        Room(pieces, deco, "room-small", start, Z, "Start Room");
        Tile(pieces, "corridor", new Vector3(0f, 0f, -tile), Z, -Z);
        StartDecoration(deco, start);
        Lamp(lights, start + Vector3.up * 3f, 9f);

        for (int k = 0; k < ForkCount; k++)
        {
            Vector3 o = new Vector3(0f, -drop * k, 4f * tile * k);
            Vector3 low = o + Vector3.down * drop;
            Transform fork = New($"Fork {k + 1}", pieces).transform;
            Tile(fork, "corridor-junction", o, -Z, X, -X);
            Tile(fork, "corridor-junction", low + Z * (2f * tile), Z, X, -X);     // the two ways merge
            Tile(fork, "corridor", low + Z * (3f * tile), Z, -Z);                  // on to the next fork / end room
            foreach (float s in new[] { -1f, 1f })
            {
                Vector3 side = X * s;
                Tile(fork, "corridor", o + side * tile, X, -X);                    // arm to the drop
                Tile(fork, "corridor-end", low + side * tile, side);               // closes the shaft below the arm
                Tile(fork, "corridor", low + side * (2f * tile), X, -X);           // landing
                Tile(fork, "corridor-corner", low + side * (3f * tile), -side, Z);
                Tile(fork, "corridor", low + side * (3f * tile) + Z * tile, Z, -Z);
                Tile(fork, "corridor-corner", low + side * (3f * tile) + Z * (2f * tile), -Z, -side);
                Tile(fork, "corridor", low + side * (2f * tile) + Z * (2f * tile), X, -X);
                Tile(fork, "corridor", low + side * tile + Z * (2f * tile), X, -X);

                GameObject gate = Gate(gates, $"Gate {k + 1} {(s < 0 ? "L" : "R")}", o + side * tile, out gateHalf);
                if (s < 0) left[k] = gate; else right[k] = gate;

                // A ghost hovering in each shaft: the player drops straight through it.
                Ghost(deco, o + side * (1.95f * tile) + Vector3.down * (drop * 0.45f));
            }
            Lamp(lights, o + Vector3.up * 3f, 8f);
            Lamp(lights, low + Z * (2f * tile) + Vector3.up * 3f, 8f);
        }

        // End: a large cave room under the graveyard (the goal is its centre).
        Vector3 end = new Vector3(0f, -drop * ForkCount, 4f * tile * (ForkCount - 1) + 6f * tile);
        Room(pieces, deco, "room-large", end, -Z, "End Room");
        EndDecoration(deco, end);
        Lamp(lights, end + Vector3.up * 3.5f, 16f);

        var course = Undo.AddComponent<CryptCourse>(root);
        course.Configure(tile, drop, ForkCount, left, right, gateHalf);
        EditorUtility.SetDirty(course);
        foreach (GameObject gate in left.Concat(right)) if (gate != null) gate.SetActive(false);

        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Selection.activeGameObject = root;
        Debug.Log($"[Crypt] built {ForkCount} forks (tile {tile:0.##} m, drop {drop:0.##} m, prop scale {propScale:0.00}). Scene saved.");
        EditorUtility.DisplayDialog("NaWaBa · 무덤 지하",
            "지하 갈림길을 만들고 씬을 저장했습니다.\n" + ScenePath + "\n\n" +
            "· 화살표 표시 시간·달리기 속도: 난이도 에셋의 Crypt Show Time / Crypt Run Speed\n" +
            "· 달리는 경로는 Crypt (built)의 CryptCourse 값으로 정해집니다(루트째 옮기는 것은 괜찮음).", "OK");
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
                string source = SourceScenes.FirstOrDefault(System.IO.File.Exists);
                if (source == null)
                {
                    EditorUtility.DisplayDialog("NaWaBa", "Stage_Grave / Stage_Gate 씬이 없어 새 씬을 만들 수 없습니다.", "OK");
                    return false;
                }
                if (!EditorUtility.DisplayDialog("NaWaBa · 무덤 지하",
                        System.IO.Path.GetFileNameWithoutExtension(source) + " 씬을 복사해 " + ScenePath + " 를 만듭니다.\n" +
                        "(기존 코스는 지우고, 플레이어·카메라·UI·영상 전송은 그대로 씁니다)", "만들기", "취소"))
                    return false;
                if (!AssetDatabase.CopyAsset(source, ScenePath))
                {
                    EditorUtility.DisplayDialog("NaWaBa", ScenePath + " 복사에 실패했습니다.", "OK");
                    return false;
                }
                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Convert(scene);
                EditorSceneManager.SaveScene(scene);
            }
        }
        AddToBuildProfile();
        AddToStageList();
        return true;
    }

    // Old stage controller -> crypt controller (same references); old courses removed; dark cave.
    private static void Convert(UnityEngine.SceneManagement.Scene scene)
    {
        var controllers = new List<MonoBehaviour>();
        controllers.AddRange(Object.FindObjectsByType<GraveStageController>(FindObjectsInactive.Include, FindObjectsSortMode.None));
        controllers.AddRange(Object.FindObjectsByType<GateStageController>(FindObjectsInactive.Include, FindObjectsSortMode.None));
        foreach (MonoBehaviour old in controllers)
        {
            var from = new SerializedObject(old);
            Object actor = from.FindProperty("actor").objectReferenceValue;
            Object router = from.FindProperty("inputRouter").objectReferenceValue;
            Object gateway = from.FindProperty("gateway").objectReferenceValue;
            Object status = from.FindProperty("statusText").objectReferenceValue;
            GameObject host = old.gameObject;
            Object.DestroyImmediate(old);
            if (host.GetComponent<CryptRunStageController>() != null) continue;
            var crypt = host.AddComponent<CryptRunStageController>();
            var to = new SerializedObject(crypt);
            to.FindProperty("actor").objectReferenceValue = actor;
            to.FindProperty("inputRouter").objectReferenceValue = router;
            to.FindProperty("gateway").objectReferenceValue = gateway;
            to.FindProperty("statusText").objectReferenceValue = status;
            to.ApplyModifiedPropertiesWithoutUndo();
        }
        foreach (string name in new[] { "Graveyard (built)", "GatePuzzle (built)", ClimbCourseBuilder.SharedCastleName })
        {
            GameObject go = Find(scene, name);
            if (go != null) Object.DestroyImmediate(go);
        }

        // Underground: dim cold light from above, dark fog that hides the deeper levels.
        foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (light.type == LightType.Directional)
            {
                light.color = new Color(0.55f, 0.6f, 0.85f);
                light.intensity = Mathf.Min(light.intensity, 0.45f);
                light.transform.rotation = Quaternion.Euler(70f, -30f, 0f);
            }
        var fog = new Color(0.03f, 0.03f, 0.05f);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.18f, 0.17f, 0.22f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogDensity = 0.03f;
        RenderSettings.fogColor = fog;
        foreach (ClimbCameraController follow in Object.FindObjectsByType<ClimbCameraController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Camera camera = follow.GetComponent<Camera>();
            if (camera == null) continue;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = fog;
        }

        Debug.Log("[Crypt] Stage_Crypt created. Root objects kept: " +
            string.Join(", ", scene.GetRootGameObjects().Select(g => g.name)) +
            "\nRemove any leftover scenery from the copied stage you do not want.");
    }

    private static void AddToBuildProfile()
    {
        var scenes = EditorBuildSettings.scenes.ToList();
        if (scenes.Any(s => s.path == ScenePath)) return;
        int after = scenes.FindIndex(s => s.path.EndsWith("/" + AfterScene + ".unity"));
        if (after < 0) after = scenes.FindIndex(s => s.path.EndsWith("/Stage_Keep.unity"));
        var entry = new EditorBuildSettingsScene(ScenePath, true);
        if (after >= 0) scenes.Insert(after + 1, entry);
        else scenes.Add(entry);
        EditorBuildSettings.scenes = scenes.ToArray();
        Debug.Log("[Crypt] added to the Build Profile scene list: " + ScenePath);
    }

    private static void AddToStageList()
    {
        var list = AssetDatabase.LoadAssetAtPath<NaWaBaStageList>(StageListPath);
        if (list == null)
        {
            Debug.LogWarning("[Crypt] " + StageListPath + " not found: add Stage_Crypt to the stage list by hand.");
            return;
        }
        if (list.IndexOf(SceneName) >= 0) return;
        Undo.RecordObject(list, "Add Stage_Crypt");
        var entry = new NaWaBaStageList.Stage { sceneName = SceneName, title = StageTitle, region = Region };
        int after = list.IndexOf(AfterScene);
        if (after >= 0) list.stages.Insert(after + 1, entry);
        else list.stages.Add(entry);
        EditorUtility.SetDirty(list);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Crypt] added {SceneName} to NaWaBaStages (region {Region}).");
    }

    // ---------- kit pieces ----------

    // Places a cave piece at "center" (floor height) turned so that exactly the given sides are open.
    private static GameObject Tile(Transform parent, string name, Vector3 center, params Vector3[] open)
    {
        GameObject prefab = Kit(CaveFolder, name);
        if (prefab == null) return null;
        float yaw = YawFor(prefab, open, exact: true);
        return Place(parent, prefab, center, yaw, 1f);
    }

    // A room with "entrance" open; every other opening of the room is shut with a rock gate.
    private static void Room(Transform parent, Transform deco, string name, Vector3 center, Vector3 entrance, string label)
    {
        GameObject prefab = Kit(CaveFolder, name);
        if (prefab == null) return;
        float yaw = YawFor(prefab, new[] { entrance }, exact: false);
        GameObject room = Place(parent, prefab, center, yaw, 1f);
        room.name = label;
        Bounds b = Measure(prefab);
        float half = Mathf.Max(b.extents.x, b.extents.z);
        bool[] open = OpenSides(prefab);
        GameObject rock = Kit(CaveFolder, "gate-rock");
        if (rock == null) return;
        Bounds rb = Measure(rock);
        bool rockAlongX = rb.size.x >= rb.size.z;
        float rockHalf = Mathf.Min(rb.extents.x, rb.extents.z);
        Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
        for (int i = 0; i < Sides.Length; i++)
        {
            if (!open[i]) continue;
            Vector3 dir = Round(turn * Sides[i]);
            if (Vector3.Dot(dir, entrance) > 0.9f) continue;
            bool sideAlongX = Mathf.Abs(dir.z) > 0.5f;    // an opening on a ±Z wall runs along X
            float rockYaw = sideAlongX == rockAlongX ? 0f : 90f;
            Place(deco, rock, center + dir * (half - rockHalf), rockYaw, 1f).name = "Shut " + label;
        }
    }

    private static GameObject Gate(Transform parent, string name, Vector3 center, out float halfThickness)
    {
        GameObject prefab = Kit(CaveFolder, "gate-metal-bars");
        Bounds b = Measure(prefab);
        // Across the arm: the arm runs along X, so the gate's wide side must run along Z.
        float yaw = b.size.x >= b.size.z ? 90f : 0f;
        halfThickness = Mathf.Min(b.extents.x, b.extents.z);
        GameObject gate = Place(parent, prefab, center, yaw, 1f);
        gate.name = name;
        return gate;
    }

    // Finds the yaw (0/90/180/270) at which the piece's open sides match "open"
    // (exact = no other side open; otherwise these sides at least).
    private static float YawFor(GameObject prefab, IList<Vector3> open, bool exact)
    {
        bool[] sides = OpenSides(prefab);
        foreach (float yaw in new[] { 0f, 90f, 180f, 270f })
        {
            Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
            var turned = new List<Vector3>();
            for (int i = 0; i < Sides.Length; i++) if (sides[i]) turned.Add(Round(turn * Sides[i]));
            bool all = open.All(d => turned.Any(v => Vector3.Dot(v, d) > 0.9f));
            if (all && (!exact || turned.Count == open.Count)) return yaw;
        }
        Debug.LogWarning($"[Crypt] no turn of {prefab.name} matches the wanted openings " +
            $"(open sides +X {sides[0]}, -X {sides[1]}, +Z {sides[2]}, -Z {sides[3]}); placed unturned, check it by hand.");
        return 0f;
    }

    private static readonly Dictionary<GameObject, bool[]> openCache = new Dictionary<GameObject, bool[]>();

    // Which of +X, -X, +Z, -Z are passable at the middle of the edge: short horizontal rays from
    // just outside each edge into the piece at body height; hitting a wall within the first
    // ~0.8 m means that side is closed. Back faces count (the rock walls are single-sided and
    // have no top). Kenney pieces have no colliders, so mesh colliders are added to a temporary
    // copy far below the scene.
    private static bool[] OpenSides(GameObject prefab)
    {
        if (openCache.TryGetValue(prefab, out bool[] cached)) return cached;
        var probe = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        probe.transform.SetPositionAndRotation(new Vector3(0f, -5000f, 0f), Quaternion.identity);
        foreach (MeshFilter filter in probe.GetComponentsInChildren<MeshFilter>())
            if (filter.sharedMesh != null) filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
        Physics.SyncTransforms();
        Bounds b = RendererBounds(probe);
        PhysicsScene physics = probe.scene.GetPhysicsScene();
        bool backfaces = Physics.queriesHitBackfaces;
        Physics.queriesHitBackfaces = true;
        var result = new bool[Sides.Length];
        var log = new System.Text.StringBuilder();
        try
        {
            for (int i = 0; i < Sides.Length; i++)
            {
                Vector3 dir = Sides[i];
                Vector3 across = new Vector3(dir.z, 0f, dir.x);
                float extent = Mathf.Abs(dir.x) > 0.5f ? b.extents.x : b.extents.z;
                int walls = 0, rays = 0;
                foreach (float height in new[] { 1.2f, 2.2f })
                    foreach (float lateral in new[] { -0.4f, 0f, 0.4f })
                    {
                        Vector3 origin = new Vector3(b.center.x, b.min.y + height, b.center.z) +
                                         dir * (extent + 0.3f) + across * lateral;
                        rays++;
                        if (physics.Raycast(origin, -dir, out RaycastHit _, 1.1f)) walls++;
                    }
                result[i] = walls * 2 < rays;   // open when fewer than half of the rays hit
                log.Append($" {SideName(i)}:{walls}/{rays}");
            }
        }
        finally
        {
            Physics.queriesHitBackfaces = backfaces;
            Object.DestroyImmediate(probe);
        }
        Debug.Log($"[Crypt] {prefab.name} wall hits per side:{log}");
        openCache[prefab] = result;
        return result;
    }

    private static string SideName(int i) => i == 0 ? "+X" : i == 1 ? "-X" : i == 2 ? "+Z" : "-Z";

    private static Vector3 Round(Vector3 v) => new Vector3(Mathf.Round(v.x), 0f, Mathf.Round(v.z));

    // ---------- decoration ----------

    private static void StartDecoration(Transform deco, Vector3 start)
    {
        Prop(deco, "coffin-old", start + new Vector3(0f, 0f, -tile * 0.9f), 0f, propScale);
        Prop(deco, "candle-multiple", start + new Vector3(-tile * 0.8f, 0f, -tile * 0.8f), 0f, propScale);
        Prop(deco, "candle-multiple", start + new Vector3(tile * 0.8f, 0f, -tile * 0.8f), 0f, propScale);
        Prop(deco, "lantern-candle", start + new Vector3(-tile * 0.6f, 0f, tile * 0.9f), 0f, propScale);
        Prop(deco, "lantern-candle", start + new Vector3(tile * 0.6f, 0f, tile * 0.9f), 0f, propScale);
    }

    private static void EndDecoration(Transform deco, Vector3 end)
    {
        Prop(deco, "fire-basket", end + new Vector3(-tile * 0.9f, 0f, tile * 0.6f), 0f, propScale * 1.3f);
        Prop(deco, "fire-basket", end + new Vector3(tile * 0.9f, 0f, tile * 0.6f), 0f, propScale * 1.3f);
        Prop(deco, "coffin", end + new Vector3(-tile * 1.4f, 0f, tile * 1.5f), 90f, propScale);
        Prop(deco, "coffin", end + new Vector3(tile * 1.4f, 0f, tile * 1.5f), -90f, propScale);
        Prop(deco, "candle-multiple", end + new Vector3(0f, 0f, tile * 1.8f), 0f, propScale);
    }

    private static void Ghost(Transform deco, Vector3 position)
    {
        GameObject prefab = Kit(GraveyardFolder, "character-ghost");
        if (prefab == null) return;
        float scale = 1.3f / Mathf.Max(0.01f, Measure(prefab).size.y);
        GameObject ghost = Place(deco, prefab, position, 0f, scale);
        ghost.name = "Ghost (shaft)";
        Undo.AddComponent<CryptFloat>(ghost);
    }

    private static void Prop(Transform parent, string name, Vector3 bottom, float yaw, float scale)
    {
        GameObject prefab = Kit(GraveyardFolder, name);
        if (prefab != null) Place(parent, prefab, bottom, yaw, scale);
    }

    private static void Lamp(Transform parent, Vector3 position, float range)
    {
        var go = New("Lamp", parent);
        go.transform.localPosition = position;
        Light light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.68f, 0.35f);
        light.range = range;
        light.intensity = 2.2f;
        light.shadows = LightShadows.None;
    }

    // ---------- helpers ----------

    private static readonly Dictionary<string, GameObject> cache = new Dictionary<string, GameObject>();

    private static GameObject Kit(string folder, string exactName)
    {
        string key = folder + "/" + exactName;
        if (cache.TryGetValue(key, out GameObject cached) && cached != null) return cached;
        GameObject found = null;
        if (AssetDatabase.IsValidFolder(folder))
            foreach (string guid in AssetDatabase.FindAssets(exactName + " t:Prefab", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) != exactName) continue;
                found = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                break;
            }
        if (found == null) Debug.LogWarning("[Crypt] prefab not found: " + key);
        cache[key] = found;
        return found;
    }

    // Kit model with its meshes' bottom-centre at "bottom" (local to parent's root space).
    private static GameObject Place(Transform parent, GameObject prefab, Vector3 bottom, float yaw, float scale)
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

    private static readonly Dictionary<GameObject, Bounds> boundsCache = new Dictionary<GameObject, Bounds>();

    private static Bounds Measure(GameObject prefab)
    {
        if (boundsCache.TryGetValue(prefab, out Bounds known)) return known;
        var probe = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        probe.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        Bounds b = RendererBounds(probe);
        Object.DestroyImmediate(probe);
        boundsCache[prefab] = b;
        return b;
    }

    private static Bounds RendererBounds(GameObject go)
    {
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
        Bounds b = renderers.Length > 0 ? renderers[0].bounds : new Bounds(go.transform.position + Vector3.up * 0.5f, Vector3.one);
        foreach (Renderer r in renderers) b.Encapsulate(r.bounds);
        return b;
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
