using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Menu: NaWaBa > 눈 비탈 만들기 (Stage_Dodge)
// Builds the stage 1 slope (ground blocks, walls, obstacles, trees, flag, goal) as normal
// scene objects under "SnowSlope (built)", using the numbers and prefabs on the SnowSlope
// component. After that you can move / delete / duplicate anything by hand.
// Run it again after changing the shape numbers (lane width, length, angle ...).
public static class SnowSlopeBuilder
{
    [MenuItem("NaWaBa/눈 비탈 만들기 (Stage_Dodge)")]
    private static void Build()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("NaWaBa", "플레이 모드를 끝낸 뒤 실행하세요.", "OK");
            return;
        }
        SnowSlope slope = Object.FindFirstObjectByType<SnowSlope>();
        if (slope == null)
        {
            EditorUtility.DisplayDialog("NaWaBa", "Stage_Dodge 씬을 열고 실행하세요. (SnowSlope를 찾지 못했습니다)", "OK");
            return;
        }
        if (slope.transform.Find(SnowSlope.BuiltName) != null &&
            !EditorUtility.DisplayDialog("NaWaBa · 눈 비탈",
                "이미 만들어진 비탈이 있습니다.\n다시 만들면 직접 옮기거나 추가한 오브젝트가 사라집니다.\n(Ctrl+Z로 되돌릴 수 있습니다)",
                "다시 만들기", "취소"))
            return;

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Build Snow Slope");
        GameObject root = slope.BuildInEditor();
        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(slope.gameObject.scene);
        Selection.activeGameObject = root;

        int obstacles = root.GetComponentsInChildren<SnowObstacle>().Length;
        EditorUtility.DisplayDialog("NaWaBa · 눈 비탈",
            $"비탈을 만들었습니다. (장애물 {obstacles}개)\n\n" +
            "Hierarchy의 SnowSlope > SnowSlope (built) 아래 오브젝트를 자유롭게 옮기거나 지우세요.\n" +
            "• 장애물을 늘리려면 Obstacles 안의 것을 Ctrl+D로 복제\n" +
            "• 비탈 길이·폭·각도를 바꾸면 이 메뉴를 다시 실행\n\n씬을 저장(Ctrl+S)하세요.", "OK");
    }
}
