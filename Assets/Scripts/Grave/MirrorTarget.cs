using UnityEngine;

// The crystal a MirrorRoom's light must reach. Stands on the walkway next to the grid.
// Until it is hit it pulses (light and markers: floor ring, light pillar, aim diamond) so the
// goal is easy to spot; lit = its renderers use the glowing material, the markers go away.
public class MirrorTarget : MonoBehaviour
{
    [SerializeField] private Material litMaterial;
    [SerializeField] private Light glow;
    [SerializeField] private Transform[] markers = new Transform[0];
    [SerializeField] private float spinWhenLit = 40f;
    [SerializeField] private float pulseSpeed = 3f;
    [SerializeField] private float idleIntensity = 1.4f, litIntensity = 3.5f;

    private Renderer[] renderers;
    private Material[][] original;
    private Vector3[] markerScale;
    private bool lit;

    public bool Lit => lit;

    public void Configure(Material glowing, Light light, Transform[] marks)
    {
        litMaterial = glowing;
        glow = light;
        markers = marks;
    }

    private void Awake()
    {
        renderers = GetComponentsInChildren<Renderer>(true);
        original = new Material[renderers.Length][];
        for (int i = 0; i < renderers.Length; i++) original[i] = renderers[i].sharedMaterials;
        markerScale = new Vector3[markers.Length];
        for (int i = 0; i < markers.Length; i++) if (markers[i] != null) markerScale[i] = markers[i].localScale;
        if (glow != null) glow.enabled = true;
    }

    public void SetLit(bool on)
    {
        if (lit == on || renderers == null) return;
        lit = on;
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] == null || IsMarker(renderers[i].transform)) continue;
            if (!on || litMaterial == null) { renderers[i].sharedMaterials = original[i]; continue; }
            var glowing = new Material[original[i].Length];
            for (int m = 0; m < glowing.Length; m++) glowing[m] = litMaterial;
            renderers[i].sharedMaterials = glowing;
        }
        foreach (Transform marker in markers) if (marker != null) marker.gameObject.SetActive(!on);
    }

    private bool IsMarker(Transform t)
    {
        foreach (Transform marker in markers)
            if (marker != null && t.IsChildOf(marker)) return true;
        return false;
    }

    private void Update()
    {
        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * pulseSpeed);
        if (glow != null) glow.intensity = lit ? litIntensity : idleIntensity * (0.6f + 0.8f * pulse);
        if (lit)
        {
            transform.Rotate(0f, spinWhenLit * Time.deltaTime, 0f, Space.World);
            return;
        }
        for (int i = 0; i < markers.Length; i++)
        {
            if (markers[i] == null) continue;
            Vector3 s = markerScale[i];
            // The floor ring breathes; the pillar and the diamond swell a little.
            markers[i].localScale = i == 0
                ? new Vector3(s.x * (0.85f + 0.3f * pulse), s.y, s.z * (0.85f + 0.3f * pulse))
                : s * (0.85f + 0.3f * pulse);
        }
    }
}
