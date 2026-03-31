using System;
using System.Collections.Generic;

namespace SamplePlugin.Models;

public class PlayerSnapshot
{
    public string Name   { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public int    Bank   { get; set; }
    public int    Bet    { get; set; }

    // Per-game net gains / stats
    public int RouletteNetGains { get; set; }
    public int CrapsNetGains    { get; set; }
    public int BaccaratNetGains { get; set; }
    public int ChocoboNetGains  { get; set; }
    public int PokerNetGains    { get; set; }
    public int UltimaWins       { get; set; }
    public int UltimaLosses     { get; set; }
    public int GamesPlayed      { get; set; }
    public int GamesWon         { get; set; }
}

public class SessionSnapshot
{
    public DateTime             SavedAt  { get; set; } = DateTime.Now;
    public string               GameType { get; set; } = string.Empty;
    public List<PlayerSnapshot> Players  { get; set; } = new();
}
