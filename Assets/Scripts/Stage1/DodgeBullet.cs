using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class DodgeBullet : MonoBehaviour
{
    [SerializeField, Min(0.1f)] private float moveSpeed = 4f;
    [SerializeField, Min(0.1f)] private float lifeTime = 6f;

    // Set by the stage while control is being handed over: bullets stay where they are.
    public static bool Frozen { get; set; }

    private Rigidbody body;
    private float age;

    private void Awake()
    {
        body = GetComponent<Rigidbody>();
    }

    // Faster or slower snowballs for the chosen difficulty. Slow ones live long enough to
    // leave the screen (the arena is about 30 m long).
    public void SetSpeed(float speed)
    {
        moveSpeed = Mathf.Max(0.1f, speed);
        lifeTime = Mathf.Max(lifeTime, 34f / moveSpeed);
    }

    private void FixedUpdate()
    {
        if (Frozen) return;

        // 경기장을 벗어난 총알이 계속 남지 않도록 제거합니다. 정지 중에는 나이를 세지 않습니다.
        age += Time.fixedDeltaTime;
        if (age >= lifeTime)
        {
            Destroy(gameObject);
            return;
        }

        // 월드 Z축의 음수 방향: 현재 카메라에서는 화면 아래쪽입니다.
        Vector3 nextPosition =
            body.position
            + Vector3.back * moveSpeed * Time.fixedDeltaTime;

        body.MovePosition(nextPosition);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => Frozen = false;
}
