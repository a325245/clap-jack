using System;

namespace SamplePlugin.Models;

public class PokerSeat
{
    public string PlayerName { get; set; } = string.Empty;
    public Card   HoleCard1  { get; set; }
    public Card   HoleCard2  { get; set; }
    public int    Bet        { get; set; }      // chips committed this street
    public int    TotalBet   { get; set; }      // total chips committed this hand
    public PokerPlayerStatus Status { get; set; } = PokerPlayerStatus.Empty;
    public bool   HasActed   { get; set; }

    public bool IsOccupied => Status != PokerPlayerStatus.Empty;
    public bool IsActive   => Status == PokerPlayerStatus.Active;
    public bool IsFolded   => Status == PokerPlayerStatus.Folded;
    public bool IsAllIn    => Status == PokerPlayerStatus.AllIn;

    public void Clear()
    {
        PlayerName = string.Empty;
        HoleCard1  = default;
        HoleCard2  = default;
        Bet        = 0;
        TotalBet   = 0;
        HasActed   = false;
        Status     = PokerPlayerStatus.Empty;
    }

    public void PrepareForHand()
    {
        HoleCard1 = default;
        HoleCard2 = default;
        Bet       = 0;
        TotalBet  = 0;
        HasActed  = false;
        Status    = PokerPlayerStatus.Active;
    }
}
