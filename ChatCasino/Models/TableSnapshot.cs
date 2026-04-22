using System;
using System.Collections.Generic;

namespace ChatCasino.Models;

public sealed class TableSnapshot
{
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    public GameType ActiveGameType { get; set; }
    public GameState State { get; set; }
    public List<Player> Players { get; set; } = new();
}
