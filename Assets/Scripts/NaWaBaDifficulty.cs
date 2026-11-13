using UnityEngine;

// One difficulty preset. The host picks it in the lobby; it is fixed for the whole run
// (retries and handoffs never change it). Assets live in Resources/Difficulty so the
// game works even if nothing is wired in the Inspector.
[CreateAssetMenu(fileName = "Difficulty_", menuName = "NaWaBa/Difficulty")]
public class NaWaBaDifficulty : ScriptableObject
{
    [Header("Shown to players")]
    [SerializeField] private string displayName = "NORMAL";   // Unity UI (ASCII-safe)
    [SerializeField] private string webLabel = "보통";          // web page

    [Header("Stage 1 · 눈덩이 피하기")]
    [SerializeField, Min(1f)] private float surviveDuration = 20f;
    [SerializeField, Min(0.1f)] private float spawnInterval = 1f;
    [SerializeField, Min(0f)] private float spawnHalfWidth = 9f;
    [SerializeField, Min(0.1f)] private float snowballSpeed = 7f;
    // How fast the scenery slides down (the "climbing up" feel). Visual only.
    [SerializeField, Min(0f)] private float climbScrollSpeed = 3f;
    // Height shown at the end of stage 1 (start of the mountain).
    [SerializeField, Min(10f)] private float summitMeters = 300f;

    [Header("Stage 2 · 성문 앞으로")]
    // Checkpoint flags on the course become respawn points after a fall.
    [SerializeField] private bool useCheckpoints = true;

    public string DisplayName => displayName;
    public string WebLabel => webLabel;
    public float SurviveDuration => surviveDuration;
    public float SpawnInterval => spawnInterval;
    public float SpawnHalfWidth => spawnHalfWidth;
    public float SnowballSpeed => snowballSpeed;
    public float ClimbScrollSpeed => climbScrollSpeed;
    public float SummitMeters => summitMeters;
    public bool UseCheckpoints => useCheckpoints;

    public const string ResourceFolder = "Difficulty";
    public const string DefaultName = "Normal";
}
