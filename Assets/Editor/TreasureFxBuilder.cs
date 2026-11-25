using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Builds the "treasure found" effect (TreasureChestFx) around a chest model: hidden gold coins
// that burst out, and a warm light. Used by the mirror cave builder and the keep builder.
// Menu: NaWaBa > 성 보물상자 연출 추가 (Stage_Keep) adds it to the exit chest of an existing
// Stage_Keep without rebuilding the keep (nothing else in the scene changes).
public static class TreasureFxBuilder
{
    private const string PlatformerFolder = "Assets/Prefabs/3D_Back/New Folder";
    public const string HostName = "Treasure FX";

    // Adds the effect on "host" (placed at the chest's foot, same parent space as the chest).
    public static TreasureChestFx Add(GameObject host, Transform chest, string undoName)
    {
        var coins = new List<Transform>();
        GameObject coin = FindPrefab("coin-gold");
        if (coin != null)
        {
            float scale = 0.45f / Mathf.Max(0.01f, Height(coin));
            for (int i = 0; i < 12; i++)
            {
                float a = i * Mathf.PI * 2f / 12f;
                float r = 1.1f + (i % 2) * 0.35f;
                var c = (GameObject)PrefabUtility.InstantiatePrefab(coin, host.scene);
                Undo.RegisterCreatedObjectUndo(c, undoName);
                c.name = "Coin (burst)";
                c.transform.SetParent(host.transform, false);
                c.transform.localScale = c.transform.localScale * scale;
                c.transform.localPosition = new Vector3(Mathf.Cos(a) * r, 1.3f + (i % 3) * 0.35f, Mathf.Sin(a) * r);
                c.transform.localRotation = Quaternion.Euler(0f, i * 30f, 0f);
                coins.Add(c.transform);
            }
        }
        else Debug.LogWarning("[Treasure] coin-gold prefab not found in " + PlatformerFolder + " (no coins).");

        var glowGo = new GameObject("Treasure Glow");
        Undo.RegisterCreatedObjectUndo(glowGo, undoName);
        glowGo.transform.SetParent(host.transform, false);
        glowGo.transform.localPosition = Vector3.up * 1.6f;
        Light glow = glowGo.AddComponent<Light>();
        glow.type = LightType.Point;
        glow.color = new Color(1f, 0.82f, 0.35f);
        glow.range = 12f;
        glow.intensity = 0f;
        glow.shadows = LightShadows.None;

        TreasureChestFx fx = host.GetComponent<TreasureChestFx>();
        if (fx == null) fx = Undo.AddComponent<TreasureChestFx>(host);
        fx.Configure(chest, coins.ToArray(), glow);
        EditorUtility.SetDirty(fx);
        return fx;
    }

    [MenuItem("NaWaBa/성 보물상자 연출 추가 (Stage_Keep)")]
    private static void AddToKeep()
    {
        if (EditorApplication.isPlaying) return;
        var scene = EditorSceneManager.GetActiveScene();
        KeepGoal goal = Object.FindFirstObjectByType<KeepGoal>();
        if (goal == null || Object.FindFirstObjectByType<KeepStageController>() == null)
        {
            EditorUtility.DisplayDialog("NaWaBa", "Stage_Keep 씬을 열고 실행하세요 (KeepGoal이 없습니다).", "OK");
            return;
        }

        // The exit chest: the "chest" model nearest to the exit.
        Transform chest = null;
        float best = 6f * 6f;
        foreach (Transform t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
        {
            if (!t.name.StartsWith("chest") || t.GetComponentInParent<TreasureChestFx>() != null) continue;
            float d = (t.position - goal.transform.position).sqrMagnitude;
            if (d < best) { best = d; chest = t; }
        }
        if (chest == null)
        {
            EditorUtility.DisplayDialog("NaWaBa", "출구 근처(6 m)에서 chest 모델을 찾지 못했습니다.", "OK");
            return;
        }

        const string undo = "Add Treasure FX";
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        foreach (TreasureChestFx old in Object.FindObjectsByType<TreasureChestFx>(FindObjectsSortMode.None))
            if (old.gameObject.scene == scene && old.name == HostName) Undo.DestroyObjectImmediate(old.gameObject);
        var host = new GameObject(HostName);
        Undo.RegisterCreatedObjectUndo(host, undo);
        host.transform.SetParent(chest.parent, false);
        host.transform.position = chest.position;
        host.transform.rotation = Quaternion.Euler(0f, chest.eulerAngles.y, 0f);
        Add(host, chest, undo);
        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = host;
        Debug.Log("[Treasure] added the treasure effect to " + chest.name + " (save the scene).");
        EditorUtility.DisplayDialog("NaWaBa", "출구 보물상자에 연출을 추가했습니다: " + chest.name +
            "\n씬을 저장하세요 (Ctrl+S).", "OK");
    }

    private static GameObject FindPrefab(string exactName)
    {
        if (!AssetDatabase.IsValidFolder(PlatformerFolder)) return null;
        foreach (string guid in AssetDatabase.FindAssets(exactName + " t:Prefab", new[] { PlatformerFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (System.IO.Path.GetFileNameWithoutExtension(path) == exactName)
                return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }
        return null;
    }

    private static float Height(GameObject prefab)
    {
        var probe = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        Renderer[] renderers = probe.GetComponentsInChildren<Renderer>();
        Bounds b = renderers.Length > 0 ? renderers[0].bounds : new Bounds(Vector3.zero, Vector3.one);
        foreach (Renderer r in renderers) b.Encapsulate(r.bounds);
        Object.DestroyImmediate(probe);
        return b.size.y;
    }
}
