using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Shared first-run steps for the builder of a new stage scene (the regions after 무덤: 숲, 도시,
// 우주 …), so each new builder only builds its own map:
//   1. OpenOrCopy        - open the stage scene, or create it by copying an existing stage
//                          (player, cameras, UI, video sender stay; nothing else is assumed).
//   2. ReplaceController - swap the copied stage controller (any IStageFlow with the usual
//                          actor / inputRouter / gateway / statusText fields) for the new one.
//   3. Register          - add the scene to the Build Profile and to NaWaBaStages in its region
//                          (after the region's last stage, keeping regions in order).
// The region itself (title / description) is in NaWaBaStages.regions; its world map island is in
// WorldMapBuilder (Spots + KitIslands).
public static class StageSceneSetup
{
    private const string StageListPath = "Assets/Resources/NaWaBaStages.asset";

    // True when the scene is open (as the active scene) afterwards. "convert" runs once, on a new copy.
    public static bool OpenOrCopy(string scenePath, string[] sources, string dialogTitle, System.Action<Scene> convert)
    {
        Scene active = EditorSceneManager.GetActiveScene();
        if (active.path == scenePath) return true;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;
        if (System.IO.File.Exists(scenePath))
        {
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            return true;
        }
        string source = sources.FirstOrDefault(System.IO.File.Exists);
        if (source == null)
        {
            EditorUtility.DisplayDialog("NaWaBa", "복사할 스테이지 씬이 없습니다: " + string.Join(", ", sources), "OK");
            return false;
        }
        if (!EditorUtility.DisplayDialog(dialogTitle,
                System.IO.Path.GetFileNameWithoutExtension(source) + " 씬을 복사해 " + scenePath + " 를 만듭니다.\n" +
                "(플레이어·카메라·UI·영상 전송은 그대로 쓰고, 맵은 빌더가 만듭니다)", "만들기", "취소"))
            return false;
        if (!AssetDatabase.CopyAsset(source, scenePath))
        {
            EditorUtility.DisplayDialog("NaWaBa", scenePath + " 복사에 실패했습니다.", "OK");
            return false;
        }
        Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        convert?.Invoke(scene);
        EditorSceneManager.SaveScene(scene);
        return true;
    }

    // Replaces every stage controller (IStageFlow) in the scene by T on the same object, keeping
    // the shared references. Returns the new controller.
    public static T ReplaceController<T>(Scene scene) where T : MonoBehaviour, IStageFlow
    {
        T result = null;
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (MonoBehaviour old in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (old == null || !(old is IStageFlow)) continue;
                if (old is T kept) { result = kept; continue; }
                var from = new SerializedObject(old);
                Object actor = Ref(from, "actor"), router = Ref(from, "inputRouter");
                Object gateway = Ref(from, "gateway"), status = Ref(from, "statusText");
                GameObject host = old.gameObject;
                Object.DestroyImmediate(old);
                T controller = host.GetComponent<T>();
                if (controller == null) controller = host.AddComponent<T>();
                var to = new SerializedObject(controller);
                Set(to, "actor", actor);
                Set(to, "inputRouter", router);
                Set(to, "gateway", gateway);
                Set(to, "statusText", status);
                to.ApplyModifiedPropertiesWithoutUndo();
                result = controller;
            }
        if (result == null) Debug.LogWarning($"[StageSetup] no stage controller found to replace with {typeof(T).Name}.");
        return result;
    }

    // Removes copied scenery roots by name (e.g. "Crypt (built)").
    public static void RemoveRoots(Scene scene, params string[] names)
    {
        foreach (GameObject go in scene.GetRootGameObjects())
            if (names.Contains(go.name)) Object.DestroyImmediate(go);
    }

    // Build Profile + NaWaBaStages entry (after the last stage of this region or of an earlier one).
    public static void Register(string scenePath, string title, int region)
    {
        string sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePath);
        var list = AssetDatabase.LoadAssetAtPath<NaWaBaStageList>(StageListPath);
        string after = null;
        if (list == null)
            Debug.LogWarning("[StageSetup] " + StageListPath + " not found: add " + sceneName + " to the stage list by hand.");
        else
        {
            if (region >= list.RegionCount)
                Debug.LogWarning($"[StageSetup] region {region} has no entry in NaWaBaStages.regions yet (title / description).");
            int at = list.IndexOf(sceneName);
            if (at < 0)
            {
                at = list.stages.FindLastIndex(s => s != null && s.region <= region) + 1;
                Undo.RecordObject(list, "Add " + sceneName);
                list.stages.Insert(at, new NaWaBaStageList.Stage { sceneName = sceneName, title = title, region = region });
                EditorUtility.SetDirty(list);
                AssetDatabase.SaveAssets();
                Debug.Log($"[StageSetup] added {sceneName} to NaWaBaStages (region {region}).");
            }
            if (at > 0) after = list.stages[at - 1].sceneName;
        }

        var scenes = EditorBuildSettings.scenes.ToList();
        if (scenes.Any(s => s.path == scenePath)) return;
        int index = after != null ? scenes.FindIndex(s => s.path.EndsWith("/" + after + ".unity")) : -1;
        var entry = new EditorBuildSettingsScene(scenePath, true);
        if (index >= 0) scenes.Insert(index + 1, entry);
        else scenes.Add(entry);
        EditorBuildSettings.scenes = scenes.ToArray();
        Debug.Log("[StageSetup] added to the Build Profile scene list: " + scenePath);
    }

    private static Object Ref(SerializedObject so, string name) => so.FindProperty(name)?.objectReferenceValue;

    private static void Set(SerializedObject so, string name, Object value)
    {
        SerializedProperty property = so.FindProperty(name);
        if (property != null && property.propertyType == SerializedPropertyType.ObjectReference)
            property.objectReferenceValue = value;
    }
}
