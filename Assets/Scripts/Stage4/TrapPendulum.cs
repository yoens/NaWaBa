using UnityEngine;

// Swinging axe: rotates around its local Z axis (the corridor direction), so the blade
// sweeps across the corridor. Put the pivot above the path; the blade hangs below it.
public class TrapPendulum : MonoBehaviour
{
    [SerializeField, Range(10f, 85f)] private float amplitude = 55f;   // degrees to each side
    [SerializeField, Min(0.5f)] private float period = 2.6f;           // seconds per full swing
    [SerializeField, Range(0f, 1f)] private float phase;

    private Quaternion rest;

    public void Configure(float degrees, float seconds, float startPhase)
    {
        amplitude = degrees;
        period = seconds;
        phase = startPhase;
    }

    private void Awake() => rest = transform.localRotation;

    private void Update()
    {
        float angle = amplitude * Mathf.Sin((TrapClock.Time / period + phase) * Mathf.PI * 2f);
        transform.localRotation = rest * Quaternion.Euler(0f, 0f, angle);
    }
}
