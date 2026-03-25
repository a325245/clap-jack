using System.Collections.Generic;

namespace SamplePlugin.Models;

public class CrapsPlayerBets
{
    public int PassLineBet { get; set; } = 0;
    public int DontPassBet { get; set; } = 0;
    public int FieldBet    { get; set; } = 0;  // one-roll, reset each resolution
    public int Big6Bet     { get; set; } = 0;  // wins if 6 before 7 in point phase
    public int Big8Bet     { get; set; } = 0;  // wins if 8 before 7 in point phase
    public Dictionary<int, int> PlaceBets { get; set; } = new(); // number → amount
}
