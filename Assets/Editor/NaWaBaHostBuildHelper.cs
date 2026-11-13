using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Step 7 helper: running the host on another (trusted) PC.
// - After a Windows build, writes HOST_SETUP.txt next to the .exe explaining where the host
//   configuration goes. The configuration itself (it holds the host key) is NEVER copied
//   into the build automatically, so a build folder can be shared without leaking it.
// - Menu "NaWaBa > 호스트 설정 파일 위치 열기" shows the project's nawaba-host.local.json so it
//   can be copied by hand to the other PC (USB / private message, not Git or a public link).
public class NaWaBaHostBuildHelper : IPostprocessBuildWithReport
{
    private const string ConfigName = "nawaba-host.local.json";

    public int callbackOrder => 0;

    public void OnPostprocessBuild(BuildReport report)
    {
        BuildTarget target = report.summary.platform;
        if (target != BuildTarget.StandaloneWindows && target != BuildTarget.StandaloneWindows64) return;

        string folder = Path.GetDirectoryName(report.summary.outputPath);
        if (string.IsNullOrEmpty(folder)) return;
        string exe = Path.GetFileName(report.summary.outputPath);
        File.WriteAllText(Path.Combine(folder, "HOST_SETUP.txt"),
            "NaWaBa host (Windows build)\r\n" +
            "==========================\r\n\r\n" +
            $"1. Put {ConfigName} in this folder, next to {exe}.\r\n" +
            "   Get it from the developer privately (USB, direct message). It contains the host key:\r\n" +
            "   never upload it to Git, a shared drive link or a public download.\r\n" +
            $"2. Run {exe} > MULTI PLAY. The room link appears in the lobby; send it to the participants.\r\n" +
            "3. Several hosts may run at the same time with the same file: each one gets its own room.\r\n\r\n" +
            "Checks on this PC (written to the log when a participant's video starts):\r\n" +
            "   [NaWaBa Room Video] HOST INFO  -> GPU, graphics API (DX11 recommended), video size\r\n" +
            "   Log file: %USERPROFILE%\\AppData\\LocalLow\\<Company>\\<Product>\\Player.log\r\n" +
            "If a participant's video never connects on some network, note the network type\r\n" +
            "(school/company/mobile hotspot) and what the web page says: a TURN relay may be needed.\r\n");

        bool hasConfig = File.Exists(Path.Combine(folder, ConfigName));
        Debug.Log($"[NaWaBa] Windows build ready: {folder}\n" +
            (hasConfig
                ? $"{ConfigName} is in the build folder. Remove it before sharing the folder with anyone untrusted."
                : $"{ConfigName} is NOT in the build folder (by design). Copy it next to {exe} on the host PC. See HOST_SETUP.txt."));
    }

    [MenuItem("NaWaBa/호스트 설정 파일 위치 열기")]
    private static void RevealConfig()
    {
        string path = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", ConfigName);
        if (!File.Exists(path))
        {
            EditorUtility.DisplayDialog("NaWaBa", $"{ConfigName} 파일이 없습니다.\nWebController 폴더에서 node setup-local.cjs 를 먼저 실행하세요.", "OK");
            return;
        }
        EditorUtility.RevealInFinder(path);
    }
}
