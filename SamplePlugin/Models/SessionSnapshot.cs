using System;
using System.Collections.Generic;

namespace SamplePlugin.Models;

public class PlayerSnapshot
{
    public string Name   { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public int    Bank   { get; set; }
    public int    Bet    { get; set; }
}

public class SessionSnapshot
{
    public DateTime           SavedAt { get; set; } = DateTime.Now;
    public List<PlayerSnapshot> Players { get; set; } = new();
}
