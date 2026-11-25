using UnityEngine;

// A mirror on a stand in a MirrorRoom. It sits on one cell; the player pushes it to the next
// cell or turns it. "Slash" = the glass runs like '/' seen from above (+X+Z diagonal), otherwise
// like '\'. Only the child "pane" turns; the stand and its box collider stay square to the grid.
public class MirrorPiece : MonoBehaviour
{
    [SerializeField] private Transform pane;
    [SerializeField] private bool slash = true;
    [Tooltip("Where this mirror stands in a solution (used when the difficulty skips the room).")]
    [SerializeField] private bool hasSolvedCell;
    [SerializeField] private Vector2Int solvedCell;
    [SerializeField, Min(0.05f)] private float moveTime = 0.25f;
    [SerializeField, Min(30f)] private float turnSpeed = 480f;   // degrees per second

    private MirrorRoom room;
    private Vector2Int startCell;
    private bool startSlash;
    private Vector3 goal;   // local position in the room

    public Vector2Int Cell { get; private set; }
    public bool Slash => slash;
    public bool HasSolvedCell => hasSolvedCell;
    public Vector2Int SolvedCell => solvedCell;
    public bool Frozen { get; set; }
    public bool Moving => room != null && (RoomLocal - goal).sqrMagnitude > 0.0004f;
    public MirrorRoom Room => room;

    private Vector3 RoomLocal => room.transform.InverseTransformPoint(transform.position);
    private Quaternion PaneTurn => Quaternion.Euler(0f, slash ? -45f : 45f, 0f);

    public void Configure(Transform glass, bool startAsSlash, bool solvedKnown, Vector2Int solved)
    {
        pane = glass;
        slash = startAsSlash;
        hasSolvedCell = solvedKnown;
        solvedCell = solved;
        if (pane != null) pane.localRotation = PaneTurn;
    }

    public void Bind(MirrorRoom owner, Vector2Int at)
    {
        room = owner;
        startCell = at;
        startSlash = slash;
        MoveTo(at, true);
        SetSlash(slash, true);
    }

    public void MoveTo(Vector2Int at, bool instant)
    {
        Cell = at;
        Vector3 c = room.Center(at);
        goal = new Vector3(c.x, RoomLocal.y, c.z);
        if (instant) transform.position = room.transform.TransformPoint(goal);
    }

    public void Turn() => SetSlash(!slash, false);

    public void SetSlash(bool value, bool instant)
    {
        slash = value;
        if (instant && pane != null) pane.localRotation = PaneTurn;
    }

    public bool ResetToStart()
    {
        bool changed = Cell != startCell || slash != startSlash;
        MoveTo(startCell, false);
        SetSlash(startSlash, false);
        return changed;
    }

    private void Update()
    {
        if (room == null || Frozen) return;
        float speed = room.Cell / moveTime;
        Vector3 now = RoomLocal;
        if ((now - goal).sqrMagnitude > 0.000001f)
            transform.position = room.transform.TransformPoint(Vector3.MoveTowards(now, goal, speed * Time.deltaTime));
        if (pane != null)
            pane.localRotation = Quaternion.RotateTowards(pane.localRotation, PaneTurn, turnSpeed * Time.deltaTime);
    }
}
