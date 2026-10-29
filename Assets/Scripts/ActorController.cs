using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class ActorController : MonoBehaviour
{
    [SerializeField] private InputRouter inputRouter;
    [SerializeField] private float moveSpeed = 4f;
    [SerializeField] private Vector2 moveLimit = new Vector2(6f, 6f);

    private Rigidbody body;

    private void Awake()
    {
        body = GetComponent<Rigidbody>();

        if (inputRouter == null)
        {
            Debug.LogError("ActorController에 InputRouter를 연결해주세요.", this);
            enabled = false;
        }
    }

    private void FixedUpdate()
    {
        if (!inputRouter.isActiveAndEnabled)
            return;

        Vector2 input = inputRouter.ReadMove();
        Vector3 direction = new Vector3(input.x, 0f, input.y);

        Vector3 nextPosition =
            body.position + direction * moveSpeed * Time.fixedDeltaTime;

        // 실험 중 상자가 화면 밖으로 나가지 않도록 제한합니다.
        nextPosition.x =
            Mathf.Clamp(nextPosition.x, -moveLimit.x, moveLimit.x);

        nextPosition.z =
            Mathf.Clamp(nextPosition.z, -moveLimit.y, moveLimit.y);

        body.MovePosition(nextPosition);
    }
}