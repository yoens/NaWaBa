using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Menu: NaWaBa > 무덤 거울 동굴 만들기 (Stage_Mirror)
// Region 2 (무덤), game 3: the mirror cave (bounce the light onto the crystal, room after room,
// then climb the stairs to the treasure).
// First run: copies Stage_Crypt.unity (or Stage_Grave / Stage_Gate) to Stage_Mirror.unity, swaps the
// stage controller for MirrorStageController, removes the old course, keeps the dark cave look,
// adds the scene to the Build Profile and to NaWaBaStages (region 1, after Stage_Crypt).
// Every run: (re)builds "MirrorCave (built)" from the cave kit so that the player stands in the
// start room: start room -> 4 mirror rooms (room-large, 7x7 cells of 2.6 m) -> stairs up -> treasure room.
// The puzzles below were checked with a solver (each is solvable, none is solved at the start).
// After building, mirrors, pillars, the light and the crystal can be moved in the scene: the room
// reads them from their positions (snapped to the nearest cell).
public static class MirrorCaveBuilder
{
    private const string ScenePath = "Assets/Scenes/Stage_Mirror.unity";
    private static readonly string[] SourceScenes =
        { "Assets/Scenes/Stage_Crypt.unity", "Assets/Scenes/Stage_Grave.unity", "Assets/Scenes/Stage_Gate.unity" };
    private const string StageListPath = "Assets/Resources/NaWaBaStages.asset";
    private const string SceneName = "Stage_Mirror";
    private const string AfterScene = "Stage_Crypt";
    private const string StageTitle = "무덤 3 · 거울 동굴";
    private const int Region = 1;
    private const string RootName = "MirrorCave (built)";
    private const string CaveFolder = "Assets/Prefabs/cave";
    private const string GraveyardFolder = "Assets/Prefabs/graveyard";
    private const string PlatformerFolder = "Assets/Prefabs/3D_Back/New Folder";
    private const string SpaceFolder = "Assets/Prefabs/space";
    private const string MaterialFolder = "Assets/Materials/NaWaBa";
    private const string UndoName = "Build Mirror Cave";
    private const int GroundLayer = 6, IgnoreRaycastLayer = 2;

    // The cave pieces are stretched sideways by this much (walls keep their height): the kit's
    // doorways are only ~2 m wide, tight for the 1.3 m-wide player. Everything below is in
    // "kit metres x Wide", so the layout keeps its proportions.
    private const float Wide = 1.3f;
    private const float Cell = 2f * Wide;
    private const int Size = 7;
    private const float RoomStep = 24f * Wide;   // room-large (20 m) + one corridor tile
    private const float BeamHeight = 1.1f;
    private static readonly Vector2Int Rock = new Vector2Int(2, 2);   // the rock lump inside room-large

    private struct Puzzle
    {
        public char sourceSide, targetSide;
        public int sourceIndex, targetIndex;
        public Vector2Int[] pillars, mirrors, solved;

        public Puzzle(char sSide, int sIndex, char tSide, int tIndex, Vector2Int[] pillars, Vector2Int[] mirrors, Vector2Int[] solved)
        {
            sourceSide = sSide; sourceIndex = sIndex; targetSide = tSide; targetIndex = tIndex;
            this.pillars = pillars; this.mirrors = mirrors; this.solved = solved;
        }
    }

    private static Vector2Int C(int x, int z) => new Vector2Int(x, z);

    // Grid cells: x = column (west -> east), z = row (south = entrance side -> north = exit side).
    // Side letters: where the light comes from / where the crystal stands, index along that side.
    private static readonly Puzzle[] Puzzles =
    {
        new Puzzle('W', 5, 'E', 6,
            new[] { C(5, 5) },
            new[] { C(4, 1), C(3, 5) },
            new[] { C(4, 5), C(4, 6) }),   // 6 pushes
        new Puzzle('N', 6, 'W', 1,
            new[] { C(6, 0), C(6, 3), C(3, 3) },
            new[] { C(2, 5), C(0, 0), C(3, 1) },
            new[] { C(1, 4), C(1, 1), C(6, 4) }),   // 10 pushes
        new Puzzle('N', 6, 'W', 4,
            new[] { C(2, 5), C(1, 4), C(1, 6), C(5, 6) },
            new[] { C(6, 3), C(6, 2), C(5, 4), C(1, 5) },
            new[] { C(6, 3), C(0, 3), C(0, 4), C(1, 5) }),   // 14 pushes
        new Puzzle('E', 2, 'W', 6,
            new[] { C(6, 1), C(0, 0), C(1, 6), C(4, 2) },
            new[] { C(0, 2), C(1, 3), C(1, 2), C(4, 3) },
            new[] { C(0, 6), C(5, 3), C(0, 3), C(5, 2) }),   // 12 pushes
    };

    private static readonly Vector3[] Sides = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
    private static float propScale = 1f;

