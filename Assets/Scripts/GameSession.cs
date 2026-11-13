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

    // Chosen in the lobby; read by each stage when it loads, so it never changes mid-run.
    private static NaWaBaDifficulty difficulty;
    public static NaWaBaDifficulty Difficulty
    {
        get
        {
            if (difficulty == null)
                difficulty = Resources.Load<NaWaBaDifficulty>(
                    NaWaBaDifficulty.ResourceFolder + "/" + NaWaBaDifficulty.DefaultName);
            return difficulty;
        }
    }

    public static void SelectDifficulty(NaWaBaDifficulty value)
    {
        if (value != null) difficulty = value;
    }

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetSession()
    {
        Mode = GameMode.SinglePlayer;
        SelectedCharacter = null;
        difficulty = null;
    }
}