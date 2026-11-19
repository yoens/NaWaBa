using System.Collections.Generic;
using UnityEngine;

// The keep is built from one box per map cell (floor tiles, wall cells). The player is a
// Rigidbody box, and a box sliding over the seam between two separate boxes can catch on
// the neighbour's edge ("ghost collision") and stop dead in an empty corridor.
// Merge replaces the per-cell colliders with a few large boxes covering exactly the same
// space: the cell colliders are disabled (their renderers stay) and a "Collision (merged)"
// child holds the merged boxes. Floors are also extended under wall cells (never
// reachable), so the whole floor usually becomes one box without a single seam.
// KeepStageController runs it on start when the scene has no merged boxes yet; the editor
// menu "NaWaBa/성 내부 충돌 합치기 (Stage_Keep)" bakes it into the scene.
public static class KeepCollision
{
    public const string RootName = "Keep (built)";
    public const string MergedName = "Collision (merged)";
    private const string FloorName = "Floor";
    private const string WallName = "WallCollider";
    private const float Eps = 0.001f;

    public struct Result
    {
        public int before, after, skipped;
    }

    private struct Piece
    {
        public Vector3 min, max;
    }

    private struct Source
    {
        public BoxCollider box;
        public Piece piece;
        public bool floor;
    }

    // "Keep (built)" in the given object's scene, or null.
    public static Transform FindRoot(UnityEngine.SceneManagement.Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return null;
        foreach (GameObject go in scene.GetRootGameObjects())
            if (go.name == RootName) return go.transform;
        return null;
    }

    public static bool IsMerged(Transform root) => root != null && root.Find(MergedName) != null;

    // willChange(object) is called before a component is modified, created(go) after a new
    // object is made, destroy(go) removes the previous merge (editor: Undo; runtime: null).
    public static Result Merge(Transform root, System.Action<Object> willChange = null,
        System.Action<GameObject> created = null, System.Action<GameObject> destroy = null)
    {
        var result = new Result();
        Transform old = root.Find(MergedName);
        if (old != null)
        {
            if (destroy != null) destroy(old.gameObject);
            else Object.DestroyImmediate(old.gameObject);
        }

        // Builder-made cell colliders (also ones a previous merge disabled).
        var sources = new List<Source>();
        foreach (BoxCollider box in root.GetComponentsInChildren<BoxCollider>(true))
        {
            string name = box.gameObject.name;
            if (name != FloorName && name != WallName) continue;
            if (!box.gameObject.activeInHierarchy || box.isTrigger) continue;
            if (!TryLocalBounds(root, box, out Piece piece))
            {
                // Rotated by hand: leave it as it is (an axis-aligned box would not fit it).
                result.skipped++;
                if (!box.enabled)
                {
                    willChange?.Invoke(box);
                    box.enabled = true;
                }
                continue;
            }
            sources.Add(new Source { box = box, piece = piece, floor = name == FloorName });
        }
        if (sources.Count == 0) return result;

        // Same layer, material and height range -> one group of boxes to merge.
        var groups = new Dictionary<(int, PhysicsMaterial, bool, long, long), List<Piece>>();
        foreach (Source s in sources)
        {
            var key = (s.box.gameObject.layer, s.box.sharedMaterial, s.floor, Q(s.piece.min.y), Q(s.piece.max.y));
            if (!groups.TryGetValue(key, out List<Piece> list)) groups[key] = list = new List<Piece>();
            list.Add(s.piece);
        }

        // Floors also fill the footprint of wall cells standing right on top of them.
        foreach (var pair in groups)
        {
            if (!pair.Key.Item3) continue;
            long top = pair.Key.Item5;
            Piece any = pair.Value[0];
            var fill = new List<Piece>();
            foreach (Source s in sources)
            {
                if (s.floor || Q(s.piece.min.y) != top) continue;
                fill.Add(new Piece
                {
                    min = new Vector3(s.piece.min.x, any.min.y, s.piece.min.z),
                    max = new Vector3(s.piece.max.x, any.max.y, s.piece.max.z),
                });
            }
            pair.Value.AddRange(fill);
        }

        var container = new GameObject(MergedName);
        created?.Invoke(container);
        container.transform.SetParent(root, false);
        foreach (var pair in groups)
        {
            int index = 0;
            foreach (Piece rect in MergeRects(pair.Value))
            {
                var go = new GameObject($"{(pair.Key.Item3 ? "FloorCollision" : "WallCollision")} {++index:00}");
                created?.Invoke(go);
                go.layer = pair.Key.Item1;
                go.transform.SetParent(container.transform, false);
                go.transform.localPosition = (rect.min + rect.max) * 0.5f;
                BoxCollider merged = go.AddComponent<BoxCollider>();
                merged.size = rect.max - rect.min;
                merged.sharedMaterial = pair.Key.Item2;
                result.after++;
            }
        }

        foreach (Source s in sources)
        {
            result.before++;
            if (!s.box.enabled) continue;
            willChange?.Invoke(s.box);
            s.box.enabled = false;
        }
        return result;
    }

