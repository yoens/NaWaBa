using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

public class ClimbCameraController : MonoBehaviour
{
    [SerializeField] private Transform target;
    [SerializeField] private InputRouter inputRouter;
    [SerializeField] private LayerMask obstacleMask;

    [SerializeField] private float targetHeight = 1f;
    [SerializeField] private float distance = 5f;
    [SerializeField] private float sensitivity = 0.15f;
    [SerializeField] private float pitch = 20f;

    // 원격 조작자가 Q/E로 카메라를 돌리는 속도(도/초)
    [SerializeField] private float remoteLookSpeed = 120f;
    // 원격 조작자가 R/F로 카메라를 위아래로 기울이는 속도(도/초)
    [SerializeField] private float remotePitchSpeed = 70f;

    private const float MinPitch = -20f, MaxPitch = 65f;
    private float defaultPitch;

    private float yaw;

    // 이동 방향에는 카메라의 수평 회전만 사용합니다.
    public Quaternion PlanarRotation =>
        Quaternion.Euler(0f, yaw, 0f);

    private void Awake()
    {
        if (target == null || inputRouter == null)
        {
            Debug.LogError(
                "ClimbCameraController: Target과 InputRouter를 연결하세요.",
                this);
            enabled = false;
            return;
        }

        yaw = target.eulerAngles.y;
        pitch = Mathf.Clamp(pitch, MinPitch, MaxPitch);
        defaultPitch = pitch;
    }

    private void Update()
    {
        if (!inputRouter.isActiveAndEnabled)
        {
            ReleaseCursor();
            return;
        }

        // 원격 조작 중: 호스트 마우스는 풀어 두고, 웹의 Q/E 입력으로만 회전합니다.
        // 호스트 창이 포커스를 잃어도 원격 회전은 계속 적용됩니다.
        if (inputRouter.IsRemoteControl)
        {
            ReleaseCursor();
            yaw = Mathf.Repeat(
                yaw + inputRouter.ReadLook() * remoteLookSpeed * Time.deltaTime,
                360f);
            // R = 위로 올려다보기(카메라가 내려감), F = 내려다보기
            pitch = Mathf.Clamp(
                pitch - inputRouter.ReadLookY() * remotePitchSpeed * Time.deltaTime,
                MinPitch, MaxPitch);
            return;
        }

        if (!Application.isFocused)
        {
            ReleaseCursor();
            return;
        }

        if (Keyboard.current != null &&
            Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            ReleaseCursor();
            return;
        }

        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        if (Cursor.lockState != CursorLockMode.Locked)
        {
            bool overUI = EventSystem.current != null &&
                          EventSystem.current.IsPointerOverGameObject();

        // UI 이외의 게임 화면을 클릭하면 조작을 시작합니다.
            if (mouse.leftButton.wasPressedThisFrame && !overUI)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            return;
        }

        Vector2 delta = mouse.delta.ReadValue();

        yaw = Mathf.Repeat(yaw + delta.x * sensitivity, 360f);
        pitch = Mathf.Clamp(
            pitch - delta.y * sensitivity, MinPitch, MaxPitch);
    }

    private void LateUpdate()
    {
        if (target == null) return;
        GetFollowPose(out Vector3 position, out Quaternion rotation);
        transform.SetPositionAndRotation(position, rotation);
    }

    // Behind the player, facing where the player faces, at the default tilt
    // (used at stage start and after the intro camera).
    public void ResetBehindTarget()
    {
        if (target == null) return;
        yaw = target.eulerAngles.y;
        pitch = defaultPitch;
    }

    // Where the follow camera wants to be this frame (the intro camera ends here).
    public void GetFollowPose(out Vector3 position, out Quaternion cameraRotation)
    {
        Vector3 pivot =
            target.position + Vector3.up * targetHeight;

        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 backward = rotation * Vector3.back;

        float actualDistance = distance;

        if (Physics.SphereCast(
            pivot,
            0.2f,
            backward,
            out RaycastHit hit,
            distance,
            obstacleMask,
            QueryTriggerInteraction.Ignore))
        {
            actualDistance = Mathf.Max(0.05f, hit.distance - 0.05f);
        }

        position = pivot + backward * actualDistance;
        cameraRotation = rotation;
    }
    private void ReleaseCursor()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
            ReleaseCursor();
    }

    private void OnDisable()
    {
        ReleaseCursor();
    }
}