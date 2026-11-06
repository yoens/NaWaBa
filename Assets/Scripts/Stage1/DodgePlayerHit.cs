using UnityEngine;

public class DodgePlayerHit : MonoBehaviour
{
    public bool IsHit { get; private set; }

    private void OnTriggerEnter(Collider other)
    {
        // 이미 피격됐다면 중복 처리하지 않습니다.
        if (IsHit)
            return;

        // DodgeBullet이 붙은 오브젝트만 총알로 취급합니다.
        if (!other.TryGetComponent<DodgeBullet>(out var bullet))
            return;

        IsHit = true;

        Destroy(bullet.gameObject);

        Debug.Log("[Dodge] 플레이어 피격!", this);
    }
    public void ResetHitState()
    {
        IsHit = false;
    }
}