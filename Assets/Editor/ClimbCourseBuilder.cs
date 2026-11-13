using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Menu: NaWaBa > 등반 코스 만들기 (Stage_Climb)
// Builds the stage 2 course from a ClimbCourse asset in the open Stage_Climb scene:
//  - places each platform (prefab instance) so it sits "gap" metres past the previous one
//    with its top "rise" metres higher, scaled to the requested width;
//  - checks every jump against the player's move speed / jump height / gravity;
//  - adds checkpoints, moves the GateGoal onto the last platform and builds a castle gate;
//  - puts a start pad under the player and turns off the old ClimbMap / ClimbDecoration
//    objects (not deleted, so the old big floor no longer catches falls).
// Everything is one Undo step (Ctrl+Z). Running it again replaces the previous build.
public static class ClimbCourseBuilder
{
    private const string MenuPath = "NaWaBa/등반 코스 만들기 (Stage_Climb)";
    private const string CourseAssetPath = "Assets/Data/ClimbCourse_Default.asset";
    private const string MaterialFolder = "Assets/Materials/NaWaBa";
    private const string RootName = "ClimbCourse (built)";
    private const int IgnoreRaycastLayer = 2;
    private static readonly string[] OldRoots = { "ClimbMap", "ClimbDecoration" };

    // Kenney platform prefabs in Assets/Prefabs/3D_Back (by GUID, so renaming is fine).
    private const string Platform = "2022cdbec974e564ea44178ef71ec000";   // ClimbPlatform_01
    private const string Tall = "3cf4cd4961b25d243977f7da683cd20b";       // block-grass-large-tall
    private const string Narrow = "ece2d03b4801ca64eaeddee661a231a1";     // block-grass-narrow
    private const string Overhang = "662d32b381177b644ad809621139e2e6";   // block-grass-overhang-corner
    private const string Slab = "550882abb60f110408a6616e2b8c7cc8";       // block-moving (thin slab)

    // Placed footprint of one platform: centre, half extents in its own frame, yaw, top height.
    private struct Footprint
    {
        public Vector3 center;
        public float halfX, halfZ, top, yaw;

