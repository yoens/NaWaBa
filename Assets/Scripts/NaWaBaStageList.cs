using System;
using System.Collections.Generic;
using UnityEngine;

// The order of the stages in one run (한 판). StageSceneLoader reads it to know which scene
// comes first and which comes next; the last stage ends the run with the result screen.
// Asset: Resources/NaWaBaStages. Stage 3 = add one entry here (and to the Build Profile).
[CreateAssetMenu(fileName = "NaWaBaStages", menuName = "NaWaBa/Stage List")]
public class NaWaBaStageList : ScriptableObject
{
    [Serializable]
    public class Stage
    {
        public string sceneName;
        [Tooltip("Shown in logs / results (the stage controllers keep their own web names).")]
        public string title;
    }

    public List<Stage> stages = new List<Stage>();

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
