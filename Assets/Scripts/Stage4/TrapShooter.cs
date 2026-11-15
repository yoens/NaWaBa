using System.Collections.Generic;
using UnityEngine;

// Arrow trap: arrows fly across the corridor (along local +X) from each lane in turn.
// Time them, or jump over.
public class TrapShooter : MonoBehaviour
{
    [SerializeField] private GameObject arrowPrefab;            // optional (a simple bolt otherwise)
    [SerializeField, Min(0.3f)] private float interval = 1.8f;  // seconds between shots of one lane
    [SerializeField, Min(1f)] private float speed = 10f;
    [SerializeField, Min(1f)] private float distance = 4.6f;    // flight length (corridor width)
    [SerializeField] private float height = 0.7f;
    [SerializeField] private float[] lanes = { -1.3f, 0f, 1.3f };  // local z of each lane
    [SerializeField, Range(0f, 1f)] private float phase;

    private class Arrow { public Transform transform; public float born; public int lane; }

    private readonly List<Arrow> arrows = new List<Arrow>();
    private int[] fired;

    public void Configure(GameObject prefab, float seconds, float flight, float startPhase)
    {
        arrowPrefab = prefab;
        interval = seconds;
        distance = flight;
        phase = startPhase;
    }

    private void Awake() => fired = new int[lanes.Length];

    private void Update()
    {
        float now = TrapClock.Time + phase * interval;
        for (int i = 0; i < lanes.Length; i++)
        {
            // Lanes fire one after another within the interval.
            int count = Mathf.FloorToInt((now - interval * i / lanes.Length) / interval);
            if (count > fired[i])
            {
                if (fired[i] != 0 || count == 1) Fire(i);
                fired[i] = count;
            }
        }
        float life = distance / speed;
        for (int i = arrows.Count - 1; i >= 0; i--)
        {
            Arrow arrow = arrows[i];
            float age = TrapClock.Time - arrow.born;
            if (age > life || arrow.transform == null)
            {
                if (arrow.transform != null) Destroy(arrow.transform.gameObject);
                arrows.RemoveAt(i);
                continue;
            }
            arrow.transform.localPosition = new Vector3(-distance * 0.5f + speed * age, height, lanes[arrow.lane]);
        }
    }

    private void Fire(int lane)
    {
        GameObject go;
        if (arrowPrefab != null)
        {
            go = Instantiate(arrowPrefab, transform);
            foreach (Collider c in go.GetComponentsInChildren<Collider>()) Destroy(c);
            // Kenney arrow points up (+Y): lay it down so it points along +X.
            go.transform.localRotation = Quaternion.Euler(0f, 0f, -90f);
            go.transform.localScale *= 2f;
        }
        else
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, false);
            go.transform.localScale = new Vector3(1.1f, 0.12f, 0.12f);
            RuntimeMaterials.Paint(go.GetComponent<Renderer>(), new Color(0.35f, 0.25f, 0.15f));
        }
        go.name = "Arrow";
        go.layer = 2;
        go.transform.localPosition = new Vector3(-distance * 0.5f, height, lanes[lane]);
        var hit = new GameObject("Hit");
        hit.layer = 2;
        hit.transform.SetParent(go.transform, false);
        hit.transform.rotation = transform.rotation;
        Vector3 s = go.transform.lossyScale;
        hit.transform.localScale = new Vector3(1f / Mathf.Max(0.01f, s.x), 1f / Mathf.Max(0.01f, s.y), 1f / Mathf.Max(0.01f, s.z));
        BoxCollider box = hit.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(1.1f, 0.35f, 0.35f);
        hit.AddComponent<TrapHazard>();
        arrows.Add(new Arrow { transform = go.transform, born = TrapClock.Time, lane = lane });
    }
}