    [MenuItem("NaWaBa/무덤 거울 동굴 만들기 (Stage_Mirror)")]
    private static void Build()
    {
        if (EditorApplication.isPlaying) return;
        if (!OpenOrCreateScene()) return;
        var scene = EditorSceneManager.GetActiveScene();

        MirrorStageController stage = Object.FindFirstObjectByType<MirrorStageController>();
        ClimbActorController actor = Object.FindFirstObjectByType<ClimbActorController>();
        if (stage == null || actor == null)
        {
            EditorUtility.DisplayDialog("NaWaBa", "Stage_Mirror 씬에 MirrorStageController와 플레이어가 필요합니다.", "OK");
            return;
        }
        string[] needed = { "corridor", "room-small", "room-large", "gate-metal-bars", "stairs", "gate-rock" };
        string missing = string.Join(", ", needed.Where(n => Kit(CaveFolder, n) == null));
        if (missing.Length > 0)
        {
            EditorUtility.DisplayDialog("NaWaBa", CaveFolder + " 에서 cave 프리팹을 찾지 못했습니다: " + missing, "OK");
            return;
        }

        GameObject old = Find(scene, RootName);
        if (old != null && !EditorUtility.DisplayDialog("NaWaBa · 거울 동굴",
                "이미 만들어진 거울 동굴이 있습니다. 다시 만들면 \"" + RootName + "\" 안에서 직접 수정한 내용" +
                "(거울·기둥 위치 포함)이 사라집니다.\n(Ctrl+Z로 되돌릴 수 있음)", "다시 만들기", "취소"))
            return;

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(UndoName);
        if (old != null) Undo.DestroyObjectImmediate(old);
        cache.Clear();
        openCache.Clear();
        boundsCache.Clear();

        GameObject gravestone = Kit(GraveyardFolder, "gravestone-cross");
        propScale = gravestone != null ? 1.2f / Mathf.Max(0.01f, Measure(gravestone).size.y) : 1f;

        var mats = new Mats();
        var root = New(RootName, null);
        Quaternion yaw = Quaternion.Euler(0f, actor.transform.eulerAngles.y, 0f);
        Vector3 start = new Vector3(0f, 0f, -20f * Wide);
        root.transform.SetPositionAndRotation(actor.transform.position - yaw * start, yaw);
        Transform t = root.transform;
        Transform pieces = New("Cave", t).transform;
        Transform deco = New("Decoration", t).transform;
        Transform lights = New("Lights", t).transform;
        Vector3 Z = Vector3.forward;

        // Start room and the corridor into the first mirror room.
        Room(pieces, deco, "room-small", start, new[] { Z }, "Start Room");
        Solid(Tile(pieces, "corridor", new Vector3(0f, 0f, -12f * Wide), Z, -Z));
        Lamp(lights, start + Vector3.up * 3f, 9f, 2f);
        Prop(deco, "lantern-candle", start + new Vector3(-3f, 0f, 3.5f), 0f, propScale);
        Prop(deco, "lantern-candle", start + new Vector3(3f, 0f, 3.5f), 0f, propScale);

        var rooms = new List<MirrorRoom>();
        for (int k = 0; k < Puzzles.Length; k++)
        {
            Vector3 center = new Vector3(0f, 0f, RoomStep * k);
            Room(pieces, deco, "room-large", center, new[] { -Z, Z }, $"Mirror Room {k + 1} (cave)");
            if (k < Puzzles.Length - 1)
                Solid(Tile(pieces, "corridor", center + Z * (RoomStep * 0.5f), Z, -Z));
            rooms.Add(BuildRoom(t, k, center, Puzzles[k], mats, lights, deco));
        }

        // The way up: stairs behind the last room's door, then the treasure room on top.
        Vector3 last = new Vector3(0f, 0f, RoomStep * (Puzzles.Length - 1));
        Vector3 stairsAt = last + Z * (14f * Wide);
        GameObject stairs = Solid(Tile(pieces, "stairs", stairsAt, -Z));
        float top = TopHeight(stairs, root.transform.TransformPoint(stairsAt + Z * (3.4f * Wide)));
        Vector3 treasureAt = last + new Vector3(0f, top, 24f * Wide);
        Room(pieces, deco, "room-small", treasureAt, new[] { -Z }, "Treasure Room");
        Lamp(lights, stairsAt + Vector3.up * (top * 0.5f + 2.5f), 10f, 1.6f);
        MirrorTreasure treasure = BuildTreasure(t, treasureAt, mats, lights, deco);

        var so = new SerializedObject(stage);
        SerializedProperty list = so.FindProperty("rooms");
        list.arraySize = rooms.Count;
        for (int i = 0; i < rooms.Count; i++) list.GetArrayElementAtIndex(i).objectReferenceValue = rooms[i];
        so.FindProperty("treasure").objectReferenceValue = treasure;
        so.ApplyModifiedProperties();

        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Selection.activeGameObject = root;
        Debug.Log($"[Mirror] built {rooms.Count} mirror rooms, stairs top {top:0.##} m. Scene saved.");
        EditorUtility.DisplayDialog("NaWaBa · 거울 동굴",
            "거울 동굴을 만들고 씬을 저장했습니다.\n" + ScenePath + "\n\n" +
            "· 방 개수: 난이도 에셋의 Mirror Rooms (Easy 2 / Normal 3 / Hard 4)\n" +
            "· 거울·기둥·빛·수정은 씬에서 옮겨도 됩니다(가까운 칸에 맞춰짐).\n" +
            "· Console에 [Mirror] 경고가 있으면 확인하세요.", "OK");
    }

