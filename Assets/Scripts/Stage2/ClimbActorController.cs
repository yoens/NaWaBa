using System;
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

    // Ground colliders touched last physics step and the surface normal under each.
    private readonly Dictionary<Collider, Vector3> groundContacts = new();
    private readonly List<Collider> staleContacts = new();

    private Rigidbody body;
    private Vector3 startPosition;
    private Quaternion startRotation;
    private Vector3 respawnPosition;
    private Quaternion respawnRotation;

    private bool jumpRequested;
    private float ignoreGroundUntil;

    // Walking up a slope moves the body upward, so the allowed vertical speed grows with
    // the slope (a jump is still far faster than that).
    public bool IsGrounded =>
        body != null &&
        groundContacts.Count > 0 &&
        Time.fixedTime >= ignoreGroundUntil &&
        body.linearVelocity.y <= 0.1f + moveSpeed * SlopeTangent(GroundNormal);

    // Average normal of the ground being touched (Vector3.up when none).
    public Vector3 GroundNormal
    {
        get
        {
            if (groundContacts.Count == 0) return Vector3.up;
            Vector3 sum = Vector3.zero;
            foreach (Vector3 normal in groundContacts.Values) sum += normal;
            return sum.sqrMagnitude > 0.0001f ? sum.normalized : Vector3.up;
        }
    }

    private static float SlopeTangent(Vector3 normal)
    {
        float y = Mathf.Clamp(normal.y, 0.05f, 1f);
        return Mathf.Sqrt(Mathf.Max(0f, 1f - y * y)) / y;
    }

    private bool defaultUseGravity = true;
    private Vector3 carryVelocity;

    private Vector3 GroundMoverVelocity()
    {
        foreach (Collider c in groundContacts.Keys)
        {
            if (c == null) continue;
            ClimbMover mover = c.GetComponentInParent<ClimbMover>();
            if (mover != null) return mover.Velocity;
        }
        return Vector3.zero;
    }

    // 원격 조작 준비(영상 연결·카운트다운) 중에는 스테이지가 입력을 잠급니다.
    public bool InputLocked { get; set; }

    // 맵 밖으로 떨어져 시작점(또는 체크포인트)으로 돌아갈 때 (기여 기록용).
    public event Action Fell;

    // True after a checkpoint was reached: falls return there instead of the start.
    public bool HasCheckpoint { get; private set; }

    // Called by ClimbCheckpoint. The point is where the player's body will be placed.
    public void SetRespawnPoint(Vector3 position, Quaternion rotation)
    {
        respawnPosition = position;
        respawnRotation = rotation;
        HasCheckpoint = true;
    }

    // Back to the stage start (new run or no checkpoints).
    public void ClearRespawnPoint()
    {
        respawnPosition = startPosition;
        respawnRotation = startRotation;
        HasCheckpoint = false;
    }

    // 교대 중 정지: 그 자리에 멈추고(중력 포함), 풀리면 멈추기 직전 속도로 이어서 움직입니다.
    private bool frozen;
    private bool frozenWasKinematic;
    private Vector3 frozenVelocity;
    public bool Frozen
    {
        get => frozen;
        set
        {
            if (frozen == value || body == null) { frozen = value; return; }
            frozen = value;
            if (value)
            {
                frozenWasKinematic = body.isKinematic;
                frozenVelocity = body.isKinematic ? Vector3.zero : body.linearVelocity;
                if (!body.isKinematic)
                {
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
                body.isKinematic = true;
                jumpRequested = false;
            }
            else
            {
                body.isKinematic = frozenWasKinematic;
                if (!body.isKinematic) body.linearVelocity = frozenVelocity;
            }
        }
    }

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
        !InputLocked && !frozen && (RemoteActive || LocalActive);

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
        defaultUseGravity = body.useGravity;
        ClearRespawnPoint();

        // The stage controller runs first and may already have asked for a freeze
        // (a participant carried over from stage 1): apply it now that the body exists.
        if (frozen)
        {
            frozen = false;
            Frozen = true;
        }
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
        if (frozen) return;

        if (body.position.y < fallResetY)
        {
            ResetPosition();
            Fell?.Invoke();
            return;
        }

        staleContacts.Clear();
        foreach (Collider c in groundContacts.Keys)
            if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) staleContacts.Add(c);
        foreach (Collider c in staleContacts) groundContacts.Remove(c);

        Vector2 input = CanControl
            ? Vector2.ClampMagnitude(inputRouter.ReadMove(), 1f)
            : Vector2.zero;

        Vector3 direction = climbCamera.PlanarRotation *
            new Vector3(input.x, 0f, input.y);

        Vector3 velocity = body.linearVelocity;
        velocity.x = direction.x * moveSpeed;
        velocity.z = direction.z * moveSpeed;

        bool grounded = IsGrounded;
        Vector3 groundNormal = GroundNormal;
        bool onSlope = grounded && groundNormal.y < 0.999f;

        // Ride moving platforms; the platform's speed is kept through a jump off it.
        if (grounded) carryVelocity = GroundMoverVelocity();
        velocity.x += carryVelocity.x;
        velocity.z += carryVelocity.z;

        float gravityScale = Mathf.Max(1f, gravityMultiplier);

        if (CanControl && wantsJump && grounded)
        {
            onSlope = false;
            float gravity = Mathf.Abs(Physics.gravity.y) * gravityScale;
            velocity.y = Mathf.Sqrt(2f * gravity * jumpHeight);

            // 점프 직전의 접촉 정보로 다시 점프하지 않도록 제거합니다.
            groundContacts.Clear();
            ignoreGroundUntil = Time.fixedTime + 0.1f;
        }

        if (onSlope)
        {
            // Walk along the slope surface at full speed and stay put when idle
            // (no sliding down, no bouncing off the slope).
            Vector3 planar = new Vector3(velocity.x, 0f, velocity.z);
            Vector3 along = Vector3.ProjectOnPlane(planar, groundNormal);
            if (along.sqrMagnitude > 0.0001f) along = along.normalized * planar.magnitude;
            velocity = along - groundNormal * 0.5f;
        }
        body.useGravity = onSlope ? false : defaultUseGravity;
        body.linearVelocity = velocity;

        // 기본 중력에 추가 중력을 더합니다(경사면 위에서는 미끄러지지 않도록 생략).
        if (!onSlope)
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

        if (other.isTrigger)
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
                groundContacts[other] = contact.normal;
                return;
            }
        }
    }

    // Back to the start of the stage (also forgets any checkpoint).
    public void RespawnAtStart()
    {
        ClearRespawnPoint();
        if (body != null) ResetPosition();
    }

    private void ResetPosition()
    {
        if (!body.isKinematic)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        frozenVelocity = Vector3.zero;
        carryVelocity = Vector3.zero;
        body.position = respawnPosition;
        body.rotation = respawnRotation;

        groundContacts.Clear();
        jumpRequested = false;
        ignoreGroundUntil = Time.fixedTime + 0.1f;
    }

    private void OnDisable()
    {
        Frozen = false;
        InputLocked = false;
        jumpRequested = false;
        groundContacts.Clear();
        if (body != null) body.useGravity = defaultUseGravity;

        if (body != null && !body.isKinematic)
        {
            Vector3 velocity = body.linearVelocity;
            velocity.x = 0f;
            velocity.z = 0f;
            body.linearVelocity = velocity;
        }
    }
}