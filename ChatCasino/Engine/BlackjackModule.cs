using System;
using System.Collections.Generic;
using System.Linq;
using ChatCasino.Models;
using ChatCasino.Services;
using ChatCasino.UI;

namespace ChatCasino.Engine;

public sealed class BlackjackModule : BaseEngine
{
    private readonly IBankService bank;
    private readonly DeckShoe<Card> shoe;
    private IDealerRuleStrategy dealerRule;
    private readonly List<Card> dealerHand = new();

    private readonly List<string> turnOrder = new();
    private int currentTurnIndex;
    private bool roundActive;
    private bool insuranceOpen;
    private DateTime currentTurnStartedUtc = DateTime.MinValue;
    private bool currentTurnWarningSent;

    public BlackjackModule(
        IMessageService msg,
        IDeckService decks,
        IPlayerService players,
        IBankService bank,
        IDealerRuleStrategy? dealerRule = null)
        : base(GameType.Blackjack, msg, decks, players)
    {
        this.bank = bank;
        shoe = decks.GetStandardDeck(1, shuffled: true);
        this.dealerRule = dealerRule ?? new HitsSoft17Strategy();
    }

    public override CmdResult Execute(string playerName, string cmd, string[] args)
    {
        cmd = cmd.ToUpperInvariant();
        var player = Players.GetPlayer(playerName);
        if (player is null) return CmdResult.Fail("Player not found.");

        return cmd switch
        {
            "BET" => SetBet(player, args),
            "DEAL" => StartRound(),
            "HIT" => Hit(player),
            "STAND" => Stand(player),
            "DOUBLE" => Double(player),
            "SPLIT" => Split(player),
            "INSURANCE" => Insurance(player),
            "RULE" => SetRule(args),
            "RULETOGGLE" => ToggleRule(),
            _ => CmdResult.Ok(string.Empty)
        };
    }

    public override IEnumerable<string> GetValidCommands()
        => ["HIT", "STAND", "DOUBLE", "SPLIT", "INSURANCE", "RULETOGGLE", "DEAL", "BET"];

    public override ICasinoViewModel GetViewModel()
    {
        var seats = new List<PlayerSlotViewModel>();

        var dealerCards = dealerHand.Select((c, idx) => roundActive && idx == 1 ? "[Hidden]" : c.GetCardDisplay()).ToList();
        seats.Add(new PlayerSlotViewModel
        {
            PlayerName = "Dealer",
            IsDealer = true,
            Cards = dealerCards,
            ResultText = !roundActive && dealerHand.Count > 0 ? DescribeScore(dealerHand) : string.Empty
        });

        foreach (var p in Players.GetAllActivePlayers())
        {
            var hands = GetHands(p);
            var bets = GetBets(p);
            var activeIdx = GetActiveHandIndex(p);

            var handGroups = hands
                .Select(h => h.Select(c => c.GetCardDisplay()).ToList())
                .ToList();

            var handResults = hands.Select(DescribeScore).ToList();

            var result = hands.Count switch
            {
                0 => string.Empty,
                1 => handResults[0],
                _ => string.Join(" | ", handResults.Select((hr, i) => $"H{i + 1}: {hr}"))
            };

            var betAmount = bets.Count > 0
                ? bets.Sum()
                : (p.Metadata.TryGetValue("Blackjack.Bet", out var b) && b is int baseBet ? baseBet : 0);

            seats.Add(new PlayerSlotViewModel
            {
                PlayerName = p.Name,
                Bank = p.CurrentBank,
                BetAmount = betAmount,
                IsActiveTurn = roundActive && IsCurrentPlayer(p.Name),
                ActiveHandIndex = activeIdx >= 0 && activeIdx < hands.Count ? activeIdx : -1,
                HandGroups = handGroups,
                HandResultTexts = handResults,
                Cards = handGroups.FirstOrDefault() ?? new List<string>(),
                ResultText = result
            });
        }

        return new BlackjackViewModel
        {
            GameTitle = "Blackjack",
            GameStatus = StatusText,
            Seats = seats,
            Actions = GetValidCommands().ToList()
        };
    }

