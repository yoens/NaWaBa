using TMPro;
using UnityEngine;

public class LobbyCharacterSelector : MonoBehaviour
{
    [SerializeField] private CharacterDefinition[] characters;
    [SerializeField] private Transform previewRoot;
    [SerializeField] private TMP_Text characterNameText;

    private GameObject currentPreview;
    private int selectedIndex;
    private bool initialized;

    private void Start()
    {
        if (previewRoot == null ||
            characterNameText == null ||
            characters == null ||
            characters.Length == 0)
        {
            Debug.LogError(
                "LobbyCharacterSelector의 Inspector 연결을 확인해주세요.",
                this);

            enabled = false;
            return;
        }

        for (int i = 0; i < characters.Length; i++)
        {
            if (characters[i] == null ||
                characters[i].VisualPrefab == null)
            {
                Debug.LogError(
                    $"Characters의 {i}번 데이터 또는 외형 프리팹이 없습니다.",
                    this);

                enabled = false;
                return;
            }
        }

        // 이전에 선택한 캐릭터가 있으면 해당 위치를 찾습니다.
        selectedIndex = 0;

        for (int i = 0; i < characters.Length; i++)
        {
            if (characters[i] == GameSession.SelectedCharacter)
            {
                selectedIndex = i;
                break;
            }
        }

        characterNameText.richText = false;
        initialized = true;

        ShowSelectedCharacter();
    }

    public void SelectPrevious()
    {
        if (!initialized || !isActiveAndEnabled)
            return;

        selectedIndex =
            (selectedIndex - 1 + characters.Length) % characters.Length;

        ShowSelectedCharacter();
    }

    public void SelectNext()
    {
        if (!initialized || !isActiveAndEnabled)
            return;

        selectedIndex =
            (selectedIndex + 1) % characters.Length;

        ShowSelectedCharacter();
    }

    private void ShowSelectedCharacter()
    {
        CharacterDefinition character = characters[selectedIndex];

        if (currentPreview != null)
        {
            currentPreview.SetActive(false);
            Destroy(currentPreview);
        }

        currentPreview = Instantiate(
            character.VisualPrefab,
            previewRoot,
            false);

        currentPreview.transform.localPosition = Vector3.zero;
        currentPreview.transform.localRotation = Quaternion.identity;

        characterNameText.text = character.DisplayName;
        GameSession.SelectCharacter(character);
    }
}