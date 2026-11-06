using UnityEngine;
using UnityEngine.Rendering;

public class PlayerCharacterVisual : MonoBehaviour
{
    [SerializeField] private CharacterDefinition defaultCharacter;
    [SerializeField] private MeshRenderer originalRenderer;

    private void Awake()
    {
        CharacterDefinition character = GameSession.SelectedCharacter;
        if (character == null) character = defaultCharacter;

        if (character == null || character.VisualPrefab == null)
        {
            Debug.LogError("PlayerCharacterVisual: assign a default character and visual prefab.", this);
            enabled = false;
            return;
        }

        if (originalRenderer == null)
            originalRenderer = GetComponent<MeshRenderer>();

        GameObject visual = Instantiate(character.VisualPrefab, transform, false);
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;

        foreach (Renderer renderer in visual.GetComponentsInChildren<Renderer>(true))
        {
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        // Hide the old cube's appearance only; preserve its collider and movement.
        if (originalRenderer != null) originalRenderer.enabled = false;
        GameSession.SelectCharacter(character);
    }
}
