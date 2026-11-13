using System;
using System.Collections.Generic;
using UnityEngine;

// Stage 2 course as data: each step is one platform, placed relative to the previous one.
// Edit the list in the Inspector, then run "NaWaBa > 등반 코스 만들기" in Stage_Climb to
// rebuild the platforms. The built platforms are normal scene objects you can still move.
[CreateAssetMenu(fileName = "ClimbCourse", menuName = "NaWaBa/Climb Course")]
public class ClimbCourse : ScriptableObject
{
    public enum Decor { None, Trees, Flowers, Rocks, Fence }

    [Serializable]
    public class Step
    {
        [Tooltip("Platform prefab (needs a Box Collider on the Ground layer).")]
        public GameObject prefab;
        [Tooltip("Width of the platform top in metres (the prefab is scaled to fit).")]
        [Min(0.8f)] public float width = 2.4f;
        [Tooltip("Empty space to jump over, edge to edge, in metres.")]
        [Min(0f)] public float gap = 1.2f;
        [Tooltip("How much higher the top is than the previous platform's top (m).")]
        public float rise = 0.5f;
        [Tooltip("Turn the course direction before this step (degrees, + = right).")]
        public float turn;
        [Tooltip("Falls after landing here return to this platform (if the difficulty allows).")]
        public bool checkpoint;
        [Tooltip("Props on top of the platform (visual only). Snow platforms get snowy trees.")]
        public Decor decor;
        [Tooltip("Moving platform: swings this many metres to each side (0 = still).")]
        [Min(0f)] public float moveDistance;
        [Tooltip("Seconds for one full swing of a moving platform.")]
        [Min(0.5f)] public float movePeriod = 4f;
    }

    [Header("Start")]
    [Tooltip("Platform under the player's start position. Empty = ClimbPlatform_01.")]
    public GameObject startPadPrefab;
    [Min(2f)] public float startPadWidth = 7f;

    [Header("Course")]
    [Tooltip("Steps from the start pad to the gate. The last one holds the goal and the gate.")]
    public List<Step> steps = new List<Step>();

    [Tooltip("Same number = same prop placement every build.")]
    public int decorSeed = 11;

    [Header("Safety check")]
    [Tooltip("Warn when a jump leaves less than this many metres to spare.")]
    [Min(0f)] public float minimumMargin = 0.3f;

    [Header("Gate")]
    public float gateHeight = 4f;
    public float gateWidth = 3f;
    public Color stoneColor = new Color(0.62f, 0.6f, 0.56f);
    public Color doorColor = new Color(0.42f, 0.26f, 0.14f);

    [Header("Castle wall (around the gate)")]
    [Tooltip("Height of the wall above the last platform (the gate opening sits inside it).")]
    public float wallHeight = 7f;
    [Tooltip("How far the wall reaches to each side of the gate (m).")]
    public float wallHalfLength = 14f;
    [Tooltip("How far the wall and towers reach down below the last platform (into the clouds).")]
    public float wallDepth = 14f;

    [Header("Castle in the background")]
    public bool buildCastle = true;
    [Tooltip("Distance behind the gate (m).")]
    public float castleDistance = 30f;
    [Min(0.3f)] public float castleScale = 1f;
    public Color roofColor = new Color(0.55f, 0.18f, 0.16f);
    public Color flagColor = new Color(0.95f, 0.8f, 0.2f);

    [Header("Below the course")]
    [Tooltip("A cloud layer under the course (visual only, no collider).")]
    public bool buildCloudLayer = true;
    public float cloudHeight = -3.5f;
    public Color cloudColor = new Color(0.9f, 0.94f, 1f);
}
