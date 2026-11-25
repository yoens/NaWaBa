using System;
using System.Collections.Generic;
using UnityEngine;

// The order of the stages, grouped into regions (지역). The world map lets the host pick an
// unlocked region; its stages then play in list order. The last stage of a region ends the run
// with the result screen and unlocks the next region (WorldProgress).
// Asset: Resources/NaWaBaStages. New stage = add one entry here (and to the Build Profile).
[CreateAssetMenu(fileName = "NaWaBaStages", menuName = "NaWaBa/Stage List")]
public class NaWaBaStageList : ScriptableObject
{
    [Serializable]
    public class Stage
    {
        public string sceneName;
        [Tooltip("Shown in logs / results (the stage controllers keep their own web names).")]
        public string title;
        [Tooltip("Index into Regions. Stages of one region must be next to each other.")]
        [Min(0)] public int region;
    }

    [Serializable]
    public class Region
    {
        public string title;
        [Tooltip("One short line shown on the world map.")]
        public string description;
    }

    public List<Region> regions = new List<Region>();
    public List<Stage> stages = new List<Stage>();

    public int RegionCount => regions.Count;

    public string RegionTitle(int region) =>
        region >= 0 && region < regions.Count && !string.IsNullOrEmpty(regions[region].title)
            ? regions[region].title : $"Region {region + 1}";

    public string RegionDescription(int region) =>
        region >= 0 && region < regions.Count ? regions[region].description ?? "" : "";

    // First stage of the region, or "" when the region has no stages yet.
    public string FirstSceneOfRegion(int region)
    {
        Stage stage = stages.Find(s => s != null && s.region == region && !string.IsNullOrEmpty(s.sceneName));
        return stage != null ? stage.sceneName : "";
    }

    public int StageCountOfRegion(int region) => stages.FindAll(s => s != null && s.region == region).Count;

    public int RegionOf(string sceneName)
    {
        int index = IndexOf(sceneName);
        return index >= 0 ? stages[index].region : -1;
    }

    public const string ResourcePath = "NaWaBaStages";

    private static NaWaBaStageList cached;
    private static bool looked;

    public static NaWaBaStageList Instance
    {
        get
        {
            if (!looked)
            {
                cached = Resources.Load<NaWaBaStageList>(ResourcePath);
                looked = true;
            }
            return cached;
        }
    }

    public int IndexOf(string sceneName) => stages.FindIndex(s => s != null && s.sceneName == sceneName);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        cached = null;
        looked = false;
    }
}