    // ---------- one mirror room ----------

    private static MirrorRoom BuildRoom(Transform parent, int k, Vector3 center, Puzzle p, Mats mats, Transform lights, Transform deco)
    {
        var go = New($"Mirror Room {k + 1}", parent);
        go.transform.localPosition = center;
        Transform r = go.transform;
        Transform blocks = New("Blocks", r).transform;
        Transform mirrors = New("Mirrors", r).transform;

        // Cells the cave piece itself fills (the rock lump) become blocks too.
        Physics.SyncTransforms();
        var rockCells = new List<Vector2Int>();
        for (int x = 0; x < Size; x++)
            for (int z = 0; z < Size; z++)
            {
                Vector3 world = r.TransformPoint(CellCenter(C(x, z)) + Vector3.up * 1.3f);
                if (Physics.CheckBox(world, new Vector3(0.45f, 0.3f, 0.45f), r.rotation, 1 << GroundLayer, QueryTriggerInteraction.Ignore))
                    rockCells.Add(C(x, z));
            }
        foreach (Vector2Int c in rockCells)
        {
            var marker = New($"Rock {c.x},{c.y} (blocks)", blocks);
            marker.transform.localPosition = CellCenter(c);
            marker.AddComponent<MirrorBlock>();
        }
        if (!rockCells.Contains(Rock)) Pillar(blocks, Rock, mats);
        foreach (Vector2Int c in p.pillars) Pillar(blocks, c, mats);
        var expected = new HashSet<Vector2Int>(p.pillars) { Rock };
        var surprise = rockCells.Where(c => !expected.Contains(c)).ToList();
        if (surprise.Count > 0)
            Debug.LogWarning($"[Mirror] room {k + 1}: the cave model also fills cells {string.Join(" ", surprise)} " +
                "(the puzzle did not plan for them; it may be unsolvable). Delete the \"Rock … (blocks)\" markers there " +
                "if the player can actually walk through.", go);

        var pieces = new List<MirrorPiece>();
        for (int i = 0; i < p.mirrors.Length; i++)
            pieces.Add(Mirror(mirrors, $"Mirror {i + 1}", p.mirrors[i], (i + k) % 2 == 0, p.solved[i], mats));

        MirrorSource source = Source(r, Edge(p.sourceSide, p.sourceIndex), Inward(p.sourceSide), mats, lights);
        MirrorTarget target = Crystal(r, Edge(p.targetSide, p.targetIndex), -Inward(p.targetSide), mats);
        GameObject plate = Plate(r, C(-1, -1), mats);

        // Door at the exit opening (+Z wall); it sinks when the room is solved.
        GameObject door = Door(r, new Vector3(0f, 0f, 10f * Wide));

        var lineGo = New("Light Beam", r);
        LineRenderer line = lineGo.AddComponent<LineRenderer>();
        line.sharedMaterial = mats.beam;
        line.widthMultiplier = 0.13f;
        line.numCapVertices = 2;
        line.numCornerVertices = 2;
        line.useWorldSpace = true;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.positionCount = 0;

        MirrorRoom room = go.AddComponent<MirrorRoom>();
        var so = new SerializedObject(room);
        so.FindProperty("cell").floatValue = Cell;
        so.FindProperty("size").intValue = Size;
        so.FindProperty("beamHeight").floatValue = BeamHeight;
        so.FindProperty("source").objectReferenceValue = source;
        so.FindProperty("target").objectReferenceValue = target;
        so.FindProperty("door").objectReferenceValue = door;
        so.FindProperty("beam").objectReferenceValue = line;
        so.FindProperty("hideWhenSolved").objectReferenceValue = plate;
        so.ApplyModifiedPropertiesWithoutUndo();

        float w = Wide;
        Lamp(lights, center + new Vector3(-5f * w, 3.6f, -5f * w), 16f, 1.7f);
        Lamp(lights, center + new Vector3(5f * w, 3.6f, 5f * w), 16f, 1.7f);
        Lamp(lights, center + new Vector3(0f, 3.8f, 0f), 14f, 1.3f);
        Prop(deco, "candle-multiple", center + new Vector3(-8.3f * w, 0f, 8.3f * w), 0f, propScale);
        Prop(deco, "candle-multiple", center + new Vector3(8.3f * w, 0f, 8.3f * w), 0f, propScale);
        Prop(deco, "candle-multiple", center + new Vector3(8.3f * w, 0f, -8.3f * w), 0f, propScale);
        return room;
    }

