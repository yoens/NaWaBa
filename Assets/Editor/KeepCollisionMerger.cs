using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Menu: NaWaBa > 성 내부 충돌 합치기 (Stage_Keep)
// Bakes KeepCollision.Merge into the open Stage_Keep scene (one Undo step). The stage also
// merges on start when the scene has not been baked, so this is optional - it just lets you
// see the merged boxes in the editor. Run it again after moving floor tiles or wall cells.
// KeepBuilder runs it after building.
public static class KeepCollisionMerger
{
    private const string Undoing = "Merge Keep Collision";

    [MenuItem("NaWaBa/성 내부 충돌 합치기 (Stage_Keep)")]
    private static void MergeMenu()
    {
        if (EditorApplication.isPlaying) return;
        Transform root = null;
        for (int i = 0; i < SceneManager.sceneCount && root == null; i++)
            root = KeepCollision.FindRoot(SceneManager.GetSceneAt(i));
        if (root == null)
        {
            EditorUtility.DisplayDialog("NaWaBa", "Stage_Keep 씬을 열고, 성 내부(\"Keep (built)\")가 있는 상태에서 실행하세요.", "OK");
            return;
        }
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(Undoing);
        KeepCollision.Result result = Merge(root);
        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
        EditorUtility.DisplayDialog("NaWaBa · 성 내부",
            $"충돌 상자를 합쳤습니다: {result.before}개 → {result.after}개" +
            (result.skipped > 0 ? $"\n(기울어져 있어 그대로 둔 상자 {result.skipped}개)" : "") +
            "\n\n바닥·벽 칸을 옮긴 뒤에는 이 메뉴를 다시 실행하세요.\n씬을 저장(Ctrl+S)하세요.", "OK");
    }

    public static KeepCollision.Result Merge(Transform root) =>
        KeepCollision.Merge(root,
            target => Undo.RecordObject(target, Undoing),
            go => Undo.RegisterCreatedObjectUndo(go, Undoing),
            Undo.DestroyObjectImmediate);
}
