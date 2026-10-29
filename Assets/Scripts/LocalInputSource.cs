using UnityEngine;
using UnityEngine.InputSystem;

public class LocalInputSource : MonoBehaviour
{
    public Vector2 ReadMove()
    {
        // 게임이 입력 초점을 잃으면 로컬 입력을 중립으로 처리합니다.
        if (!Application.isFocused)
            return Vector2.zero;

        Keyboard keyboard = Keyboard.current;

        if (keyboard == null)
            return Vector2.zero;

        float x = 0f;
        float y = 0f;

        if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
            x -= 1f;

        if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
            x += 1f;

        if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
            y -= 1f;

        if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
            y += 1f;

        // 대각선 입력의 길이가 1을 넘지 않도록 제한합니다.
        return Vector2.ClampMagnitude(new Vector2(x, y), 1f);
    }
}