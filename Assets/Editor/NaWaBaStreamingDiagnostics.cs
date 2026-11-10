using UnityEditor;
using UnityEngine;

public static class NaWaBaStreamingDiagnostics
{
    [MenuItem("NaWaBa/Streaming/Print Environment")]
    private static void PrintEnvironment()
    {
        Debug.Log(
            "[NaWaBa Streaming Environment]\n" +
            $"Unity: {Application.unityVersion}\n" +
            $"GPU: {SystemInfo.graphicsDeviceName}\n" +
            $"Graphics API: {SystemInfo.graphicsDeviceType}\n" +
            $"Graphics Version: {SystemInfo.graphicsDeviceVersion}"
        );
    }
}