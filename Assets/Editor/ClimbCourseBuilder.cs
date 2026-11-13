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
    // Built when nothing else is selected. Select another ClimbCourse asset (e.g. the old
    // ClimbCourse_Default) in the Project window before running the menu to build that one.
    private const string CourseAssetPath = "Assets/Data/ClimbCourse_Long.asset";
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

        // Several courses can live in the scene (one per difficulty): "ClimbCourse (built) <asset>".
        // Rebuilding replaces only this course's root (and an old unnamed root from before).
        string rootName = $"{RootName} {course.name}";
        foreach (GameObject sceneRoot in actor.gameObject.scene.GetRootGameObjects())
        {
            if (sceneRoot.name == rootName || sceneRoot.name == RootName)
                Undo.DestroyObjectImmediate(sceneRoot);
            else if (sceneRoot.name.StartsWith(RootName))
            {
                Undo.RecordObject(sceneRoot, "Hide other course");
                sceneRoot.SetActive(false);   // the stage turns on the course the difficulty asks for
            }
        }
        foreach (string oldName in OldRoots)
        {
            GameObject old = GameObject.Find(oldName);
            if (old == null) continue;
            Undo.RecordObject(old, "Disable old map");
            old.SetActive(false);
        }

        var root = new GameObject(rootName);
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
            Decorate(instance, placed, step, new System.Random(course.decorSeed + i * 31));
            string moving = "";
            if (step.moveDistance > 0f)
            {
                ClimbMover mover = Undo.AddComponent<ClimbMover>(instance);
                mover.Configure(Quaternion.Euler(0f, heading, 0f) * Vector3.right * step.moveDistance,
                    step.movePeriod, i * 0.17f);
                EditorUtility.SetDirty(mover);
                moving = $"  (moving ±{step.moveDistance:0.0}m)";
            }

            // Can the player make this jump? (edge to edge, landing "rise" metres higher)
            float rise = placed.top - last.top;
            float disc = jumpSpeed * jumpSpeed - 2f * gravity * rise;
            float reach = disc < 0f ? -1f : moveSpeed * (jumpSpeed + Mathf.Sqrt(disc)) / gravity;
            float margin = reach - step.gap;
            string line = $"{i + 1:00} {step.prefab.name,-28} gap {step.gap:0.0} rise {rise:0.0} " +
                $"top {placed.top:0.0}m  margin {margin:0.00}m{moving}";
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
        CreateGoal(root, last);
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
        GameObject prefab = course.startPadPrefab != null ? course.startPadPrefab : FindPrefab("block-grass-large");
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
        EnsureCollider(instance);
        Vector3 baseScale = t.localScale;

        // 1) Measure standing at the origin, unrotated (local box relative to the pivot).
        t.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        MeasureLocal(instance, out Vector3 center, out Vector3 half);

        // 2) Scale so the top's narrow side equals the requested width.
        float factor = step.width / Mathf.Max(0.01f, 2f * Mathf.Min(half.x, half.z));
        t.localScale = baseScale * factor;
        center *= factor;
        half *= factor;

        // 3) Turn to the course heading; centre "gap" past the previous edge, top at the target height.
        Quaternion rotation = Quaternion.Euler(0f, heading, 0f);
        var placed = new Footprint { halfX = half.x, halfZ = half.z, yaw = heading };
        float distance = last.Extent(direction) + step.gap + placed.Extent(direction);
        Vector3 targetCenter = last.center + direction * distance;
        float targetTop = last.top + step.rise;
        Vector3 offset = rotation * center;
        t.SetPositionAndRotation(
            new Vector3(targetCenter.x - offset.x, targetTop - (center.y + half.y), targetCenter.z - offset.z),
            rotation);

        placed.center = new Vector3(targetCenter.x, targetTop, targetCenter.z);
        placed.top = targetTop;
        return placed;
    }

    // Kenney models from the kit come without colliders: give every mesh a mesh collider
    // (exact shape, so hexagons and narrow blocks have no invisible edges).
    private static void EnsureCollider(GameObject instance)
    {
        if (instance.GetComponentInChildren<Collider>() != null) return;
        foreach (MeshFilter filter in instance.GetComponentsInChildren<MeshFilter>())
        {
            if (filter.sharedMesh == null) continue;
            MeshCollider meshCollider = Undo.AddComponent<MeshCollider>(filter.gameObject);
            meshCollider.sharedMesh = filter.sharedMesh;
        }
    }

    // Box around the platform while it stands at the origin unrotated: centre and half size.
    // Uses its Box Collider when it has one, otherwise the meshes.
    private static void MeasureLocal(GameObject instance, out Vector3 center, out Vector3 half)
    {
        BoxCollider box = instance.GetComponentInChildren<BoxCollider>();
        if (box != null)
        {
            Vector3 scale = box.transform.lossyScale;
            center = box.transform.TransformPoint(box.center);
            half = new Vector3(Mathf.Abs(box.size.x * scale.x), Mathf.Abs(box.size.y * scale.y),
                Mathf.Abs(box.size.z * scale.z)) * 0.5f;
            return;
        }
        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            center = Vector3.up * 0.5f;
            half = Vector3.one * 0.5f;
            return;
        }
        Bounds b = renderers[0].bounds;
        foreach (Renderer r in renderers) b.Encapsulate(r.bounds);
        center = b.center;
        half = b.extents;
    }

    // ---------- decoration ----------

    private static readonly Dictionary<string, GameObject> prefabCache = new Dictionary<string, GameObject>();

    private static GameObject FindPrefab(string exactName)
    {
        if (prefabCache.TryGetValue(exactName, out GameObject cached) && cached != null) return cached;
        // Prefer the Kenney kit copies in "New Folder" over older edited prefabs of the same name.
        string best = null;
        foreach (string guid in AssetDatabase.FindAssets(exactName + " t:Prefab"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".prefab") || System.IO.Path.GetFileNameWithoutExtension(path) != exactName) continue;
            if (best == null || path.Contains("/New Folder/")) best = path;
        }
        if (best == null)
        {
            Debug.LogWarning("[ClimbCourse] prefab not found: " + exactName);
            return null;
        }
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(best);
        prefabCache[exactName] = prefab;
        return prefab;
    }

    // Props on the platform top, kept to the sides so the landing spot stays clear.
    private static void Decorate(GameObject platform, Footprint on, ClimbCourse.Step step, System.Random random)
    {
        if (step.decor == ClimbCourse.Decor.None || step.moveDistance > 0f) return;
        if (Mathf.Min(on.halfX, on.halfZ) < 0.9f) return;
        bool snow = step.prefab.name.Contains("snow");
        Quaternion rotation = Quaternion.Euler(0f, on.yaw, 0f);

        if (step.decor == ClimbCourse.Decor.Fence)
        {
            GameObject fence = FindPrefab("fence-straight");
            if (fence == null) return;
            float side = random.NextDouble() < 0.5 ? -1f : 1f;
            int count = Mathf.Max(1, Mathf.FloorToInt(on.halfZ * 2f / 1.2f));
            for (int i = 0; i < count; i++)
            {
                float z = -on.halfZ + 1.2f * (i + 0.5f) + (on.halfZ * 2f - 1.2f * count) * 0.5f;
                // fence-straight stands along X near its +Z edge: turn it to run along the heading.
                Prop(platform, fence, on, rotation * Quaternion.Euler(0f, side > 0f ? 90f : -90f, 0f),
                    new Vector3(side * (on.halfX - 0.55f), 0f, z), 1.2f, true);
            }
            return;
        }

        string[] names;
        Vector2 size;
        int count2;
        bool byHeight;
        switch (step.decor)
        {
            case ClimbCourse.Decor.Trees:
                names = snow ? new[] { "tree-pine-snow", "tree-snow", "tree-pine-snow-small" }
                             : new[] { "tree-pine", "tree", "tree-pine-small" };
                size = new Vector2(2.2f, 3.4f); count2 = 1 + random.Next(2); byHeight = true;
                break;
            case ClimbCourse.Decor.Flowers:
                names = snow ? new[] { "rocks", "stones" } : new[] { "flowers", "flowers-tall", "grass", "mushrooms", "plant" };
                size = new Vector2(0.7f, 1.1f); count2 = 2 + random.Next(3); byHeight = false;
                break;
            default:
                names = new[] { "rocks", "stones", "rocks" };
                size = new Vector2(0.8f, 1.3f); count2 = 1 + random.Next(2); byHeight = false;
                break;
        }
        for (int i = 0; i < count2; i++)
        {
            GameObject prefab = FindPrefab(names[random.Next(names.Length)]);
            if (prefab == null) continue;
            float side = i % 2 == 0 ? -1f : 1f;
            var local = new Vector3(side * on.halfX * Lerp(random, 0.55f, 0.78f), 0f,
                on.halfZ * Lerp(random, -0.6f, 0.6f));
            Prop(platform, prefab, on, rotation * Quaternion.Euler(0f, Lerp(random, 0f, 360f), 0f),
                local, Lerp(random, size.x, size.y), !byHeight);
        }
    }

    private static float Lerp(System.Random random, float a, float b) => Mathf.Lerp(a, b, (float)random.NextDouble());

    // One prop on the platform top; scaled to a footprint (byFootprint) or a height.
    private static void Prop(GameObject platform, GameObject prefab, Footprint on, Quaternion rotation,
        Vector3 localOffset, float size, bool byFootprint)
    {
        var prop = (GameObject)PrefabUtility.InstantiatePrefab(prefab, platform.scene);
        Undo.RegisterCreatedObjectUndo(prop, "Build Climb Course");
        Transform t = prop.transform;
        t.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        MeasureLocal(prop, out _, out Vector3 half);
        float factor = byFootprint
            ? size / Mathf.Max(0.01f, 2f * Mathf.Max(half.x, half.z))
            : size / Mathf.Max(0.01f, 2f * half.y);
        t.localScale *= factor;
        Quaternion heading = Quaternion.Euler(0f, on.yaw, 0f);
        t.SetPositionAndRotation(on.center + heading * localOffset - Vector3.up * 0.02f, rotation);
        t.SetParent(platform.transform, true);
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

    // The goal belongs to its course. Goals outside any built course (the old GateGoal) are
    // turned off so only the active course's goal counts.
    private static void CreateGoal(GameObject root, Footprint final)
    {
        foreach (ClimbGoal old in Object.FindObjectsByType<ClimbGoal>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (old.transform.root.name.StartsWith(RootName)) continue;
            Undo.RecordObject(old.gameObject, "Disable old goal");
            old.gameObject.SetActive(false);
        }

        var go = new GameObject("GateGoal");
        Undo.RegisterCreatedObjectUndo(go, "Build Climb Course");
        go.layer = IgnoreRaycastLayer;
        go.transform.SetParent(root.transform, false);
        Quaternion rotation = Quaternion.Euler(0f, final.yaw, 0f);
        // In front of the gate (the gate stands at the far edge).
        go.transform.SetPositionAndRotation(
            new Vector3(final.center.x, final.top + 1f, final.center.z) + rotation * new Vector3(0f, 0f, -final.halfZ * 0.15f),
            rotation);
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(final.halfX * 1.6f, 2f, final.halfZ * 1.2f);
        go.AddComponent<ClimbGoal>();
    }

    // Gatehouse + curtain wall at the far edge of the last platform. The wall rises out of the
    // clouds, the closed doors wait for stage 3 (opening the gate).
    private static void BuildGate(GameObject root, Footprint final, ClimbCourse course, Material stone, Material door)
    {
        Material roof = GetMaterial("CastleRoof", course.roofColor);
        Material dark = GetMaterial("CastleStoneDark", course.stoneColor * 0.75f);
        Mesh cone = GetConeMesh();

        var gate = new GameObject("CastleGate");
        Undo.RegisterCreatedObjectUndo(gate, "Build Climb Course");
        gate.transform.SetParent(root.transform, false);
        Quaternion rotation = Quaternion.Euler(0f, final.yaw, 0f);
        gate.transform.SetPositionAndRotation(
            new Vector3(final.center.x, final.top, final.center.z) + rotation * new Vector3(0f, 0f, final.halfZ - 0.6f),
            rotation);

        float opening = Mathf.Clamp(Mathf.Min(course.gateWidth, final.halfX * 2f - 0.6f), 1.6f, 6f);
        float wallH = Mathf.Max(course.wallHeight, 4f);
        float doorH = Mathf.Clamp(Mathf.Max(course.gateHeight, wallH * 0.6f), 2.4f, wallH - 1f);
        float below = Mathf.Max(0f, course.wallDepth);
        float halfLength = Mathf.Max(course.wallHalfLength, opening * 0.5f + 4f);
        const float towerW = 2.4f, thick = 1.6f;
        float towerH = wallH + 3f;
        float towerX = opening * 0.5f + towerW * 0.5f;

        // Gatehouse towers on both sides of the opening.
        foreach (float side in new[] { -1f, 1f })
        {
            Part(gate, "GateTower", new Vector3(side * towerX, (towerH - below) * 0.5f, 0f),
                new Vector3(towerW, towerH + below, 2.8f), stone);
            Battlements(gate, side * towerX, towerH, towerW, 2.8f, stone);
        }
        // Wall above the opening, doors inside it.
        Part(gate, "GateLintel", new Vector3(0f, (doorH + wallH) * 0.5f, 0f),
            new Vector3(opening + 0.2f, wallH - doorH, thick), stone);
        Part(gate, "GateArch", new Vector3(0f, doorH + 0.15f, -thick * 0.5f - 0.05f),
            new Vector3(opening + 0.6f, 0.3f, 0.2f), dark);
        Part(gate, "DoorL", new Vector3(-opening * 0.25f, doorH * 0.5f, 0.1f), new Vector3(opening * 0.5f - 0.04f, doorH, 0.25f), door);
        Part(gate, "DoorR", new Vector3(opening * 0.25f, doorH * 0.5f, 0.1f), new Vector3(opening * 0.5f - 0.04f, doorH, 0.25f), door);
        // The platform-level ground in front of the doors goes down into the wall's base.
        Part(gate, "WallBase", new Vector3(0f, -below * 0.5f - 0.05f, 0f), new Vector3(opening + 0.2f, below, thick), stone);

        // Curtain walls to each side with battlements, and round corner towers with roofs.
        foreach (float side in new[] { -1f, 1f })
        {
            float inner = towerX + towerW * 0.5f;
            float length = halfLength - inner;
            float centerX = side * (inner + length * 0.5f);
            Part(gate, "CurtainWall", new Vector3(centerX, (wallH - below) * 0.5f, 0.3f),
                new Vector3(length, wallH + below, thick), stone);
            Battlements(gate, centerX, wallH, length, thick, stone, 0.3f);

            Vector3 corner = new Vector3(side * (halfLength + 1.2f), 0f, 0.3f);
            RoundTower(gate, corner, 1.9f, wallH + 4.5f, below, stone, roof, cone);
        }
        // A banner over the gate.
        Part(gate, "Banner", new Vector3(0f, doorH + (wallH - doorH) * 0.5f, -thick * 0.5f - 0.06f),
            new Vector3(0.9f, (wallH - doorH) * 0.7f, 0.05f), GetMaterial("CastleFlag", course.flagColor));

        if (course.buildCastle)
            BuildCastle(gate.transform, course, wallH, below, stone, dark, roof, cone,
                GetMaterial("CastleFlag", course.flagColor));
    }

    // Big castle behind the wall (scenery only, no colliders).
    private static void BuildCastle(Transform gate, ClimbCourse course, float wallH, float below,
        Material stone, Material dark, Material roof, Mesh cone, Material flag)
    {
        var castle = new GameObject("BackgroundCastle");
        Undo.RegisterCreatedObjectUndo(castle, "Build Climb Course");
        castle.transform.SetParent(gate, false);
        castle.transform.localPosition = new Vector3(0f, 0f, course.castleDistance);
        castle.transform.localScale = Vector3.one * course.castleScale;

        float baseY = -below;
        // Main hall and keep.
        Part(castle, "Hall", new Vector3(0f, (wallH + 8f + baseY) * 0.5f, 0f), new Vector3(20f, wallH + 8f - baseY, 12f), stone, false);
        Part(castle, "HallRoof", new Vector3(0f, wallH + 9.2f, 0f), new Vector3(20.6f, 2.4f, 12.6f), roof, false);
        Battlements(castle, 0f, wallH + 8f, 20f, 12f, stone, 0f, false);
        Part(castle, "Keep", new Vector3(0f, (wallH + 18f + baseY) * 0.5f, 3f), new Vector3(8f, wallH + 18f - baseY, 8f), dark, false);
        Battlements(castle, 0f, wallH + 18f, 8f, 8f, dark, 3f, false);
        RoundTower(castle, new Vector3(0f, 0f, 3f), 2.2f, wallH + 26f, below, stone, roof, cone, false);
        Flag(castle, new Vector3(0f, wallH + 26f + 4.6f, 3f), flag);

        // Four corner towers of different heights.
        Vector3[] corners = { new Vector3(-11f, 0f, -6.5f), new Vector3(11f, 0f, -6.5f),
                              new Vector3(-11f, 0f, 6.5f), new Vector3(11f, 0f, 6.5f) };
        float[] heights = { wallH + 13f, wallH + 13f, wallH + 17f, wallH + 15f };
        for (int i = 0; i < corners.Length; i++)
        {
            RoundTower(castle, corners[i], 2.6f, heights[i], below, stone, roof, cone, false);
            if (i >= 2) Flag(castle, corners[i] + Vector3.up * (heights[i] + 5f), flag);
        }
        // Windows (dark slits) on the side that faces the player.
        for (int i = -3; i <= 3; i++)
            Part(castle, "Window", new Vector3(i * 2.6f, wallH + 4f, -6.06f), new Vector3(0.6f, 1.6f, 0.1f), dark, false);
    }

    private static void RoundTower(GameObject parent, Vector3 basePoint, float radius, float height, float below,
        Material stone, Material roof, Mesh cone, bool collider = true)
    {
        // Unity's cylinder is 2 units tall, so the y scale is half the height.
        float total = height + below;
        Part(parent, "Tower", basePoint + new Vector3(0f, (height - below) * 0.5f, 0f),
            new Vector3(radius * 2f, total * 0.5f, radius * 2f), stone, collider, PrimitiveType.Cylinder);
        Part(parent, "TowerRing", basePoint + new Vector3(0f, height + 0.25f, 0f),
            new Vector3(radius * 2.3f, 0.25f, radius * 2.3f), stone, collider, PrimitiveType.Cylinder);
        if (cone == null) return;
        var top = new GameObject("TowerRoof");
        Undo.RegisterCreatedObjectUndo(top, "Build Climb Course");
        top.transform.SetParent(parent.transform, false);
        top.transform.localPosition = basePoint + new Vector3(0f, height + 0.5f, 0f);
        top.transform.localScale = new Vector3(radius * 2.5f, radius * 2.2f, radius * 2.5f);
        top.AddComponent<MeshFilter>().sharedMesh = cone;
        top.AddComponent<MeshRenderer>().sharedMaterial = roof;
    }

    private static void Battlements(GameObject parent, float centerX, float topY, float length, float depth,
        Material stone, float z = 0f, bool collider = true)
    {
        int count = Mathf.Max(2, Mathf.FloorToInt(length / 1.2f));
        float step = length / count;
        for (int i = 0; i < count; i += 2)
        {
            float x = centerX - length * 0.5f + step * (i + 0.5f);
            Part(parent, "Merlon", new Vector3(x, topY + 0.4f, z), new Vector3(step, 0.8f, depth), stone, collider);
        }
    }

    private static void Flag(GameObject parent, Vector3 basePoint, Material flag)
    {
        Part(parent, "FlagPole", basePoint + new Vector3(0f, 1.5f, 0f), new Vector3(0.12f, 3f, 0.12f), flag, false);
        Part(parent, "Flag", basePoint + new Vector3(0.8f, 2.5f, 0f), new Vector3(1.5f, 0.9f, 0.05f), flag, false);
    }

    // Cone mesh (base radius 0.5 at y=0, tip at y=1) saved as an asset so the scene keeps it.
    private static Mesh GetConeMesh()
    {
        string path = $"{MaterialFolder}/RoofCone.asset";
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (mesh != null) return mesh;

        const int segments = 16;
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        Vector3 tip = new Vector3(0f, 1f, 0f);
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * Mathf.PI * 2f / segments, a1 = (i + 1) * Mathf.PI * 2f / segments;
            Vector3 p0 = new Vector3(Mathf.Cos(a0) * 0.5f, 0f, Mathf.Sin(a0) * 0.5f);
            Vector3 p1 = new Vector3(Mathf.Cos(a1) * 0.5f, 0f, Mathf.Sin(a1) * 0.5f);
            int v = vertices.Count;
            vertices.Add(p0); vertices.Add(tip); vertices.Add(p1);       // side (clockwise from outside)
            triangles.Add(v); triangles.Add(v + 1); triangles.Add(v + 2);
            v = vertices.Count;
            vertices.Add(p0); vertices.Add(p1); vertices.Add(Vector3.zero); // bottom
            triangles.Add(v); triangles.Add(v + 1); triangles.Add(v + 2);
        }
        mesh = new Mesh { name = "RoofCone" };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        EnsureFolder(MaterialFolder);
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }

    private static void Part(GameObject parent, string name, Vector3 localPosition, Vector3 size, Material material,
        bool collider = true, PrimitiveType type = PrimitiveType.Cube)
    {
        GameObject part = GameObject.CreatePrimitive(type);
        Undo.RegisterCreatedObjectUndo(part, "Build Climb Course");
        part.name = name;
        if (!collider) Object.DestroyImmediate(part.GetComponent<Collider>());
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

        // Long course with the Kenney platform kit (about 34 jumps, 5 checkpoints, moving platforms).
        course = ScriptableObject.CreateInstance<ClimbCourse>();
        // prefab, width, gap, rise, turn, checkpoint, decor[, move distance, move period]
        // ── 초원 ──
        Add(course, "block-grass-large", 3.2f, 1.2f, 0.5f, 0f, false, ClimbCourse.Decor.Trees);
        Add(course, "block-grass", 2.4f, 1.4f, 0.6f, 0f, false, ClimbCourse.Decor.Flowers);
        Add(course, "block-grass-long", 2.2f, 1.3f, 0.6f, 40f, false, ClimbCourse.Decor.None);
        Add(course, "block-grass-hexagon", 2.4f, 1.5f, 0.5f, 20f, false, ClimbCourse.Decor.Flowers);
        Add(course, "block-grass-low-large", 3.0f, 1.6f, 0.3f, 0f, false, ClimbCourse.Decor.Trees);
        Add(course, "block-grass-narrow", 1.6f, 1.6f, 0.5f, -30f, false, ClimbCourse.Decor.None);
        Add(course, "block-grass-large", 3.0f, 1.2f, 0.8f, -30f, true, ClimbCourse.Decor.Fence);
        // ── 폐허 발판 ──
        Add(course, "platform", 2.4f, 1.8f, 0.2f, 0f, false, ClimbCourse.Decor.None);
        Add(course, "platform-fortified", 2.4f, 1.8f, 0.3f, 30f, false, ClimbCourse.Decor.None);
        Add(course, "platform", 2.0f, 2.0f, 0.0f, 30f, false, ClimbCourse.Decor.None);
        Add(course, "block-grass-overhang-large", 3.0f, 1.4f, 0.9f, 0f, false, ClimbCourse.Decor.Rocks);
        Add(course, "block-grass-overhang-hexagon", 2.2f, 1.6f, 0.6f, 45f, false, ClimbCourse.Decor.Flowers);
        Add(course, "block-grass-large-tall", 3.0f, 1.3f, 0.8f, 45f, true, ClimbCourse.Decor.Trees);
        // ── 움직이는 발판 ──
        Add(course, "block-moving-large", 2.4f, 1.8f, 0.3f, 0f, false, ClimbCourse.Decor.None, 1.5f, 3.5f);
        Add(course, "block-moving-blue", 2.4f, 1.8f, 0.3f, -20f, false, ClimbCourse.Decor.None, 1.8f, 3.5f);
        Add(course, "block-moving-large", 2.4f, 1.8f, 0.3f, -20f, false, ClimbCourse.Decor.None, 1.2f, 3.0f);
        Add(course, "block-grass-large", 3.0f, 1.4f, 0.6f, -20f, true, ClimbCourse.Decor.Trees);
        // ── 설산 ──
        Add(course, "block-snow", 2.4f, 1.2f, 0.8f, -40f, false, ClimbCourse.Decor.Trees);
        Add(course, "block-snow-narrow", 1.6f, 1.6f, 0.4f, -30f, false, ClimbCourse.Decor.None);
        Add(course, "block-snow-hexagon", 2.2f, 1.6f, 0.5f, 0f, false, ClimbCourse.Decor.None);
        Add(course, "block-snow-overhang-narrow", 1.5f, 1.7f, 0.3f, 30f, false, ClimbCourse.Decor.None);
        Add(course, "block-snow-large-tall", 3.0f, 1.2f, 0.9f, 30f, false, ClimbCourse.Decor.Trees);
        Add(course, "block-snow-narrow", 1.5f, 1.9f, 0.0f, 60f, false, ClimbCourse.Decor.None);
        Add(course, "block-snow-overhang-large", 3.0f, 1.4f, 0.7f, 0f, true, ClimbCourse.Decor.Rocks);
        // ── 정상 직전 ──
        Add(course, "block-moving-blue", 2.2f, 1.8f, 0.4f, -30f, false, ClimbCourse.Decor.None, 1.5f, 3.5f);
        Add(course, "block-snow-hexagon", 2.0f, 1.7f, 0.6f, -30f, false, ClimbCourse.Decor.None);
        Add(course, "block-snow-narrow", 1.4f, 1.9f, 0.2f, -30f, false, ClimbCourse.Decor.None);
        Add(course, "platform-fortified", 2.4f, 1.8f, 0.3f, 0f, false, ClimbCourse.Decor.None);
        Add(course, "block-snow-overhang-long", 2.0f, 1.4f, 0.8f, 40f, false, ClimbCourse.Decor.None);
        Add(course, "block-snow-large", 3.0f, 1.3f, 0.7f, 20f, true, ClimbCourse.Decor.Trees);
        Add(course, "block-snow-narrow", 1.4f, 2.0f, 0.2f, 0f, false, ClimbCourse.Decor.None);
        Add(course, "block-snow-hexagon", 2.0f, 1.7f, 0.6f, -25f, false, ClimbCourse.Decor.None);
        Add(course, "platform-fortified", 2.6f, 1.6f, 0.5f, -25f, false, ClimbCourse.Decor.None);
        Add(course, "block-snow-large", 6.0f, 1.3f, 0.6f, 0f, false, ClimbCourse.Decor.None);   // last: castle wall + goal

        EnsureFolder("Assets/Data");
        AssetDatabase.CreateAsset(course, CourseAssetPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[ClimbCourse] created {CourseAssetPath}. Edit it and run the menu again to rebuild.", course);
        return course;
    }

    private static void Add(ClimbCourse course, string prefabName, float width, float gap, float rise, float turn,
        bool checkpoint, ClimbCourse.Decor decor, float moveDistance = 0f, float movePeriod = 4f)
    {
        GameObject prefab = FindPrefab(prefabName);
        course.steps.Add(new ClimbCourse.Step
        {
            prefab = prefab, width = width, gap = gap, rise = rise, turn = turn, checkpoint = checkpoint,
            decor = decor, moveDistance = moveDistance, movePeriod = movePeriod
        });
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
