using UnityEngine;

// One difficulty preset. The host picks it in the lobby; it is fixed for the whole run
// (retries and handoffs never change it; NEW GAME uses the one currently selected).
// Assets live in Resources/Difficulty, so a new preset is just a new asset there
// (Create > NaWaBa > Difficulty) - no code change. The lobby button lists them easiest first.
[CreateAssetMenu(fileName = "Difficulty_", menuName = "NaWaBa/Difficulty")]
public class NaWaBaDifficulty : ScriptableObject
{
    public enum RespawnPolicy
    {
        Checkpoint,   // a fall returns to the last checkpoint flag reached
        StageStart    // a fall returns to the start of stage 2
    }

    [Header("Shown to players")]
    [SerializeField] private string displayName = "NORMAL";   // Unity UI (ASCII-safe)
    [SerializeField] private string webLabel = "보통";          // web page
    [Tooltip("Lobby order: lower first.")]
    [SerializeField] private int order = 1;

    [Header("Stage 1 · 눈덩이 피하며 오르기")]
    [Tooltip("Seconds between snowball spawns.")]
    [SerializeField, Min(0.1f)] private float spawnInterval = 1f;
    [Tooltip("Snowballs appear within this many metres of the lane centre (clamped to the lane).")]
    [SerializeField, Min(0f)] private float spawnHalfWidth = 7.5f;
    [Tooltip("Rolling speed (m/s).")]
    [SerializeField, Min(0.1f)] private float snowballSpeed = 7f;
    [Header("Stage 1 · patterns (chance per spawn, 0~1)")]
    [Tooltip("A second snowball at the same time.")]
    [SerializeField, Range(0f, 1f)] private float doubleChance = 0.3f;
    [Tooltip("A row of snowballs across the lane with one gap to slip through.")]
    [SerializeField, Range(0f, 1f)] private float rowChance;
    [Tooltip("A snowball aimed at the player's lane position.")]
    [SerializeField, Range(0f, 1f)] private float aimedChance;
    [Tooltip("Altitude shown at the summit of stage 1 (ALT ...m on screen).")]
    [SerializeField, Min(10f)] private float summitMeters = 300f;

    [Header("Stage 2 · 성문 앞으로")]
    [Tooltip("Where a fall returns the player.")]
    [SerializeField] private RespawnPolicy respawn = RespawnPolicy.Checkpoint;
    [Tooltip("Course built in Stage_Climb to use (ClimbCourse asset name, e.g. ClimbCourse_Long). " +
             "Empty or not built = the course that is there.")]
    [SerializeField] private string courseName = "";
    [Tooltip("Moving platform speed (1 = as built).")]
    [SerializeField, Range(0.3f, 3f)] private float moverSpeed = 1f;

    public string DisplayName => displayName;
    public string WebLabel => webLabel;
    public int Order => order;
    public float SpawnInterval => spawnInterval;
    public float SpawnHalfWidth => spawnHalfWidth;
    public float SnowballSpeed => snowballSpeed;
    public float DoubleChance => doubleChance;
    public float RowChance => rowChance;
    public float AimedChance => aimedChance;
    public float SummitMeters => summitMeters;
    public RespawnPolicy Respawn => respawn;
    public bool UseCheckpoints => respawn == RespawnPolicy.Checkpoint;
    public string CourseName => courseName;
    public float MoverSpeed => moverSpeed;

    public const string ResourceFolder = "Difficulty";
    public const string DefaultName = "Normal";
}
