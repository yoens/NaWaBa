using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Menu: NaWaBa > 성 내부 만들기 (Stage_Keep)
// Builds stage 4 from a KeepLayout text map: stone floors, castle walls and corner towers
// (Kenney Castle Kit), traps, checkpoints, the entrance gate behind the start and the exit.
// The result is "Keep (built)" - normal scene objects you can move or delete afterwards.
// Select a KeepLayout asset before running to build that one (default:
// Assets/Data/KeepLayout_Default.asset, created automatically). The start cell is placed at
// the player's position; north on the map is the player's forward (+Z).
public static class KeepBuilder
{
    private const string RootName = "Keep (built)";
    private const string LayoutPath = "Assets/Data/KeepLayout_Default.asset";
    private const string Undoing = "Build Keep";
    private const string Floors = ".SECAWPR";

    [MenuItem("NaWaBa/성 내부 만들기 (Stage_Keep)")]
    private static void Build()
    {
        if (EditorApplication.isPlaying) return;
        KeepStageController stage = Object.FindFirstObjectByType<KeepStageController>();
        ClimbActorController actor = Object.FindFirstObjectByType<ClimbActorController>();
        if (stage == null || actor == null)
        {
            EditorUtility.DisplayDialog("NaWaBa", "Stage_Keep 씬을 열고 실행하세요.", "OK");
            return;
        }
        KeepLayout layout = Selection.activeObject as KeepLayout ?? LoadOrCreateLayout();
        string[] rows = layout.Rows();
        if (!Validate(rows, out Vector2Int start, out Vector2Int exit, out string problem))
        {
            EditorUtility.DisplayDialog("NaWaBa · 성 내부", "지도에 문제가 있습니다:\n" + problem, "OK");
            return;
        }
        Dictionary<Vector2Int, int> toExit = Distances(rows, exit);
        if (!toExit.ContainsKey(start))
        {
            EditorUtility.DisplayDialog("NaWaBa · 성 내부", "시작(S)에서 출구(E)까지 길이 이어져 있지 않습니다.", "OK");
            return;
        }

        var scene = stage.gameObject.scene;
        GameObject old = null;
        foreach (GameObject go in scene.GetRootGameObjects())
            if (go.name == RootName) old = go;
        if (old != null && !EditorUtility.DisplayDialog("NaWaBa · 성 내부",
                "이미 만들어진 성 내부가 있습니다. 다시 만들면 직접 수정한 내용이 사라집니다.\n(Ctrl+Z로 되돌릴 수 있음)",
                "다시 만들기", "취소"))
            return;

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(Undoing);
        if (old != null) Undo.DestroyObjectImmediate(old);

        float cell = layout.cellSize;
        int groundLayer = GroundLayer(actor);
        var root = New(RootName, null);
        root.transform.SetPositionAndRotation(actor.transform.position, Quaternion.Euler(0f, actor.transform.eulerAngles.y, 0f));
        Transform floors = New("Floors", root.transform).transform;
        Transform walls = New("Walls", root.transform).transform;
        Transform traps = New("Traps", root.transform).transform;
        Transform marks = New("Checkpoints and Exit", root.transform).transform;
        Transform props = New("Props", root.transform).transform;

        Material floorA = ClimbCourseBuilder.MaterialAsset("KeepFloorA", layout.floorColor);
        Material floorB = ClimbCourseBuilder.MaterialAsset("KeepFloorB", layout.floorColorAlt);
        Material mark = ClimbCourseBuilder.MaterialAsset("KeepTrapMark", layout.trapMarkColor);
        Material iron = ClimbCourseBuilder.MaterialAsset("KeepIron", new Color(0.62f, 0.64f, 0.68f));
        Material wood = ClimbCourseBuilder.MaterialAsset("KeepWood", new Color(0.4f, 0.27f, 0.16f));
        Material stone = ClimbCourseBuilder.MaterialAsset("GateStone", new Color(0.62f, 0.6f, 0.56f));
        bool kit = ClimbCourseBuilder.HasCastleKit;
        // Kit pieces are scaled so that one wall piece fills exactly one map cell.
        Vector3 wallSize = kit ? ClimbCourseBuilder.KitSize("wall") : new Vector3(1f, 1.31f, 1f);
        float kitScale = cell / Mathf.Max(0.01f, wallSize.z);
        float wallHeight = wallSize.y * kitScale;

        int height = rows.Length, width = rows[0].Length;
        System.Func<int, int, Vector3> at = (x, y) => new Vector3((x - start.x) * cell, 0f, (start.y - y) * cell);
        System.Func<int, int, bool> floor = (x, y) => y >= 0 && y < height && x >= 0 && x < width && Floors.IndexOf(rows[y][x]) >= 0;

        int trapCount = 0, checkpointCount = 0, towerCount = 0;
        var random = new System.Random(9);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            char c = rows[y][x];
            Vector3 p = at(x, y);
            if (floor(x, y))
            {
                Box(floors, "Floor", p + new Vector3(0f, -0.5f, 0f), new Vector3(cell, 1f, cell), (x + y) % 2 == 0 ? floorA : floorB, groundLayer, true);
                bool alongX = floor(x - 1, y) || floor(x + 1, y);
                bool alongZ = floor(x, y - 1) || floor(x, y + 1);
                // Corridor direction through this cell (traps face along it).
                float yaw = alongZ && !alongX ? 0f : alongX && !alongZ ? 90f : (floor(x, y - 1) && floor(x, y + 1) ? 0f : 90f);
                float phase = (float)random.NextDouble();
                switch (c)
                {
                    case 'A': Axe(traps, p, yaw, cell, phase, iron, wood); trapCount++; break;
                    case 'W': Saw(traps, p, yaw, cell, phase, mark); trapCount++; break;
                    case 'P': Spikes(traps, p, yaw, cell, phase, mark); trapCount++; break;
                    case 'R': Arrows(traps, p, yaw, cell, phase, stone); trapCount++; break;
                    case 'C':
                        Checkpoint(marks, p, cell, 1000 - toExit[new Vector2Int(x, y)]);
                        checkpointCount++;
                        break;
                    case 'E': Exit(marks, props, p, cell, kitScale * 0.45f); break;
                }
                // Dead ends get a little treasure.
                int exits = (floor(x - 1, y) ? 1 : 0) + (floor(x + 1, y) ? 1 : 0) + (floor(x, y - 1) ? 1 : 0) + (floor(x, y + 1) ? 1 : 0);
                if (exits == 1 && c == '.') Treasure(props, p, random);
                continue;
            }

            // Wall cell: only where it borders a floor (also diagonally, so corners close).
            bool west = floor(x - 1, y), east = floor(x + 1, y), north = floor(x, y - 1), south = floor(x, y + 1);
            bool diagonal = floor(x - 1, y - 1) || floor(x + 1, y - 1) || floor(x - 1, y + 1) || floor(x + 1, y + 1);
            if (!(west || east || north || south || diagonal)) continue;
            Box(walls, "WallCollider", p + new Vector3(0f, wallHeight * 0.5f, 0f), new Vector3(cell, wallHeight, cell), stone, 0, !kit);
            if (!kit) continue;
            bool entrance = x == start.x && y == start.y + 1;
            bool sideways = west || east, lengthways = north || south;
            if (entrance)
            {
                // The gate the player came through, closed behind them.
                ClimbCourseBuilder.PlaceKit(walls, "wall-doorway", p, 90f, kitScale);
                ClimbCourseBuilder.PlaceKit(walls, "gate", p, 90f, kitScale);
            }
            else if (sideways != lengthways)
                ClimbCourseBuilder.PlaceKit(walls, "wall", p, sideways ? 0f : 90f, kitScale);
            else
            {
                // Corners and junctions become towers of different heights.
                towerCount++;
                if ((x * 7 + y * 3) % 3 == 0)
                    ClimbCourseBuilder.PlaceKitStack(walls, p, 0f, kitScale, "tower-square-base", "tower-square-mid-windows", "tower-square-top-roof-high");
                else if ((x + y) % 2 == 0)
                    ClimbCourseBuilder.PlaceKitStack(walls, p, 0f, kitScale, "tower-square-base", "tower-square-top-roof");
                else
                    ClimbCourseBuilder.PlaceKitStack(walls, p, 0f, kitScale * 0.92f, "tower-hexagon-base", "tower-hexagon-mid", "tower-hexagon-roof");
            }
        }