    private CmdResult ToggleRule()
    {
        dealerRule = dealerRule is HitsSoft17Strategy ? new StandsSoft17Strategy() : new HitsSoft17Strategy();
        Msg.QueueAdminEcho($"Blackjack dealer rule set to {dealerRule.Name}");
        return CmdResult.Ok($"Rule is now {dealerRule.Name}");
    }

    private CmdResult SetBet(Player p, string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[0], out var amount) || amount <= 0)
            return CmdResult.Fail("Usage: BET [amount]");

        if (amount < CasinoUI.GlobalMinBet || amount > CasinoUI.GlobalMaxBet)
            return CmdResult.Fail($"Bet must be between {CasinoUI.GlobalMinBet} and {CasinoUI.GlobalMaxBet}. ");

        if (p.CurrentBank < amount)
            return CmdResult.Fail("Insufficient funds for that bet.");

        p.Metadata["Blackjack.Bet"] = amount;
        StatusText = "Waiting for Deal";
        Msg.QueuePartyMessage($"[BLACKJACK] {p.Name} bet {StandardizedFormatting.FormatCurrency(amount)}");
        return CmdResult.Ok("Bet placed.");
    }

    private CmdResult SetRule(string[] args)
    {
        if (args.Length < 1) return CmdResult.Fail("Usage: RULE H17|S17");

        dealerRule = args[0].Equals("S17", StringComparison.OrdinalIgnoreCase)
            ? new StandsSoft17Strategy()
            : new HitsSoft17Strategy();

        Msg.QueueAdminEcho($"Blackjack dealer rule set to {dealerRule.Name}");
        return CmdResult.Ok("Rule updated.");
    }

    public override void Tick()
    {
        base.Tick();

        if (!roundActive || turnOrder.Count == 0)
            return;

        var current = Players.GetPlayer(CurrentPlayerName());
        if (current is null)
        {
            AdvanceToNextPlayer();
            return;
        }

        if (current.IsAfk || current.IsKicked)
        {
            Msg.QueuePartyMessage($"[BLACKJACK] {current.Name} is unavailable and stands.");
            _ = Stand(current);
            return;
        }

        var turnLimit = Math.Max(10.0, CasinoUI.GlobalTurnTimeLimitSeconds);
        var elapsed = (DateTime.UtcNow - currentTurnStartedUtc).TotalSeconds;
        var warnAt = turnLimit * (2.0 / 3.0);

        if (!currentTurnWarningSent && elapsed >= warnAt)
        {
            currentTurnWarningSent = true;
            Msg.QueuePartyMessage($"[BLACKJACK] {current.Name}, time's almost up.");
        }

        if (elapsed < turnLimit)
            return;

        _ = Stand(current);
    }

    private CmdResult StartRound()
    {
        if (roundActive) return CmdResult.Fail("Round already active.");

        var players = Players.GetAllActivePlayers();
        if (players.Count == 0) return CmdResult.Fail("No active players.");

        var anyBets = players.Any(p => p.Metadata.TryGetValue("Blackjack.Bet", out var b) && b is int bet && bet > 0);
        if (!anyBets) return CmdResult.Fail("No bets placed. Players must >BET before dealing.");

        dealerHand.Clear();
        turnOrder.Clear();
        currentTurnIndex = 0;
        insuranceOpen = false;

        foreach (var p in players)
        {
            if (!p.Metadata.TryGetValue("Blackjack.Bet", out var betObj) || betObj is not int bet || bet <= 0)
                continue;

            if (bank.Deduct(p, bet, "Blackjack bet") != TransactionResult.Success)
                continue;

            var hands = new List<List<Card>> { new() { shoe.Draw(), shoe.Draw() } };

            p.Metadata["Blackjack.Hands"] = hands;
            p.Metadata["Blackjack.Bets"] = new List<int> { bet };
            p.Metadata["Blackjack.ActiveHand"] = 0;
            p.Metadata["Blackjack.Done"] = false;
            p.Metadata["Blackjack.InsuranceBet"] = 0;
            p.Metadata["Blackjack.CalledInsurance"] = false;

            turnOrder.Add(p.Name);
        }

        if (turnOrder.Count == 0)
            return CmdResult.Fail("No valid bets placed.");

        dealerHand.Add(shoe.Draw());
        dealerHand.Add(shoe.Draw());
        roundActive = true;
        currentTurnStartedUtc = DateTime.UtcNow;
        currentTurnWarningSent = false;

        if (dealerHand[0].Value == "A")
        {
            insuranceOpen = true;
            Msg.QueuePartyMessage("[BLACKJACK] Dealer shows an Ace. Insurance available: >INSURANCE");
        }

        StatusText = $"In Round - {turnOrder[currentTurnIndex]}'s turn";
        Msg.QueuePartyMessage($"[BLACKJACK] Dealer shows {dealerHand[0].GetCardDisplay()} [Hidden]");

        foreach (var name in turnOrder)
        {
            var p = Players.GetPlayer(name);
            if (p == null) continue;
            var hands = GetHands(p);
            if (hands.Count == 0) continue;
            Msg.QueuePartyMessage($"[BLACKJACK] {name}: {string.Join(" ", hands[0].Select(c => c.GetCardDisplay()))} ({DescribeScore(hands[0])})");
        }

        // Do not short-circuit the hand immediately on dealer natural; resolve at normal reveal.
        SkipNaturalsAtStart();
        if (!roundActive) return CmdResult.Ok("Round started (resolved naturals).");
        AnnounceTurn();
        return CmdResult.Ok("Round started.");
    }

    private CmdResult Hit(Player p)
    {
        if (!roundActive) return CmdResult.Fail("No active round.");
        if (!IsCurrentPlayer(p.Name)) return CmdResult.Fail($"It's {CurrentPlayerName()}'s turn.");
        if (!TryGetCurrentHand(p, out var hand)) return CmdResult.Fail("No active hand.");

        hand.Add(shoe.Draw());
        Msg.QueuePartyMessage($"[BLACKJACK] {p.Name}: {string.Join(" ", hand.Select(c => c.GetCardDisplay()))} ({DescribeScore(hand)})");

        var (score, _) = BlackjackScoring.Score(hand);
        if (score > 21)
        {
            Msg.QueuePartyMessage($"[BLACKJACK] {p.Name} busts this hand.");
            AdvanceHandOrPlayer(p);
        }
        else
        {
            AnnounceTurn();
        }

        return CmdResult.Ok("Hit.");
    }

    private CmdResult Stand(Player p)
    {
        if (!roundActive) return CmdResult.Fail("No active round.");
        if (!IsCurrentPlayer(p.Name)) return CmdResult.Fail($"It's {CurrentPlayerName()}'s turn.");

        AdvanceHandOrPlayer(p);
        return CmdResult.Ok("Stand.");
    }

    private CmdResult Double(Player p)
    {
        if (!roundActive) return CmdResult.Fail("No active round.");
        if (!IsCurrentPlayer(p.Name)) return CmdResult.Fail($"It's {CurrentPlayerName()}'s turn.");
        if (!TryGetCurrentHand(p, out var hand)) return CmdResult.Fail("No active hand.");

        var bets = GetBets(p);
        var idx = GetActiveHandIndex(p);
        if (idx < 0 || idx >= bets.Count) return CmdResult.Fail("Invalid hand state.");

        if (hand.Count != 2) return CmdResult.Fail("Double only allowed on first two cards.");

        var additional = bets[idx];
        if (bank.Deduct(p, additional, "Blackjack double") != TransactionResult.Success)
            return CmdResult.Fail("Insufficient funds to double.");

        bets[idx] += additional;
        hand.Add(shoe.Draw());

        Msg.QueuePartyMessage($"[BLACKJACK] {p.Name} doubles: {string.Join(" ", hand.Select(c => c.GetCardDisplay()))} ({DescribeScore(hand)})");

        AdvanceHandOrPlayer(p);
        return CmdResult.Ok("Double.");
    }

    private CmdResult Split(Player p)
    {
        if (!roundActive) return CmdResult.Fail("No active round.");
        if (!IsCurrentPlayer(p.Name)) return CmdResult.Fail($"It's {CurrentPlayerName()}'s turn.");

        var hands = GetHands(p);
        var bets = GetBets(p);
        var idx = GetActiveHandIndex(p);
        if (idx < 0 || idx >= hands.Count || idx >= bets.Count) return CmdResult.Fail("Invalid hand state.");

        var hand = hands[idx];
        if (hand.Count != 2) return CmdResult.Fail("Split requires exactly two cards.");
        if (!CanSplit(hand[0], hand[1])) return CmdResult.Fail("Cards cannot be split.");
        if (hands.Count >= 2) return CmdResult.Fail("Maximum split hands reached.");

        var splitCost = bets[idx];
        if (bank.Deduct(p, splitCost, "Blackjack split") != TransactionResult.Success)
            return CmdResult.Fail("Insufficient funds to split.");

        var newHand = new List<Card> { hand[1] };
        hand.RemoveAt(1);
        hand.Add(shoe.Draw());
        newHand.Add(shoe.Draw());

        hands.Insert(idx + 1, newHand);
        bets.Insert(idx + 1, splitCost);

        Msg.QueuePartyMessage($"[BLACKJACK] {p.Name} splits.");
        Msg.QueuePartyMessage($"[BLACKJACK] {p.Name} H1: {string.Join(" ", hands[0].Select(c => c.GetCardDisplay()))} ({DescribeScore(hands[0])})");
        Msg.QueuePartyMessage($"[BLACKJACK] {p.Name} H2: {string.Join(" ", hands[1].Select(c => c.GetCardDisplay()))} ({DescribeScore(hands[1])})");
        StatusText = $"In Round - {p.Name}'s hand 1";
        AnnounceTurn();
        return CmdResult.Ok("Split.");
    }

    private CmdResult Insurance(Player p)
    {
        if (!roundActive) return CmdResult.Fail("No active round.");
        if (!insuranceOpen) return CmdResult.Fail("Insurance is not available.");

        if (p.Metadata.TryGetValue("Blackjack.CalledInsurance", out var calledObj) && calledObj is true)
            return CmdResult.Fail("Insurance already purchased.");

        var bets = GetBets(p);
        if (bets.Count == 0) return CmdResult.Fail("No bet found.");

        var insurance = Math.Max(1, bets[0] / 2);
        if (bank.Deduct(p, insurance, "Blackjack insurance") != TransactionResult.Success)
            return CmdResult.Fail("Insufficient funds for insurance.");

        p.Metadata["Blackjack.InsuranceBet"] = insurance;
        p.Metadata["Blackjack.CalledInsurance"] = true;
        Msg.QueuePartyMessage($"[BLACKJACK] {p.Name} buys insurance ({StandardizedFormatting.FormatCurrency(insurance)}). ");
        return CmdResult.Ok("Insurance purchased.");
    }

    private void AdvanceHandOrPlayer(Player p)
    {
        var idx = GetActiveHandIndex(p) + 1;
        var hands = GetHands(p);
        p.Metadata["Blackjack.ActiveHand"] = idx;

        if (idx < hands.Count)
        {
            StatusText = $"In Round - {p.Name}'s hand {idx + 1}";
            AnnounceTurn();
            return;
        }

        p.Metadata["Blackjack.Done"] = true;
        AdvanceToNextPlayer();
    }

    private void AdvanceToNextPlayer()
    {
        if (turnOrder.Count == 0)
        {
            ResolveDealerAndPayouts();
            return;
        }

        for (var i = 0; i < turnOrder.Count; i++)
        {
            currentTurnIndex = (currentTurnIndex + 1) % turnOrder.Count;
            var p = Players.GetPlayer(turnOrder[currentTurnIndex]);
            if (p is null) continue;
            if (p.Metadata.TryGetValue("Blackjack.Done", out var doneObj) && doneObj is bool done && done)
                continue;

            StatusText = $"In Round - {p.Name}'s turn";
            AnnounceTurn();
            return;
        }

        ResolveDealerAndPayouts();
    }

    private void AnnounceTurn()
    {
        var name = CurrentPlayerName();
        var p = Players.GetPlayer(name);
        if (p == null) return;
        var hand = GetCurrentDisplayHand(p);
        if (hand.Count == 0) return;

        currentTurnStartedUtc = DateTime.UtcNow;
        currentTurnWarningSent = false;

        var legal = GetLegalTurnCommands(p);
        var activeIdx = GetActiveHandIndex(p);
        var handTag = GetHands(p).Count > 1 && activeIdx >= 0 ? $" H{activeIdx + 1}" : string.Empty;
        Msg.QueuePartyMessage($"[BLACKJACK] Turn: {name}{handTag} -> {string.Join(" ", hand.Select(c => c.GetCardDisplay()))} ({DescribeScore(hand)})");
        Msg.QueuePartyMessage($"[BLACKJACK] {name}: {string.Join(" / ", legal)}");
    }

    private List<string> GetLegalTurnCommands(Player p)
    {
        var commands = new List<string> { ">HIT", ">STAND" };
        var bets = GetBets(p);
        var idx = GetActiveHandIndex(p);

        if (TryGetCurrentHand(p, out var hand))
        {
            if (hand.Count == 2 && idx >= 0 && idx < bets.Count && p.CurrentBank >= bets[idx])
                commands.Add(">DOUBLE");

            if (hand.Count == 2 && CanSplit(hand[0], hand[1]) && idx >= 0 && idx < bets.Count && p.CurrentBank >= bets[idx] && GetHands(p).Count < 2)
                commands.Add(">SPLIT");
        }

        if (insuranceOpen && !(p.Metadata.TryGetValue("Blackjack.CalledInsurance", out var c) && c is true))
        {
            var insuranceCost = bets.Count > 0 ? Math.Max(1, bets[0] / 2) : 1;
            if (p.CurrentBank >= insuranceCost)
                commands.Add(">INSURANCE");
        }

        return commands;
    }

    private void ResolveDealerNatural()
    {
        foreach (var p in Players.GetAllActivePlayers())
        {
            var insuranceBet = p.Metadata.TryGetValue("Blackjack.InsuranceBet", out var insObj) && insObj is int ins ? ins : 0;
            if (insuranceBet > 0)
                bank.Award(p, insuranceBet * 3, "Blackjack insurance win");
        }

        ResolveDealerAndPayouts();
    }

    private void SkipNaturalsAtStart()
    {
        foreach (var name in turnOrder)
        {
            var p = Players.GetPlayer(name);
            if (p is null) continue;
            var hands = GetHands(p);
            if (hands.Count == 0) continue;
            if (IsNaturalBlackjack(hands[0]))
                p.Metadata["Blackjack.Done"] = true;
        }

        var current = Players.GetPlayer(CurrentPlayerName());
        if (current != null && current.Metadata.TryGetValue("Blackjack.Done", out var doneObj) && doneObj is bool done && done)
            AdvanceToNextPlayer();
    }

    private void ResolveDealerAndPayouts()
    {
        while (dealerRule.ShouldHit(dealerHand))
        {
            var next = shoe.Draw();
            dealerHand.Add(next);
            var (runningScore, _) = BlackjackScoring.Score(dealerHand);
            Msg.QueuePartyMessage($"[BLACKJACK] Dealer draws {next.GetCardDisplay()} ({runningScore})");
        }

        var dealerNatural = HasDealerNatural();
        var (dealerScore, _) = BlackjackScoring.Score(dealerHand);

        foreach (var p in Players.GetAllActivePlayers())
        {
            var hands = GetHands(p);
            var bets = GetBets(p);
            if (hands.Count == 0 || bets.Count == 0) continue;

            for (var i = 0; i < hands.Count && i < bets.Count; i++)
            {
                var hand = hands[i];
                var bet = bets[i];
                var (playerScore, _) = BlackjackScoring.Score(hand);
                var playerNatural = IsNaturalBlackjack(hand) && hands.Count == 1;

                if (playerNatural && !dealerNatural)
                {
                    var den = Math.Max(1, CasinoUI.BlackjackNaturalPayoutDenominator);
                    var num = Math.Max(1, CasinoUI.BlackjackNaturalPayoutNumerator);
                    var bonus = (int)Math.Ceiling((double)bet * num / den);
                    bank.Award(p, bet + bonus, "Blackjack natural");
                    continue;
                }

                if (playerScore > 21)
                    continue;

                if (dealerScore > 21 || playerScore > dealerScore)
                    bank.Award(p, bet * 2, "Blackjack win");
                else if (playerScore == dealerScore)
                    bank.Award(p, bet, "Blackjack push");
            }
        }

        roundActive = false;
        insuranceOpen = false;
        StatusText = "Round Complete";
        Msg.QueuePartyMessage($"[BLACKJACK] Dealer: {string.Join(" ", dealerHand.Select(c => c.GetCardDisplay()))} ({dealerScore})");

        // Clear round-specific data so bet display updates correctly
        foreach (var p in Players.GetAllActivePlayers())
        {
            p.Metadata.Remove("Blackjack.Hands");
            p.Metadata.Remove("Blackjack.Bets");
            p.Metadata.Remove("Blackjack.ActiveHand");
            p.Metadata.Remove("Blackjack.Done");
            p.Metadata.Remove("Blackjack.InsuranceBet");
            p.Metadata.Remove("Blackjack.CalledInsurance");

            // AFK players with 0 bank
            if (p.CurrentBank <= 0)
                p.IsAfk = true;
        }

        OnRoundComplete();
    }

    private bool HasDealerNatural() => dealerHand.Count == 2 && IsNaturalBlackjack(dealerHand);

    private string CurrentPlayerName() => turnOrder.Count == 0 ? string.Empty : turnOrder[currentTurnIndex];

    private bool IsCurrentPlayer(string playerName)
        => !string.IsNullOrWhiteSpace(CurrentPlayerName()) && CurrentPlayerName().Equals(playerName, StringComparison.OrdinalIgnoreCase);

    private static bool IsNaturalBlackjack(List<Card> hand)
    {
        if (hand.Count != 2) return false;
        var hasAce = hand.Any(c => c.Value == "A");
        var hasTen = hand.Any(c => c.Value is "10" or "J" or "Q" or "K");
        return hasAce && hasTen;
    }

    private static bool CanSplit(Card a, Card b)
        => a.Value == b.Value || (IsTenValue(a.Value) && IsTenValue(b.Value));

    private static bool IsTenValue(string value) => value is "10" or "J" or "Q" or "K";

    private static List<List<Card>> GetHands(Player p)
    {
        if (p.Metadata.TryGetValue("Blackjack.Hands", out var handsObj) && handsObj is List<List<Card>> hands)
            return hands;
        return new List<List<Card>>();
    }

    private static List<int> GetBets(Player p)
    {
        if (p.Metadata.TryGetValue("Blackjack.Bets", out var betsObj) && betsObj is List<int> bets)
            return bets;
        return new List<int>();
    }

    private static int GetActiveHandIndex(Player p)
        => p.Metadata.TryGetValue("Blackjack.ActiveHand", out var idxObj) && idxObj is int i ? i : 0;

    private static bool TryGetCurrentHand(Player p, out List<Card> hand)
    {
        hand = [];
        var hands = GetHands(p);
        if (hands.Count == 0) return false;
        var idx = GetActiveHandIndex(p);
        if (idx < 0 || idx >= hands.Count) return false;
        hand = hands[idx];
        return true;
    }

    private static List<Card> GetCurrentDisplayHand(Player p)
    {
        return TryGetCurrentHand(p, out var hand) ? hand : new List<Card>();
    }

    private static string DescribeScore(List<Card> hand)
    {
        if (hand.Count == 0) return string.Empty;
        var (score, _) = BlackjackScoring.Score(hand);
        if (score > 21) return $"BUST {score}";
        if (IsNaturalBlackjack(hand)) return "BLACKJACK";
        return score.ToString();
    }

    public override void OnForceStop()
    {
        dealerHand.Clear();
        turnOrder.Clear();
        currentTurnIndex = 0;
        roundActive = false;
        insuranceOpen = false;
        currentTurnStartedUtc = DateTime.MinValue;
        currentTurnWarningSent = false;
        StatusText = "Waiting for Deal";
    }

    private sealed class BlackjackViewModel : BaseViewModel
    {
        public List<string> Actions { get; set; } = new();

        public override IReadOnlyList<string> GetActionButtons() => Actions;
    }
}
