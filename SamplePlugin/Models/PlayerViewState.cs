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

public class PVPokerPlayer
{
    public string Name   { get; set; } = string.Empty;
    public int    Bank   { get; set; }
    public string Status { get; set; } = string.Empty; // "Active","Folded","AllIn","AFK"
    public int    Bet    { get; set; }
}

public class PVChocoboRacer
{
    public int    Number { get; set; }
    public string Name   { get; set; } = string.Empty;
    public string Odds   { get; set; } = string.Empty;
}

public class PlayerViewState
{
    // ── Configuration ────────────────────────────────────────────────────────
    public string DealerName   { get; set; } = string.Empty;
    public string DetectedGame { get; set; } = string.Empty;

    /// <summary>Bank values parsed from chat messages. Key = player name (case-preserved).</summary>
    public Dictionary<string, int> PlayerBanks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ── Blackjack ─────────────────────────────────────────────────────────────
    public bool           BJActive       { get; set; }
    public List<string>   BJDealerCards  { get; set; } = new();
    public bool           BJHoleRevealed { get; set; }
    public List<PVBJHand> BJPlayers      { get; set; } = new();
    public string         BJCurrentPlayer { get; set; } = string.Empty;
    public HashSet<string> BJAvailableCmds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
    public List<PVPokerPlayer>   PokerPlayers    { get; set; } = new();
    public string                PokerActionTo   { get; set; } = string.Empty;
    public HashSet<string>       PokerAvailCmds  { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ── Feed ─────────────────────────────────────────────────────────────────
    public List<string> Feed { get; } = new();

    // ── Player command UI state ───────────────────────────────────────────────
    public int    PVChatMode       { get; set; } = 0;   // 0=Say 1=Party
    public string PVBetAmount      { get; set; } = "100";
    public string PVRouletteTarget { get; set; } = "RED";
    public int    PVCrapsPlaceNum  { get; set; } = 0;   // index into {4,5,6,8,9,10}
    public int    PVCrapsBetType   { get; set; } = 0;   // index into bet type dropdown
    public int    PVPokerRaiseAmt  { get; set; } = 100;

    // ── Baccarat player view ──────────────────────────────────────────────────
    public int    PVBaccaratBetType { get; set; } = 0;  // 0=Player, 1=Banker, 2=Tie
    public string BacPlayerCards   { get; set; } = string.Empty;
    public string BacBankerCards   { get; set; } = string.Empty;
    public int    BacPlayerScore   { get; set; }
    public int    BacBankerScore   { get; set; }
    public string BacResult        { get; set; } = string.Empty;
    public bool   BacActive        { get; set; }

    // ── Chocobo player view ───────────────────────────────────────────────────
    public List<PVChocoboRacer>  ChocoboRacers      { get; set; } = new();
    public int                   PVChocoboRacerPick  { get; set; } = 0;
    public bool                  ChocoboBettingOpen  { get; set; }
    public string                ChocoboRaceHash     { get; set; } = string.Empty;
    public bool                  ChocoboRacing       { get; set; }
    public DateTime              ChocoboRaceStart    { get; set; }

    // ── Auto-switch ───────────────────────────────────────────────────────────
    /// <summary>Set when DetectedGame is first populated; PluginUI polls and resets it.</summary>
    public bool ShouldAutoSwitch { get; set; } = false;

    // ── Craps bet tracking ────────────────────────────────────────────────────
    /// <summary>
    /// Keys: PASS, DONTPASS, FIELD, BIG6, BIG8, PLACE4..PLACE10.
    /// Values: names of players observed placing that bet.
    /// </summary>
    public Dictionary<string, List<string>> CrapsBets { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // __ Ultima! _______________________________________________________________
    public List<UltimaCard>        UltimaHand          { get; set; } = new();
    public UltimaCard?             UltimaTopCard        { get; set; }
    public UltimaColor             UltimaActiveColor    { get; set; } = UltimaColor.Wild;
    public string                  UltimaCurrentPlayer  { get; set; } = string.Empty;
    public string                  UltimaWinner         { get; set; } = string.Empty;
    public bool                    UltimaClockwise      { get; set; } = true;
    public bool                    UltimaSortByColor    { get; set; } = true;
    public int?                    UltimaSelectedIdx    { get; set; }
    public int                     UltimaColorPickIdx   { get; set; } = 0;
    public Dictionary<string, int> UltimaCardCounts     { get; set; } = new();
    public List<string>            UltimaPlayerOrder    { get; set; } = new();

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
        PokerPlayers.Clear();
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
