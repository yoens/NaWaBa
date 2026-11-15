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
    // Stage 2 starts on a copy of this summit and jumps onto these first platforms.
    private const string CoursePath = "Assets/Data/ClimbCourse_Long.asset";
    private const int PreviewSteps = 4;

    // Beyond the summit's open edge: the first platforms of stage 2, clouds below them, and the
    // castle far away - so reaching the summit already shows where the run goes next.
    private static void BuildNextStageView(SnowSlope slope, GameObject root)
    {
        var course = AssetDatabase.LoadAssetAtPath<ClimbCourse>(CoursePath) ??
                     AssetDatabase.LoadAssetAtPath<ClimbCourse>("Assets/Data/ClimbCourse_Default.asset");
        if (course == null)
        {
            Debug.Log("[SnowSlope] 2스테이지 코스 데이터가 없어서 정상 너머 풍경은 건너뜁니다. " +
                "(Stage_Climb에서 '등반 코스 만들기'를 먼저 실행하면 생깁니다)");
            return;
        }
        var view = new GameObject("NextStageView");
        Undo.RegisterCreatedObjectUndo(view, "Build Snow Slope");
        view.transform.SetParent(root.transform, false);

        Transform s = slope.transform;
        Vector3 edge = s.TransformPoint(slope.SummitFarEdgeLocal);
        float yaw = s.eulerAngles.y;
        Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
        ClimbCourseBuilder.BuildCoursePreview(view.transform, edge, yaw, course, PreviewSteps);

        var castle = new GameObject("DistantCastle");
        Undo.RegisterCreatedObjectUndo(castle, "Build Snow Slope");
        castle.transform.SetParent(view.transform, false);
        castle.transform.SetPositionAndRotation(edge + rotation * new Vector3(0f, 14f, 130f), rotation);
        ClimbCourseBuilder.BuildCastleInto(castle, course);

        // Clouds start at the summit's edge (they never cut through the slope).
        var clouds = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Undo.RegisterCreatedObjectUndo(clouds, "Build Snow Slope");
        clouds.name = "CloudLayer (visual)";
        Object.DestroyImmediate(clouds.GetComponent<Collider>());
        clouds.transform.SetParent(view.transform, false);
        clouds.transform.SetPositionAndRotation(edge + rotation * new Vector3(0f, -9f, 200f),
            rotation * Quaternion.Euler(90f, 0f, 0f));
        clouds.transform.localScale = new Vector3(500f, 400f, 1f);
        clouds.GetComponent<Renderer>().sharedMaterial = ClimbCourseBuilder.MaterialAsset("Clouds", course.cloudColor);
    }

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
        BuildNextStageView(slope, root);
        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(slope.gameObject.scene);
        Selection.activeGameObject = root;

        int obstacles = root.GetComponentsInChildren<SnowObstacle>().Length;
        bool view = root.transform.Find("NextStageView") != null;
        EditorUtility.DisplayDialog("NaWaBa · 눈 비탈",
            $"비탈을 만들었습니다. (장애물 {obstacles}개)\n" +
            (view ? "정상 너머로 2스테이지의 첫 발판과 멀리 성이 보입니다.\n\n" : "\n") +
            "Hierarchy의 SnowSlope > SnowSlope (built) 아래 오브젝트를 자유롭게 옮기거나 지우세요.\n" +
            "• 장애물을 늘리려면 Obstacles 안의 것을 Ctrl+D로 복제\n" +
            "• 비탈 길이·폭·각도를 바꾸면 이 메뉴를 다시 실행\n\n씬을 저장(Ctrl+S)하세요.", "OK");
    }
}
