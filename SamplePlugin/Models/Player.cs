using System;
using System.Collections.Generic;
using System.Linq;

namespace SamplePlugin.Models;

public class Player
{
    public string Name { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public int Bank { get; set; } = 10000;
    public int PersistentBet { get; set; } = 0;
    private bool _isAfk = false;
    public bool IsAfk
    {
        get => _isAfk;
        set
        {
            if (value && !_isAfk)  { AfkSince = DateTime.Now; AfkNotifiedMinutes = 0; }
            else if (!value)       { AfkSince = null;         AfkNotifiedMinutes = 0; }
            _isAfk = value;
        }
    }
    public DateTime? AfkSince { get; private set; }
    public int AfkNotifiedMinutes { get; set; } = 0;
    public bool IsStanding { get; set; } = false;

    // Enhanced hand management
    public List<List<Card>> Hands { get; set; } = new();
    public List<int> CurrentBets { get; set; } = new();
    public int ActiveHandIndex { get; set; } = 0;
    public int MaxSplits { get; set; } = 3; // Can split up to 4 hands
    public bool HasDoubledDown { get; set; } = false;

    // Side bets
    public int InsuranceBet { get; set; } = 0;
    public bool HasInsurance { get; set; } = false;

    // Betting history
    public List<BetResult> BetHistory { get; set; } = new();

    // Session stats
    public int GamesPlayed { get; set; } = 0;
    public int GamesWon { get; set; } = 0;
    public int TotalWinnings { get; set; } = 0;
    public int TurnStartBank { get; set; } = 0;
    public int PreDealBank { get; set; } = 0;

    // Roulette
    public List<RouletteBet> RouletteBets { get; set; } = new();
    public int RouletteNetGains  { get; set; } = 0;
    public int CrapsNetGains      { get; set; } = 0;
    public int BaccaratNetGains   { get; set; } = 0;
    public int ChocoboNetGains    { get; set; } = 0;
    public int PokerNetGains      { get; set; } = 0;

    public Player(string name, string server = "")
    {
        Name = name;
        Server = server;
    }

    public int GetCurrentHandScore()
    {
        if (ActiveHandIndex >= Hands.Count) return 0;
        return CalculateScore(Hands[ActiveHandIndex]);
    }

    public HandInfo GetHandInfo(int handIndex = -1)
    {
        if (handIndex == -1) handIndex = ActiveHandIndex;
        if (handIndex >= Hands.Count) return new HandInfo();

        var hand = Hands[handIndex];
        return new HandInfo
        {
            Cards = hand,
            Score = CalculateScore(hand),
            IsBlackjack = IsNaturalBlackjack(hand),
            IsBust = CalculateScore(hand) > 21,
            IsSoft = IsSoftHand(hand),
            AceCount = hand.Count(c => c.IsAce)
        };
    }

    public bool CanSplit()
    {
        if (ActiveHandIndex >= Hands.Count) return false;
        var hand = Hands[ActiveHandIndex];

        if (hand.Count != 2 || Hands.Count >= MaxSplits + 1 || Bank < CurrentBets[ActiveHandIndex])
            return false;

        var card1 = hand[0];
        var card2 = hand[1];

        // Allow exact same value OR both are 10-value cards (10, J, Q, K)
        return card1.Value == card2.Value || 
               (card1.IsTenValue && card2.IsTenValue);
    }

    public bool CanDoubleDown()
    {
        if (ActiveHandIndex >= Hands.Count) return false;
        var hand = Hands[ActiveHandIndex];

        return hand.Count == 2 && 
               Bank >= CurrentBets[ActiveHandIndex] &&
               !HasDoubledDown;
    }

    public bool IsNaturalBlackjack(List<Card> hand)
    {
        return hand.Count == 2 && 
               CalculateScore(hand) == 21 && 
               Hands.Count == 1; // Only natural if not split
    }

    public bool IsSoftHand(List<Card> hand)
    {
        int aces = hand.Count(c => c.IsAce);
        int total = hand.Sum(c => c.GetNumericValue());

        // If we have aces and the total is using at least one as 11
        return aces > 0 && total <= 21 && (total - (aces * 10)) < 12;
    }

    public static int CalculateScore(List<Card> hand)
    {
        int score = 0;
        int aces = 0;

        foreach (var card in hand)
        {
            if (card.Value == "A")
            {
                aces++;
                score += 11;
            }
            else if (card.Value is "J" or "Q" or "K")
            {
                score += 10;
            }
            else
            {
                score += int.Parse(card.Value);
            }
        }

        // Correction loop: convert aces from 11 to 1 if needed
        while (score > 21 && aces > 0)
        {
            score -= 10;
            aces--;
        }

        return score;
    }

    public void AddBetResult(BetResult result)
    {
        BetHistory.Add(result);
        GamesPlayed++;
        if (result.Result is "WIN" or "BLACKJACK")
        {
            GamesWon++;
            TotalWinnings += result.AmountWon;
        }
        else if (result.Result == "LOSE")
        {
            TotalWinnings -= result.AmountLost;
        }
    }

    public double GetWinPercentage()
    {
        return GamesPlayed > 0 ? (double)GamesWon / GamesPlayed * 100 : 0;
    }
}

public class HandInfo
{
    public List<Card> Cards { get; set; } = new();
    public int Score { get; set; } = 0;
    public bool IsBlackjack { get; set; } = false;
    public bool IsBust { get; set; } = false;
    public bool IsSoft { get; set; } = false;
    public int AceCount { get; set; } = 0;

    public string GetHandDescription()
    {
        if (IsBust) return $"BUST ({Score})";
        if (IsBlackjack) return "BLACKJACK";
        if (IsSoft && Score != 21) return $"Soft {Score}";
        return Score.ToString();
    }
}

public class BetResult
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public int BetAmount { get; set; }
    public string Result { get; set; } = string.Empty; // WIN, LOSE, PUSH, BLACKJACK
    public int AmountWon { get; set; }
    public int AmountLost { get; set; }
    public string HandDescription { get; set; } = string.Empty;
}
