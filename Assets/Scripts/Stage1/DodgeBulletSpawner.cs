using UnityEngine;

public class DodgeBulletSpawner : MonoBehaviour
{
    [SerializeField] private DodgeBullet bulletPrefab;
    [SerializeField, Min(0.1f)] private float spawnInterval = 1f;
    [SerializeField, Min(0f)] private float spawnHalfWidth = 9f;
    // 0 = use the speed saved on the bullet prefab. Set from the difficulty at stage start.
    [SerializeField, Min(0f)] private float bulletSpeed = 0f;

    private float elapsed;

    private void Start()
    {
        if (bulletPrefab == null)
        {
            Debug.LogError(
                "DodgeBulletSpawner에 총알 프리팹을 연결해주세요.",
                this);

            enabled = false;
        }
    }

    private void Update()
    {
        elapsed += Time.deltaTime;

        if (elapsed < spawnInterval)
            return;

        // 프레임이 지연돼도 밀린 총알을 한꺼번에 생성하지 않습니다.
        elapsed = 0f;

        SpawnBullet();
    }

    private void SpawnBullet()
    {
        Vector3 spawnPosition = transform.position;

        // 생성기 위치를 중심으로 좌우 위치를 무작위로 선택합니다.
        spawnPosition.x += Random.Range(
            -spawnHalfWidth,
            spawnHalfWidth);

        DodgeBullet bullet = Instantiate(
            bulletPrefab,
            spawnPosition,
            Quaternion.identity);
        if (bulletSpeed > 0f) bullet.SetSpeed(bulletSpeed);
    }

    // Difficulty settings for this run (interval in seconds, half width in metres, speed in m/s).
    public void ApplySettings(float interval, float halfWidth, float speed)
    {
        spawnInterval = Mathf.Max(0.1f, interval);
        spawnHalfWidth = Mathf.Max(0f, halfWidth);
        bulletSpeed = Mathf.Max(0f, speed);
    }
    public void ResetSpawnTimer()
    {
        elapsed = 0f;
    }
}