using System;
using System.Collections.Generic;

namespace ChatCasino.Models;

public sealed class PlayerStats
{
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int NetGains { get; set; }
}

public sealed class Player
{
    public string Name { get; set; } = string.Empty;
    public string HomeWorld { get; set; } = string.Empty;
    public int CurrentBank { get; set; }
    public bool IsAfk { get; set; }
    public bool IsKicked { get; set; }

    public Dictionary<string, object> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<GameType, PlayerStats> SessionStats { get; } = new();

    public PlayerStats GetStats(GameType gameType)
    {
        if (!SessionStats.TryGetValue(gameType, out var stats))
        {
            stats = new PlayerStats();
            SessionStats[gameType] = stats;
        }

        return stats;
    }

    public void ResetMetadata() => Metadata.Clear();
}
