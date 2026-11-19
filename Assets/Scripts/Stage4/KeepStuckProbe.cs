using System.Collections.Generic;
using System.Text;
using UnityEngine;

// Diagnostic for stage 4: when the player pushes a direction but barely moves for a moment,
// logs what the player's collider is touching (object path, contact normal, point) to the
// console / Player.log as "[Keep Stuck] ...". Added to the player by KeepStageController.
[RequireComponent(typeof(Rigidbody))]
public class KeepStuckProbe : MonoBehaviour
{
    private const float StuckSpeed = 0.3f;     // m/s (horizontal) counted as "not moving"
    private const float StuckTime = 0.3f;      // seconds of pushing without moving
    private const float LogCooldown = 1.5f;

    private struct Touch
    {
        public Collider collider;
        public Vector3 normal, point;
    }

    private Rigidbody body;
    private ClimbActorController actor;
    private InputRouter input;
    private readonly List<Touch> touches = new List<Touch>();
    private float stuckFor, nextLog;

    private void Awake()
    {
        body = GetComponent<Rigidbody>();
        actor = GetComponent<ClimbActorController>();
    }

    public void Configure(InputRouter router) => input = router;

    private void FixedUpdate()
    {
        Vector2 move = input != null ? input.ReadMove() : Vector2.zero;
        Vector3 v = body.linearVelocity;
        bool pushing = move.sqrMagnitude > 0.25f && (actor == null || !actor.Frozen);
        if (pushing && new Vector2(v.x, v.z).magnitude < StuckSpeed) stuckFor += Time.fixedDeltaTime;
        else stuckFor = 0f;

        if (stuckFor >= StuckTime && Time.time >= nextLog)
        {
            nextLog = Time.time + LogCooldown;
            Log(move);
        }
        touches.Clear();   // refilled by OnCollisionStay after this step
    }

    private void OnCollisionStay(Collision collision)
    {
        for (int i = 0; i < collision.contactCount; i++)
        {
            ContactPoint c = collision.GetContact(i);
            touches.Add(new Touch { collider = collision.collider, normal = c.normal, point = c.point });
        }
    }

    private void Log(Vector2 move)
    {
        var text = new StringBuilder();
        text.Append($"[Keep Stuck] pos {body.position.ToString("F2")} input {move.ToString("F2")} " +
            $"vel {body.linearVelocity.ToString("F2")} grounded {(actor != null && actor.IsGrounded)} touching {touches.Count}:");
        foreach (Touch t in touches)
        {
            string path = t.collider != null ? Path(t.collider.transform) : "(destroyed)";
            string kind = t.collider != null ? $"{t.collider.GetType().Name}{(t.collider.isTrigger ? " trigger" : "")} layer {t.collider.gameObject.layer}" : "";
            text.Append($"\n  {path} [{kind}] normal {t.normal.ToString("F2")} point {t.point.ToString("F2")}");
        }
        Debug.LogWarning(text.ToString(), this);
    }

    private static string Path(Transform t)
    {
        string path = t.name;
        for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }
}
