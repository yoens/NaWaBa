using System.Collections.Generic;
using UnityEngine;

// Layout of the crypt descent (region 1 · game 2), in this object's local space (+Z = on).
// Fork k is a T-junction at Fork(k): the player arrives from -Z and turns left (-X) or right (+X).
// Both arms look the same: each runs to an edge, drops one level and comes back to the middle,
// where the two ways merge and lead on to the next fork. The wrong arm is shut at run time by
// an iron gate that slams down (CloseGate), so the course itself never gives the answer away.
// The builder (NaWaBa > 무덤 지하 갈림길 만들기) places the kit pieces from the same numbers;
// moving the whole root is fine, moving single pieces does not move the running path.
public class CryptCourse : MonoBehaviour
{
    public struct Segment
    {
        public Vector3 a, b;   // local points
        public bool fall;      // drop from a to b (gravity) instead of running
    }

    [SerializeField, Min(1f)] private float tile = 4f;        // kit tile size
    [SerializeField, Min(1f)] private float drop = 6f;        // height between levels
    [SerializeField, Min(1)] private int forkCount = 8;
    [Tooltip("Gates that close the left (-X) arm of each fork, inactive until used.")]
    [SerializeField] private GameObject[] leftGates = new GameObject[0];
    [Tooltip("Gates that close the right (+X) arm of each fork, inactive until used.")]
    [SerializeField] private GameObject[] rightGates = new GameObject[0];
    [SerializeField, Min(0.1f)] private float gateHalfThickness = 0.7f;
    [SerializeField, Min(0.05f)] private float gateDropTime = 0.14f;

    private readonly Dictionary<GameObject, Vector3> gateBase = new Dictionary<GameObject, Vector3>();
    private readonly List<(GameObject gate, float t)> closing = new List<(GameObject, float)>();

    public int ForkCount => forkCount;
    public float Tile => tile;
    private float Radius => tile * 0.375f;                    // turn radius inside a tile

    public Vector3 StartPoint => new Vector3(0f, 0f, -3f * tile);
    public Vector3 GoalPoint => Fork(forkCount - 1) + new Vector3(0f, -drop, 6f * tile);
    public Vector3 Fork(int k) => new Vector3(0f, -drop * k, 4f * tile * k);
    public Vector3 TurnStart(int k) => Fork(k) + new Vector3(0f, 0f, -Radius);

    public void Configure(float tileSize, float levelDrop, int forks, GameObject[] left, GameObject[] right, float gateHalf)
    {
        tile = tileSize;
        drop = levelDrop;
        forkCount = forks;
        leftGates = left;
        rightGates = right;
        gateHalfThickness = gateHalf;
    }

    private void Awake()
    {
        foreach (GameObject gate in AllGates())
            if (gate != null) gateBase[gate] = gate.transform.localPosition;
        ResetGates();
    }

    private IEnumerable<GameObject> AllGates()
    {
        foreach (GameObject g in leftGates) yield return g;
        foreach (GameObject g in rightGates) yield return g;
    }

    public void ResetGates()
    {
        closing.Clear();
        foreach (GameObject gate in AllGates())
        {
            if (gate == null) continue;
            if (gateBase.TryGetValue(gate, out Vector3 home)) gate.transform.localPosition = home;
            gate.SetActive(false);
        }
    }

    // Slams the gate of fork k on the given side (-1 left, +1 right).
    public void CloseGate(int k, int side)
    {
        GameObject[] gates = side < 0 ? leftGates : rightGates;
        if (k < 0 || k >= gates.Length || gates[k] == null) return;
        gates[k].SetActive(true);
        closing.Add((gates[k], 0f));
        Place(gates[k], 0f);
    }

    private void Update()
    {
        for (int i = closing.Count - 1; i >= 0; i--)
        {
            var (gate, t) = closing[i];
            t += Time.deltaTime / gateDropTime;
            Place(gate, Mathf.Clamp01(t));
            if (t >= 1f) closing.RemoveAt(i);
            else closing[i] = (gate, t);
        }
    }

    private void Place(GameObject gate, float t)
    {
        if (gate == null || !gateBase.TryGetValue(gate, out Vector3 home)) return;
        gate.transform.localPosition = home + Vector3.up * (tile * (1f - t * t));
    }

    // ---------- running path ----------

