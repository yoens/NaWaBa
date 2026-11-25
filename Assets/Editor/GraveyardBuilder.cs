using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Menu: NaWaBa > 무덤 유령 길 만들기 (Stage_Grave)
// Region 2 (무덤), game 1: the ghost patrol path.
// First run: copies Stage_Gate.unity to Stage_Grave.unity, swaps GateStageController for
// GraveStageController, removes the gate puzzle and the castle, adds the scene to the Build
// Profile (after Stage_Keep) and to NaWaBaStages (region 1).
// Every run: (re)builds "Graveyard (built)" in front of the player (+Z = forward):
//   start -> 1 crossing ghosts -> CP1 -> 2 gravestone lanes -> CP2 -> 3 ring round a crypt
//   -> CP3 -> 4 narrow walk with side alcoves -> CP4 -> 5 obelisk yard -> crypt (goal).
// Ghost routes are GraveGhost components (points editable in the Inspector). Everything can
// be moved afterwards; running it again replaces "Graveyard (built)" (confirm + one Undo).
public static class GraveyardBuilder
{
    private const string ScenePath = "Assets/Scenes/Stage_Grave.unity";
    private const string SourceScenePath = "Assets/Scenes/Stage_Gate.unity";
    private const string StageListPath = "Assets/Resources/NaWaBaStages.asset";
    private const string SceneName = "Stage_Grave";
    private const string StageTitle = "무덤 1 · 유령 순찰 길";
    private const int Region = 1;
    private const string RootName = "Graveyard (built)";
    private const string GraveyardFolder = "Assets/Prefabs/graveyard";
    private const string UndoName = "Build Graveyard";

    private const float HalfWidth = 7f;             // walkable half width of the path
    private const float ZStart = -6f, ZEnd = 166f;  // course ends (player starts at z 0)
    private const int IgnoreRaycast = 2;

    private static int groundLayer = 6;
    private static float kit = 1f;                  // graveyard kit scale (measured)
    private static Mats m;

