using UnityEngine;

// Floor spikes: hidden -> peek out (warning) -> up (deadly) -> hidden, on a loop.
// "spikes" is the child that moves; "hazard" is armed only while the spikes are up.
public class TrapSpikes : MonoBehaviour
{
    [SerializeField, Min(0.1f)] private float downTime = 1.5f;
    [SerializeField, Min(0.1f)] private float warnTime = 0.7f;
    [SerializeField, Min(0.1f)] private float upTime = 1.0f;
    [SerializeField, Range(0f, 1f)] private float phase;
    [SerializeField] private float hiddenDepth = 0.36f;   // metres below the floor when down
    [SerializeField] private float peek = 0.1f;           // metres showing during the warning
    [SerializeField] private Transform spikes;
    [SerializeField] private TrapHazard hazard;

    public void Configure(float startPhase, Transform movingPart, TrapHazard deadlyPart)
    {
        phase = startPhase;
        spikes = movingPart;
        hazard = deadlyPart;
    }

    private void Update()
    {
        float cycle = downTime + warnTime + upTime;
        float t = Mathf.Repeat(TrapClock.Time + phase * cycle, cycle);
        float y;
        bool armed = false;
        if (t < downTime) y = -hiddenDepth;
        else if (t < downTime + warnTime) y = -hiddenDepth + peek;
        else
        {
            // Snap up fast, sink back at the end.
            float u = (t - downTime - warnTime) / upTime;
            float k = u < 0.12f ? u / 0.12f : u > 0.85f ? (1f - u) / 0.15f : 1f;
            y = Mathf.Lerp(-hiddenDepth + peek, 0f, k);
            armed = k > 0.5f;
        }
        if (spikes != null) spikes.localPosition = new Vector3(0f, y, 0f);
        if (hazard != null) hazard.Armed = armed;
    }
}
