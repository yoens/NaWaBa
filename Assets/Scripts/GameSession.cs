using UnityEngine;

public enum GameMode
{
    SinglePlayer,
    Multiplayer
}

public static class GameSession
{
    public static GameMode Mode { get; private set; }
        = GameMode.SinglePlayer;

    public static CharacterDefinition SelectedCharacter { get; private set; }

    public static void SelectMode(GameMode mode)
    {
        Mode = mode;
    }

    public static void SelectCharacter(CharacterDefinition character)
    {
        SelectedCharacter = character;
    }

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetSession()
    {
        Mode = GameMode.SinglePlayer;
        SelectedCharacter = null;
    }
}