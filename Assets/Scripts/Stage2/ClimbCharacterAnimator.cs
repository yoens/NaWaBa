using UnityEngine;

[RequireComponent(typeof(Animator))]
public class ClimbCharacterAnimator : MonoBehaviour
{
    private static readonly int SpeedId =
        Animator.StringToHash("Speed");

    private static readonly int GroundedId =
        Animator.StringToHash("Grounded");

    private static readonly int VerticalSpeedId =
        Animator.StringToHash("VerticalSpeed");

    private Animator animator;
    private ClimbActorController actor;
    private Rigidbody body;

    private void Awake()
    {
        animator = GetComponent<Animator>();
        animator.applyRootMotion = false;
    }

    private void Start()
    {
        // 게임 시작 시 생성된 비주얼에서 부모 Player를 찾습니다.
        actor = GetComponentInParent<ClimbActorController>();

        if (actor != null)
            body = actor.GetComponent<Rigidbody>();
    }

    private void Update()
    {
        if (animator == null || !animator.isActiveAndEnabled)
            return;

        // 대기실이나 1스테이지에서는 우선 Idle을 유지합니다.
        if (actor == null || body == null ||
            !actor.isActiveAndEnabled)
        {
            animator.SetFloat(SpeedId, 0f);
            animator.SetBool(GroundedId, true);
            animator.SetFloat(VerticalSpeedId, 0f);
            return;
        }

        Vector3 velocity = body.linearVelocity;

        float horizontalSpeed = new Vector2(
            velocity.x,
            velocity.z
        ).magnitude;

        animator.SetFloat(SpeedId, horizontalSpeed);
        animator.SetBool(GroundedId, actor.IsGrounded);
        animator.SetFloat(VerticalSpeedId, velocity.y);
    }
}