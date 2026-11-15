using UnityEngine;

// Saw blade: slides back and forth along "travel" (local metres to each side) and spins
// its "spinner" child.
public class TrapSlider : MonoBehaviour
{
    [SerializeField] private Vector3 travel = new Vector3(1.6f, 0f, 0f);
    [SerializeField, Min(0.5f)] private float period = 2.4f;
    [SerializeField, Range(0f, 1f)] private float phase;
    [SerializeField] private Transform spinner;
    [SerializeField] private float spinSpeed = 540f;    // degrees per second

    private Vector3 rest;

    public void Configure(Vector3 localTravel, float seconds, float startPhase, Transform spinPart)
    {
        travel = localTravel;
        period = seconds;
        phase = startPhase;
        spinner = spinPart;
    }

    private void Awake() => rest = transform.localPosition;

    private void Update()
    {
        transform.localPosition = rest + transform.localRotation * travel *
            Mathf.Sin((TrapClock.Time / period + phase) * Mathf.PI * 2f);
        if (spinner != null) spinner.Rotate(0f, 0f, spinSpeed * TrapClock.Delta, Space.Self);
    }
}
