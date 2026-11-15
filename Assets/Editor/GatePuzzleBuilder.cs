using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Menu: NaWaBa > 성문 퍼즐 만들기 (Stage_Gate)
// Builds stage 3 as editable scene objects:
//   "GatePuzzle (built)" - snowy courtyard in front of the gate, 4 coloured plates, 4 lamps
//                          above the doorway, the entrance trigger behind the gate, trees.
//   "Castle (shared)"    - the same castle as stage 2 (gate wall + background castle), with a
//                          GateDoor on the closed gate so the puzzle can raise it.
// Everything can be moved afterwards (plates, lamps, trees ...). Running it again rebuilds both.
public static class GatePuzzleBuilder
{
    private const string RootName = "GatePuzzle (built)";
    public const float GateZ = 16f;                 // where the gate wall starts (player at 0,0,0)
    public const float FloorBack = -6f, HalfWidth = 12f;  // courtyard edges (local)
    private static readonly Color[] Colours =
    {
        new Color(0.95f, 0.25f, 0.25f), new Color(0.25f, 0.5f, 1f),
        new Color(0.3f, 0.85f, 0.35f), new Color(1f, 0.85f, 0.2f)
    };

    [MenuItem("NaWaBa/성문 퍼즐 만들기 (Stage_Gate)")]
    private static void Build()
    {
        if (EditorApplication.isPlaying) return;
        GateStageController stage = Object.FindFirstObjectByType<GateStageController>();
        ClimbActorController actor = Object.FindFirstObjectByType<ClimbActorController>();
        if (stage == null || actor == null)
        {
            EditorUtility.DisplayDialog("NaWaBa", "Stage_Gate 씬을 열고 실행하세요.", "OK");
            return;
        }
        var scene = stage.gameObject.scene;
        GameObject oldRoot = Find(scene, RootName), oldCastle = Find(scene, ClimbCourseBuilder.SharedCastleName);
        if ((oldRoot != null || oldCastle != null) &&
            !EditorUtility.DisplayDialog("NaWaBa · 성문 퍼즐",
                "이미 만들어진 퍼즐/성이 있습니다. 다시 만들면 직접 수정한 내용이 사라집니다.\n(Ctrl+Z로 되돌릴 수 있음)",
                "다시 만들기", "취소"))
            return;

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Build Gate Puzzle");
        if (oldRoot != null) Undo.DestroyObjectImmediate(oldRoot);
        if (oldCastle != null) Undo.DestroyObjectImmediate(oldCastle);

        ClimbCourse settings = ClimbCourseBuilder.CastleSettings();
        int groundLayer = GroundLayer(actor);
        Vector3 start = actor.transform.position;

        var root = New(RootName, null);
        root.transform.position = new Vector3(start.x, 0f, start.z);
        Transform t = root.transform;

        // Castle first (its wall depth decides where the floor and entrance go).
        var castle = New(ClimbCourseBuilder.SharedCastleName, null);
        castle.transform.SetPositionAndRotation(t.TransformPoint(new Vector3(0f, start.y, GateZ)), Quaternion.identity);
        ClimbCourseBuilder.BuildCastleInto(castle, settings);
        float wallDepth = ClimbCourseBuilder.KitWallDepth(settings);
        int doors = 0;
        foreach (Transform child in castle.GetComponentsInChildren<Transform>(true))
        {
            if (child.name == "Gate (closed)" || child.name == "DoorL" || child.name == "DoorR")
            {
                Undo.AddComponent<GateDoor>(child.gameObject);
                doors++;
            }
        }

        BuildCourtyard(t, start.y, settings, groundLayer, wallDepth, true);

        // Clouds below the floating courtyard.
        var clouds = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Undo.RegisterCreatedObjectUndo(clouds, "Build Gate Puzzle");
        clouds.name = "CloudLayer (visual)";
        Object.DestroyImmediate(clouds.GetComponent<Collider>());
        clouds.transform.SetParent(t, false);
        clouds.transform.localPosition = new Vector3(0f, start.y - 8f, GateZ);
        clouds.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        clouds.transform.localScale = new Vector3(400f, 400f, 1f);
        clouds.GetComponent<Renderer>().sharedMaterial = ClimbCourseBuilder.MaterialAsset("Clouds", settings.cloudColor);

        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = root;
        EditorUtility.DisplayDialog("NaWaBa · 성문 퍼즐",
            $"발판 {Colours.Length}개, 등불 {Colours.Length}개, 성문 {doors}개를 만들었습니다.\n\n" +
            "발판·등불·나무는 자유롭게 옮겨도 됩니다 (발판 번호 = 등불 번호 = 색).\n씬을 저장(Ctrl+S)하세요.", "OK");
    }

