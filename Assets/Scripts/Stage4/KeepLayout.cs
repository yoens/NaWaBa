using UnityEngine;

// Map of the castle interior (stage 4) as text: one string per row, north (far) row first.
// Edit it in the Inspector, then run "NaWaBa > 성 내부 만들기 (Stage_Keep)" to rebuild.
//   #  wall (towers appear on corners automatically)
//   .  floor
//   S  start (one)            E  exit / goal (one)
//   C  checkpoint
//   A  swinging axe           W  saw sliding across
//   P  floor spikes           R  arrows from the wall
// Every row must have the same length, and the outer border should be walls.
[CreateAssetMenu(fileName = "KeepLayout", menuName = "NaWaBa/Keep Layout")]
public class KeepLayout : ScriptableObject
{
    [TextArea(8, 30)] public string map =
        "#################\n" +
        "#E.A..#....R....#\n" +
        "####A.#.#######.#\n" +
        "#..W.##.#.....#.#\n" +
        "#P####..#.....#C#\n" +
        "#.R.CA..#..W..#.#\n" +
        "#####.###.....#.#\n" +
        "#...#.W.P..W..#.#\n" +
        "#.#.######A####A#\n" +
        "#.#....P..C..#..#\n" +
        "#.##########P#.##\n" +
        "#...A..S.R...W..#\n" +
        "#################";

    [Tooltip("Size of one map cell in metres (corridor width). Castle pieces are scaled to it.")]
    [Min(3f)] public float cellSize = 5f;
    public Color floorColor = new Color(0.55f, 0.53f, 0.5f);
    public Color floorColorAlt = new Color(0.48f, 0.46f, 0.44f);
    public Color trapMarkColor = new Color(0.45f, 0.18f, 0.16f);

    public string[] Rows() => map.Replace("\r", "").Trim('\n').Split('\n');
}
