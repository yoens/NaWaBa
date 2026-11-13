using UnityEngine;

// Relays trigger hits from snowballs and the summit area to the SnowSlope.
public class SnowSlopeTrigger : MonoBehaviour
{
    public enum Kind { Snowball, Summit }

    private SnowSlope slope;
    private Kind kind;

    public void Init(SnowSlope owner, Kind triggerKind)
    {
        slope = owner;
        kind = triggerKind;
    }

    private void OnTriggerEnter(Collider other) => Relay(other);
    private void OnTriggerStay(Collider other) { if (kind == Kind.Summit) Relay(other); }

    private void Relay(Collider other)
    {
        if (slope == null) return;
        ClimbActorController actor = other.GetComponentInParent<ClimbActorController>();
        if (actor != null && !actor.Frozen) slope.OnTrigger(kind, actor);
    }
}