        // Distance from the centre to the edge in a world direction (box support function).
        public float Extent(Vector3 direction)
        {
            Vector3 local = Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f)) * direction;
            return Mathf.Abs(local.x) * halfX + Mathf.Abs(local.z) * halfZ;
        }
    }

    [MenuItem(MenuPath)]
    private static void Build()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("NaWaBa", "플레이 모드를 끝낸 뒤 실행하세요.", "OK");
            return;
        }

        ClimbStageController stage = Object.FindFirstObjectByType<ClimbStageController>();
        ClimbActorController actor = Object.FindFirstObjectByType<ClimbActorController>();
        if (stage == null || actor == null)
        {
            EditorUtility.DisplayDialog("NaWaBa",
                "Stage_Climb 씬을 열고 실행하세요.\n(ClimbStageController / ClimbActorController를 찾지 못했습니다.)", "OK");
            return;
        }

        ClimbCourse course = Selection.activeObject as ClimbCourse;
        if (course == null) course = LoadOrCreateCourse();
        if (course == null || course.steps.Count == 0)
        {
            EditorUtility.DisplayDialog("NaWaBa", "코스 데이터에 스텝이 없습니다.", "OK");
            return;
        }

        // Jump physics from the player in this scene.
        var actorData = new SerializedObject(actor);
        float moveSpeed = FloatOr(actorData, "moveSpeed", 4f);
        float jumpHeight = FloatOr(actorData, "jumpHeight", 1.5f);
        float gravityMultiplier = Mathf.Max(1f, FloatOr(actorData, "gravityMultiplier", 3f));
        int groundMask = actorData.FindProperty("groundMask") != null
            ? actorData.FindProperty("groundMask").intValue : 1 << 6;
        int groundLayer = FirstLayer(groundMask, 6);
        float gravity = Mathf.Abs(Physics.gravity.y) * gravityMultiplier;
        float jumpSpeed = Mathf.Sqrt(2f * gravity * jumpHeight);

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Build Climb Course");

        GameObject previous = GameObject.Find(RootName);
        if (previous != null) Undo.DestroyObjectImmediate(previous);
        foreach (string oldName in OldRoots)
        {
            GameObject old = GameObject.Find(oldName);
            if (old == null) continue;
            Undo.RecordObject(old, "Disable old map");
            old.SetActive(false);
        }

        var root = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(root, "Build Climb Course");
        SceneManagerMoveToActorScene(root, actor);

        float heading = actor.transform.eulerAngles.y;
        Footprint last = StartPad(root, actor, course, heading, groundLayer);
        var report = new List<string>();
        int warnings = 0, impossible = 0, checkpointOrder = 0;
        Bounds courseBounds = new Bounds(last.center, Vector3.one * 8f);

        for (int i = 0; i < course.steps.Count; i++)
        {
            ClimbCourse.Step step = course.steps[i];
            if (step.prefab == null)
            {
                Debug.LogWarning($"[ClimbCourse] step {i + 1}: no prefab, skipped.");
                continue;
            }

            heading += step.turn;
            Vector3 direction = Quaternion.Euler(0f, heading, 0f) * Vector3.forward;
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(step.prefab, root.scene);
            Undo.RegisterCreatedObjectUndo(instance, "Build Climb Course");
            instance.name = $"{i + 1:00}_{step.prefab.name}";
            instance.transform.SetParent(root.transform, false);
            SetLayer(instance, groundLayer);

            Footprint placed = Place(instance, step, last, heading, direction);

            // Can the player make this jump? (edge to edge, landing "rise" metres higher)
            float rise = placed.top - last.top;
            float disc = jumpSpeed * jumpSpeed - 2f * gravity * rise;
            float reach = disc < 0f ? -1f : moveSpeed * (jumpSpeed + Mathf.Sqrt(disc)) / gravity;
            float margin = reach - step.gap;
            string line = $"{i + 1:00} {step.prefab.name,-28} gap {step.gap:0.0} rise {rise:0.0} " +
                $"top {placed.top:0.0}m  margin {margin:0.00}m";
            if (margin < 0f) { impossible++; line += "  <-- 점프 불가!"; }
            else if (margin < course.minimumMargin) { warnings++; line += "  <-- 아슬아슬"; }
            report.Add(line);

            if (step.checkpoint) AddCheckpoint(root, placed, ++checkpointOrder);
            courseBounds.Encapsulate(placed.center);
            last = placed;
        }

        Material stone = GetMaterial("GateStone", course.stoneColor);
        Material door = GetMaterial("GateDoor", course.doorColor);
        BuildGate(root, last, course, stone, door);
        MoveGoal(last);
        if (course.buildCloudLayer)
            BuildClouds(root, courseBounds, course.cloudHeight, GetMaterial("Clouds", course.cloudColor));

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(root.scene);
        Selection.activeGameObject = root;

        string header = $"[ClimbCourse] {course.name}: {report.Count} platforms, " +
            $"{checkpointOrder} checkpoints, summit {last.top:0.0}m " +
            $"(move {moveSpeed}, jump {jumpHeight}, gravity x{gravityMultiplier})";
        string body = header + "\n" + string.Join("\n", report);
        if (impossible > 0) Debug.LogError(body);
        else if (warnings > 0) Debug.LogWarning(body);
        else Debug.Log(body);

        EditorUtility.DisplayDialog("NaWaBa · 등반 코스",
            $"발판 {report.Count}개, 체크포인트 {checkpointOrder}개, 정상 높이 {last.top:0.0}m\n" +
            (impossible > 0 ? $"점프 불가 구간 {impossible}개 — Console을 확인하세요.\n" :
             warnings > 0 ? $"아슬아슬한 구간 {warnings}개 — Console을 확인하세요.\n" : "모든 점프 여유 OK\n") +
            "\n씬을 저장(Ctrl+S)하세요. 되돌리려면 Ctrl+Z.", "OK");
    }

    // ---------- placement ----------

    // A wide platform right under the player's start position (top = the player's feet).
    private static Footprint StartPad(GameObject root, ClimbActorController actor, ClimbCourse course,
        float heading, int groundLayer)
    {
        Vector3 feet = actor.transform.position;
        GameObject prefab = course.startPadPrefab != null ? course.startPadPrefab : LoadPrefab(Platform);
        if (prefab == null)
        {
            Debug.LogWarning("[ClimbCourse] no start pad prefab; the player needs a floor at the start.");
            return new Footprint { center = feet, halfX = 1.5f, halfZ = 1.5f, top = feet.y, yaw = heading };
        }

        var pad = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.scene);
        Undo.RegisterCreatedObjectUndo(pad, "Build Climb Course");
        pad.name = "00_StartPad";
        pad.transform.SetParent(root.transform, false);
        SetLayer(pad, groundLayer);

        // Reuse the normal placement with a zero gap measured from a point at the player.
        var origin = new Footprint { center = feet, halfX = 0f, halfZ = 0f, top = feet.y, yaw = heading };
        var step = new ClimbCourse.Step { prefab = prefab, width = course.startPadWidth, gap = 0f, rise = 0f };
        Vector3 back = Quaternion.Euler(0f, heading, 0f) * Vector3.forward;
        Footprint placed = Place(pad, step, origin, heading, back);
        // Centre the pad on the player instead of in front of them.
        Vector3 shift = feet - placed.center;
        shift.y = 0f;
        pad.transform.position += shift;
        placed.center += shift;
        return placed;
    }

    private static GameObject LoadPrefab(string guid)
    {
        string path = AssetDatabase.GUIDToAssetPath(guid);
        return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }

    private static Footprint Place(GameObject instance, ClimbCourse.Step step, Footprint last,
        float heading, Vector3 direction)
    {
        Transform t = instance.transform;
        BoxCollider box = instance.GetComponentInChildren<BoxCollider>();
        Vector3 baseScale = t.localScale;

        // 1) Scale so the top's narrow side equals the requested width.
        t.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        Footprint raw = box != null ? Measure(box) : MeasureRenderers(instance);
        float narrowSide = Mathf.Max(0.01f, 2f * Mathf.Min(raw.halfX, raw.halfZ));
        t.localScale = baseScale * (step.width / narrowSide);

        // 2) Turn to the course heading and measure again.
        t.rotation = Quaternion.Euler(0f, heading, 0f);
        Footprint now = box != null ? Measure(box) : MeasureRenderers(instance);

        // 3) Move: centre "gap" past the previous edge, top at the target height.
        float distance = last.Extent(direction) + step.gap + now.Extent(direction);
        Vector3 targetCenter = last.center + direction * distance;
        float targetTop = last.top + step.rise;
        Vector3 offset = new Vector3(targetCenter.x - now.center.x, targetTop - now.top,
            targetCenter.z - now.center.z);
        t.position += offset;

        now.center += offset;
        now.center.y = targetTop;
        now.top = targetTop;
        return now;
    }

    // Box collider in world space (assumes only a yaw rotation).
    private static Footprint Measure(BoxCollider box)
    {
        Transform t = box.transform;
        Vector3 scale = t.lossyScale;
        Vector3 center = t.TransformPoint(box.center);
        float halfY = Mathf.Abs(box.size.y * scale.y) * 0.5f;
        return new Footprint
        {
            center = center,
            halfX = Mathf.Abs(box.size.x * scale.x) * 0.5f,
            halfZ = Mathf.Abs(box.size.z * scale.z) * 0.5f,
            top = center.y + halfY,
            yaw = t.eulerAngles.y
        };
    }

    private static Footprint MeasureRenderers(GameObject instance)
    {
        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return new Footprint { center = instance.transform.position, halfX = 0.5f, halfZ = 0.5f,
                top = instance.transform.position.y, yaw = instance.transform.eulerAngles.y };
        Bounds b = renderers[0].bounds;
        foreach (Renderer r in renderers) b.Encapsulate(r.bounds);
        return new Footprint { center = b.center, halfX = b.extents.x, halfZ = b.extents.z,
            top = b.max.y, yaw = instance.transform.eulerAngles.y };
    }

    // ---------- checkpoints, goal, gate ----------

    private static void AddCheckpoint(GameObject root, Footprint on, int order)
    {
        var go = new GameObject($"Checkpoint {order}");
        Undo.RegisterCreatedObjectUndo(go, "Build Climb Course");
        go.layer = IgnoreRaycastLayer;   // keeps the follow camera from bumping into it
        go.transform.SetParent(root.transform, false);
        go.transform.SetPositionAndRotation(new Vector3(on.center.x, on.top + 1f, on.center.z),
            Quaternion.Euler(0f, on.yaw, 0f));
        var box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(on.halfX * 1.8f, 2f, on.halfZ * 1.8f);
        go.AddComponent<ClimbCheckpoint>().Configure(order);
    }

    private static void MoveGoal(Footprint final)
    {
        ClimbGoal goal = Object.FindFirstObjectByType<ClimbGoal>(FindObjectsInactive.Include);
        if (goal == null)
        {
            var go = new GameObject("GateGoal");
            Undo.RegisterCreatedObjectUndo(go, "Build Climb Course");
            go.AddComponent<BoxCollider>();
            goal = go.AddComponent<ClimbGoal>();
        }
        Transform t = goal.transform;
        Undo.RecordObject(t, "Move goal");
        Undo.RecordObject(goal.gameObject, "Move goal");
        goal.gameObject.SetActive(true);
        goal.gameObject.layer = IgnoreRaycastLayer;
        Quaternion rotation = Quaternion.Euler(0f, final.yaw, 0f);
        // In front of the gate (the gate stands at the far edge).
        t.SetPositionAndRotation(
            new Vector3(final.center.x, final.top + 1f, final.center.z) + rotation * new Vector3(0f, 0f, -final.halfZ * 0.15f),
            rotation);
        t.localScale = Vector3.one;
        BoxCollider box = goal.GetComponent<BoxCollider>();
        if (box != null)
        {
            Undo.RecordObject(box, "Move goal");
            box.isTrigger = true;
            box.center = Vector3.zero;
            box.size = new Vector3(final.halfX * 1.6f, 2f, final.halfZ * 1.2f);
        }
    }

    private static void BuildGate(GameObject root, Footprint final, ClimbCourse course, Material stone, Material door)
    {
        var gate = new GameObject("CastleGate");
        Undo.RegisterCreatedObjectUndo(gate, "Build Climb Course");
        gate.transform.SetParent(root.transform, false);
        Quaternion rotation = Quaternion.Euler(0f, final.yaw, 0f);
        gate.transform.SetPositionAndRotation(
            new Vector3(final.center.x, final.top, final.center.z) + rotation * new Vector3(0f, 0f, final.halfZ - 0.45f),
            rotation);

        float width = Mathf.Clamp(Mathf.Min(course.gateWidth, final.halfX * 2f - 1.6f), 1.4f, 6f);
        float height = Mathf.Max(2.2f, course.gateHeight);
        const float pillar = 0.7f;
        float half = width * 0.5f + pillar * 0.5f;

        Part(gate, "PillarL", new Vector3(-half, height * 0.5f, 0f), new Vector3(pillar, height, 0.9f), stone);
        Part(gate, "PillarR", new Vector3(half, height * 0.5f, 0f), new Vector3(pillar, height, 0.9f), stone);
        Part(gate, "Lintel", new Vector3(0f, height + 0.35f, 0f), new Vector3(width + pillar * 2f, 0.7f, 1f), stone);
        for (int i = -1; i <= 1; i++)
            Part(gate, "Battlement", new Vector3(i * (width * 0.5f + pillar * 0.5f), height + 0.95f, 0f),
                new Vector3(0.5f, 0.5f, 0.9f), stone);
        Part(gate, "DoorL", new Vector3(-width * 0.25f, height * 0.5f, 0.15f), new Vector3(width * 0.5f - 0.04f, height, 0.2f), door);
        Part(gate, "DoorR", new Vector3(width * 0.25f, height * 0.5f, 0.15f), new Vector3(width * 0.5f - 0.04f, height, 0.2f), door);
    }

    private static void Part(GameObject parent, string name, Vector3 localPosition, Vector3 size, Material material)
    {
        GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(part, "Build Climb Course");
        part.name = name;
        part.transform.SetParent(parent.transform, false);
        part.transform.localPosition = localPosition;
        part.transform.localScale = size;
        if (material != null) part.GetComponent<Renderer>().sharedMaterial = material;
    }

    private static void BuildClouds(GameObject root, Bounds area, float height, Material material)
    {
        GameObject clouds = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Undo.RegisterCreatedObjectUndo(clouds, "Build Climb Course");
        clouds.name = "CloudLayer (visual)";
        Object.DestroyImmediate(clouds.GetComponent<Collider>());
        clouds.transform.SetParent(root.transform, false);
        clouds.transform.SetPositionAndRotation(new Vector3(area.center.x, height, area.center.z),
            Quaternion.Euler(90f, 0f, 0f));
        float size = Mathf.Max(area.size.x, area.size.z) + 200f;
        clouds.transform.localScale = new Vector3(size, size, 1f);
        if (material != null) clouds.GetComponent<Renderer>().sharedMaterial = material;
    }

    // ---------- assets ----------

    private static ClimbCourse LoadOrCreateCourse()
    {
        var course = AssetDatabase.LoadAssetAtPath<ClimbCourse>(CourseAssetPath);
        if (course != null) return course;

        course = ScriptableObject.CreateInstance<ClimbCourse>();
        // prefab, width, gap, rise, turn, checkpoint
        Add(course, Platform, 3.0f, 1.0f, 0.4f, 0f);
        Add(course, Platform, 2.6f, 1.2f, 0.5f, 0f);
        Add(course, Tall, 3.0f, 1.2f, 0.7f, 0f);
        Add(course, Narrow, 2.0f, 1.0f, 0.5f, 60f);
        Add(course, Narrow, 1.8f, 1.4f, 0.3f, 30f);
        Add(course, Overhang, 2.6f, 1.2f, 0.8f, 0f, true);
        Add(course, Narrow, 1.6f, 1.9f, 0.0f, -90f);
        Add(course, Narrow, 1.6f, 2.0f, 0.3f, 0f);
        Add(course, Slab, 2.2f, 1.5f, 0.5f, 0f);
        Add(course, Platform, 2.4f, 1.0f, 0.9f, -60f);
        Add(course, Platform, 2.4f, 1.0f, 0.9f, 0f);
        Add(course, Narrow, 1.7f, 1.6f, 0.3f, 0f, true);
        Add(course, Overhang, 2.2f, 1.3f, 0.8f, 45f);
        Add(course, Narrow, 1.6f, 2.0f, 0.2f, 45f);
        Add(course, Platform, 2.4f, 1.4f, 0.6f, 0f);
        Add(course, Tall, 4.0f, 1.2f, 0.7f, -30f);   // last: goal + gate

        EnsureFolder("Assets/Data");
        AssetDatabase.CreateAsset(course, CourseAssetPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[ClimbCourse] created {CourseAssetPath}. Edit it and run the menu again to rebuild.", course);
        return course;
    }

    private static void Add(ClimbCourse course, string guid, float width, float gap, float rise, float turn,
        bool checkpoint = false)
    {
        GameObject prefab = LoadPrefab(guid);
        if (prefab == null) Debug.LogWarning($"[ClimbCourse] platform prefab {guid} not found.");
        course.steps.Add(new ClimbCourse.Step
            { prefab = prefab, width = width, gap = gap, rise = rise, turn = turn, checkpoint = checkpoint });
    }

    private static Material GetMaterial(string name, Color color)
    {
        string path = $"{MaterialFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null) return material;
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        if (shader == null) return null;
        EnsureFolder(MaterialFolder);
        material = new Material(shader) { name = name };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        material.color = color;
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.1f);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }

    // ---------- helpers ----------

    private static float FloatOr(SerializedObject data, string name, float fallback)
    {
        SerializedProperty property = data.FindProperty(name);
        return property != null ? property.floatValue : fallback;
    }

    private static int FirstLayer(int mask, int fallback)
    {
        for (int i = 0; i < 32; i++)
            if ((mask & (1 << i)) != 0) return i;
        return fallback;
    }

    private static void SetLayer(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform) SetLayer(child.gameObject, layer);
    }

    private static void SceneManagerMoveToActorScene(GameObject go, ClimbActorController actor)
    {
        if (go.scene != actor.gameObject.scene)
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, actor.gameObject.scene);
    }
}
