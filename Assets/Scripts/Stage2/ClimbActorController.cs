using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class ClimbActorController : MonoBehaviour
{
    [SerializeField] private InputRouter inputRouter;
    [SerializeField] private ClimbCameraController climbCamera;
    [SerializeField] private LayerMask groundMask;

    [SerializeField] private float moveSpeed = 4f;
    [SerializeField] private float turnSpeed = 720f;
    [SerializeField] private float jumpHeight = 2f;

    [SerializeField, Min(1f)]
    private float gravityMultiplier = 2.5f;

    [SerializeField] private float fallResetY = -5f;

    private readonly HashSet<Collider> groundContacts = new();

    private Rigidbody body;
    private Vector3 startPosition;
    private Quaternion startRotation;

    private bool jumpRequested;
    private float ignoreGroundUntil;

    public bool IsGrounded =>
        body != null &&
        groundContacts.Count > 0 &&
        Time.fixedTime >= ignoreGroundUntil &&
        body.linearVelocity.y <= 0.1f;

    // 원격 조작 준비(영상 연결·카운트다운) 중에는 스테이지가 입력을 잠급니다.
    public bool InputLocked { get; set; }

    // 원격 조작자는 호스트 창의 포커스·마우스 잠금과 무관하게 조작합니다.
    private bool RemoteActive =>
        inputRouter.isActiveAndEnabled &&
        inputRouter.IsRemoteControl;

    private bool LocalActive =>
        Application.isFocused &&
        Cursor.lockState == CursorLockMode.Locked &&
        inputRouter.isActiveAndEnabled &&
        !inputRouter.IsRemoteControl &&
        climbCamera.isActiveAndEnabled;

    private bool CanControl =>
        !InputLocked && (RemoteActive || LocalActive);

    private void Awake()
    {
        body = GetComponent<Rigidbody>();

        if (inputRouter == null || climbCamera == null ||
            groundMask.value == 0)
        {
            Debug.LogError(
                "ClimbActorController: 참조와 Ground Mask를 확인하세요.",
                this);
            enabled = false;
            return;
        }

        startPosition = body.position;
        startRotation = body.rotation;
    }

    private void Start()
    {
        inputRouter.UseLocalInput();
    }

    private void Update()
    {
        // 조작할 수 없는 동안 눌린 점프는 버립니다(나중에 갑자기 점프하지 않도록).
        bool jumpPressed = inputRouter.isActiveAndEnabled && inputRouter.ConsumeJump();

        if (!CanControl)
        {
            jumpRequested = false;
            return;
        }

        if (jumpPressed)
            jumpRequested = true;
    }

    private void FixedUpdate()
    {
        bool wantsJump = jumpRequested;
        jumpRequested = false;

        if (body.position.y < fallResetY)
        {
            ResetPosition();
            return;
        }

        groundContacts.RemoveWhere(c =>
            c == null || !c.enabled || !c.gameObject.activeInHierarchy);

        Vector2 input = CanControl
            ? Vector2.ClampMagnitude(inputRouter.ReadMove(), 1f)
            : Vector2.zero;

        Vector3 direction = climbCamera.PlanarRotation *
            new Vector3(input.x, 0f, input.y);

        Vector3 velocity = body.linearVelocity;
        velocity.x = direction.x * moveSpeed;
        velocity.z = direction.z * moveSpeed;

        bool grounded = IsGrounded;

        float gravityScale = Mathf.Max(1f, gravityMultiplier);

        if (CanControl && wantsJump && grounded)
        {
            float gravity = Mathf.Abs(Physics.gravity.y) * gravityScale;
            velocity.y = Mathf.Sqrt(2f * gravity * jumpHeight);

            // 점프 직전의 접촉 정보로 다시 점프하지 않도록 제거합니다.
            groundContacts.Clear();
            ignoreGroundUntil = Time.fixedTime + 0.1f;
        }

        body.linearVelocity = velocity;

        // 기본 중력에 추가 중력을 더합니다.
        body.AddForce(
            Physics.gravity * (gravityScale - 1f),
            ForceMode.Acceleration);

        if (direction.sqrMagnitude > 0.001f)
        {
            Quaternion desiredRotation =
                Quaternion.LookRotation(direction, Vector3.up);

            body.MoveRotation(Quaternion.RotateTowards(
                body.rotation,
                desiredRotation,
                turnSpeed * Time.fixedDeltaTime));
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        UpdateGroundContact(collision);
    }

    private void OnCollisionStay(Collision collision)
    {
        UpdateGroundContact(collision);
    }

    private void OnCollisionExit(Collision collision)
    {
        groundContacts.Remove(collision.collider);
    }

    private void UpdateGroundContact(Collision collision)
    {
        Collider other = collision.collider;
        groundContacts.Remove(other);

        if (!isActiveAndEnabled ||
            Time.fixedTime < ignoreGroundUntil)
            return;

        if ((groundMask.value & (1 << other.gameObject.layer)) == 0)
            return;

        if (other.attachedRigidbody == body)
            return;

        for (int i = 0; i < collision.contactCount; i++)
        {
            ContactPoint contact = collision.GetContact(i);

            // 아래쪽에서 받쳐 주는 표면만 바닥으로 인정합니다.
            if (contact.normal.y >= 0.65f &&
                contact.point.y < body.worldCenterOfMass.y)
            {
                groundContacts.Add(other);
                return;
            }
        }
    }

    private void ResetPosition()
    {
        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        body.position = startPosition;
        body.rotation = startRotation;

        groundContacts.Clear();
        jumpRequested = false;
        ignoreGroundUntil = Time.fixedTime + 0.1f;
    }

    private void OnDisable()
    {
        InputLocked = false;
        jumpRequested = false;
        groundContacts.Clear();

        if (body != null && !body.isKinematic)
        {
            Vector3 velocity = body.linearVelocity;
            velocity.x = 0f;
            velocity.z = 0f;
            body.linearVelocity = velocity;
        }
    }
}