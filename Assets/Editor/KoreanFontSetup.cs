using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

public static class KoreanFontSetup
{
    private const string MenuPath = "NaWaBa/한글 폰트 설정";
    private const string TestText = "가나다 성문 앞으로 보통 쉬움 어려움 통";
    private static readonly string[] KoreanHints = { "KR", "Korean", "Nanum", "Pretendard", "Gothic", "CJK", "Malgun", "Spoqa" };

    [MenuItem(MenuPath)]
    private static void Setup()
    {
        Font font = Selection.activeObject as Font ?? FindKoreanFont();
        if (font == null)
        {
            EditorUtility.DisplayDialog("NaWaBa · 한글 폰트",
                "프로젝트에서 한글 폰트 파일을 찾지 못했습니다.\n\n" +
                "Noto Sans KR의 static/NotoSansKR-Bold.ttf 를 Assets/Fonts 폴더에 넣고 다시 실행하세요.\n" +
                "(다른 한글 폰트를 쓰려면 Project 창에서 그 .ttf를 선택한 채 실행)", "OK");
            return;
        }

        string fontPath = AssetDatabase.GetAssetPath(font);
        string assetPath = Path.Combine(Path.GetDirectoryName(fontPath) ?? "Assets",
            Path.GetFileNameWithoutExtension(fontPath) + " SDF (KR).asset").Replace('\\', '/');

        TMP_FontAsset korean = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
        if (korean == null)
        {
            korean = TMP_FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024,
                AtlasPopulationMode.Dynamic, true);
            if (korean == null)
            {
                EditorUtility.DisplayDialog("NaWaBa · 한글 폰트",
                    $"'{font.name}'로 폰트 에셋을 만들 수 없습니다.\n" +
                    "가변 폰트([wght])라면 static 폴더의 일반 .ttf를 쓰세요.", "OK");
                return;
            }
            korean.name = Path.GetFileNameWithoutExtension(assetPath);
            AssetDatabase.CreateAsset(korean, assetPath);
            if (korean.atlasTextures != null && korean.atlasTextures.Length > 0 && korean.atlasTextures[0] != null)
            {
                korean.atlasTextures[0].name = korean.name + " Atlas";
                AssetDatabase.AddObjectToAsset(korean.atlasTextures[0], korean);
            }
            if (korean.material != null)
            {
                korean.material.name = korean.name + " Material";
                AssetDatabase.AddObjectToAsset(korean.material, korean);
            }
        }
        else
        {
            korean.atlasPopulationMode = AtlasPopulationMode.Dynamic;
            korean.isMultiAtlasTexturesEnabled = true;
        }

        // Can this font actually draw Korean?
        bool drawsKorean = korean.HasCharacters(TestText.Replace(" ", ""), out uint[] missing, false, true);
        EditorUtility.SetDirty(korean);

        // Register as a fallback on LiberationSans SDF (and the default font) and globally.
        int linked = 0;
        var targets = new List<TMP_FontAsset>();
        if (TMP_Settings.defaultFontAsset != null) targets.Add(TMP_Settings.defaultFontAsset);
        foreach (string guid in AssetDatabase.FindAssets("t:TMP_FontAsset LiberationSans"))
        {
            var asset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath(guid));
            if (asset != null && !targets.Contains(asset)) targets.Add(asset);
        }
        foreach (TMP_FontAsset target in targets)
        {
            if (target == korean) continue;
            if (target.fallbackFontAssetTable == null) target.fallbackFontAssetTable = new List<TMP_FontAsset>();
            if (!target.fallbackFontAssetTable.Contains(korean))
            {
                Undo.RecordObject(target, "Add Korean fallback");
                target.fallbackFontAssetTable.Add(korean);
                EditorUtility.SetDirty(target);
            }
            linked++;
        }
        List<TMP_FontAsset> global = TMP_Settings.fallbackFontAssets;
        if (global != null && !global.Contains(korean))
        {
            global.Add(korean);
            if (TMP_Settings.instance != null) EditorUtility.SetDirty(TMP_Settings.instance);
        }

        AssetDatabase.SaveAssets();
        Selection.activeObject = korean;
        EditorGUIUtility.PingObject(korean);

        string result = drawsKorean
            ? "한글 글자 생성 확인 완료."
            : $"주의: 이 폰트에 없는 글자가 있습니다 ({MissingText(missing)}). 한글 폰트가 맞는지 확인하세요.";
        Debug.Log($"[KoreanFont] {korean.name} → fallback of {linked} font asset(s). {result}", korean);
        EditorUtility.DisplayDialog("NaWaBa · 한글 폰트",
            $"폰트: {font.name}\n에셋: {assetPath}\n대체 폰트로 연결: {linked}개 + 전역 설정\n\n{result}\n\n" +
            "플레이해서 한글이 보이는지 확인하세요.", "OK");
    }

    private static string MissingText(uint[] codes)
    {
        if (codes == null) return "";
        var text = new System.Text.StringBuilder();
        foreach (uint code in codes) text.Append(char.ConvertFromUtf32((int)code));
        return text.ToString();
    }

    private static Font FindKoreanFont()
    {
        Font any = null;
        foreach (string guid in AssetDatabase.FindAssets("t:Font"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.Contains("TextMesh Pro") || path.Contains("Packages/")) continue;
            var font = AssetDatabase.LoadAssetAtPath<Font>(path);
            if (font == null) continue;
            string name = Path.GetFileNameWithoutExtension(path);
            foreach (string hint in KoreanHints)
                if (name.IndexOf(hint, System.StringComparison.OrdinalIgnoreCase) >= 0) return font;
            if (any == null) any = font;
        }
        return any;
    }
}
