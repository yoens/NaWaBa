using System.Collections;
using UnityEngine;

// The closed castle gate. Open() raises it like a portcullis and then lets the player pass.
public class GateDoor : MonoBehaviour
{
    [SerializeField, Min(0.2f)] private float openSeconds = 2.5f;
    [SerializeField] private float riseHeight;      // 0 = its own height

    public bool IsOpen { get; private set; }          // opening started
    public bool IsFullyOpen { get; private set; }     // risen, player can pass

    public void Open()
    {
        if (IsOpen) return;
        IsOpen = true;
        StartCoroutine(Raise());
    }

    private IEnumerator Raise()
    {
        float height = riseHeight;
        if (height <= 0f)
        {
            Bounds b = new Bounds(transform.position, Vector3.zero);
            foreach (Renderer r in GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
            height = Mathf.Max(1f, b.size.y * 0.95f);
        }
        Vector3 from = transform.position, to = from + Vector3.up * height;
        for (float t = 0f; t < openSeconds; t += Time.deltaTime)
        {
            transform.position = Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t / openSeconds));
            yield return null;
        }
        transform.position = to;
        foreach (Collider c in GetComponentsInChildren<Collider>()) c.enabled = false;
        IsFullyOpen = true;
    }
}
