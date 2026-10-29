using UnityEngine;

public class RemoteInputSource : MonoBehaviour
{
    [SerializeField, Min(0.05f)]
    private float inputTimeout = 0.5f;

    private Vector2 moveInput;
    private double lastReceivedTime = double.NegativeInfinity;

    // 나중에 웹에서 받은 입력을 Unity 메인 스레드에서 전달합니다.
    public void SetMoveInput(Vector2 input, double receivedAge = 0)
    {
        if (!isActiveAndEnabled)
            return;

        if (float.IsNaN(input.x) || float.IsNaN(input.y) ||
            float.IsInfinity(input.x) || float.IsInfinity(input.y) ||
            double.IsNaN(receivedAge) || double.IsInfinity(receivedAge) ||
            receivedAge < 0 || receivedAge >= inputTimeout)
        {
            ClearInput();
            return;
        }

        moveInput = Vector2.ClampMagnitude(input, 1f);

    // 수신 후 Unity에서 처리하기까지 걸린 시간도 만료 시간에 포함합니다.
        lastReceivedTime =
            Time.realtimeSinceStartupAsDouble - receivedAge;
    }

    public Vector2 ReadMove()
    {
        double elapsed =
            Time.realtimeSinceStartupAsDouble - lastReceivedTime;

        if (elapsed >= inputTimeout)
            ClearInput();

        return moveInput;
    }

    public void ClearInput()
    {
        moveInput = Vector2.zero;
        lastReceivedTime = double.NegativeInfinity;
    }

    private void OnEnable()
    {
        ClearInput();
    }

    private void OnDisable()
    {
        ClearInput();
    }

    // 웹 연결 전, 입력을 한 번 수신한 상황을 재현합니다.
    [ContextMenu("Test/Receive Right Input Once")]
    private void TestReceiveRightInput()
    {
        if (!Application.isPlaying)
            return;

        SetMoveInput(Vector2.right);
    }
}