    // The courtyard in front of the gate, in "t"'s local space (stage start = local origin,
    // gate wall at local z = GateZ). Stage 3 uses it as its level; stage 2 ends on the same
    // courtyard so the arrival looks exactly like the start of stage 3.
    public static void BuildCourtyard(Transform t, float y, ClimbCourse settings, int groundLayer,
        float wallDepth, bool withEntrance)
    {
        // Courtyard floor: one invisible solid box + snow tiles on top (visual).
        float floorBack = FloorBack, floorFront = GateZ + wallDepth + 6f, halfWidth = HalfWidth;
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(floor, "Build Gate Puzzle");
        floor.name = "FloorCollider";
        floor.layer = groundLayer;
        floor.transform.SetParent(t, false);
        floor.transform.localPosition = new Vector3(0f, y - 1.5f, (floorBack + floorFront) * 0.5f);
        floor.transform.localScale = new Vector3(halfWidth * 2f, 3f, floorFront - floorBack);
        floor.GetComponent<Renderer>().sharedMaterial = ClimbCourseBuilder.MaterialAsset("CourtyardSnow", new Color(0.92f, 0.95f, 1f));
        TileFloor(t, floorBack, floorFront, halfWidth, y);

        // Plates in a row in front of the gate, lamps above the doorway (same order and colours).
        var plates = New("Plates", t);
        var lamps = New("Lamps", t);
        for (int i = 0; i < Colours.Length; i++)
        {
            float x = (i - 1.5f) * 4f;
            MakePlate(plates.transform, i, new Vector3(x, y, GateZ - 7f));
            MakeLamp(lamps.transform, i, new Vector3(x * 0.75f, y + Mathf.Max(settings.wallHeight, 4f) + 1.6f, GateZ - 0.9f));
        }

        // Walking through the opened gate ends the stage.
        if (!withEntrance) { Scenery(t, y, halfWidth, floorBack); return; }
        var entrance = New("GateEntrance", t);
        entrance.layer = 2;
        entrance.transform.localPosition = new Vector3(0f, y + 1.5f, GateZ + wallDepth * 0.75f);
        var box = entrance.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(3f, 3f, 1.5f);
        entrance.AddComponent<GateEntrance>();

        Scenery(t, y, halfWidth, floorBack);

    }

    private static void MakePlate(Transform parent, int index, Vector3 position)
    {
        var plate = New($"Plate {index + 1}", parent);
        plate.layer = 2;
        plate.transform.localPosition = position;
        var trigger = plate.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.center = new Vector3(0f, 1f, 0f);
        trigger.size = new Vector3(2.6f, 2f, 2.6f);

        var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Undo.RegisterCreatedObjectUndo(disc, "Build Gate Puzzle");
        disc.name = "Disc";
        Object.DestroyImmediate(disc.GetComponent<Collider>());
        disc.transform.SetParent(plate.transform, false);
        disc.transform.localPosition = new Vector3(0f, 0.04f, 0f);
        disc.transform.localScale = new Vector3(2.6f, 0.04f, 2.6f);
        Renderer renderer = disc.GetComponent<Renderer>();
        renderer.sharedMaterial = ClimbCourseBuilder.MaterialAsset($"Plate{index + 1}", Colours[index] * 0.6f);

        Undo.AddComponent<MemoryPlate>(plate).Configure(index, Colours[index], renderer);
    }

