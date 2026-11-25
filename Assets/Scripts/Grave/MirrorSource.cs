using UnityEngine;

// Where the light of a MirrorRoom comes from: it shoots along this object's forward (snapped to
// the grid). Stands on the walkway next to the grid. The optional light flickers a little.
public class MirrorSource : MonoBehaviour
{
    [SerializeField] private Light glow;
    [SerializeField, Min(0f)] private float flicker = 0.25f;

    private float baseIntensity;

    private void Awake()
    {
        if (glow == null) glow = GetComponentInChildren<Light>();
        if (glow != null) baseIntensity = glow.intensity;
    }

    private void Update()
    {
        if (glow == null) return;
        float n = Mathf.PerlinNoise(Time.time * 3f, transform.position.x * 0.1f) - 0.5f;
        glow.intensity = baseIntensity * (1f + n * flicker * 2f);
    }
}