    public void FirstLeg(List<Segment> path)
    {
        path.Clear();
        Line(path, StartPoint, TurnStart(0));
    }

    // Straight on (no choice) into the far wall of the junction.
    public void WallLeg(int k, List<Segment> path)
    {
        path.Clear();
        Vector3 o = Fork(k);
        Line(path, TurnStart(k), o + new Vector3(0f, 0f, tile * 0.25f - 0.45f));
    }

    // Turn into the arm on "side". Wrong = stop at the gate; right = drop a level, come back to
    // the middle and run to the next fork (or the goal after the last one).
    public void ForkLeg(int k, int side, bool correct, List<Segment> path)
    {
        path.Clear();
        float s = side < 0 ? -1f : 1f, r = Radius, t = tile;
        Vector3 o = Fork(k);
        Vector3 armIn = o + new Vector3(s * r, 0f, 0f);
        Arc(path, TurnStart(k), armIn, o + new Vector3(s * r, 0f, -r));
        if (!correct)
        {
            Line(path, armIn, o + new Vector3(s * (t - gateHalfThickness - 0.4f), 0f, 0f));
            return;
        }

        Vector3 edge = o + new Vector3(s * 1.5f * t, 0f, 0f);
        Vector3 land = o + new Vector3(s * 2.1f * t, -drop, 0f);
        Line(path, armIn, edge);
        path.Add(new Segment { a = edge, b = land, fall = true });

        Vector3 low = o + Vector3.down * drop;
        Vector3 c1In = low + new Vector3(s * (3f * t - r), 0f, 0f);
        Vector3 c1Out = low + new Vector3(s * 3f * t, 0f, r);
        Vector3 c2In = low + new Vector3(s * 3f * t, 0f, 2f * t - r);
        Vector3 c2Out = low + new Vector3(s * (3f * t - r), 0f, 2f * t);
        Vector3 mIn = low + new Vector3(s * r, 0f, 2f * t);
        Vector3 mOut = low + new Vector3(0f, 0f, 2f * t + r);
        Line(path, land, c1In);
        Arc(path, c1In, c1Out, low + new Vector3(s * (3f * t - r), 0f, r));
        Line(path, c1Out, c2In);
        Arc(path, c2In, c2Out, low + new Vector3(s * (3f * t - r), 0f, 2f * t - r));
        Line(path, c2Out, mIn);
        Arc(path, mIn, mOut, low + new Vector3(s * r, 0f, 2f * t + r));
        Line(path, mOut, k < forkCount - 1 ? TurnStart(k + 1) : GoalPoint);
    }

    private static void Line(List<Segment> path, Vector3 a, Vector3 b)
    {
        if ((b - a).sqrMagnitude > 0.0001f) path.Add(new Segment { a = a, b = b });
    }

    // Quarter turn from "from" to "to" round "center" (same height), the short way.
    private static void Arc(List<Segment> path, Vector3 from, Vector3 to, Vector3 center)
    {
        float a0 = Mathf.Atan2(from.x - center.x, from.z - center.z);
        float a1 = Mathf.Atan2(to.x - center.x, to.z - center.z);
        float delta = Mathf.DeltaAngle(a0 * Mathf.Rad2Deg, a1 * Mathf.Rad2Deg) * Mathf.Deg2Rad;
        float radius = new Vector2(from.x - center.x, from.z - center.z).magnitude;
        const int steps = 6;
        Vector3 previous = from;
        for (int i = 1; i <= steps; i++)
        {
            float a = a0 + delta * i / steps;
            Vector3 point = i == steps ? to
                : new Vector3(center.x + Mathf.Sin(a) * radius, from.y, center.z + Mathf.Cos(a) * radius);
            Line(path, previous, point);
            previous = point;
        }
    }

    private void OnDrawGizmosSelected()
    {
        // Preview: the "all right" way and the start / goal.
        var path = new List<Segment>();
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(1f, 0.8f, 0.3f);
        Gizmos.DrawWireSphere(StartPoint, 0.6f);
        Gizmos.DrawWireSphere(GoalPoint, 0.6f);
        for (int k = 0; k < forkCount; k++)
        {
            ForkLeg(k, 1, true, path);
            foreach (Segment seg in path) Gizmos.DrawLine(seg.a, seg.b);
        }
        FirstLeg(path);
        foreach (Segment seg in path) Gizmos.DrawLine(seg.a, seg.b);
    }
}