    private static Vector3 CellCenter(Vector2Int c) => new Vector3((c.x - (Size - 1) * 0.5f) * Cell, 0f, (c.y - (Size - 1) * 0.5f) * Cell);

    // The walkway cell next to the grid on a side ('W' = x -1, 'E' = x Size, 'S' = z -1, 'N' = z Size).
    private static Vector2Int Edge(char side, int index) => side switch
    {
        'W' => C(-1, index),
        'E' => C(Size, index),
        'S' => C(index, -1),
        _ => C(index, Size),
    };

    private static Vector3 Inward(char side) => side switch
    {
        'W' => Vector3.right,
        'E' => Vector3.left,
        'S' => Vector3.forward,
        _ => Vector3.back,
    };

    private static void Pillar(Transform parent, Vector2Int c, Mats mats)
    {
        GameObject prefab = Kit(GraveyardFolder, "pillar-square") ?? Kit(GraveyardFolder, "pillar-large");
        GameObject pillar;
        if (prefab != null)
        {
            Bounds b = Measure(prefab);
            float scale = Mathf.Min(2.6f / Mathf.Max(0.01f, b.size.y), 1.5f / Mathf.Max(0.01f, Mathf.Max(b.size.x, b.size.z)));
            scale = Mathf.Max(scale, 2.2f / Mathf.Max(0.01f, b.size.y));
            var holder = New($"Pillar {c.x},{c.y}", parent);
            holder.transform.localPosition = CellCenter(c);
            Place(holder.transform, prefab, Vector3.zero, 0f, scale);
            pillar = holder;
        }
        else
        {
            pillar = New($"Pillar {c.x},{c.y}", parent);
            pillar.transform.localPosition = CellCenter(c);
            Box("Stone", pillar.transform, Vector3.up * 1.2f, new Vector3(1.2f, 2.4f, 1.2f), mats.stand);
        }
        var box = pillar.AddComponent<BoxCollider>();
        box.center = Vector3.up * 1.3f;
        box.size = new Vector3(1.5f, 2.6f, 1.5f);
        pillar.layer = GroundLayer;
        pillar.AddComponent<MirrorBlock>();
    }

    private static MirrorPiece Mirror(Transform parent, string name, Vector2Int c, bool slash, Vector2Int solved, Mats mats)
    {
        var go = New(name, parent);
        go.transform.localPosition = CellCenter(c);
        Box("Stand", go.transform, new Vector3(0f, 0.15f, 0f), new Vector3(1.3f, 0.3f, 1.3f), mats.stand);
        var pane = New("Pane", go.transform).transform;
        pane.localPosition = Vector3.zero;
        // Glass on both faces (the light can come from either side), a thin frame all round.
        Box("Glass Front", pane, new Vector3(0f, 1.2f, 0.025f), new Vector3(1.7f, 1.6f, 0.03f), mats.glass);
        Box("Glass Back", pane, new Vector3(0f, 1.2f, -0.025f), new Vector3(1.7f, 1.6f, 0.03f), mats.glass);
        Box("Frame Top", pane, new Vector3(0f, 2.04f, 0f), new Vector3(1.86f, 0.08f, 0.1f), mats.frame);
        Box("Frame Bottom", pane, new Vector3(0f, 0.36f, 0f), new Vector3(1.86f, 0.08f, 0.1f), mats.frame);
        Box("Frame L", pane, new Vector3(-0.89f, 1.2f, 0f), new Vector3(0.08f, 1.76f, 0.1f), mats.frame);
        Box("Frame R", pane, new Vector3(0.89f, 1.2f, 0f), new Vector3(0.08f, 1.76f, 0.1f), mats.frame);
        Box("Post", go.transform, new Vector3(0f, 0.35f, 0f), new Vector3(0.2f, 0.4f, 0.2f), mats.frame);
        var box = go.AddComponent<BoxCollider>();
        box.center = Vector3.up * 1.5f;
        box.size = new Vector3(1.5f, 3f, 1.5f);
        var body = go.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.interpolation = RigidbodyInterpolation.None;
        MirrorPiece piece = go.AddComponent<MirrorPiece>();
        piece.Configure(pane, slash, true, solved);
        return piece;
    }

