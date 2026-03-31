using System;
using System.Collections.Generic;
using System.Linq;

namespace SamplePlugin.Models;

public class Table
{
    public Dictionary<string, Player> Players { get; set; } = new();
    public List<string> TurnOrder { get; set; } = new();
    public List<Card> Deck { get; set; } = new();
    public List<Card> DealerHand { get; set; } = new();
    public List<string> GameLog { get; set; } = new();

    public int CurrentTurnIndex { get; set; } = 0;
    public int TurnTimeRemaining { get; set; } = 0;
    public DateTime TurnStartTime { get; set; } = DateTime.Now;
    public bool TimerWarningShown { get; set; } = false;
    public bool TimerTimeoutShown { get; set; } = false;  // prevents manual-mode timeout spam
    public int MinBet { get; set; } = 10;
    public int MaxBet { get; set; } = 1000;
    public int TurnTimeLimit { get; set; } = 60;
    public int MaxSplitsAllowed { get; set; } = 2;        // 0 = no splits, up to 4
    public bool PersistentDeck { get; set; } = false;
    public List<Card> DealtCards { get; set; } = new();   // cards dealt from persistent deck

    public GameState GameState { get; set; } = GameState.Lobby;
    public GameType GameType { get; set; } = GameType.None;

    // Configurable message delay (milliseconds, shared by all engines)
    public int MessageDelayMs { get; set; } = 3000;

    // Announce when players are added to the table
    public bool AnnounceNewPlayers { get; set; } = true;

    // Use only the player's first name in dealer announcements
    public bool UseFirstNameOnly { get; set; } = true;

    public string GetDisplayName(string fullName)
    {
        if (!UseFirstNameOnly || string.IsNullOrEmpty(fullName)) return fullName;
        int space = fullName.IndexOf(' ');
        return space > 0 ? fullName[..space] : fullName;
    }

    // Roulette
    public int? RouletteResult { get; set; } = null;
    public RouletteSpinState RouletteSpinState { get; set; } = RouletteSpinState.Idle;
    public DateTime RouletteSpinStart { get; set; }

    // Craps
    public CrapsPhase CrapsPhase { get; set; } = CrapsPhase.WaitingForBets;
    public int CrapsPoint { get; set; } = 0;
    public int CrapsDie1 { get; set; } = 1;
    public int CrapsDie2 { get; set; } = 1;
    public bool CrapsRolling { get; set; } = false;
    public DateTime CrapsRollStart { get; set; }
    public Dictionary<string, CrapsPlayerBets> CrapsBets { get; set; } = new();
    public string   CrapsShooterName  { get; set; } = string.Empty;
    public bool     CrapsBettingPhase { get; set; } = false;
    public DateTime CrapsBettingStart { get; set; }

    // Baccarat
    public BaccaratPhase BaccaratPhase { get; set; } = BaccaratPhase.WaitingForBets;
    public List<Card> BaccaratPlayerHand { get; set; } = new();
    public List<Card> BaccaratBankerHand { get; set; } = new();
    public Dictionary<string, BaccaratBet> BaccaratBets { get; set; } = new();

    // Chocobo Racing
    public ChocoboRacePhase ChocoboRacePhase { get; set; } = ChocoboRacePhase.Idle;
    public DateTime         ChocoboRaceStart { get; set; }
    public Dictionary<string, ChocoboBet> ChocoboBets { get; set; } = new();
    public int ChocoboMinBet { get; set; } = 10;
    public int ChocoboMaxBet { get; set; } = 10000;

    // Texas Hold'Em
    public PokerPhase   PokerPhase         { get; set; } = PokerPhase.WaitingForPlayers;
    public int          PokerDealerSeat    { get; set; } = -1;
    public int          PokerCurrentSeat   { get; set; } = -1;
    public int          PokerPot           { get; set; } = 0;
    public int          PokerStreetBet     { get; set; } = 0;
    public int          PokerLastAggressor { get; set; } = -1;
    public int          PokerSmallBlind    { get; set; } = 50;
    public int          PokerAnte          { get; set; } = 50;
    public List<Card>   PokerCommunity     { get; set; } = new();
    public DateTime     PokerTurnStart     { get; set; }

