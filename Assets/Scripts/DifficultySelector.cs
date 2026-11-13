using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Lobby button that cycles the difficulty (EASY → NORMAL → HARD → ...).
// Put it on any Button in the Lobby scene; no other wiring is needed.
[RequireComponent(typeof(Button))]
public class DifficultySelector : MonoBehaviour
{
    // Leave empty to use every asset in Resources/Difficulty, easiest first.
    [SerializeField] private NaWaBaDifficulty[] presets;
    [SerializeField] private TMP_Text label;

    private Button button;

    private void Awake()
    {
        button = GetComponent<Button>();
        if (label == null) label = GetComponentInChildren<TMP_Text>();
        if (presets == null || presets.Length == 0)
            presets = Resources.LoadAll<NaWaBaDifficulty>(NaWaBaDifficulty.ResourceFolder)
                .OrderBy(d => d.Order).ThenBy(d => d.SnowballSpeed).ToArray();
        if (presets.Length == 0)
        {
            Debug.LogError("DifficultySelector: no NaWaBaDifficulty assets in Resources/Difficulty.", this);
            enabled = false;
        }
    }

    private void OnEnable()
    {
        if (button != null) button.onClick.AddListener(Next);
        Refresh();
    }

    private void OnDisable()
    {
        if (button != null) button.onClick.RemoveListener(Next);
    }

    private void Next()
    {
        int index = System.Array.IndexOf(presets, GameSession.Difficulty);
        GameSession.SelectDifficulty(presets[(index + 1) % presets.Length]);
        Refresh();
    }

    private void Refresh()
    {
        if (!enabled || presets == null || presets.Length == 0) return;
        if (System.Array.IndexOf(presets, GameSession.Difficulty) < 0)
            GameSession.SelectDifficulty(presets[0]);
        if (label != null) label.text = "DIFFICULTY: " + GameSession.Difficulty.DisplayName;
    }
}