    private static MirrorSource Source(Transform room, Vector2Int c, Vector3 inward, Mats mats, Transform lights)
    {
        var go = New("Light Source", room);
        go.transform.localPosition = CellCenter(c) - inward * 0.6f;
        go.transform.localRotation = Quaternion.LookRotation(inward, Vector3.up);
        GameObject orb = Shape(PrimitiveType.Sphere, "Glow", go.transform, Vector3.up * BeamHeight, Vector3.one * 0.45f, mats.source);
        orb.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        Box("Holder", go.transform, new Vector3(0f, BeamHeight * 0.5f, -0.1f), new Vector3(0.35f, BeamHeight, 0.35f), mats.stand);
        var lightGo = New("Light", go.transform);
        lightGo.transform.localPosition = Vector3.up * BeamHeight;
        Light light = lightGo.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.92f, 0.7f);
        light.range = 6f;
        light.intensity = 2.5f;
        light.shadows = LightShadows.None;
        MirrorSource source = go.AddComponent<MirrorSource>();
        return source;
    }

    private static MirrorTarget Crystal(Transform room, Vector2Int c, Vector3 outward, Mats mats)
    {
        // Against the wall, so the walkway beside the grid stays free.
        var go = New("Crystal (target)", room);
        go.transform.localPosition = CellCenter(c) + outward * 0.5f;
        GameObject prefab = Kit(SpaceFolder, "rock_crystals") ?? Kit(SpaceFolder, "rock_crystalsLargeA");
        if (prefab != null)
        {
            Bounds b = Measure(prefab);
            float scale = Mathf.Min(1.9f / Mathf.Max(0.01f, b.size.y), 1.2f / Mathf.Max(0.01f, Mathf.Max(b.size.x, b.size.z)));
            GameObject model = Place(go.transform, prefab, Vector3.zero, 0f, scale);
            // A cold glow even before it is hit, so it reads as "the goal" from across the room.
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>())
                renderer.sharedMaterials = Enumerable.Repeat(mats.crystalIdle, renderer.sharedMaterials.Length).ToArray();
        }
        else Shape(PrimitiveType.Capsule, "Crystal", go.transform, Vector3.up * 0.95f, new Vector3(0.7f, 0.95f, 0.7f), mats.crystalIdle);

        // Markers (hidden once the crystal is lit): a ring on the floor, a light pillar seen over
        // the walls, and a diamond at beam height on the side the light must come from.
        Vector3 inward = -outward;
        var markers = new List<Transform>();
        GameObject ring = Shape(PrimitiveType.Cylinder, "Target Ring", go.transform, Vector3.up * 0.02f, new Vector3(1.9f, 0.01f, 1.9f), mats.mark);
        GameObject ringHole = Shape(PrimitiveType.Cylinder, "Ring Inner", ring.transform, Vector3.up * 0.6f, new Vector3(0.8f, 1.2f, 0.8f), mats.stand);
        GameObject beacon = Shape(PrimitiveType.Cylinder, "Beacon", go.transform, Vector3.up * 3.5f, new Vector3(0.14f, 3.5f, 0.14f), mats.mark);
        GameObject aim = Shape(PrimitiveType.Cube, "Aim", go.transform, Vector3.up * BeamHeight + inward * 0.75f, Vector3.one * 0.32f, mats.mark);
        aim.transform.localRotation = Quaternion.LookRotation(inward) * Quaternion.Euler(0f, 0f, 45f);
        foreach (GameObject m in new[] { ring, ringHole, beacon, aim })
            m.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        markers.Add(ring.transform);
        markers.Add(beacon.transform);
        markers.Add(aim.transform);

        var lightGo = New("Glow", go.transform);
        lightGo.transform.localPosition = Vector3.up * 1.2f;
        Light light = lightGo.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(0.45f, 0.95f, 1f);
        light.range = 9f;
        light.intensity = 3f;
        light.shadows = LightShadows.None;
        var box = go.AddComponent<BoxCollider>();
        box.center = Vector3.up * 0.8f;
        box.size = new Vector3(0.6f, 1.6f, 0.6f);
        MirrorTarget target = go.AddComponent<MirrorTarget>();
        target.Configure(mats.crystalLit, light, markers.ToArray());
        return target;
    }

    private static GameObject Plate(Transform room, Vector2Int c, Mats mats)
    {
        var go = New("Reset Plate", room);
        go.transform.localPosition = CellCenter(c);
        go.layer = IgnoreRaycastLayer;
        GameObject disc = Shape(PrimitiveType.Cylinder, "Plate", go.transform, Vector3.up * 0.03f, new Vector3(1.5f, 0.03f, 1.5f), mats.plate);
        disc.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        var box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.center = Vector3.up * 0.6f;
        box.size = new Vector3(1.5f, 1.2f, 1.5f);
        go.AddComponent<MirrorResetPlate>();
        return go;
    }

    private static GameObject Door(Transform room, Vector3 local)
    {
        GameObject prefab = Kit(CaveFolder, "gate-metal-bars");
        Bounds b = Measure(prefab);
        float yaw = b.size.x >= b.size.z ? 0f : 90f;   // wide side across the +Z opening (along X)
        var holder = New("Door", room);
        holder.transform.localPosition = local;
        Place(holder.transform, prefab, Vector3.zero, yaw, 1f);
        var box = holder.AddComponent<BoxCollider>();
        box.center = Vector3.up * (b.size.y * 0.5f);
        box.size = new Vector3(Mathf.Max(b.size.x, b.size.z), b.size.y, 0.6f);
        return holder;
    }

    // ---------- treasure ----------

    private static MirrorTreasure BuildTreasure(Transform parent, Vector3 at, Mats mats, Transform lights, Transform deco)
    {
        var go = New("Treasure", parent);
        go.transform.localPosition = at + Vector3.forward * 1.5f;
        go.layer = IgnoreRaycastLayer;
        Transform chest = null;
        GameObject chestPrefab = Kit(PlatformerFolder, "chest");
        if (chestPrefab != null)
        {
            Bounds b = Measure(chestPrefab);
            var holder = New("Chest", go.transform);
            Place(holder.transform, chestPrefab, Vector3.zero, 180f, 1.1f / Mathf.Max(0.01f, b.size.y));
            chest = holder.transform;
            var solid = New("Chest Collider", holder.transform);
            solid.layer = GroundLayer;
            var sb = solid.AddComponent<BoxCollider>();
            sb.center = Vector3.up * 0.55f;
            sb.size = new Vector3(1.4f, 1.1f, 1f);
        }
        Box("Pedestal", go.transform, new Vector3(0f, -0.05f, 0f), new Vector3(2.4f, 0.1f, 2.4f), mats.frame);

        GameObject coin = Kit(PlatformerFolder, "coin-gold");
        if (coin != null)
        {
            float scale = 0.45f / Mathf.Max(0.01f, Measure(coin).size.y);
            for (int i = 0; i < 8; i++)   // a few already on the floor
            {
                float a = i * 0.8f;
                Place(deco, coin, at + Vector3.forward * 1.5f + new Vector3(Mathf.Cos(a) * (1.6f + i * 0.1f), 0f, Mathf.Sin(a) * 1.5f), i * 50f, scale);
            }
        }
        Lamp(lights, at + Vector3.up * 3.2f, 11f, 1.8f);
        // Coins bursting out + light: the same effect as stage 4's exit chest.
        TreasureFxBuilder.Add(go, chest, UndoName);
        Prop(deco, "fire-basket", at + new Vector3(-3.5f, 0f, 3.5f), 0f, propScale * 1.2f);
        Prop(deco, "fire-basket", at + new Vector3(3.5f, 0f, 3.5f), 0f, propScale * 1.2f);

        var trigger = go.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.center = Vector3.up * 1f;
        trigger.size = new Vector3(3f, 2f, 3f);
        return go.AddComponent<MirrorTreasure>();
    }

    // Floor height at the top of the stairs (ray down onto the stairs' own colliders).
    private static float TopHeight(GameObject stairs, Vector3 worldAbove)
    {
        const float fallback = 4.5f;
        if (stairs == null) return fallback;
        Physics.SyncTransforms();
        float baseY = stairs.transform.parent.TransformPoint(Vector3.zero).y;
        Vector3 from = new Vector3(worldAbove.x, baseY + 6.5f, worldAbove.z);
        if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, 8f, 1 << GroundLayer, QueryTriggerInteraction.Ignore))
        {
            float h = hit.point.y - baseY;
            if (h > 1f && h < 6.4f) return Mathf.Round(h * 100f) / 100f;
        }
        Debug.LogWarning($"[Mirror] could not measure the top of the stairs; using {fallback} m. Move the Treasure Room by hand if it does not meet the stairs.");
        return fallback;
    }

    // ---------- materials ----------

    private class Mats
    {
        public readonly Material glass, stand, frame, beam, source, crystalLit, crystalIdle, plate, mark;

        public Mats()
        {
            glass = Lit("MirrorGlass", new Color(0.78f, 0.88f, 0.98f), 0.9f, 0.95f, Color.black);
            stand = Lit("MirrorStand", new Color(0.32f, 0.31f, 0.34f), 0f, 0.15f, Color.black);
            frame = Lit("MirrorFrame", new Color(0.85f, 0.66f, 0.28f), 0.7f, 0.55f, Color.black);
            crystalLit = Lit("MirrorCrystalLit", new Color(0.55f, 0.95f, 1f), 0f, 0.8f, new Color(0.5f, 1.6f, 2f));
            crystalIdle = Lit("MirrorCrystalIdleGlow", new Color(0.35f, 0.7f, 0.85f), 0f, 0.7f, new Color(0.08f, 0.35f, 0.5f));
            mark = Unlit("MirrorTargetMark", new Color(0.35f, 0.95f, 1f));
            plate = Lit("MirrorResetPlate", new Color(0.2f, 0.6f, 0.9f), 0f, 0.4f, new Color(0.1f, 0.4f, 0.8f));
            beam = Unlit("MirrorBeam", new Color(1f, 0.93f, 0.62f));
            source = Unlit("MirrorSourceGlow", new Color(1f, 0.97f, 0.85f));
        }

        private static Material Lit(string name, Color color, float metallic, float smoothness, Color emission)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            material = new Material(shader) { name = name };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            material.color = color;
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (emission.maxColorComponent > 0f)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emission);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            Save(material, path);
            return material;
        }

        private static Material Unlit(string name, Color color)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            material = new Material(shader) { name = name };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            material.color = color;
            Save(material, path);
            return material;
        }

        private static void Save(Material material, string path)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
            if (!AssetDatabase.IsValidFolder(MaterialFolder)) AssetDatabase.CreateFolder("Assets/Materials", "NaWaBa");
            AssetDatabase.CreateAsset(material, path);
        }
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
                    EditorUtility.DisplayDialog("NaWaBa", "Stage_Crypt / Stage_Grave / Stage_Gate 씬이 없어 새 씬을 만들 수 없습니다.", "OK");
                    return false;
                }
                if (!EditorUtility.DisplayDialog("NaWaBa · 거울 동굴",
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

    private static void Convert(UnityEngine.SceneManagement.Scene scene)
    {
        var controllers = new List<MonoBehaviour>();
        controllers.AddRange(Object.FindObjectsByType<CryptRunStageController>(FindObjectsInactive.Include, FindObjectsSortMode.None));
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
            if (host.GetComponent<MirrorStageController>() != null) continue;
            var mirror = host.AddComponent<MirrorStageController>();
            var to = new SerializedObject(mirror);
            to.FindProperty("actor").objectReferenceValue = actor;
            to.FindProperty("inputRouter").objectReferenceValue = router;
            to.FindProperty("gateway").objectReferenceValue = gateway;
            to.FindProperty("statusText").objectReferenceValue = status;
            to.ApplyModifiedPropertiesWithoutUndo();
        }
        foreach (string name in new[] { "Crypt (built)", "Graveyard (built)", "GatePuzzle (built)", ClimbCourseBuilder.SharedCastleName })
        {
            GameObject go = Find(scene, name);
            if (go != null) Object.DestroyImmediate(go);
        }

        // Underground, but bright enough to read the puzzle.
        foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (light.type == LightType.Directional)
            {
                light.color = new Color(0.6f, 0.65f, 0.9f);
                light.intensity = Mathf.Min(light.intensity, 0.5f);
                light.transform.rotation = Quaternion.Euler(70f, -30f, 0f);
            }
        var fog = new Color(0.03f, 0.03f, 0.05f);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.24f, 0.23f, 0.28f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogDensity = 0.018f;
        RenderSettings.fogColor = fog;
        foreach (ClimbCameraController follow in Object.FindObjectsByType<ClimbCameraController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Camera camera = follow.GetComponent<Camera>();
            if (camera == null) continue;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = fog;
        }

        Debug.Log("[Mirror] Stage_Mirror created. Root objects kept: " +
            string.Join(", ", scene.GetRootGameObjects().Select(g => g.name)) +
            "\nRemove any leftover scenery from the copied stage you do not want.");
    }

    private static void AddToBuildProfile()
    {
        var scenes = EditorBuildSettings.scenes.ToList();
        if (scenes.Any(s => s.path == ScenePath)) return;
        int after = scenes.FindIndex(s => s.path.EndsWith("/" + AfterScene + ".unity"));
        if (after < 0) after = scenes.FindIndex(s => s.path.EndsWith("/Stage_Grave.unity"));
        var entry = new EditorBuildSettingsScene(ScenePath, true);
        if (after >= 0) scenes.Insert(after + 1, entry);
        else scenes.Add(entry);
        EditorBuildSettings.scenes = scenes.ToArray();
        Debug.Log("[Mirror] added to the Build Profile scene list: " + ScenePath);
    }

    private static void AddToStageList()
    {
        var list = AssetDatabase.LoadAssetAtPath<NaWaBaStageList>(StageListPath);
        if (list == null)
        {
            Debug.LogWarning("[Mirror] " + StageListPath + " not found: add Stage_Mirror to the stage list by hand.");
            return;
        }
        if (list.IndexOf(SceneName) >= 0) return;
        Undo.RecordObject(list, "Add Stage_Mirror");
        var entry = new NaWaBaStageList.Stage { sceneName = SceneName, title = StageTitle, region = Region };
        int after = list.IndexOf(AfterScene);
        if (after < 0) after = list.IndexOf("Stage_Grave");
        if (after >= 0) list.stages.Insert(after + 1, entry);
        else list.stages.Add(entry);
        EditorUtility.SetDirty(list);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Mirror] added {SceneName} to NaWaBaStages (region {Region}).");
    }

    // ---------- kit pieces (same method as the crypt builder) ----------

    // Gives a cave piece mesh colliders on the Ground layer (the player walks in this stage).
    private static GameObject Solid(GameObject piece)
    {
        if (piece == null) return null;
        foreach (MeshFilter filter in piece.GetComponentsInChildren<MeshFilter>())
        {
            if (filter.sharedMesh == null) continue;
            filter.gameObject.layer = GroundLayer;
            if (filter.GetComponent<Collider>() == null) filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
        }
        piece.layer = GroundLayer;
        return piece;
    }

    private static GameObject Tile(Transform parent, string name, Vector3 center, params Vector3[] open)
    {
        GameObject prefab = Kit(CaveFolder, name);
        if (prefab == null) return null;
        float yaw = YawFor(prefab, open, exact: true);
        return Place(parent, prefab, center, yaw, Spread);
    }

    // A room with the given sides open; every other opening is shut with a rock gate.
    private static void Room(Transform parent, Transform deco, string name, Vector3 center, Vector3[] keepOpen, string label)
    {
        GameObject prefab = Kit(CaveFolder, name);
        if (prefab == null) return;
        float yaw = YawFor(prefab, keepOpen, exact: false);
        GameObject room = Solid(Place(parent, prefab, center, yaw, Spread));
        room.name = label;
        Bounds b = Measure(prefab);
        float half = Mathf.Max(b.extents.x, b.extents.z) * Wide;
        bool[] open = OpenSides(prefab);
        GameObject rock = Kit(CaveFolder, "gate-rock");
        if (rock == null) return;
        Bounds rb = Measure(rock);
        bool rockAlongX = rb.size.x >= rb.size.z;
        float rockHalf = Mathf.Min(rb.extents.x, rb.extents.z) * Wide;
        Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
        for (int i = 0; i < Sides.Length; i++)
        {
            if (!open[i]) continue;
            Vector3 dir = Round(turn * Sides[i]);
            if (keepOpen.Any(e => Vector3.Dot(dir, e) > 0.9f)) continue;
            bool sideAlongX = Mathf.Abs(dir.z) > 0.5f;
            float rockYaw = sideAlongX == rockAlongX ? 0f : 90f;
            GameObject shut = Solid(Place(deco, rock, center + dir * (half - rockHalf), rockYaw, Spread));
            shut.name = "Shut " + label;
        }
    }

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
        Debug.LogWarning($"[Mirror] no turn of {prefab.name} matches the wanted openings " +
            $"(open sides +X {sides[0]}, -X {sides[1]}, +Z {sides[2]}, -Z {sides[3]}); placed unturned, check it by hand.");
        return 0f;
    }

    private static readonly Dictionary<GameObject, bool[]> openCache = new Dictionary<GameObject, bool[]>();

    // Which of +X, -X, +Z, -Z are passable at body height (short horizontal rays from just outside
    // each edge, back faces count). See CryptDescentBuilder for the details.
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
                result[i] = walls * 2 < rays;
            }
        }
        finally
        {
            Physics.queriesHitBackfaces = backfaces;
            Object.DestroyImmediate(probe);
        }
        openCache[prefab] = result;
        return result;
    }

    private static Vector3 Round(Vector3 v) => new Vector3(Mathf.Round(v.x), 0f, Mathf.Round(v.z));

    // ---------- helpers ----------

    private static void Lamp(Transform parent, Vector3 position, float range, float intensity)
    {
        var go = New("Lamp", parent);
        go.transform.localPosition = position;
        Light light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.7f, 0.4f);
        light.range = range;
        light.intensity = intensity;
        light.shadows = LightShadows.None;
    }

    private static void Prop(Transform parent, string name, Vector3 bottom, float yaw, float scale)
    {
        GameObject prefab = Kit(GraveyardFolder, name);
        if (prefab != null) Place(parent, prefab, bottom, yaw, scale);
    }

    // Primitive with a material asset (never the default material) and no collider.
    private static GameObject Shape(PrimitiveType type, string name, Transform parent, Vector3 local, Vector3 scale, Material material)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        Undo.RegisterCreatedObjectUndo(go, UndoName);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = local;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = material;
        return go;
    }

    private static GameObject Box(string name, Transform parent, Vector3 local, Vector3 size, Material material) =>
        Shape(PrimitiveType.Cube, name, parent, local, size, material);

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
        if (found == null) Debug.LogWarning("[Mirror] prefab not found: " + key);
        cache[key] = found;
        return found;
    }

    private static Vector3 Spread => new Vector3(Wide, 1f, Wide);

    private static GameObject Place(Transform parent, GameObject prefab, Vector3 bottom, float yaw, float scale) =>
        Place(parent, prefab, bottom, yaw, Vector3.one * scale);

    // Kit model with its meshes' bottom-centre at "bottom"; "scale" is along the model's own axes.
    private static GameObject Place(Transform parent, GameObject prefab, Vector3 bottom, float yaw, Vector3 scale)
    {
        Bounds b = Measure(prefab);
        var piece = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.gameObject.scene);
        Undo.RegisterCreatedObjectUndo(piece, UndoName);
        piece.transform.SetParent(parent, false);
        piece.transform.localScale = Vector3.Scale(piece.transform.localScale, scale);
        Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
        piece.transform.localRotation = rotation;
        piece.transform.localPosition = bottom - rotation * Vector3.Scale(new Vector3(b.center.x, b.min.y, b.center.z), scale);
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