    // Dealer rules
    public DealerRules DealerRules { get; set; } = DealerRules.HitsOnSoft17;
    public bool DealerHasBlackjack { get; set; } = false;
    public Card DealerHoleCard { get; set; } = default;
    public bool HoleCardRevealed { get; set; } = false;

    // Ultima!
    public UltimaPhase UltimaPhase       { get; set; } = UltimaPhase.WaitingForPlayers;
    public List<UltimaCard>                     UltimaDrawPile    { get; set; } = new();
    public List<UltimaCard>                     UltimaDiscardPile { get; set; } = new();
    public Dictionary<string, List<UltimaCard>> UltimaHands       { get; set; } = new();
    public List<string>                         UltimaPlayerOrder { get; set; } = new();
    public int                                  UltimaCurrentIndex{ get; set; } = 0;
    public bool                                 UltimaClockwise   { get; set; } = true;
    public UltimaColor                          UltimaActiveColor { get; set; } = UltimaColor.Wild;
    public UltimaCard?                          UltimaTopCard     { get; set; }
    public HashSet<string>                      UltimaCalled      { get; set; } = new();
    public string                               UltimaWinner      { get; set; } = string.Empty;

    // Game statistics
    public int TotalGames { get; set; } = 0;
    public int TotalCardsDealt { get; set; } = 0;
    public DateTime SessionStart { get; set; } = DateTime.Now;

    // Side bet settings
    public bool InsuranceEnabled { get; set; } = true;
    public bool PerfectPairsEnabled { get; set; } = false;
    public bool TwentyOnePlusThreeEnabled { get; set; } = false;

    public void BuildDeck()
    {
        Deck.Clear();
        string[] suits = { "S", "H", "D", "C" };
        string[] values = { "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K", "A" };

        foreach (var suit in suits)
        {
            foreach (var value in values)
            {
                Deck.Add(new Card(suit, value));
            }
        }

        ShuffleDeck();
        TotalCardsDealt = 0;
    }

    private void ShuffleDeck()
    {
        // Fisher-Yates shuffle
        Random rng = new Random();
        int n = Deck.Count;
        while (n > 1)
        {
            n--;
            int k = rng.Next(n + 1);
            (Deck[k], Deck[n]) = (Deck[n], Deck[k]);
        }
    }

    public Card DrawCard()
    {
        if (Deck.Count == 0)
        {
            BuildDeck();  // Reshuffle if needed
        }
        var card = Deck[0];
        Deck.RemoveAt(0);
        TotalCardsDealt++;
        if (PersistentDeck) DealtCards.Add(card);
        return card;
    }

    public int GetDealerScore()
    {
        return Player.CalculateScore(DealerHand);
    }

    public bool GetDealerHasBlackjack()
    {
        if (DealerHand.Count < 2) return false;
        return Player.CalculateScore(DealerHand) == 21;
    }

    public bool ShouldDealerHit()
    {
        int score = GetDealerScore();

        switch (DealerRules)
        {
            case DealerRules.HitsOnSoft17:
                if (score < 17) return true;
                if (score > 17) return false;

                // Score is exactly 17 - check if soft
                int aces = DealerHand.Count(c => c.IsAce);
                if (aces > 0)
                {
                    int hardTotal = DealerHand.Sum(c => c.IsAce ? 1 : c.GetNumericValue());
                    return hardTotal + 10 == 17; // This means we have a soft 17
                }
                return false;

            case DealerRules.StandsOnSoft17:
                return score < 17;

            case DealerRules.HitsOn16OrLower:
                return score <= 16;

            default:
                return score < 17;
        }
    }

    public string GetDealerHandDisplay(bool hideHoleCard = false)
    {
        if (DealerHand.Count == 0) return "No cards";

        if (hideHoleCard && DealerHand.Count >= 2 && !HoleCardRevealed)
        {
            return $"[Hidden] {DealerHand[1].GetCardDisplay()}";
        }

        return string.Join(" ", DealerHand.Select(c => c.GetCardDisplay()));
    }

    public double GetCardsRemainingPercent()
    {
        return (double)Deck.Count / 52 * 100;
    }

    public TimeSpan GetSessionDuration()
    {
        return DateTime.Now - SessionStart;
    }
}
