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
    public int MinBet { get; set; } = 10;
    public int MaxBet { get; set; } = 1000;
    public int TurnTimeLimit { get; set; } = 60;

    public GameState GameState { get; set; } = GameState.Lobby;
    public GameType GameType { get; set; } = GameType.Blackjack;

    // Configurable message delay (milliseconds, shared by all engines)
    public int MessageDelayMs { get; set; } = 3000;

    // Announce when players are added to the table
    public bool AnnounceNewPlayers { get; set; } = true;

    // Roulette
    public int? RouletteResult { get; set; } = null;
    public RouletteSpinState RouletteSpinState { get; set; } = RouletteSpinState.Idle;
    public DateTime RouletteSpinStart { get; set; }

    // Dealer rules and properties
    public DealerRules DealerRules { get; set; } = DealerRules.HitsOnSoft17;
    public bool DealerHasBlackjack { get; set; } = false;
    public Card DealerHoleCard { get; set; } = default;
    public bool HoleCardRevealed { get; set; } = false;

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
