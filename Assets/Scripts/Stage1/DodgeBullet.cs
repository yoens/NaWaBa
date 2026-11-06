using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class DodgeBullet : MonoBehaviour
{
    [SerializeField, Min(0.1f)] private float moveSpeed = 4f;
    [SerializeField, Min(0.1f)] private float lifeTime = 6f;

    private Rigidbody body;

    private void Awake()
    {
        body = GetComponent<Rigidbody>();
    }

    private void Start()
    {
        // 경기장을 벗어난 총알이 계속 남지 않도록 제거합니다.
        Destroy(gameObject, lifeTime);
    }

    private void FixedUpdate()
    {
        // 월드 Z축의 음수 방향: 현재 카메라에서는 화면 아래쪽입니다.
        Vector3 nextPosition =
            body.position
            + Vector3.back * moveSpeed * Time.fixedDeltaTime;

        body.MovePosition(nextPosition);
    }
}