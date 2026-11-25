using System;
using System.Collections.Generic;
using UnityEngine;

// One mirror puzzle room (region 1 · game 3). A square grid of cells lies around this object
// (local space, +X = columns, +Z = rows, centre cell at the origin) with a free walkway ring
// around it. Mirrors (MirrorPiece) stand on cells: the player pushes them one cell and turns
// them ('/' or '\'). The light from the MirrorSource runs cell to cell, every mirror it meets
// turns it 90°, pillars (MirrorBlock) stop it. When it reaches the MirrorTarget crystal the
// room is solved: the crystal glows and the door sinks into the floor.
// Everything is read from the children's positions on Awake, so moving a mirror, pillar,
// source or crystal in the editor changes the puzzle (positions snap to the nearest cell).
public class MirrorRoom : MonoBehaviour
{
    [SerializeField, Min(0.5f)] private float cell = 2f;
    [SerializeField, Min(3)] private int size = 7;
    [SerializeField] private float beamHeight = 1.1f;
    [SerializeField] private MirrorSource source;
    [SerializeField] private MirrorTarget target;
    [Tooltip("Gate that sinks into the floor when the room is solved (optional).")]
    [SerializeField] private GameObject door;
    [SerializeField] private float doorSink = 4.6f;
    [SerializeField, Min(0.1f)] private float doorTime = 1.4f;
    [SerializeField] private LineRenderer beam;
    [Tooltip("Optional: hidden when the room is solved (e.g. the reset plate).")]
    [SerializeField] private GameObject hideWhenSolved;

    private readonly HashSet<Vector2Int> blocked = new HashSet<Vector2Int>();
    private readonly List<MirrorPiece> pieces = new List<MirrorPiece>();
    private readonly List<Vector3> points = new List<Vector3>();
    private readonly HashSet<(Vector2Int, Vector2Int)> visited = new HashSet<(Vector2Int, Vector2Int)>();
    private Vector3 doorHome;
    private float doorT = -1f;
    private bool frozen;

    public bool Solved { get; private set; }
    public MirrorTarget Target => target;
    public float Cell => cell;
    public IReadOnlyList<MirrorPiece> Pieces => pieces;
    public event Action<MirrorRoom> SolvedNow;

    // Stops the pieces moving (handoff pause).
    public bool Frozen
    {
        get => frozen;
        set { frozen = value; foreach (MirrorPiece piece in pieces) piece.Frozen = value; }
    }

    private void Awake()
    {
        foreach (MirrorBlock block in GetComponentsInChildren<MirrorBlock>(true))
            blocked.Add(CellOf(transform.InverseTransformPoint(block.transform.position)));
        foreach (MirrorPiece piece in GetComponentsInChildren<MirrorPiece>(true))
        {
            Vector2Int at = CellOf(transform.InverseTransformPoint(piece.transform.position));
            if (!Inside(at) || blocked.Contains(at) || PieceAt(at) != null)
            {
                Debug.LogWarning($"[Mirror] {name}: {piece.name} is not on a free cell ({at}); it is left out.", piece);
                continue;
            }
            piece.Bind(this, at);
            pieces.Add(piece);
        }
        if (source == null) source = GetComponentInChildren<MirrorSource>(true);
        if (target == null) target = GetComponentInChildren<MirrorTarget>(true);
        if (source == null || target == null)
            Debug.LogError($"[Mirror] {name}: a MirrorSource and a MirrorTarget are needed.", this);
        if (door != null) doorHome = door.transform.localPosition;
        if (beam != null) beam.useWorldSpace = true;
    }

    // ---------- grid ----------

    private float Half => (size - 1) * 0.5f;

    public Vector2Int CellOf(Vector3 local) =>
        new Vector2Int(Mathf.RoundToInt(local.x / cell + Half), Mathf.RoundToInt(local.z / cell + Half));

    public Vector3 Center(Vector2Int c) => new Vector3((c.x - Half) * cell, 0f, (c.y - Half) * cell);

    public bool Inside(Vector2Int c) => c.x >= 0 && c.y >= 0 && c.x < size && c.y < size;

    private bool Walkway(Vector2Int c) => c.x >= -1 && c.y >= -1 && c.x <= size && c.y <= size && !Inside(c);

    public MirrorPiece PieceAt(Vector2Int c)
    {
        foreach (MirrorPiece piece in pieces)
            if (piece.Cell == c) return piece;
        return null;
    }

    // Grid direction (one of the four axes) closest to a local direction.
    public static Vector2Int Axis(Vector3 local) =>
        Mathf.Abs(local.x) >= Mathf.Abs(local.z)
            ? new Vector2Int(local.x >= 0f ? 1 : -1, 0)
            : new Vector2Int(0, local.z >= 0f ? 1 : -1);

    // ---------- player actions ----------

