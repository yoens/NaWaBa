using UnityEngine;

public class InputRouter : MonoBehaviour
{
    [SerializeField] private LocalInputSource localInputSource;
    [SerializeField] private RemoteInputSource remoteInputSource;

    private bool useRemoteInput;

    public bool IsRemoteControl => useRemoteInput;

    public Vector2 ReadMove()
    {
        if (useRemoteInput)
        {
            if (remoteInputSource == null ||
                !remoteInputSource.isActiveAndEnabled)
            {
                return Vector2.zero;
            }

            return remoteInputSource.ReadMove();
        }

        if (localInputSource == null ||
            !localInputSource.isActiveAndEnabled)
        {
            return Vector2.zero;
        }

        return localInputSource.ReadMove();
    }

    // Update에서 호출하세요. 현재 조작자가 이번 프레임에 점프를 눌렀으면 true입니다.
    public bool ConsumeJump()
    {
        if (useRemoteInput)
        {
            return remoteInputSource != null &&
                remoteInputSource.isActiveAndEnabled &&
                remoteInputSource.ConsumeJump();
        }

        return localInputSource != null &&
            localInputSource.isActiveAndEnabled &&
            localInputSource.ReadJumpPressed();
    }

    // 원격 조작자의 카메라 좌우 회전(-1~1). 로컬은 마우스를 사용하므로 0입니다.
    public float ReadLook()
    {
        if (!useRemoteInput || remoteInputSource == null ||
            !remoteInputSource.isActiveAndEnabled)
            return 0f;

        return remoteInputSource.ReadLook();
    }

    // 원격 조작자의 카메라 위아래 기울기(-1~1, + = 위). 로컬은 마우스를 사용하므로 0입니다.
    public float ReadLookY()
    {
        if (!useRemoteInput || remoteInputSource == null ||
            !remoteInputSource.isActiveAndEnabled)
            return 0f;

        return remoteInputSource.ReadLookY();
    }

    [ContextMenu("Control/Use Remote Input")]
    public void UseRemoteInput()
    {
        if (!Application.isPlaying)
            return;

        if (remoteInputSource == null ||
            !remoteInputSource.isActiveAndEnabled)
        {
            Debug.LogError("활성화된 RemoteInputSource가 필요합니다.", this);
            return;
        }

        // 이전에 저장된 입력으로 갑자기 움직이지 않도록 초기화합니다.
        remoteInputSource.ClearInput();
        useRemoteInput = true;

        Debug.Log("입력 모드: Remote", this);
    }

    [ContextMenu("Control/Use Local Input")]
    public void UseLocalInput()
    {
        useRemoteInput = false;

        if (remoteInputSource != null)
            remoteInputSource.ClearInput();

        Debug.Log("입력 모드: Local", this);
    }

    private void OnDisable()
    {
        useRemoteInput = false;

        if (remoteInputSource != null)
            remoteInputSource.ClearInput();
    }
}