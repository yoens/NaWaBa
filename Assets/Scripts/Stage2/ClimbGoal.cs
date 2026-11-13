using UnityEngine;

// Goal in front of the castle gate. Put this on a GameObject with a trigger collider
// (a Box Collider is added if missing) and move it to where the player should land.
// Clear = the player stands on the ground inside the trigger. Reported once per run.
[RequireComponent(typeof(Collider))]
public class ClimbGoal : MonoBehaviour
{
    [SerializeField] private ClimbStageController stage;
    // A simple flag is drawn at runtime so the goal is visible without extra art.
    [SerializeField] private bool showMarker = true;
    [SerializeField] private Color markerColor = new Color(1f, 0.78f, 0.1f);

    private void Awake()
    {
        Collider area = GetComponent<Collider>();
        area.isTrigger = true;
        if (stage == null)
        {
            foreach (ClimbStageController candidate in
                     FindObjectsByType<ClimbStageController>(FindObjectsSortMode.None))
            {
                if (candidate.gameObject.scene == gameObject.scene) { stage = candidate; break; }
            }
        }
        if (stage == null)
            Debug.LogError("ClimbGoal: no ClimbStageController in this scene.", this);
        if (showMarker) BuildMarker(area);
    }

    private void OnTriggerStay(Collider other)
    {
        if (stage == null) return;
        ClimbActorController actor = other.GetComponentInParent<ClimbActorController>();
        // Landing counts, passing through mid-air does not.
        if (actor != null && actor.IsGrounded && !actor.Frozen)
            stage.ReachGoal();
    }

    private void BuildMarker(Collider area)
    {
        Bounds bounds = area.bounds;
        Vector3 basePoint = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);

        GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pole.name = "GoalPole";
        Destroy(pole.GetComponent<Collider>());
        pole.transform.SetParent(transform, true);
        pole.transform.position = basePoint + Vector3.up * 1f;
        pole.transform.localScale = new Vector3(0.08f, 1f, 0.08f);

        GameObject flag = GameObject.CreatePrimitive(PrimitiveType.Cube);
        flag.name = "GoalFlag";
        Destroy(flag.GetComponent<Collider>());
        flag.transform.SetParent(transform, true);
        flag.transform.position = basePoint + new Vector3(0.3f, 1.75f, 0f);
        flag.transform.localScale = new Vector3(0.55f, 0.35f, 0.04f);
        Renderer flagRenderer = flag.GetComponent<Renderer>();
        if (flagRenderer != null) flagRenderer.material.color = markerColor;
    }
}
