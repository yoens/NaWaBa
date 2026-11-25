using UnityEngine;

// Which regions of the world map are open. Saved on this PC (PlayerPrefs) so it survives
// restarts; not tied to the difficulty. Region 0 is always open; clearing region N opens N+1.
public static class WorldProgress
{
    private const string ClearedKey = "NaWaBa.RegionsCleared";

    // Number of regions cleared in order (0 = none).
    public static int ClearedCount => Mathf.Max(0, PlayerPrefs.GetInt(ClearedKey, 0));

    public static bool IsCleared(int region) => region >= 0 && region < ClearedCount;
    public static bool IsUnlocked(int region) => region >= 0 && region <= ClearedCount;

    // Set when a clear opened a new region; the world map plays the unlock once and consumes it.
    public static int JustUnlocked { get; private set; } = -1;

    // Region the last run was played in (the map focuses it when it opens again).
    public static int LastRegion { get; set; } = -1;

    public static void MarkCleared(int region)
    {
        if (region < 0) return;
        LastRegion = region;
        if (region + 1 <= ClearedCount) return;
        PlayerPrefs.SetInt(ClearedKey, region + 1);
        PlayerPrefs.Save();
        JustUnlocked = region + 1;
    }

    public static int ConsumeJustUnlocked()
    {
        int value = JustUnlocked;
        JustUnlocked = -1;
        return value;
    }

    public static void ResetAll()
    {
        PlayerPrefs.DeleteKey(ClearedKey);
        PlayerPrefs.Save();
        JustUnlocked = -1;
        LastRegion = -1;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        JustUnlocked = -1;
        LastRegion = -1;
    }
}
