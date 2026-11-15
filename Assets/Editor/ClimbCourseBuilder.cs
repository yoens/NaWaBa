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
        Footprint last = course.startAsSummit
            ? SummitStart(root, actor, course, heading, groundLayer)
            : StartPad(root, actor, course, heading, groundLayer);
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
        if (course.endWithCourtyard)
        {
            // Last jump lands on the stage 3 courtyard; the goal is where stage 3 starts.
            Vector3 direction = Quaternion.Euler(0f, heading, 0f) * Vector3.forward;
            float rise = course.courtyardRise;
            float disc = jumpSpeed * jumpSpeed - 2f * gravity * rise;
            float reach = disc < 0f ? -1f : moveSpeed * (jumpSpeed + Mathf.Sqrt(disc)) / gravity;
            float margin = reach - course.courtyardGap;
            report.Add($"-- courtyard (stage 3 start)        gap {course.courtyardGap:0.0} rise {rise:0.0} " +
                $"margin {margin:0.00}m" + (margin < 0f ? "  <-- 점프 불가!" : margin < course.minimumMargin ? "  <-- 아슬아슬" : ""));
            if (margin < 0f) impossible++; else if (margin < course.minimumMargin) warnings++;

            Vector3 origin = last.center + direction * (last.Extent(direction) + course.courtyardGap - GatePuzzleBuilder.FloorBack);
            origin.y = last.top + rise;
            var yard = new GameObject("ArrivalCourtyard");
            Undo.RegisterCreatedObjectUndo(yard, "Build Climb Course");
            yard.transform.SetParent(root.transform, false);
            yard.transform.SetPositionAndRotation(new Vector3(origin.x, 0f, origin.z), Quaternion.Euler(0f, heading, 0f));
            GatePuzzleBuilder.BuildCourtyard(yard.transform, origin.y, course, groundLayer, KitWallDepth(course), false);

            // Castle stands where it stands in stage 3; the goal covers the stage 3 start area.
            Vector3 gatePoint = origin + direction * GatePuzzleBuilder.GateZ;
            PlaceSharedCastle(root, new Footprint { center = gatePoint, halfX = 3f, halfZ = 0f, top = origin.y, yaw = heading },
                course, stone, door, false);
            CreateGoal(root, new Footprint { center = origin + direction * 1.5f, halfX = 5f, halfZ = 5f, top = origin.y, yaw = heading });
            courseBounds.Encapsulate(gatePoint);
            last = new Footprint { center = origin, halfX = GatePuzzleBuilder.HalfWidth, halfZ = 6f, top = origin.y, yaw = heading };
        }
        else
        {
            PlaceSharedCastle(root, last, course, stone, door, false);
            CreateGoal(root, last);
        }
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

    // ---------- start = stage 1 summit ----------
    // The player stands where they reached the summit in stage 1 (2 m past the top of the
    // slope). The summit plateau, its side walls, flag, rocks and trees match stage 1, and the
    // top of the slope goes down behind. The first jump starts at the plateau's far edge -
    // the same islands stage 1 shows beyond its summit.
    private static Footprint SummitStart(GameObject root, ClimbActorController actor, ClimbCourse course,
        float heading, int groundLayer)
    {
        Vector3 feet = actor.transform.position;
        var summit = new GameObject("00_SummitStart");
        Undo.RegisterCreatedObjectUndo(summit, "Build Climb Course");
        summit.transform.SetParent(root.transform, false);
        summit.transform.SetPositionAndRotation(feet, Quaternion.Euler(0f, heading, 0f));
        Transform t = summit.transform;

        float width = Mathf.Max(4f, course.summitWidth), depth = Mathf.Max(6f, course.summitDepth);
        float back = -2f, front = depth - 2f;                     // plateau along local z
        float wall = Mathf.Max(1.6f, course.summitWallHeight);
        float angle = Mathf.Clamp(course.summitSlopeAngle, 0f, 40f) * Mathf.Deg2Rad;
        Material snow = GetMaterial("CourtyardSnow", new Color(0.92f, 0.95f, 1f));

        // Plateau: solid box (ground) + snow blocks on top.
        SolidBox(t, "SummitFloor", new Vector3(0f, -1.5f, (back + front) * 0.5f), new Vector3(width + 32f, 3f, depth),
            Quaternion.identity, snow, groundLayer);
        TileArea(t, "block-snow-large", width * 0.5f + 4f, back, front, Quaternion.identity, Vector3.zero, Vector3.forward);

        // The slope going down behind the start (same angle as stage 1).
        float tail = Mathf.Max(0f, course.summitSlopeTail);
        if (tail > 0f)
        {
            Vector3 down = new Vector3(0f, -Mathf.Sin(angle), -Mathf.Cos(angle));
            Vector3 normal = new Vector3(0f, Mathf.Cos(angle), -Mathf.Sin(angle));
            Quaternion tilt = Quaternion.LookRotation(-down, normal);
            Vector3 top = new Vector3(0f, 0f, back);
            Vector3 mid = top + down * (tail * 0.5f);
            SolidBox(t, "SlopeBelow", mid - normal * 0.5f, new Vector3(width + 32f, 1f, tail), tilt, snow, groundLayer);
            TileArea(t, "block-snow-large", width * 0.5f + 4f, 0f, tail, tilt, top, down);
            foreach (float side in new[] { -1f, 1f })
                WallRow(t, new Vector3(side * (width * 0.5f + 0.25f), 0f, 0f) + top,
                    new Vector3(side * (width * 0.5f + 0.25f), 0f, 0f) + top + down * tail, normal, wall);
        }
        foreach (float side in new[] { -1f, 1f })
            WallRow(t, new Vector3(side * (width * 0.5f + 0.25f), 0f, back), new Vector3(side * (width * 0.5f + 0.25f), 0f, front),
                Vector3.up, wall);

        // Flag, rocks and trees where stage 1 has them (flag 2.5 m before the far edge).
        Vector3 flag = new Vector3(0f, 0f, front - 2.5f);
        Prop(t, "flag", flag, 0f, 4.5f, false);
        Prop(t, "rocks", flag + new Vector3(-4f, 0f, 0.8f), 0f, 1.2f, false);
        Prop(t, "rocks", flag + new Vector3(4.2f, 0f, -0.6f), 70f, 1.2f, false);
        Prop(t, "tree-pine-snow", flag + new Vector3(-6.5f, 0f, -1.5f), 140f, 4f, false);
        Prop(t, "tree-snow", flag + new Vector3(6.8f, 0f, 1f), 210f, 4f, false);
        var random = new System.Random(3);
        for (float z = back - tail * Mathf.Cos(angle) + 2f; z < front; z += 3f)
            foreach (float side in new[] { -1f, 1f })
            {
                float x = side * (width * 0.5f + 2f + (float)random.NextDouble() * 10f);
                float y = z < back ? -(back - z) * Mathf.Tan(angle) : 0f;
                Prop(t, random.NextDouble() < 0.7 ? "tree-pine-snow" : "tree-snow", new Vector3(x, y, z),
                    (float)random.NextDouble() * 360f, Mathf.Lerp(3f, 5f, (float)random.NextDouble()), false);
            }

        Vector3 forward = Quaternion.Euler(0f, heading, 0f) * Vector3.forward;
        return new Footprint
        {
            center = feet + forward * ((back + front) * 0.5f), halfX = width * 0.5f, halfZ = depth * 0.5f,
            top = feet.y, yaw = heading
        };
    }

    private static void SolidBox(Transform parent, string name, Vector3 localCenter, Vector3 size, Quaternion rotation,
        Material material, int layer)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(box, "Build Climb Course");
        box.name = name;
        box.layer = layer;
        box.transform.SetParent(parent, false);
        box.transform.localPosition = localCenter;
        box.transform.localRotation = rotation;
        box.transform.localScale = size;
        box.GetComponent<Renderer>().sharedMaterial = material;
    }

    // Blocks (no colliders) covering a strip: "along" from "from" to "to" metres, starting at "start".
    private static void TileArea(Transform parent, string prefabName, float halfWidth, float from, float to,
        Quaternion rotation, Vector3 start, Vector3 along)
    {
        GameObject prefab = FindPrefab(prefabName);
        if (prefab == null) return;
        const float size = 2.5f;
        Vector3 up = rotation * Vector3.up;
        int countX = Mathf.CeilToInt(halfWidth * 2f / size), countZ = Mathf.Max(1, Mathf.CeilToInt((to - from) / size));
        float sx = halfWidth * 2f / countX, sz = (to - from) / countZ;
        for (int ix = 0; ix < countX; ix++)
        for (int iz = 0; iz < countZ; iz++)
        {
            Vector3 top = start + new Vector3(-halfWidth + sx * (ix + 0.5f), 0f, 0f) + along * (from + sz * (iz + 0.5f));
            GameObject tile = FitTo(prefab, parent, new Vector3(sx + 0.02f, 1f, sz + 0.02f));
            tile.transform.localRotation = rotation;
            tile.transform.localPosition = top - up * 1f;
        }
    }

    private static void WallRow(Transform parent, Vector3 from, Vector3 to, Vector3 standUp, float height)
    {
        GameObject prefab = FindPrefab("block-snow");
        Vector3 along = to - from;
        float length = along.magnitude;
        if (length < 0.1f) return;
        // Solid wall (not ground) + snow blocks for the look.
        Quaternion rotation = Quaternion.LookRotation(along.normalized, standUp);
        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(wall, "Build Climb Course");
        wall.name = "Wall";
        wall.transform.SetParent(parent, false);
        wall.transform.localRotation = rotation;
        wall.transform.localPosition = (from + to) * 0.5f + standUp * (height * 0.5f - 0.5f);
        wall.transform.localScale = new Vector3(0.5f, height + 1f, length);
        wall.GetComponent<Renderer>().sharedMaterial = GetMaterial("SummitWall", new Color(0.62f, 0.72f, 0.82f));
        if (prefab == null) return;
        wall.GetComponent<Renderer>().enabled = false;
        int count = Mathf.Max(1, Mathf.CeilToInt(length / 2f));
        for (int i = 0; i < count; i++)
        {
            GameObject block = FitTo(prefab, parent, new Vector3(0.6f, height + 0.6f, length / count + 0.02f));
            block.transform.localRotation = rotation;
            block.transform.localPosition = from + along * ((i + 0.5f) / count) - standUp * 0.6f;
        }
    }

    // Prefab instance (no colliders) scaled to exactly "size"; pivot at its bottom centre.
    private static GameObject FitTo(GameObject prefab, Transform parent, Vector3 size)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.gameObject.scene);
        Undo.RegisterCreatedObjectUndo(go, "Build Climb Course");
        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        MeasureLocal(go, out Vector3 center, out Vector3 half);
        Vector3 s = go.transform.localScale;
        go.transform.localScale = new Vector3(s.x * size.x / Mathf.Max(0.01f, half.x * 2f),
            s.y * size.y / Mathf.Max(0.01f, half.y * 2f), s.z * size.z / Mathf.Max(0.01f, half.z * 2f));
        go.transform.SetParent(parent, false);
        return go;
    }

    // Uniformly scaled prop standing on "position" (parent space), "height" metres tall.
    private static void Prop(Transform parent, string prefabName, Vector3 position, float yaw, float height, bool solid)
    {
        GameObject prefab = FindPrefab(prefabName);
        if (prefab == null) return;
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.gameObject.scene);
        Undo.RegisterCreatedObjectUndo(go, "Build Climb Course");
        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        MeasureLocal(go, out Vector3 center, out Vector3 half);
        float factor = height / Mathf.Max(0.01f, half.y * 2f);
        go.transform.localScale *= factor;
        go.transform.SetParent(parent, false);
        Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
        go.transform.localRotation = rotation;
        go.transform.localPosition = position - rotation * new Vector3(center.x, center.y - half.y, center.z) * factor;
        if (solid) EnsureCollider(go);
    }

    // ---------- stage 1 helper: the view onto stage 2 from the summit ----------

    // The first "count" platforms of a course, starting at "edge" (world) heading "yaw".
    // Stage 1 shows these beyond its summit - stage 2 starts with exactly these jumps.
    public static void BuildCoursePreview(Transform parent, Vector3 edge, float yaw, ClimbCourse course, int count)
    {
        var last = new Footprint { center = edge, halfX = 0f, halfZ = 0f, top = edge.y, yaw = yaw };
        float heading = yaw;
        for (int i = 0; i < Mathf.Min(count, course.steps.Count); i++)
        {
            ClimbCourse.Step step = course.steps[i];
            if (step.prefab == null) continue;
            heading += step.turn;
            Vector3 direction = Quaternion.Euler(0f, heading, 0f) * Vector3.forward;
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(step.prefab, parent.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(instance, "Build Snow Slope");
            instance.name = $"Preview {i + 1:00}_{step.prefab.name}";
            instance.transform.SetParent(parent, true);
            last = Place(instance, step, last, heading, direction);
            Decorate(instance, last, step, new System.Random(course.decorSeed + i * 31));
        }
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
    // ---------- shared castle ----------
    // One castle for all courses: "Castle (shared)" at the scene root. Each course only has a
    // "CastleAnchor" on its last platform; the stage moves the castle to the anchor of the
    // course it uses. Edit the castle once - building a course never overwrites it
    // (menu "NaWaBa > 성 다시 만들기" rebuilds it on purpose).
    public const string SharedCastleName = "Castle (shared)";
    public const string CastleAnchorName = "CastleAnchor";

    private static void PlaceSharedCastle(GameObject courseRoot, Footprint final, ClimbCourse course,
        Material stone, Material door, bool rebuild)
    {
        var anchor = new GameObject(CastleAnchorName);
        Undo.RegisterCreatedObjectUndo(anchor, "Build Climb Course");
        anchor.transform.SetParent(courseRoot.transform, false);
        Quaternion rotation = Quaternion.Euler(0f, final.yaw, 0f);
        anchor.transform.SetPositionAndRotation(
            new Vector3(final.center.x, final.top, final.center.z) + rotation * new Vector3(0f, 0f, final.halfZ),
            rotation);

        GameObject castle = FindSceneRoot(courseRoot.scene, SharedCastleName);
        if (castle != null && rebuild)
        {
            Undo.DestroyObjectImmediate(castle);
            castle = null;
        }
        if (castle == null)
        {
            castle = new GameObject(SharedCastleName);
            Undo.RegisterCreatedObjectUndo(castle, "Build Climb Course");
            if (castle.scene != courseRoot.scene)
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(castle, courseRoot.scene);
            castle.transform.SetPositionAndRotation(anchor.transform.position, anchor.transform.rotation);
            BuildGate(castle, final, course, stone, door);
        }
        else
        {
            // Keep the hand-edited castle; just stand it on this course's last platform.
            Undo.RecordObject(castle.transform, "Move castle");
            castle.transform.SetPositionAndRotation(anchor.transform.position, anchor.transform.rotation);
        }
    }

    private static GameObject FindSceneRoot(UnityEngine.SceneManagement.Scene scene, string name)
    {
        foreach (GameObject go in scene.GetRootGameObjects())
            if (go.name == name) return go;
        return null;
    }

    // ---------- shared helpers for the stage 3 builder ----------

    // Builds the castle (gate wall + background castle) inside "root", standing at root's
    // position and facing root's forward - the same castle as stage 2.
    public static void BuildCastleInto(GameObject root, ClimbCourse course)
    {
        Vector3 p = root.transform.position;
        var final = new Footprint { center = p, halfX = 3f, halfZ = 0f, top = p.y, yaw = root.transform.eulerAngles.y };
        BuildGate(root, final, course, GetMaterial("GateStone", course.stoneColor), GetMaterial("GateDoor", course.doorColor));
    }

    // Castle kit pieces for other builders (no colliders; the caller adds simple ones).
    public static Vector3 PlaceKit(Transform parent, string name, Vector3 bottom, float yaw, float scale) =>
        Kit(parent, name, bottom, yaw, scale, false);
    public static float PlaceKitStack(Transform parent, Vector3 bottom, float yaw, float scale, params string[] names) =>
        KitStack(parent, bottom, yaw, scale, false, names);
    public static bool HasCastleKit => FindCastlePrefab("wall") != null;
    // Size of a castle kit piece at scale 1 (a wall cell is about 1 x 1.31 x 1 m).
    public static Vector3 KitSize(string name)
    {
        GameObject prefab = FindCastlePrefab(name);
        return prefab != null ? KitBounds(prefab).size : Vector3.one;
    }

    public static ClimbCourse CastleSettings() => Selection.activeObject as ClimbCourse ?? LoadOrCreateCourse();
    public static GameObject FindKitPrefab(string name) => FindPrefab(name);
    public static Material MaterialAsset(string name, Color color) => GetMaterial(name, color);
    public static float KitWallDepth(ClimbCourse course)
    {
        GameObject doorway = FindCastlePrefab("wall-doorway"), wall = FindCastlePrefab("wall");
        if (!course.useCastleKit || doorway == null) return 1.6f;
        float scale = Mathf.Max(course.wallHeight, 4f) / Mathf.Max(0.01f, KitBounds(doorway).size.y);
        return KitBounds(wall ?? doorway).size.x * scale;
    }

    [MenuItem("NaWaBa/성 다시 만들기 (Stage_Climb)")]
    private static void RebuildCastle()
    {
        if (EditorApplication.isPlaying) return;
        ClimbActorController actor = Object.FindFirstObjectByType<ClimbActorController>();
        if (actor == null)
        {
            EditorUtility.DisplayDialog("NaWaBa", "Stage_Climb 씬을 열고 실행하세요.", "OK");
            return;
        }
        // The anchor of the course that is switched on (or any built course).
        Transform anchor = null;
        foreach (GameObject root in actor.gameObject.scene.GetRootGameObjects())
        {
            if (!root.name.StartsWith(RootName)) continue;
            Transform found = root.transform.Find(CastleAnchorName);
            if (found != null && (anchor == null || root.activeSelf)) anchor = found;
        }
        if (anchor == null)
        {
            EditorUtility.DisplayDialog("NaWaBa", "먼저 'NaWaBa > 등반 코스 만들기'로 코스를 만드세요.", "OK");
            return;
        }
        if (FindSceneRoot(actor.gameObject.scene, SharedCastleName) != null &&
            !EditorUtility.DisplayDialog("NaWaBa · 성",
                "성을 새로 만들면 직접 수정한 내용이 사라집니다. (Ctrl+Z로 되돌릴 수 있음)", "다시 만들기", "취소"))
            return;

        ClimbCourse course = Selection.activeObject as ClimbCourse ?? LoadOrCreateCourse();
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Rebuild Castle");
        GameObject old = FindSceneRoot(actor.gameObject.scene, SharedCastleName);
        if (old != null) Undo.DestroyObjectImmediate(old);
        var castle = new GameObject(SharedCastleName);
        Undo.RegisterCreatedObjectUndo(castle, "Rebuild Castle");
        castle.transform.SetPositionAndRotation(anchor.position, anchor.rotation);
        // Platform size only matters for the simple-shape fallback.
        var final = new Footprint { center = anchor.position, halfX = 2f, halfZ = 0f, top = anchor.position.y, yaw = anchor.eulerAngles.y };
        BuildGate(castle, final, course, GetMaterial("GateStone", course.stoneColor), GetMaterial("GateDoor", course.doorColor));
        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(actor.gameObject.scene);
        Selection.activeGameObject = castle;
    }

    private static void BuildGate(GameObject root, Footprint final, ClimbCourse course, Material stone, Material door)
    {
        // Kenney Castle Kit (Assets/Prefabs/Castle) when it is in the project; simple shapes otherwise.
        if (course.useCastleKit && FindCastlePrefab("wall-doorway") != null)
        {
            BuildKitGate(root, final, course, stone);
            return;
        }

        Material roof = GetMaterial("CastleRoof", course.roofColor);
        Material dark = GetMaterial("CastleStoneDark", course.stoneColor * 0.75f);
        Mesh cone = GetConeMesh();

        var gate = new GameObject("CastleGate");
        Undo.RegisterCreatedObjectUndo(gate, "Build Climb Course");
        gate.transform.SetParent(root.transform, false);
        // root = the shared castle, standing on the last platform's far edge.
        gate.transform.localPosition = new Vector3(0f, 0f, -0.6f);

        float opening = Mathf.Clamp(Mathf.Min(course.gateWidth, final.halfX * 2f - 0.6f), 1.6f, 6f);
        float wallH = Mathf.Max(course.wallHeight, 4f);
        float doorH = Mathf.Clamp(Mathf.Max(course.gateHeight, wallH * 0.6f), 2.4f, wallH - 1f);
        // Deep enough for any course height (the same castle is used by every course).
        float below = Mathf.Max(course.wallDepth, 40f);
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

    // ---------- Kenney Castle Kit ----------

    private static readonly Dictionary<string, GameObject> castleCache = new Dictionary<string, GameObject>();

    // Castle pieces live in a "Castle" folder (some names, like "flag", also exist in other kits).
    private static GameObject FindCastlePrefab(string exactName)
    {
        if (castleCache.TryGetValue(exactName, out GameObject cached) && cached != null) return cached;
        string best = null;
        foreach (string guid in AssetDatabase.FindAssets(exactName + " t:Prefab"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".prefab") || System.IO.Path.GetFileNameWithoutExtension(path) != exactName) continue;
            if (best == null || path.Contains("/Castle/")) best = path;
        }
        GameObject prefab = best != null ? AssetDatabase.LoadAssetAtPath<GameObject>(best) : null;
        castleCache[exactName] = prefab;
        return prefab;
    }

    // Size of a piece at scale 1 (measured from its meshes).
    private static Bounds KitBounds(GameObject prefab)
    {
        var probe = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        probe.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        MeasureLocal(probe, out Vector3 center, out Vector3 half);
        Object.DestroyImmediate(probe);
        return new Bounds(center, half * 2f);
    }

    // Places a kit piece so the bottom-centre of its meshes sits on "bottom" (parent space).
    // Returns its size after scaling. Pieces of the kit share one scale so they line up.
    private static Vector3 Kit(Transform parent, string name, Vector3 bottom, float yaw, float scale,
        bool solid, string label = null)
    {
        GameObject prefab = FindCastlePrefab(name);
        if (prefab == null)
        {
            Debug.LogWarning("[ClimbCourse] castle piece not found: " + name);
            return Vector3.zero;
        }
        Bounds b = KitBounds(prefab);
        var piece = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.gameObject.scene);
        Undo.RegisterCreatedObjectUndo(piece, "Build Climb Course");
        piece.name = label ?? name;
        piece.transform.SetParent(parent, false);
        piece.transform.localScale = piece.transform.localScale * scale;
        Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
        Vector3 offset = new Vector3(b.center.x, b.min.y, b.center.z) * scale;
        piece.transform.localRotation = rotation;
        piece.transform.localPosition = bottom - rotation * offset;
        if (solid) EnsureCollider(piece);
        return b.size * scale;
    }

    // Pieces stacked on top of each other (tower base -> middles -> roof). Returns the top height.
    private static float KitStack(Transform parent, Vector3 bottom, float yaw, float scale, bool solid,
        params string[] names)
    {
        float y = bottom.y;
        foreach (string name in names)
        {
            Vector3 size = Kit(parent, name, new Vector3(bottom.x, y, bottom.z), yaw, scale, solid);
            y += size.y;
        }
        return y;
    }

    // Gatehouse from the castle kit at the far edge of the last platform:
    // [hexagon tower] wall wall [square tower] DOORWAY+GATE [square tower] wall wall [hexagon tower]
    // The closed gate is what stage 3 opens.
    private static void BuildKitGate(GameObject root, Footprint final, ClimbCourse course, Material stone)
    {
        // Kit pieces run along their local Z (a wall cell is 1 x 1.31 x 1 m at scale 1), so
        // front-row pieces are turned 90 degrees to run across the gate.
        const float Across = 90f;
        Bounds doorway = KitBounds(FindCastlePrefab("wall-doorway"));
        Bounds wallPiece = KitBounds(FindCastlePrefab("wall") ?? FindCastlePrefab("wall-doorway"));
        float scale = Mathf.Max(course.wallHeight, 4f) / Mathf.Max(0.01f, doorway.size.y);
        float cell = doorway.size.z * scale;        // length of one wall piece after scaling
        float depth = wallPiece.size.x * scale;     // wall thickness
        // Deep enough for any course height (the same castle is used by every course).
        float below = Mathf.Max(course.wallDepth, 40f);

        var gate = new GameObject("CastleGate");
        Undo.RegisterCreatedObjectUndo(gate, "Build Climb Course");
        gate.transform.SetParent(root.transform, false);
        // root = the shared castle, standing on the last platform's far edge; the wall starts there.
        gate.transform.localPosition = new Vector3(0f, 0f, depth * 0.5f - 0.05f);
        Transform t = gate.transform;

        Kit(t, "wall-doorway", Vector3.zero, Across, scale, true, "GateDoorway");
        Kit(t, "gate", Vector3.zero, Across, scale, true, "Gate (closed)");

        int wallCells = Mathf.Max(1, Mathf.CeilToInt((course.wallHalfLength - cell * 1.5f) / cell));
        foreach (float side in new[] { -1f, 1f })
        {
            float x = side * cell;
            float towerTop = KitStack(t, new Vector3(x, 0f, 0f), Across, scale, true,
                "tower-square-base", "tower-square-mid-windows", "tower-square-top-roof-high");
            // Banner hanging on the tower's face toward the player.
            Kit(t, "flag-banner-long", new Vector3(x, towerTop * 0.3f, -depth * 0.5f - 0.08f), Across, scale * 0.6f, false, "Banner");
            for (int i = 1; i <= wallCells; i++)
                Kit(t, "wall", new Vector3(side * cell * (1 + i), 0f, 0f), Across, scale, true, "Wall");
            float endX = side * cell * (2 + wallCells);
            KitStack(t, new Vector3(endX, 0f, 0f), 0f, scale, true,
                "tower-hexagon-base", "tower-hexagon-mid", "tower-hexagon-roof");
        }

        // Stone foundation from the wall down into the clouds.
        float span = cell * (5 + wallCells * 2);
        Part(gate, "Foundation", new Vector3(0f, -below * 0.5f - 0.02f, 0f), new Vector3(span, below, depth * 1.05f), stone);

        if (course.buildCastle) BuildKitCastle(t, course, scale, below, stone);
    }

    // Big castle behind the gate, built from the kit (scenery only, no colliders).
    private static void BuildKitCastle(Transform gate, ClimbCourse course, float gateScale, float below, Material stone)
    {
        var castle = new GameObject("BackgroundCastle");
        Undo.RegisterCreatedObjectUndo(castle, "Build Climb Course");
        castle.transform.SetParent(gate, false);
        // Raised a little so the keep and towers show above the gate wall.
        castle.transform.localPosition = new Vector3(0f, Mathf.Max(course.wallHeight, 4f) * 0.8f, course.castleDistance);
        Transform t = castle.transform;

        float scale = gateScale * Mathf.Max(0.3f, course.castleScale);
        const float Across = 90f;    // kit walls run along their local Z
        Bounds wallBounds = KitBounds(FindCastlePrefab("wall"));
        float cell = wallBounds.size.z * scale;
        const int halfW = 3, depthCells = 5;   // outer ward: 7 cells wide, 6 deep

        // Outer walls: front (with a doorway), back and both sides; hexagon towers on the corners.
        for (int i = -halfW; i <= halfW; i++)
        {
            Kit(t, i == 0 ? "wall-doorway" : "wall", new Vector3(i * cell, 0f, 0f), Across, scale, false);
            Kit(t, "wall", new Vector3(i * cell, 0f, depthCells * cell), Across, scale, false);
        }
        for (int j = 1; j < depthCells; j++)
            foreach (float side in new[] { -1f, 1f })
                Kit(t, "wall", new Vector3(side * (halfW + 1) * cell, 0f, j * cell), 0f, scale, false);
        Kit(t, "gate", Vector3.zero, Across, scale, false);

        foreach (float sx in new[] { -1f, 1f })
        foreach (float sz in new[] { 0f, 1f })
        {
            var corner = new Vector3(sx * (halfW + 1) * cell, 0f, sz * depthCells * cell);
            float top = KitStack(t, corner, 0f, scale, false, "tower-hexagon-base", "tower-hexagon-mid",
                "tower-hexagon-mid", "tower-hexagon-roof");
            if (sz > 0f) Kit(t, "flag", new Vector3(corner.x, top, corner.z), Across, scale, false);
        }

        // Towers halfway along the front and side walls, and an inner wall with its own towers
        // (the trap corridors of stage 4 run between these walls).
        foreach (float side in new[] { -1f, 1f })
        {
            KitStack(t, new Vector3(side * 2f * cell, 0f, 0f), 0f, scale, false,
                "tower-square-base", "tower-square-mid-windows", "tower-square-top-roof");
            KitStack(t, new Vector3(side * (halfW + 1) * cell, 0f, depthCells * cell * 0.5f), 0f, scale, false,
                "tower-square-base", "tower-square-mid", "tower-square-mid-windows", "tower-square-top-roof-high");
            for (int i = -1; i <= 1; i++)
                Kit(t, "wall", new Vector3(i * cell, 0f, 2f * cell + (side > 0f ? 2f * cell : 0f)), Across, scale, false);
            KitStack(t, new Vector3(side * 2f * cell, 0f, 2f * cell), 0f, scale * 0.92f, false,
                "tower-hexagon-base", "tower-hexagon-mid", "tower-hexagon-roof");
            KitStack(t, new Vector3(side * 2f * cell, 0f, 4f * cell), 0f, scale * 0.92f, false,
                "tower-hexagon-base", "tower-hexagon-mid", "tower-hexagon-mid", "tower-hexagon-roof");
        }

        // The keep in the middle: a tall square tower, bigger than the rest.
        float keepScale = scale * 1.3f;
        var keepBase = new Vector3(0f, 0f, depthCells * cell * 0.55f);
        float keepTop = KitStack(t, keepBase, 0f, keepScale, false, "tower-square-base-color",
            "tower-square-mid-windows", "tower-square-mid", "tower-square-mid-windows",
            "tower-square-top-roof-high-windows");
        Kit(t, "flag-wide", new Vector3(keepBase.x, keepTop, keepBase.z), Across, keepScale, false);
        // Two smaller square towers beside the keep.
        foreach (float side in new[] { -1f, 1f })
            KitStack(t, new Vector3(side * cell * 1.2f, 0f, depthCells * cell * 0.7f), 0f, scale, false,
                "tower-square-base", "tower-square-mid-windows", "tower-square-top-roof");

        // Rock plinth under the whole castle, down into the clouds.
        float width = cell * (halfW * 2 + 3), length = cell * (depthCells + 2);
        float lift = castle.transform.localPosition.y;
        Part(castle, "Plinth", new Vector3(0f, -(below + lift) * 0.5f - 0.02f, depthCells * cell * 0.5f),
            new Vector3(width, below + lift, length), stone, false);
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
