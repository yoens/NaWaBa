using TMPro;
using UnityEngine;

[RequireComponent(typeof(TextMeshProUGUI))]
public sealed class NaWaBaVideoClock : MonoBehaviour
{
    private TextMeshProUGUI clockText;
    private double startedAt;

    private void Awake()
    {
        clockText = GetComponent<TextMeshProUGUI>();
        clockText.richText = false;
        clockText.raycastTarget = false;
    }

    private void OnEnable()
    {
        startedAt = Time.realtimeSinceStartupAsDouble;
    }

    private void LateUpdate()
    {
        long elapsedMilliseconds = (long)(
            (Time.realtimeSinceStartupAsDouble - startedAt) * 1000.0
        );

        clockText.text = $"{elapsedMilliseconds:D8} ms";
    }
}