    [MenuItem("NaWaBa/무덤 유령 길 만들기 (Stage_Grave)")]
    private static void Build()
    {
        if (EditorApplication.isPlaying) return;
        if (!OpenOrCreateScene()) return;
        var scene = EditorSceneManager.GetActiveScene();

        GraveStageController stage = Object.FindFirstObjectByType<GraveStageController>();
        ClimbActorController actor = Object.FindFirstObjectByType<ClimbActorController>();
        if (stage == null || actor == null)
        {
            EditorUtility.DisplayDialog("NaWaBa", "Stage_Grave 씬에 GraveStageController와 플레이어가 필요합니다.", "OK");
            return;
        }
        if (Grave("gravestone-cross") == null)
        {
            EditorUtility.DisplayDialog("NaWaBa", GraveyardFolder + " 에서 graveyard 프리팹을 찾지 못했습니다.", "OK");
            return;
        }

        GameObject old = Find(scene, RootName);
        if (old != null && !EditorUtility.DisplayDialog("NaWaBa · 무덤",
                "이미 만들어진 무덤 길이 있습니다. 다시 만들면 \"" + RootName + "\" 안에서 직접 수정한 내용이 사라집니다.\n" +
                "(Ctrl+Z로 되돌릴 수 있음)", "다시 만들기", "취소"))
            return;

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(UndoName);
        if (old != null) Undo.DestroyObjectImmediate(old);

        groundLayer = GroundLayer(actor);
        m = Mats.Load();
        kit = 1.2f / Mathf.Max(0.01f, Measure(Grave("gravestone-cross")).size.y);   // a gravestone ~1.2 m

        var root = New(RootName, null);
        root.transform.SetPositionAndRotation(actor.transform.position,
            Quaternion.Euler(0f, actor.transform.eulerAngles.y, 0f));
        Transform t = root.transform;
        Transform ground = New("Ground and Walls", t).transform;
        Transform props = New("Props", t).transform;
        Transform ghosts = New("Ghosts", t).transform;
        Transform marks = New("Checkpoints and Goal", t).transform;

        BuildGround(ground, props);
        var random = new System.Random(11);

        // 1 · entrance: three ghosts crossing the path.
        Ghost(ghosts, "Ghost 1-1", Line(-6f, 12f, 6f, 12f), false, 2.2f, 0f);
        Ghost(ghosts, "Ghost 1-2", Line(6f, 18f, -6f, 18f), false, 2.8f, 0.5f);
        Ghost(ghosts, "Ghost 1-3", Line(-6f, 24f, 6f, 24f), false, 2.4f, 0.25f);
        SideGraves(props, 6f, 30f, random);
        Checkpoint(marks, props, 33f, 1);

        // 2 · gravestone lanes: stones block the way, two ghosts walk the lanes, one crosses.
        float[] rows = { 41f, 47f, 53f, 59f };
        string[] blockers = { "gravestone-wide", "gravestone-decorative", "gravestone-cross", "gravestone-roof", "gravestone-round" };
        for (int r = 0; r < rows.Length; r++)
        {
            // Even rows: stones in the middle and at the sides (gaps on the ghost lanes and by the walls).
            // Odd rows: stones by the walls only (the way goes through the ghost lanes).
            float[] xs = r % 2 == 0 ? new[] { -4.5f, 0f, 4.5f } : new[] { -6.2f, 6.2f };
            foreach (float x in xs)
                Prop(props, blockers[(r + (int)(x + 10)) % blockers.Length], new Vector3(x, 0f, rows[r]),
                    180f + (float)(random.NextDouble() * 16 - 8), kit * 1.6f, true);
        }
        Ghost(ghosts, "Ghost 2-1", Line(-2.25f, 38f, -2.25f, 61f), false, 3f, 0f);
        Ghost(ghosts, "Ghost 2-2", Line(2.25f, 61f, 2.25f, 38f), false, 3f, 0.5f);
        Ghost(ghosts, "Ghost 2-3", Line(-6f, 50f, 6f, 50f), false, 1.8f, 0.7f);
        Checkpoint(marks, props, 65f, 2);

        // 3 · ring round a crypt: two ghosts circle close to the walls; slip through behind one.
        var ringCenter = new Vector3(0f, 0f, 81f);
        Prop(props, "crypt-small", ringCenter, 180f, kit * 1.3f, true);
        Ghost(ghosts, "Ghost 3-1", Ring(ringCenter, 5.6f, 20, false), true, 2.6f, 0f);
        Ghost(ghosts, "Ghost 3-2", Ring(ringCenter, 5.6f, 20, false), true, 2.6f, 0.5f);
        Checkpoint(marks, props, 97f, 3);

        // 4 · narrow walk: walls leave a 3 m lane; hide in the alcoves while the ghost passes.
        NarrowWalk(ground, props, 101f, 127f);
        Ghost(ghosts, "Ghost 4-1", Line(0f, 101.5f, 0f, 126.5f), false, 3.2f, 0f);
        Checkpoint(marks, props, 131f, 4);

        // 5 · obelisk yard: a ghost circles the obelisk, two cross in front of the crypt.
        var obelisk = new Vector3(0f, 0f, 147f);
        Prop(props, "pillar-obelisk", obelisk, 0f, kit * 1.6f, true);
        Ghost(ghosts, "Ghost 5-1", Ring(obelisk, 3.6f, 16, true), true, 2.2f, 0f);
        Ghost(ghosts, "Ghost 5-2", Line(-6f, 139f, 6f, 139f), false, 2.6f, 0.4f);
        Ghost(ghosts, "Ghost 5-3", Line(6f, 155f, -6f, 155f), false, 3f, 0.1f);
        SideGraves(props, 135f, 158f, random);
        Goal(marks, props, 160.5f);

        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Selection.activeGameObject = root;
        Debug.Log($"[Graveyard] built {ghosts.childCount} ghosts, 4 checkpoints (kit scale {kit:0.00}). Scene saved.");
        EditorUtility.DisplayDialog("NaWaBa · 무덤",
            "무덤 유령 길을 만들고 씬을 저장했습니다.\n" + ScenePath + "\n\n" +
            "· 유령 경로: Ghosts 아래 각 GraveGhost의 Points (씬에서 선택하면 경로가 선으로 보임)\n" +
            "· 유령 속도: 난이도 에셋의 Ghost Speed", "OK");
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
                if (!System.IO.File.Exists(SourceScenePath))
                {
                    EditorUtility.DisplayDialog("NaWaBa", SourceScenePath + " 이 없어 새 씬을 만들 수 없습니다.", "OK");
                    return false;
                }
                if (!EditorUtility.DisplayDialog("NaWaBa · 무덤",
                        "Stage_Gate 씬을 복사해 " + ScenePath + " 를 만듭니다.\n" +
                        "(성문 퍼즐과 성은 지우고, 플레이어·카메라·UI·영상 전송은 그대로 씁니다)", "만들기", "취소"))
                    return false;
                if (!AssetDatabase.CopyAsset(SourceScenePath, ScenePath))
                {
                    EditorUtility.DisplayDialog("NaWaBa", ScenePath + " 복사에 실패했습니다.", "OK");
                    return false;
                }
                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                ConvertFromGate(scene);
                EditorSceneManager.SaveScene(scene);
            }
        }
        AddToBuildProfile();
        AddToStageList();
        return true;
    }

    // Gate controller -> grave controller (same references); gate puzzle and castle removed.
    private static void ConvertFromGate(UnityEngine.SceneManagement.Scene scene)
    {
        foreach (GateStageController gate in Object.FindObjectsByType<GateStageController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var from = new SerializedObject(gate);
            Object actor = from.FindProperty("actor").objectReferenceValue;
            Object router = from.FindProperty("inputRouter").objectReferenceValue;
            Object gateway = from.FindProperty("gateway").objectReferenceValue;
            Object status = from.FindProperty("statusText").objectReferenceValue;
            GameObject host = gate.gameObject;
            Object.DestroyImmediate(gate);
            var grave = host.AddComponent<GraveStageController>();
            var to = new SerializedObject(grave);
            to.FindProperty("actor").objectReferenceValue = actor;
            to.FindProperty("inputRouter").objectReferenceValue = router;
            to.FindProperty("gateway").objectReferenceValue = gateway;
            to.FindProperty("statusText").objectReferenceValue = status;
            to.ApplyModifiedPropertiesWithoutUndo();
        }
        foreach (string name in new[] { "GatePuzzle (built)", ClimbCourseBuilder.SharedCastleName })
        {
            GameObject go = Find(scene, name);
            if (go != null) Object.DestroyImmediate(go);
        }

        // Dusk light for the graveyard.
        foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (light.type == LightType.Directional)
            {
                light.color = new Color(0.72f, 0.76f, 0.95f);
                light.intensity = Mathf.Min(light.intensity, 0.85f);
                light.transform.rotation = Quaternion.Euler(35f, -40f, 0f);
            }

        Debug.Log("[Graveyard] Stage_Grave created from Stage_Gate. Root objects kept: " +
            string.Join(", ", scene.GetRootGameObjects().Select(g => g.name)) +
            "\nRemove any leftover stage-3 scenery you do not want.");
    }

    private static void AddToBuildProfile()
    {
        var scenes = EditorBuildSettings.scenes.ToList();
        if (scenes.Any(s => s.path == ScenePath)) return;
        int keep = scenes.FindIndex(s => s.path.EndsWith("/Stage_Keep.unity"));
        var entry = new EditorBuildSettingsScene(ScenePath, true);
        if (keep >= 0) scenes.Insert(keep + 1, entry);
        else scenes.Add(entry);
        EditorBuildSettings.scenes = scenes.ToArray();
        Debug.Log("[Graveyard] added to the Build Profile scene list: " + ScenePath);
    }

    private static void AddToStageList()
    {
        var list = AssetDatabase.LoadAssetAtPath<NaWaBaStageList>(StageListPath);
        if (list == null)
        {
            Debug.LogWarning("[Graveyard] " + StageListPath + " not found: add Stage_Grave to the stage list by hand.");
            return;
        }
        if (list.IndexOf(SceneName) >= 0) return;
        Undo.RecordObject(list, "Add Stage_Grave");
        list.stages.Add(new NaWaBaStageList.Stage { sceneName = SceneName, title = StageTitle, region = Region });
        EditorUtility.SetDirty(list);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Graveyard] added {SceneName} to NaWaBaStages (region {Region}).");
    }

    // ---------- course ----------

    private static void BuildGround(Transform ground, Transform props)
    {
        float length = ZEnd - ZStart, mid = (ZStart + ZEnd) * 0.5f;
        // One floor box for the whole course: no seams for the player's box collider to snag on.
        Box(ground, "Floor", new Vector3(0f, -0.5f, mid), new Vector3(HalfWidth * 2f + 14f, 1f, length + 8f),
            m.grass, groundLayer, true, true);
        Box(ground, "PathStrip (visual)", new Vector3(0f, 0.01f, mid), new Vector3(3.2f, 0.02f, length),
            m.path, 0, true, false);

        // Invisible walls round the course; iron fence and pines are the visible edge.
        foreach (float side in new[] { -1f, 1f })
            Box(ground, side < 0 ? "Wall L" : "Wall R", new Vector3(side * (HalfWidth + 0.3f), 1.5f, mid),
                new Vector3(0.6f, 3f, length), m.stone, 0, false, true);
        Box(ground, "Wall Back", new Vector3(0f, 1.5f, ZStart - 0.3f), new Vector3(HalfWidth * 2f, 3f, 0.6f), m.stone, 0, false, true);
        Box(ground, "Wall End", new Vector3(0f, 1.5f, ZEnd + 0.3f), new Vector3(HalfWidth * 2f, 3f, 0.6f), m.stone, 0, false, true);

        Transform fences = New("Fences", props).transform;
        foreach (float side in new[] { -1f, 1f })
            FenceLine(fences, new Vector3(side * (HalfWidth + 0.3f), 0f, ZStart), new Vector3(side * (HalfWidth + 0.3f), 0f, ZEnd));
        FenceLine(fences, new Vector3(-HalfWidth, 0f, ZStart - 0.3f), new Vector3(HalfWidth, 0f, ZStart - 0.3f), "iron-fence-border-gate");

        var random = new System.Random(5);
        Transform trees = New("Trees", props).transform;
        string[] pines = { "pine", "pine-crooked", "pine-fall", "pine" };
        for (float z = ZStart; z < ZEnd; z += 5.5f)
            foreach (float side in new[] { -1f, 1f })
            {
                float x = side * (HalfWidth + 2f + (float)random.NextDouble() * 3f);
                Prop(trees, pines[random.Next(pines.Length)], new Vector3(x, 0f, z + (float)random.NextDouble() * 3f),
                    (float)random.NextDouble() * 360f, kit * (1.2f + (float)random.NextDouble() * 0.6f), false);
            }
    }

    private static void SideGraves(Transform props, float from, float to, System.Random random)
    {
        string[] names = { "gravestone-cross", "gravestone-round", "gravestone-broken", "gravestone-bevel", "grave" };
        for (float z = from; z < to; z += 3f)
            foreach (float side in new[] { -1f, 1f })
                if (random.NextDouble() < 0.6)
                    Prop(props, names[random.Next(names.Length)], new Vector3(side * (HalfWidth - 0.6f), 0f, z),
                        side < 0 ? 90f : -90f, kit, true);
    }

    // Lane x in [-1.5, 1.5] between walls; alcoves (3 m long, 3 m deep) alternate sides.
    private static void NarrowWalk(Transform ground, Transform props, float z0, float z1)
    {
        const float lane = 1.5f, pocket = 4.5f, wallH = 2.2f;
        (float side, float z)[] alcoves = { (-1f, 106f), (1f, 112f), (-1f, 118f), (1f, 123.5f) };
        Transform walls = New("Narrow Walk", ground).transform;
        foreach (float side in new[] { -1f, 1f })
        {
            var cuts = alcoves.Where(a => a.side == side).Select(a => a.z).OrderBy(z => z).ToList();
            float z = z0;
            foreach (float cut in cuts)
            {
                WallBlock(walls, side, lane, HalfWidth, z, cut, wallH);                 // full-depth wall
                WallBlock(walls, side, pocket, HalfWidth, cut, cut + 3f, wallH);        // back of the alcove
                z = cut + 3f;
            }
            WallBlock(walls, side, lane, HalfWidth, z, z1, wallH);
        }
        foreach (var a in alcoves)
            Prop(props, "candle-multiple", new Vector3(a.side * (pocket - 0.5f), 0f, a.z + 1.5f), 0f, kit, false);
    }

    private static void WallBlock(Transform parent, float side, float inner, float outer, float z0, float z1, float height)
    {
        if (z1 - z0 < 0.05f) return;
        float x = side * (inner + outer) * 0.5f;
        Box(parent, "Wall", new Vector3(x, height * 0.5f, (z0 + z1) * 0.5f),
            new Vector3(outer - inner, height, z1 - z0), m.stone, 0, true, true);
    }

    private static void Checkpoint(Transform marks, Transform props, float z, int progress)
    {
        var go = New($"Checkpoint {progress}", marks);
        go.layer = IgnoreRaycast;
        go.transform.localPosition = new Vector3(0f, 0.1f, z);
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.center = new Vector3(0f, 1f, 0f);
        box.size = new Vector3(HalfWidth * 2f - 0.6f, 2f, 3f);
        Undo.AddComponent<GraveCheckpoint>(go).Configure(progress);
        Prop(props, "lightpost-single", new Vector3(HalfWidth - 1f, 0f, z), -90f, kit * 1.3f, false);
        Prop(props, "lightpost-single", new Vector3(-HalfWidth + 1f, 0f, z), 90f, kit * 1.3f, false);
    }

    private static void Goal(Transform marks, Transform props, float z)
    {
        var go = New("Goal (crypt door)", marks);
        go.layer = IgnoreRaycast;
        go.transform.localPosition = new Vector3(0f, 1.2f, z);
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(4f, 2.4f, 2.5f);
        go.AddComponent<GraveGoal>();
        Prop(props, "crypt-large", new Vector3(0f, 0f, z + 3.2f), 180f, kit * 1.4f, true);
        Prop(props, "fire-basket", new Vector3(-2.6f, 0f, z), 0f, kit, false);
        Prop(props, "fire-basket", new Vector3(2.6f, 0f, z), 0f, kit, false);
    }

    // ---------- ghosts ----------

    private static void Ghost(Transform parent, string name, Vector3[] route, bool loop, float speed, float phase)
    {
        var go = New(name, parent);
        go.layer = IgnoreRaycast;
        go.transform.localPosition = route[0];
        SphereCollider hit = go.AddComponent<SphereCollider>();
        hit.isTrigger = true;
        hit.center = new Vector3(0f, 0.9f, 0f);
        hit.radius = 0.65f;
        Rigidbody body = go.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;

        // Model floating a little above the ground, sized to about 1.7 m.
        Transform visual = null;
        GameObject prefab = Grave("character-ghost");
        if (prefab != null)
        {
            float scale = 1.7f / Mathf.Max(0.01f, Measure(prefab).size.y);
            GameObject model = Place(go.transform, prefab, new Vector3(0f, 0.2f, 0f), 0f, scale);
            model.name = "Model";
            visual = model.transform;
        }
        // Dark disc on the ground = the area that catches the player (readable on video).
        Box(go.transform, "Shadow (visual)", new Vector3(0f, 0.03f, 0f), new Vector3(1.3f, 0.02f, 1.3f), m.shadow, IgnoreRaycast, true, false)
            .transform.localRotation = Quaternion.Euler(0f, 45f, 0f);

        var ghost = Undo.AddComponent<GraveGhost>(go);
        ghost.Configure(route, loop, speed, phase, visual);
        EditorUtility.SetDirty(ghost);
    }

    private static Vector3[] Line(float x0, float z0, float x1, float z1) =>
        new[] { new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z1) };

    private static Vector3[] Ring(Vector3 center, float radius, int count, bool reverse)
    {
        var points = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            float a = (reverse ? -1f : 1f) * i * Mathf.PI * 2f / count;
            points[i] = center + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * radius;
        }
        return points;
    }

    // ---------- graveyard kit ----------

    private static readonly Dictionary<string, GameObject> cache = new Dictionary<string, GameObject>();

    private static GameObject Grave(string exactName)
    {
        if (cache.TryGetValue(exactName, out GameObject cached) && cached != null) return cached;
        GameObject found = null;
        if (AssetDatabase.IsValidFolder(GraveyardFolder))
            foreach (string guid in AssetDatabase.FindAssets(exactName + " t:Prefab", new[] { GraveyardFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) != exactName) continue;
                found = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                break;
            }
        if (found == null) Debug.LogWarning("[Graveyard] prefab not found: " + exactName);
        cache[exactName] = found;
        return found;
    }

    // Kit model with its meshes' bottom-centre at "bottom". solid = an invisible box blocker
    // the size of the model (Kenney models come without colliders).
    private static GameObject Prop(Transform parent, string name, Vector3 bottom, float yaw, float scale, bool solid)
    {
        GameObject prefab = Grave(name);
        if (prefab == null) return null;
        GameObject piece = Place(parent, prefab, bottom, yaw, scale);
        if (solid)
        {
            Bounds b = Measure(prefab);
            var blocker = New("Blocker", piece.transform.parent);
            blocker.transform.localPosition = bottom + Vector3.up * (b.size.y * scale * 0.5f);
            blocker.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            BoxCollider box = blocker.AddComponent<BoxCollider>();
            box.size = new Vector3(Mathf.Max(0.3f, b.size.x * scale), b.size.y * scale, Mathf.Max(0.3f, b.size.z * scale));
            blocker.transform.SetParent(piece.transform, true);
        }
        return piece;
    }

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

    // Fence pieces end to end from a to b (the long side of the piece follows the line).
    private static void FenceLine(Transform parent, Vector3 a, Vector3 b, string middle = null)
    {
        GameObject fence = Grave("iron-fence");
        if (fence == null) return;
        Bounds size = Measure(fence);
        bool alongX = size.size.x >= size.size.z;
        float piece = Mathf.Max(0.2f, (alongX ? size.size.x : size.size.z) * kit);
        Vector3 dir = (b - a).normalized;
        float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg - (alongX ? 90f : 0f);
        int count = Mathf.Max(1, Mathf.FloorToInt(Vector3.Distance(a, b) / piece));
        for (int i = 0; i < count; i++)
        {
            Vector3 p = a + dir * (piece * (i + 0.5f));
            string name = middle != null && i == count / 2 ? middle : "iron-fence";
            Prop(parent, name, p, yaw, kit, false);
        }
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

    private sealed class Mats
    {
        public Material grass, path, stone, shadow;

        public static Mats Load() => new Mats
        {
            grass = ClimbCourseBuilder.MaterialAsset("GraveGrass", new Color(0.24f, 0.29f, 0.22f)),
            path = ClimbCourseBuilder.MaterialAsset("GravePath", new Color(0.45f, 0.42f, 0.36f)),
            stone = ClimbCourseBuilder.MaterialAsset("GraveStone", new Color(0.42f, 0.42f, 0.46f)),
            shadow = ClimbCourseBuilder.MaterialAsset("GraveGhostShadow", new Color(0.1f, 0.08f, 0.16f)),
        };
    }

    private static GameObject Box(Transform parent, string name, Vector3 center, Vector3 size, Material material,
        int layer, bool visible, bool solid)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(box, UndoName);
        box.name = name;
        box.layer = layer;
        if (!solid) Object.DestroyImmediate(box.GetComponent<Collider>());
        box.transform.SetParent(parent, false);
        box.transform.localPosition = center;
        box.transform.localScale = size;
        Renderer renderer = box.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        renderer.enabled = visible;
        return box;
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

    private static int GroundLayer(ClimbActorController actor)
    {
        SerializedProperty mask = new SerializedObject(actor).FindProperty("groundMask");
        int value = mask != null ? mask.intValue : 1 << 6;
        for (int i = 0; i < 32; i++)
            if ((value & (1 << i)) != 0) return i;
        return 6;
    }
}
