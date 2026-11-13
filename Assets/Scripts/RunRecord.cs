using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// One run (한 판) = stage 1 → stage 2 → all clear. Lives across scene loads and is reset
// whenever stage 1 is entered again (new game). Players are identified by the server's
// per-connection ID, never by nickname; the host is "host".
public static class RunRecord
{
    public const string HostId = "host";
    public const string HostName = "호스트";

    public sealed class Player
    {
        public string Id;
        public string Name;
        public float ControlSeconds;
        public int Turns;      // times this player took control
        public int Fails;      // stage 1 hits
        public int Falls;      // stage 2 falls off the map
        public readonly List<string> Clears = new List<string>();
    }

    private static readonly List<Player> players = new List<Player>();
    private static readonly List<string> stageLines = new List<string>();
    private static string lastControllerId = "";
    private static float startedAt;
    private static float finishedAt = -1f;

    public static bool IsFinished => finishedAt >= 0f;
    public static IReadOnlyList<Player> Players => players;

    public static void BeginRun()
    {
        players.Clear();
        stageLines.Clear();
        lastControllerId = "";
        startedAt = Time.unscaledTime;
        finishedAt = -1f;
    }

    // Who has the controls right now.
    public static Player Current(RemoteInputGateway gateway)
    {
        bool remote = gateway != null && gateway.IsControlling && !string.IsNullOrEmpty(gateway.ControllerId);
        string id = remote ? gateway.ControllerId : HostId;
        string name = remote ? gateway.PlayerName : HostName;
        Player player = players.Find(p => p.Id == id);
        if (player == null)
        {
            player = new Player { Id = id, Name = name };
            players.Add(player);
        }
        else if (!string.IsNullOrEmpty(name)) player.Name = name;
        return player;
    }

    // Call every frame the game actually runs (not while paused for a handoff).
    public static void AddControlTime(RemoteInputGateway gateway, float deltaTime)
    {
        if (IsFinished) return;
        Player player = Current(gateway);
        if (player.Id != lastControllerId)
        {
            player.Turns++;
            lastControllerId = player.Id;
        }
        player.ControlSeconds += deltaTime;
    }

    public static Player NoteFail(RemoteInputGateway gateway)
    {
        Player player = Current(gateway);
        player.Fails++;
        return player;
    }

    public static Player NoteFall(RemoteInputGateway gateway)
    {
        Player player = Current(gateway);
        player.Falls++;
        return player;
    }

    public static Player NoteClear(RemoteInputGateway gateway, string stageName)
    {
        Player player = Current(gateway);
        player.Clears.Add(stageName);
        stageLines.Add($"{stageName} 클리어: {player.Name}");
        return player;
    }

    public static void Finish()
    {
        if (!IsFinished) finishedAt = Time.unscaledTime;
    }

    public static int TotalHandoffs => Mathf.Max(0, players.Sum(p => p.Turns) - 1);

    public static string Duration
    {
        get
        {
            float end = IsFinished ? finishedAt : Time.unscaledTime;
            int seconds = Mathf.Max(0, Mathf.RoundToInt(end - startedAt));
            return $"{seconds / 60}:{seconds % 60:00}";
        }
    }

    // Short lines for Unity and the web result panel (each well under 100 characters).
    public static string[] SummaryLines()
    {
        var lines = new List<string>(stageLines)
        {
            $"총 {Duration} · 교대 {TotalHandoffs}회"
        };
        foreach (Player p in players.OrderByDescending(p => p.ControlSeconds).Take(8))
        {
            string name = p.Name.Length > 20 ? p.Name.Substring(0, 20) : p.Name;
            lines.Add($"{name} — 조작 {Mathf.RoundToInt(p.ControlSeconds)}초 · 넘겨받음 {p.Turns}회 · 피격 {p.Fails} · 추락 {p.Falls}");
        }
        return lines.ToArray();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        players.Clear();
        stageLines.Clear();
        lastControllerId = "";
        startedAt = 0f;
        finishedAt = -1f;
    }
}
