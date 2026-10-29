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