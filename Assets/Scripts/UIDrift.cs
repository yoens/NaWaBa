using UnityEngine;

public class UIDrift : MonoBehaviour
{
    [SerializeField] private float speed = 20f;   // 초당 이동 픽셀
    [SerializeField] private float wrapWidth = 2200f; // 이 거리만큼 가면 반대편으로

    private RectTransform rect;
    private float startX;

    private void Awake()
    {
        rect = (RectTransform)transform;
        startX = rect.anchoredPosition.x;
    }

    private void Update()
    {
        Vector2 pos = rect.anchoredPosition;
        pos.x += speed * Time.unscaledDeltaTime; // 일시정지 중에도 메뉴는 움직이게
        if (pos.x > startX + wrapWidth) pos.x -= wrapWidth * 2f;
        rect.anchoredPosition = pos;
    }
}