using UnityEngine;

[CreateAssetMenu(
    fileName = "Character_",
    menuName = "NaWaBa/Character Definition")]
public class CharacterDefinition : ScriptableObject
{
    [SerializeField] private string characterId;
    [SerializeField] private string displayName;
    [SerializeField] private GameObject visualPrefab;

    public string CharacterId => characterId;
    public string DisplayName => displayName;
    public GameObject VisualPrefab => visualPrefab;
}