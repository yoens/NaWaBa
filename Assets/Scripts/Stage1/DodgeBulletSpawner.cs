using UnityEngine;

public class DodgeBulletSpawner : MonoBehaviour
{
    [SerializeField] private DodgeBullet bulletPrefab;
    [SerializeField, Min(0.1f)] private float spawnInterval = 1f;
    [SerializeField, Min(0f)] private float spawnHalfWidth = 9f;

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

        Instantiate(
            bulletPrefab,
            spawnPosition,
            Quaternion.identity);
    }
    public void ResetSpawnTimer()
    {
        elapsed = 0f;
    }
}