    private static void MakeLamp(Transform parent, int index, Vector3 position)
    {
        var lamp = New($"Lamp {index + 1}", parent);
        lamp.transform.localPosition = position;
        var bulb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Undo.RegisterCreatedObjectUndo(bulb, "Build Gate Puzzle");
        bulb.name = "Bulb";
        Object.DestroyImmediate(bulb.GetComponent<Collider>());
        bulb.transform.SetParent(lamp.transform, false);
        bulb.transform.localScale = Vector3.one * 1.4f;
        Renderer renderer = bulb.GetComponent<Renderer>();
        renderer.sharedMaterial = ClimbCourseBuilder.MaterialAsset($"Lamp{index + 1}", Colours[index] * 0.4f);
        var mount = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(mount, "Build Gate Puzzle");
        mount.name = "Mount";
        Object.DestroyImmediate(mount.GetComponent<Collider>());
        mount.transform.SetParent(lamp.transform, false);
        mount.transform.localPosition = new Vector3(0f, -0.9f, 0.3f);
        mount.transform.localScale = new Vector3(0.5f, 0.6f, 0.6f);
        mount.GetComponent<Renderer>().sharedMaterial = ClimbCourseBuilder.MaterialAsset("CastleStoneDark", new Color(0.45f, 0.43f, 0.4f));
        Undo.AddComponent<MemoryLamp>(lamp).Configure(index, Colours[index], renderer);
    }

    private static void TileFloor(Transform parent, float zFrom, float zTo, float halfWidth, float y)
    {
        GameObject tile = ClimbCourseBuilder.FindKitPrefab("block-snow-large");
        if (tile == null) return;
        var tiles = New("FloorTiles", parent);
        const float size = 3f;
        for (float x = -halfWidth + size * 0.5f; x < halfWidth; x += size)
        for (float z = zFrom + size * 0.5f; z < zTo; z += size)
        {
            var piece = (GameObject)PrefabUtility.InstantiatePrefab(tile, parent.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(piece, "Build Gate Puzzle");
            piece.transform.SetParent(tiles.transform, false);
            Bounds b = RendererBounds(piece);
            Vector3 scale = new Vector3(size / b.size.x, 1f / b.size.y, size / b.size.z);
            piece.transform.localScale = Vector3.Scale(piece.transform.localScale, scale);
            piece.transform.localPosition = new Vector3(x, y - 1f, z);
        }
    }

    private static void Scenery(Transform parent, float y, float halfWidth, float back)
    {
        var group = New("Scenery", parent);
        string[] trees = { "tree-pine-snow", "tree-snow", "tree-pine-snow-small" };
        var random = new System.Random(5);
        for (int i = 0; i < 12; i++)
        {
            GameObject prefab = ClimbCourseBuilder.FindKitPrefab(trees[random.Next(trees.Length)]);
            if (prefab == null) continue;
            float side = i % 2 == 0 ? -1f : 1f;
            var tree = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(tree, "Build Gate Puzzle");
            tree.transform.SetParent(group.transform, false);
            float height = Mathf.Lerp(3f, 5f, (float)random.NextDouble());
            Bounds b = RendererBounds(tree);
            tree.transform.localScale *= height / Mathf.Max(0.01f, b.size.y);
            tree.transform.localPosition = new Vector3(side * (halfWidth - 1.5f - (float)random.NextDouble() * 1.5f), y,
                back + 2f + (i / 2) * 2.6f);
            tree.transform.localRotation = Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f);
        }
    }

    private static Bounds RendererBounds(GameObject go)
    {
        Vector3 keep = go.transform.position;
        Quaternion keepRotation = go.transform.rotation;
        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
        Bounds b = renderers.Length > 0 ? renderers[0].bounds : new Bounds(Vector3.zero, Vector3.one);
        foreach (Renderer r in renderers) b.Encapsulate(r.bounds);
        go.transform.SetPositionAndRotation(keep, keepRotation);
        return b;
    }

    private static GameObject New(string name, Transform parent)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Build Gate Puzzle");
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
