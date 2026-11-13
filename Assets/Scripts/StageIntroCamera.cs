using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Opening shot for stages with a destination: the camera starts at the goal and glides back
// along the route to the player, then hands over to the follow camera. The stage controller
// adds this to its camera and calls Play(); gameplay waits until it finishes.
// The host can skip it with Space / Enter / a click.
public class StageIntroCamera : MonoBehaviour
{
    [SerializeField, Min(1f)] private float metresPerSecond = 13f;
    [SerializeField] private Vector2 durationRange = new Vector2(3.5f, 8f);
    [SerializeField] private float height = 4f;        // above the route
    [SerializeField] private float backDistance = 8f;  // behind, looking toward the goal
    [SerializeField, Range(0.05f, 0.5f)] private float blendToFollow = 0.2f;
    [SerializeField] private bool allowSkip = true;

    private readonly List<Vector3> samples = new List<Vector3>();
    private readonly List<float> distances = new List<float>();
    private ClimbCameraController follow;
    private Action finished;
    private float elapsed, duration, totalLength;

    public bool IsPlaying { get; private set; }

    // route: points from the goal (first) to the player (last), at ground height.
    public void Play(IList<Vector3> route, ClimbCameraController followCamera, Action onFinished)
    {
        follow = followCamera;
        finished = onFinished;
        BuildPath(route);
        if (samples.Count < 2 || totalLength < 1f)
        {
            Finish();
            return;
        }

        duration = Mathf.Clamp(totalLength / metresPerSecond, durationRange.x, durationRange.y);
        elapsed = 0f;
        IsPlaying = true;
        if (follow != null)
        {
            follow.ResetBehindTarget();
            follow.enabled = false;
        }
        Apply(0f);
    }

    public void Skip()
    {
        if (IsPlaying) Finish();
    }

    private void LateUpdate()
    {
        if (!IsPlaying) return;
        if (allowSkip && SkipPressed())
        {
            Finish();
            return;
        }

        elapsed += Time.deltaTime;
        float t = Mathf.Clamp01(elapsed / duration);
        Apply(Mathf.SmoothStep(0f, 1f, t));
        if (t >= 1f) Finish();
    }

    private void Apply(float t)
    {
        float along = t * totalLength;
        Vector3 point = PointAt(along);
        // "Forward" = toward the goal, so the shot always looks at what lies ahead.
        Vector3 ahead = PointAt(Mathf.Max(0f, along - 6f));
        Vector3 forward = ahead - point;
        if (along < 6f) forward = PointAt(0f) - PointAt(Mathf.Min(totalLength, 6f));
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.01f) forward = transform.forward;
        forward.Normalize();

        Vector3 position = point + Vector3.up * height - forward * backDistance;
        Vector3 lookAt = (along < 6f ? PointAt(0f) : ahead) + Vector3.up * 1.5f;
        Quaternion rotation = Quaternion.LookRotation(lookAt - position, Vector3.up);

        // Ease into the follow camera's pose at the end so there is no jump cut.
        if (follow != null && t > 1f - blendToFollow)
        {
            follow.GetFollowPose(out Vector3 followPosition, out Quaternion followRotation);
            float k = Mathf.SmoothStep(0f, 1f, (t - (1f - blendToFollow)) / blendToFollow);
            position = Vector3.Lerp(position, followPosition, k);
            rotation = Quaternion.Slerp(rotation, followRotation, k);
        }
        transform.SetPositionAndRotation(position, rotation);
    }

    private void Finish()
    {
        IsPlaying = false;
        if (follow != null) follow.enabled = true;
        Action callback = finished;
        finished = null;
        callback?.Invoke();
    }

    private static bool SkipPressed()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && (keyboard.spaceKey.wasPressedThisFrame ||
                                 keyboard.enterKey.wasPressedThisFrame))
            return true;
        Mouse mouse = Mouse.current;
        return mouse != null && mouse.leftButton.wasPressedThisFrame;
    }

    // Smooth Catmull-Rom path through the route, measured by length.
    private void BuildPath(IList<Vector3> route)
    {
        samples.Clear();
        distances.Clear();
        totalLength = 0f;
        if (route == null || route.Count == 0) return;
        if (route.Count == 1)
        {
            samples.Add(route[0]);
            distances.Add(0f);
            return;
        }

        for (int i = 0; i < route.Count - 1; i++)
        {
            Vector3 p0 = route[Mathf.Max(0, i - 1)];
            Vector3 p1 = route[i];
            Vector3 p2 = route[i + 1];
            Vector3 p3 = route[Mathf.Min(route.Count - 1, i + 2)];
            for (int s = 0; s < 12; s++)
                AddSample(CatmullRom(p0, p1, p2, p3, s / 12f));
        }
        AddSample(route[route.Count - 1]);
    }

    private void AddSample(Vector3 point)
    {
        if (samples.Count > 0) totalLength += Vector3.Distance(samples[samples.Count - 1], point);
        samples.Add(point);
        distances.Add(totalLength);
    }

    private Vector3 PointAt(float along)
    {
        if (samples.Count == 0) return transform.position;
        if (along <= 0f) return samples[0];
        for (int i = 1; i < samples.Count; i++)
        {
            if (distances[i] < along) continue;
            float span = distances[i] - distances[i - 1];
            float k = span > 0.0001f ? (along - distances[i - 1]) / span : 0f;
            return Vector3.Lerp(samples[i - 1], samples[i], k);
        }
        return samples[samples.Count - 1];
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                       (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    // Convenience for stage controllers: find or add the intro on the follow camera.
    public static StageIntroCamera For(ClimbCameraController followCamera)
    {
        if (followCamera == null) return null;
        StageIntroCamera intro = followCamera.GetComponent<StageIntroCamera>();
        return intro != null ? intro : followCamera.gameObject.AddComponent<StageIntroCamera>();
    }
}