        // One box per cell leaves seams the player's box collider snags on; merge them.
        KeepCollisionMerger.Merge(root.transform);

        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = root;
        Debug.Log($"[Keep] {layout.name}: {width}x{height} cells ({width * cell:0}x{height * cell:0} m), " +
            $"shortest route {toExit[start]} cells ({toExit[start] * cell:0} m), {trapCount} traps, " +
            $"{checkpointCount} checkpoints, {towerCount} towers", layout);
        EditorUtility.DisplayDialog("NaWaBa · 성 내부",
            $"성 내부를 만들었습니다.\n크기 {width * cell:0}×{height * cell:0}m · 최단 경로 {toExit[start] * cell:0}m\n" +
            $"함정 {trapCount}개 · 체크포인트 {checkpointCount}개 · 탑 {towerCount}개\n\n" +
            "함정·체크포인트·소품은 자유롭게 옮겨도 됩니다. 지도 자체는 KeepLayout 에셋에서 고치고 다시 실행하세요.\n씬을 저장(Ctrl+S)하세요.", "OK");
    }

    // ---------- traps (local +Z = corridor direction, local X = across) ----------

    private static Transform TrapRoot(Transform parent, string name, Vector3 position, float yaw)
    {
        var go = New(name, parent);
        go.transform.localPosition = position;
        go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        return go.transform;
    }

    private static void Axe(Transform parent, Vector3 p, float yaw, float cell, float phase, Material iron, Material wood)
    {
        Transform t = TrapRoot(parent, "Trap Axe", p, yaw);
        // Short arm + wide swing: the blade stays inside the corridor and rises clearly at the sides.
        float pivotHeight = 3.6f, arm = 2.6f;
        // Beam across the corridor that the axe hangs from.
        Box(t, "Beam", new Vector3(0f, pivotHeight + 0.15f, 0f), new Vector3(cell, 0.3f, 0.3f), wood, 0, true, false);
        var pivot = New("Pivot", t);
        pivot.transform.localPosition = new Vector3(0f, pivotHeight, 0f);
        Box(pivot.transform, "Handle", new Vector3(0f, -arm * 0.5f, 0f), new Vector3(0.16f, arm, 0.16f), wood, 0, true, false);
        GameObject blade = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Undo.RegisterCreatedObjectUndo(blade, Undoing);
        blade.name = "Blade";
        blade.layer = 2;
        Object.DestroyImmediate(blade.GetComponent<Collider>());
        blade.transform.SetParent(pivot.transform, false);
        blade.transform.localPosition = new Vector3(0f, -arm, 0f);
        blade.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);   // disc faces along the corridor
        blade.transform.localScale = new Vector3(1.8f, 0.06f, 1.8f);
        blade.GetComponent<Renderer>().sharedMaterial = iron;
        BoxCollider box = blade.AddComponent<BoxCollider>();
        box.size = new Vector3(0.95f, 6f, 0.95f);
        blade.AddComponent<TrapHazard>();
        Undo.AddComponent<TrapPendulum>(pivot).Configure(55f, 2.6f, phase);
    }

    private static void Saw(Transform parent, Vector3 p, float yaw, float cell, float phase, Material mark)
    {
        Transform t = TrapRoot(parent, "Trap Saw", p, yaw);
        Box(t, "Track", new Vector3(0f, 0.011f, 0f), new Vector3(cell - 0.4f, 0.02f, 0.5f), mark, 0, true, false);
        var saw = New("Saw", t);
        saw.layer = 2;
        saw.transform.localPosition = new Vector3(0f, 0.95f, 0f);
        Transform spinner = Model(saw.transform, "saw", Vector3.zero, 2f, false);
        if (spinner == null)
        {
            GameObject disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Undo.RegisterCreatedObjectUndo(disc, Undoing);
            Object.DestroyImmediate(disc.GetComponent<Collider>());
            disc.transform.SetParent(saw.transform, false);
            disc.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            disc.transform.localScale = new Vector3(2f, 0.08f, 2f);
            spinner = disc.transform;
        }
        BoxCollider box = saw.AddComponent<BoxCollider>();
        box.size = new Vector3(1.7f, 1.7f, 0.5f);
        saw.AddComponent<TrapHazard>();
        Undo.AddComponent<TrapSlider>(saw).Configure(new Vector3(cell * 0.5f - 1.2f, 0f, 0f), 2.4f, phase, spinner);
    }

    private static void Spikes(Transform parent, Vector3 p, float yaw, float cell, float phase, Material mark)
    {
        Transform t = TrapRoot(parent, "Trap Spikes", p, yaw);
        float across = cell - 0.4f, deep = 2.6f;
        Box(t, "Plate", new Vector3(0f, 0.011f, 0f), new Vector3(across, 0.02f, deep), mark, 0, true, false);
        var moving = New("Spikes", t);
        const float size = 0.9f;
        int countX = Mathf.FloorToInt(across / size), countZ = Mathf.FloorToInt(deep / size);
        bool any = false;
        for (int ix = 0; ix < countX; ix++)
        for (int iz = 0; iz < countZ; iz++)
        {
            var at = new Vector3((ix - (countX - 1) * 0.5f) * size, 0f, (iz - (countZ - 1) * 0.5f) * size);
            if (Model(moving.transform, "trap-spikes-large", at, 0.36f, true, size) != null) any = true;
        }
        if (!any) Box(moving.transform, "SpikeBlock", new Vector3(0f, 0.18f, 0f), new Vector3(across, 0.36f, deep), mark, 0, true, false);
        var hit = New("Hit", t);
        hit.layer = 2;
        hit.transform.localPosition = new Vector3(0f, 0.45f, 0f);
        BoxCollider box = hit.AddComponent<BoxCollider>();
        box.size = new Vector3(across - 0.3f, 0.9f, deep - 0.3f);
        TrapHazard hazard = hit.AddComponent<TrapHazard>();
        Undo.AddComponent<TrapSpikes>(t.gameObject).Configure(phase, moving.transform, hazard);
    }

    private static void Arrows(Transform parent, Vector3 p, float yaw, float cell, float phase, Material stone)
    {
        Transform t = TrapRoot(parent, "Trap Arrows", p, yaw);
        float[] lanes = { -1.3f, 0f, 1.3f };
        foreach (float z in lanes)
        {
            Box(t, "ArrowSlit", new Vector3(-cell * 0.5f + 0.15f, 0.7f, z), new Vector3(0.3f, 0.5f, 0.5f), stone, 0, true, false);
            Box(t, "ArrowCatch", new Vector3(cell * 0.5f - 0.1f, 0.7f, z), new Vector3(0.2f, 0.6f, 0.6f), stone, 0, true, false);
        }
        Undo.AddComponent<TrapShooter>(t.gameObject).Configure(ClimbCourseBuilder.FindKitPrefab("arrow"), 1.8f, cell - 0.5f, phase);
    }

    private static void Checkpoint(Transform parent, Vector3 p, float cell, int progress)
    {
        var go = New("Checkpoint", parent);
        go.layer = 2;
        go.transform.localPosition = p + new Vector3(0f, 0.1f, 0f);
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.center = new Vector3(0f, 1f, 0f);
        box.size = new Vector3(cell - 0.6f, 2f, cell - 0.6f);
        Undo.AddComponent<TrapCheckpoint>(go).Configure(progress);
    }

    private static void Exit(Transform marks, Transform props, Vector3 p, float cell, float flagScale)
    {
        var go = New("Exit", marks);
        go.layer = 2;
        go.transform.localPosition = p + new Vector3(0f, 1.2f, 0f);
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(cell - 1f, 2.4f, cell - 1f);
        go.AddComponent<KeepGoal>();
        Model(props, "chest", p + new Vector3(0f, 0f, 0.8f), 1.3f, true);
        if (ClimbCourseBuilder.HasCastleKit)
            ClimbCourseBuilder.PlaceKit(props, "flag-wide", p + new Vector3(-1.4f, 0f, 1.2f), 90f, flagScale);
    }

    private static void Treasure(Transform props, Vector3 p, System.Random random)
    {
        string[] names = { "barrel", "crate", "crate-strong", "chest", "barrel" };
        int count = 2 + random.Next(2);
        for (int i = 0; i < count; i++)
        {
            var offset = new Vector3(((float)random.NextDouble() - 0.5f) * 2.6f, 0f, ((float)random.NextDouble() - 0.5f) * 2.6f);
            Model(props, names[random.Next(names.Length)], p + offset, 1.1f, true);
        }
    }

    // ---------- helpers ----------

    // Platformer-kit prop standing at "position"; scaled to a height (or a footprint).
    private static Transform Model(Transform parent, string prefabName, Vector3 position, float size, bool byHeight, float footprint = 0f)
    {
        GameObject prefab = ClimbCourseBuilder.FindKitPrefab(prefabName);
        if (prefab == null) return null;
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.gameObject.scene);
        Undo.RegisterCreatedObjectUndo(go, Undoing);
        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
        Bounds b = renderers.Length > 0 ? renderers[0].bounds : new Bounds(Vector3.zero, Vector3.one);
        foreach (Renderer r in renderers) b.Encapsulate(r.bounds);
        if (footprint > 0f)
        {
            Vector3 s = go.transform.localScale;
            go.transform.localScale = new Vector3(s.x * footprint / Mathf.Max(0.01f, b.size.x),
                s.y * size / Mathf.Max(0.01f, b.size.y), s.z * footprint / Mathf.Max(0.01f, b.size.z));
        }
        else
            go.transform.localScale *= size / Mathf.Max(0.01f, byHeight ? b.size.y : Mathf.Max(b.size.x, b.size.y));
        go.transform.SetParent(parent, false);
        go.transform.localPosition = position;
        return go.transform;
    }

    private static GameObject Box(Transform parent, string name, Vector3 localCenter, Vector3 size, Material material,
        int layer, bool visible, bool solid = true)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(box, Undoing);
        box.name = name;
        box.layer = layer;
        if (!solid) Object.DestroyImmediate(box.GetComponent<Collider>());
        box.transform.SetParent(parent, false);
        box.transform.localPosition = localCenter;
        box.transform.localScale = size;
        Renderer renderer = box.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        renderer.enabled = visible;
        return box;
    }

    private static GameObject New(string name, Transform parent)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, Undoing);
        if (parent != null) go.transform.SetParent(parent, false);
        return go;
    }

    private static bool Validate(string[] rows, out Vector2Int start, out Vector2Int exit, out string problem)
    {
        start = exit = new Vector2Int(-1, -1);
        problem = "";
        if (rows.Length < 3) { problem = "줄이 너무 적습니다."; return false; }
        int starts = 0, exits = 0;
        for (int y = 0; y < rows.Length; y++)
        {
            if (rows[y].Length != rows[0].Length) { problem = $"{y + 1}번째 줄의 길이가 다릅니다."; return false; }
            for (int x = 0; x < rows[y].Length; x++)
            {
                char c = rows[y][x];
                if (c != '#' && Floors.IndexOf(c) < 0) { problem = $"{y + 1}번째 줄에 모르는 글자 '{c}'가 있습니다."; return false; }
                if (c == 'S') { start = new Vector2Int(x, y); starts++; }
                if (c == 'E') { exit = new Vector2Int(x, y); exits++; }
            }
        }
        if (starts != 1 || exits != 1) { problem = "S(시작)와 E(출구)가 각각 하나씩 있어야 합니다."; return false; }
        return true;
    }

    // Steps from every floor cell to the exit (for checkpoint order and the route length).
    private static Dictionary<Vector2Int, int> Distances(string[] rows, Vector2Int exit)
    {
        var distance = new Dictionary<Vector2Int, int> { [exit] = 0 };
        var queue = new Queue<Vector2Int>();
        queue.Enqueue(exit);
        Vector2Int[] steps = { Vector2Int.left, Vector2Int.right, Vector2Int.up, Vector2Int.down };
        while (queue.Count > 0)
        {
            Vector2Int at = queue.Dequeue();
            foreach (Vector2Int step in steps)
            {
                Vector2Int next = at + step;
                if (next.y < 0 || next.y >= rows.Length || next.x < 0 || next.x >= rows[0].Length) continue;
                if (Floors.IndexOf(rows[next.y][next.x]) < 0 || distance.ContainsKey(next)) continue;
                distance[next] = distance[at] + 1;
                queue.Enqueue(next);
            }
        }
        return distance;
    }

    private static KeepLayout LoadOrCreateLayout()
    {
        var layout = AssetDatabase.LoadAssetAtPath<KeepLayout>(LayoutPath);
        if (layout != null) return layout;
        layout = ScriptableObject.CreateInstance<KeepLayout>();
        if (!AssetDatabase.IsValidFolder("Assets/Data")) AssetDatabase.CreateFolder("Assets", "Data");
        AssetDatabase.CreateAsset(layout, LayoutPath);
        AssetDatabase.SaveAssets();
        return layout;
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