    public bool TryPush(MirrorPiece piece, Vector2Int dir)
    {
        if (Solved || frozen || piece == null || piece.Moving) return false;
        Vector2Int to = piece.Cell + dir;
        if (!Inside(to) || blocked.Contains(to) || PieceAt(to) != null) return false;
        piece.MoveTo(to, false);
        return true;
    }

    public bool TryTurn(MirrorPiece piece)
    {
        if (Solved || frozen || piece == null) return false;
        piece.Turn();
        return true;
    }

    // Back to the start positions (the reset plate).
    public bool ResetPieces()
    {
        if (Solved) return false;
        bool changed = false;
        foreach (MirrorPiece piece in pieces) changed |= piece.ResetToStart();
        return changed;
    }

    // Rooms the difficulty skips: mirrors go to the stored solution, the door is open already.
    public void PresetSolved()
    {
        foreach (MirrorPiece piece in pieces)
            if (piece.HasSolvedCell && Inside(piece.SolvedCell) && !blocked.Contains(piece.SolvedCell))
                piece.MoveTo(piece.SolvedCell, true);
        int n = Mathf.Min(pieces.Count, 12);
        bool found = false;
        for (int mask = 0; mask < (1 << n) && !found; mask++)
        {
            for (int i = 0; i < n; i++) pieces[i].SetSlash((mask & (1 << i)) != 0, true);
            found = Trace(null);
        }
        if (!found) Debug.LogWarning($"[Mirror] {name}: the stored solution does not reach the crystal; door opened anyway.", this);
        Solve(instant: true);
        Draw();
    }

    // ---------- light ----------

    private void LateUpdate()
    {
        bool hit = Draw();
        if (hit && !Solved) Solve(instant: false);

        if (door != null && doorT >= 0f && doorT < 1f)
        {
            if (!frozen) doorT = Mathf.Min(1f, doorT + Time.deltaTime / doorTime);
            float e = doorT * doorT * (3f - 2f * doorT);
            door.transform.localPosition = doorHome + Vector3.down * (doorSink * e);
            if (doorT >= 1f) door.SetActive(false);
        }
    }

    private bool Draw()
    {
        bool hit = Trace(points);
        if (target != null && !Solved) target.SetLit(hit);
        if (beam != null)
        {
            beam.positionCount = points.Count;
            for (int i = 0; i < points.Count; i++) beam.SetPosition(i, points[i]);
        }
        return hit;
    }

    // Follows the light; fills "path" (world points) when given. True when it reaches the crystal.
    private bool Trace(List<Vector3> path)
    {
        path?.Clear();
        if (source == null || target == null) return false;
        Vector3 from = transform.InverseTransformPoint(source.transform.position);
        Vector2Int at = CellOf(from);
        Vector2Int d = Axis(transform.InverseTransformDirection(source.transform.forward));
        Vector2Int goal = CellOf(transform.InverseTransformPoint(target.transform.position));
        path?.Add(Point(new Vector3(from.x, 0f, from.z)));
        visited.Clear();
        int limit = (size + 2) * (size + 2) * 4;
        for (int step = 0; step < limit; step++)
        {
            at += d;
            if (at == goal)
            {
                Vector3 t = transform.InverseTransformPoint(target.transform.position);
                path?.Add(Point(new Vector3(t.x, 0f, t.z)));
                return true;
            }
            if (!Inside(at))
            {
                if (Walkway(at)) continue;
                // Out past the walkway: the light ends on the wall.
                path?.Add(Point(Center(at - d) + new Vector3(d.x, 0f, d.y) * (cell * 0.5f)));
                return false;
            }
            if (blocked.Contains(at))
            {
                path?.Add(Point(Center(at) - new Vector3(d.x, 0f, d.y) * (cell * 0.35f)));
                return false;
            }
            MirrorPiece piece = PieceAt(at);
            if (piece == null) continue;
            path?.Add(Point(Center(at)));
            d = piece.Slash ? new Vector2Int(d.y, d.x) : new Vector2Int(-d.y, -d.x);
            if (!visited.Add((at, d))) return false;   // a loop
        }
        return false;
    }

    private Vector3 Point(Vector3 local) => transform.TransformPoint(local + Vector3.up * beamHeight);

    private void Solve(bool instant)
    {
        if (Solved) return;
        Solved = true;
        if (target != null) target.SetLit(true);
        if (hideWhenSolved != null) hideWhenSolved.SetActive(false);
        if (door != null)
        {
            doorT = instant ? 1f : 0f;
            if (instant)
            {
                door.transform.localPosition = doorHome + Vector3.down * doorSink;
                door.SetActive(false);
            }
        }
        SolvedNow?.Invoke(this);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(0.4f, 0.9f, 1f, 0.6f);
        float h = (size - 1) * 0.5f;
        for (int i = 0; i < size; i++)
            for (int j = 0; j < size; j++)
                Gizmos.DrawWireCube(new Vector3((i - h) * cell, 0.02f, (j - h) * cell), new Vector3(cell * 0.95f, 0.02f, cell * 0.95f));
    }
}
