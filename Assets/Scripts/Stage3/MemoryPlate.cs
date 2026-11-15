using UnityEngine;

// A coloured floor plate for the stage 3 memory puzzle. Landing on it (from walking in or a
// jump) counts as one press, so the same colour twice = jump on the plate again.
// The builder menu makes these; you can move them freely. Index = its colour slot (0..).
[RequireComponent(typeof(Collider))]
public class MemoryPlate : MonoBehaviour
{
    [SerializeField, Min(0)] private int index;
    [SerializeField] private Color color = Color.red;
    [SerializeField] private Renderer top;            // the part that lights up (auto-found)

    private GateStageController stage;
    private bool groundedInside;
    private float lit;

    public int Index => index;
    public Color Color => color;

    public void Configure(int plateIndex, Color plateColor, Renderer litPart)
    {
        index = plateIndex;
        color = plateColor;
        top = litPart;
    }

    private void Awake()
    {
        GetComponent<Collider>().isTrigger = true;
        if (top == null) top = GetComponentInChildren<Renderer>();
        stage = FindFirstObjectByType<GateStageController>();
        Paint(false);
    }

    // Light for a moment (sequence playback or a press).
    public void Flash(float seconds)
    {
        lit = Mathf.Max(lit, seconds);
        Paint(true);
    }

    private void Update()
    {
        if (lit <= 0f) return;
        lit -= Time.deltaTime;
        if (lit <= 0f) Paint(false);
    }

    private void Paint(bool on)
    {
        // Dim when idle, bright when lit (works in builds: URP material from RuntimeMaterials).
        Color c = on ? Color.Lerp(color, Color.white, 0.35f) : color * 0.45f;
        c.a = 1f;
        RuntimeMaterials.Paint(top, c);
    }

    private void OnTriggerStay(Collider other)
    {
        ClimbActorController actor = other.GetComponentInParent<ClimbActorController>();
        if (actor == null || stage == null) return;
        bool grounded = actor.IsGrounded && !actor.Frozen;
        if (grounded && !groundedInside) stage.OnPlatePressed(this);
        groundedInside = grounded;
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.GetComponentInParent<ClimbActorController>() != null) groundedInside = false;
    }
}
