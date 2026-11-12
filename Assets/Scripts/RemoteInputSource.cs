using UnityEngine;

public class RemoteInputSource : MonoBehaviour
{
    [SerializeField, Min(0.05f)]
    private float inputTimeout = 0.5f;

    private Vector2 moveInput;
    private float lookInput;
    private double lastReceivedTime = double.NegativeInfinity;

    // The web sends a cumulative jump press count. A higher count means new presses.
    // -1 means "the next received count becomes the baseline" (no jump).
    private long lastJumpCount = -1;
    private bool jumpPending;
    private double jumpPendingAt = double.NegativeInfinity;

    // 웹에서 받은 입력을 Unity 메인 스레드에서 전달합니다.
    public void SetMoveInput(Vector2 input, double receivedAge = 0)
    {
        SetInput(input, 0f, lastJumpCount < 0 ? 0 : lastJumpCount, receivedAge);
    }

    public void SetInput(Vector2 move, float look, long jumpCount, double receivedAge = 0)
    {
        if (!isActiveAndEnabled)
            return;

        if (float.IsNaN(move.x) || float.IsNaN(move.y) ||
            float.IsInfinity(move.x) || float.IsInfinity(move.y) ||
            float.IsNaN(look) || float.IsInfinity(look) || jumpCount < 0 ||
            double.IsNaN(receivedAge) || double.IsInfinity(receivedAge) ||
            receivedAge < 0 || receivedAge >= inputTimeout)
        {
            ClearInput();
            return;
        }

        moveInput = Vector2.ClampMagnitude(move, 1f);
        lookInput = Mathf.Clamp(look, -1f, 1f);

        // 수신 후 Unity에서 처리하기까지 걸린 시간도 만료 시간에 포함합니다.
        lastReceivedTime =
            Time.realtimeSinceStartupAsDouble - receivedAge;

        if (lastJumpCount >= 0 && jumpCount > lastJumpCount)
        {
            jumpPending = true;
            jumpPendingAt = lastReceivedTime;
        }
        lastJumpCount = jumpCount;
    }

    public Vector2 ReadMove()
    {
        ExpireIfStale();
        return moveInput;
    }

    public float ReadLook()
    {
        ExpireIfStale();
        return lookInput;
    }

    // Returns true once per new remote jump press.
    public bool ConsumeJump()
    {
        if (!jumpPending) return false;
        jumpPending = false;
        return Time.realtimeSinceStartupAsDouble - jumpPendingAt < inputTimeout;
    }

    public void ClearInput()
    {
        moveInput = Vector2.zero;
        lookInput = 0f;
        lastReceivedTime = double.NegativeInfinity;
        lastJumpCount = -1;
        jumpPending = false;
        jumpPendingAt = double.NegativeInfinity;
    }

    private void ExpireIfStale()
    {
        double elapsed =
            Time.realtimeSinceStartupAsDouble - lastReceivedTime;

        if (elapsed >= inputTimeout)
        {
            moveInput = Vector2.zero;
            lookInput = 0f;
            jumpPending = false;
        }
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