    // Box of the collider in root space; false unless it is axis-aligned with the root.
    private static bool TryLocalBounds(Transform root, BoxCollider box, out Piece piece)
    {
        piece = default;
        Transform t = box.transform;
        Quaternion relative = Quaternion.Inverse(root.rotation) * t.rotation;
        foreach (Vector3 axis in new[] { Vector3.right, Vector3.up, Vector3.forward })
        {
            Vector3 a = relative * axis;
            if (Mathf.Max(Mathf.Abs(a.x), Mathf.Abs(a.y), Mathf.Abs(a.z)) < 0.999f) return false;
        }
        Vector3 half = box.size * 0.5f;
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
            Vector3 local = root.InverseTransformPoint(t.TransformPoint(box.center + corner));
            min = Vector3.Min(min, local);
            max = Vector3.Max(max, local);
        }
        piece = new Piece { min = min, max = max };
        return true;
    }

    // Exact union of boxes sharing one height range: runs along X (same Z extent), then
    // those runs along Z (same X extent).
    private static List<Piece> MergeRects(List<Piece> pieces)
    {
        var runs = new List<Piece>();
        foreach (List<Piece> row in GroupBy(pieces, p => (Q(p.min.z), Q(p.max.z))))
        {
            row.Sort((a, b) => a.min.x.CompareTo(b.min.x));
            Piece current = row[0];
            for (int i = 1; i < row.Count; i++)
            {
                if (row[i].min.x <= current.max.x + Eps) current.max.x = Mathf.Max(current.max.x, row[i].max.x);
                else { runs.Add(current); current = row[i]; }
            }
            runs.Add(current);
        }

        var result = new List<Piece>();
        foreach (List<Piece> column in GroupBy(runs, p => (Q(p.min.x), Q(p.max.x))))
        {
            column.Sort((a, b) => a.min.z.CompareTo(b.min.z));
            Piece current = column[0];
            for (int i = 1; i < column.Count; i++)
            {
                if (column[i].min.z <= current.max.z + Eps) current.max.z = Mathf.Max(current.max.z, column[i].max.z);
                else { result.Add(current); current = column[i]; }
            }
            result.Add(current);
        }
        return result;
    }

    private static IEnumerable<List<Piece>> GroupBy(List<Piece> pieces, System.Func<Piece, (long, long)> key)
    {
        var groups = new Dictionary<(long, long), List<Piece>>();
        foreach (Piece p in pieces)
        {
            var k = key(p);
            if (!groups.TryGetValue(k, out List<Piece> list)) groups[k] = list = new List<Piece>();
            list.Add(p);
        }
        return groups.Values;
    }

    private static long Q(float value) => (long)Mathf.Round(value / Eps);
}
