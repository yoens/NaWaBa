using System.Collections;
using UnityEngine;

// One region on the world map diorama (built by "NaWaBa > 지도 만들기 (WorldMap)").
// The region index refers to NaWaBaStages.regions; an index past that list is a teaser
// ("???") that always stays locked. Its box collider is what the mouse clicks.
public class WorldMapRegion : MonoBehaviour
{
    [SerializeField, Min(0)] private int regionIndex;
    [Tooltip("Name used when the stage list has no entry for this region (teaser).")]
    [SerializeField] private string fallbackTitle = "???";
    [Tooltip("Clouds covering the region while it is locked.")]
    [SerializeField] private GameObject lockCover;
    [Tooltip("Stepping stones from the previous region; shown once this region is open.")]
    [SerializeField] private GameObject path;
    [SerializeField, Min(0f)] private float labelHeight = 4.5f;

    private Vector3 coverScale = Vector3.one;
    private Coroutine unlocking;

    public int RegionIndex => regionIndex;
    public string FallbackTitle => fallbackTitle;
    public Vector3 LabelPoint => transform.position + Vector3.up * labelHeight;
    public Vector3 FocusPoint => transform.position + Vector3.up * 1.2f;
    public bool IsAnimating => unlocking != null;

    private void Awake()
    {
        if (lockCover != null) coverScale = lockCover.transform.localScale;
    }

    public void ShowLocked(bool locked)
    {
        StopUnlock();
        if (lockCover != null)
        {
            lockCover.transform.localScale = coverScale;
            lockCover.SetActive(locked);
        }
        if (path != null)
        {
            path.SetActive(!locked);
            foreach (Transform stone in path.transform) stone.gameObject.SetActive(true);
        }
    }

    // Clouds shrink away, then the path appears stone by stone (from the previous region).
    public void PlayUnlock(float duration)
    {
        StopUnlock();
        unlocking = StartCoroutine(Unlock(Mathf.Max(0.1f, duration)));
    }

    public void FinishUnlock()
    {
        if (unlocking != null) ShowLocked(false);
    }

    private void StopUnlock()
    {
        if (unlocking != null) StopCoroutine(unlocking);
        unlocking = null;
    }

    private IEnumerator Unlock(float duration)
    {
        if (path != null)
        {
            path.SetActive(true);
            foreach (Transform stone in path.transform) stone.gameObject.SetActive(false);
        }
        if (lockCover != null) lockCover.SetActive(true);

        float coverTime = duration * 0.6f;
        for (float t = 0f; t < coverTime; t += Time.unscaledDeltaTime)
        {
            if (lockCover != null)
            {
                float k = 1f - Mathf.SmoothStep(0f, 1f, t / coverTime);
                lockCover.transform.localScale = coverScale * k;
            }
            yield return null;
        }
        if (lockCover != null)
        {
            lockCover.SetActive(false);
            lockCover.transform.localScale = coverScale;
        }

        if (path != null && path.transform.childCount > 0)
        {
            float step = duration * 0.4f / path.transform.childCount;
            foreach (Transform stone in path.transform)
            {
                stone.gameObject.SetActive(true);
                for (float t = 0f; t < step; t += Time.unscaledDeltaTime) yield return null;
            }
        }
        unlocking = null;
    }
}
