using System;
using System.Collections.Generic;

namespace SamplePlugin.Models;

public class PVBJHand
{
    public string       Name   { get; set; } = string.Empty;
    public List<string> Cards  { get; set; } = new();
    public string       Desc   { get; set; } = string.Empty;
    public bool         IsBust { get; set; }
    public bool         IsBJ   { get; set; }
}

public class PVRouletteBet
{
    public string PlayerName { get; set; } = string.Empty;
    public string Target     { get; set; } = string.Empty;
}

public class PVPokerShowdown
{
    public string Name     { get; set; } = string.Empty;
    public string Card1    { get; set; } = string.Empty;
    public string Card2    { get; set; } = string.Empty;
    public string HandDesc { get; set; } = string.Empty;
}

public class PlayerViewState
{
    // ── Configuration ────────────────────────────────────────────────────────
    public string DealerName   { get; set; } = string.Empty;
    public string DetectedGame { get; set; } = string.Empty;

    // ── Blackjack ─────────────────────────────────────────────────────────────
    public bool           BJActive       { get; set; }
    public List<string>   BJDealerCards  { get; set; } = new();
    public bool           BJHoleRevealed { get; set; }
    public List<PVBJHand> BJPlayers      { get; set; } = new();

    // ── Craps ─────────────────────────────────────────────────────────────────
    public bool     CrapsDiceRolling { get; set; }
    public int      CrapsDie1        { get; set; } = 1;
    public int      CrapsDie2        { get; set; } = 1;
    public bool     CrapsHasResult   { get; set; }
    public DateTime CrapsRollStart   { get; set; }
    public int      CrapsPoint       { get; set; }
    public bool     CrapsPointSet    { get; set; }
    public string   CrapsShooter     { get; set; } = string.Empty;

    // ── Roulette ─────────────────────────────────────────────────────────────
    public bool                RouletteSpinning  { get; set; }
    public DateTime            RouletteSpinStart { get; set; }
    public int?                RouletteResult    { get; set; }
    public List<PVRouletteBet> RouletteBets      { get; set; } = new();

    // ── Poker ─────────────────────────────────────────────────────────────────
    public List<string>          PokerCommunity  { get; set; } = new();
    public string                PokerPhaseLabel { get; set; } = string.Empty;
    public int                   PokerPot        { get; set; }
    public string                MyHoleCard1     { get; set; } = string.Empty;
    public string                MyHoleCard2     { get; set; } = string.Empty;
    public bool                  MyHoleReceived  { get; set; }
    public List<PVPokerShowdown> PokerShowdown   { get; set; } = new();

    // ── Feed ─────────────────────────────────────────────────────────────────
    public List<string> Feed { get; } = new();

    // ── Player command UI state ───────────────────────────────────────────────
    public int    PVChatMode       { get; set; } = 0;   // 0=Say 1=Party
    public string PVBetAmount      { get; set; } = "100";
    public string PVRouletteTarget { get; set; } = "RED";
    public int    PVCrapsPlaceNum  { get; set; } = 0;   // index into {4,5,6,8,9,10}
    public int    PVPokerRaiseAmt  { get; set; } = 100;

    // ── Auto-switch ───────────────────────────────────────────────────────────
    /// <summary>Set when DetectedGame is first populated; PluginUI polls and resets it.</summary>
    public bool ShouldAutoSwitch { get; set; } = false;

    // ── Craps bet tracking ────────────────────────────────────────────────────
    /// <summary>
    /// Active bet sections parsed from dealer chat.
    /// Keys: PASS, DONTPASS, FIELD, BIG6, BIG8, PLACE4, PLACE5, PLACE6, PLACE8, PLACE9, PLACE10
    /// Values: number of distinct players with that bet (approximate).
    /// </summary>
    public Dictionary<string, int> CrapsBetTotals { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void AddFeed(string msg)
    {
        Feed.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
        while (Feed.Count > 30) Feed.RemoveAt(30);
    }

    /// <summary>Full reset (manual clear).</summary>
    public void ResetPoker()
    {
        PokerCommunity.Clear();
        MyHoleCard1 = MyHoleCard2 = string.Empty;
        MyHoleReceived = false;
        PokerShowdown.Clear();
        PokerPhaseLabel = string.Empty;
        PokerPot = 0;
    }

    /// <summary>
    /// Reset community/pot/showdown for a new hand WITHOUT clearing hole cards.
    /// Hole cards arrive via tell BEFORE the public "New hand!" message, so
    /// wiping them here would erase them immediately after they were received.
    /// </summary>
    public void ResetPokerForNewHand()
    {
        PokerCommunity.Clear();
        PokerShowdown.Clear();
        PokerPhaseLabel = string.Empty;
        PokerPot = 0;
    }
}
