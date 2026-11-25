using UnityEngine;

// Decoration: a ghost (or anything) hovering in the crypt shafts, bobbing and turning slowly.
// No collider, no effect on the game.
public class CryptFloat : MonoBehaviour
{
    [SerializeField] private float bobHeight = 0.35f;
    [SerializeField] private float bobSpeed = 1.6f;
    [SerializeField] private float turnSpeed = 25f;   // degrees per second

    private Vector3 home;
    private float phase;

    private void Awake()
    {
        home = transform.localPosition;
        phase = (home.x * 0.37f + home.z * 0.21f) % 6.28f;
    }

    private void Update()
    {
        transform.localPosition = home + Vector3.up * (Mathf.Sin(Time.time * bobSpeed + phase) * bobHeight);
        transform.Rotate(0f, turnSpeed * Time.deltaTime, 0f, Space.World);
    }
}
