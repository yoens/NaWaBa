using UnityEngine;

// A lamp above the gate that shows the sequence (one per plate colour, same index).
public class MemoryLamp : MonoBehaviour
{
    [SerializeField, Min(0)] private int index;
    [SerializeField] private Color color = Color.red;
    [SerializeField] private Renderer bulb;

    private float lit;
    private Light glow;

    public int Index => index;

    public void Configure(int lampIndex, Color lampColor, Renderer bulbRenderer)
    {
        index = lampIndex;
        color = lampColor;
        bulb = bulbRenderer;
    }

    private void Awake()
    {
        if (bulb == null) bulb = GetComponentInChildren<Renderer>();
        glow = GetComponentInChildren<Light>();
        if (glow == null)
        {
            var go = new GameObject("Glow");
            go.transform.SetParent(transform, false);
            glow = go.AddComponent<Light>();
            glow.type = LightType.Point;
            glow.range = 8f;
            glow.intensity = 6f;
        }
        glow.color = color;
        Paint(false);
    }

    public void Flash(float seconds)
    {
        lit = Mathf.Max(lit, seconds);
        Paint(true);
    }

    public void SetOn(bool on)
    {
        lit = on ? float.MaxValue : 0f;
        Paint(on);
    }

    private void Update()
    {
        if (lit <= 0f || lit == float.MaxValue) return;
        lit -= Time.deltaTime;
        if (lit <= 0f) Paint(false);
    }

    private void Paint(bool on)
    {
        Color c = on ? Color.Lerp(color, Color.white, 0.4f) : color * 0.3f;
        c.a = 1f;
        RuntimeMaterials.Paint(bulb, c);
        if (glow != null) glow.enabled = on;
    }
}
