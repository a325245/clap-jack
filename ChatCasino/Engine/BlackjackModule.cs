using System;
using System.Collections.Generic;
using System.Linq;
using ChatCasino.Models;
using ChatCasino.Services;
using ChatCasino.UI;

namespace ChatCasino.Engine;

public sealed class BlackjackModule : BaseEngine, IDiceReceiver
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

    // Dice mode
    private bool awaitingDice;
    private string diceTargetPlayer = string.Empty;
    private bool diceForDealer;
    private int diceDealSequenceIndex;

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
        StatusText = "Waiting for Deal";
    }

    public override CmdResult Execute(string playerName, string cmd, string[] args)
    {
        cmd = cmd.ToUpperInvariant();
        var player = Players.GetPlayer(playerName);
        if (player is null) return CmdResult.Fail("Player not found.");

        return cmd switch
        {
            "BET"        => SetBet(player, args),
            "DEAL"       => StartRound(),
            "HIT"        => Hit(player),
            "STAND"      => Stand(player),
            "DOUBLE"     => Double(player),
            "SPLIT"      => Split(player),
            "INSURANCE"  => Insurance(player),
            "RULE"       => SetRule(args),
            "RULETOGGLE" => ToggleRule(),
            "DICEMODE"   => ToggleDiceMode(),
            _            => CmdResult.Ok(string.Empty)
        };
    }

    public override IEnumerable<string> GetValidCommands()
        => ["HIT", "STAND", "DOUBLE", "SPLIT", "INSURANCE", "RULETOGGLE", "DEAL", "BET", "DICEMODE"];

    // -----------------------------------------------------------------------
    // IDiceReceiver
    // -----------------------------------------------------------------------
    public void ReceiveDice(int value)
    {
        if (!awaitingDice) return;
        awaitingDice = false;

        var card = DiceToCard(value);

        if (diceTargetPlayer.StartsWith("__DEAL_", StringComparison.Ordinal) || diceTargetPlayer == "__DEALER_DEAL__")
        {
            HandleDiceDealSlot(card);
            return;
        }

        if (diceForDealer)
        {
            dealerHand.Add(card);
            var (ds, _) = BlackjackScoring.Score(dealerHand);
            Msg.QueuePartyMessage($"[BLACKJACK] Dealer draws {card.GetCardDisplay()} ({ds})");
            LogRound($"Dealer draws {card.GetCardDisplay()} ({ds})");
            if (dealerRule.ShouldHit(dealerHand)) RequestNextDealerDice();
            else FinalizeDealerAndPayouts();
            return;
        }

        var p = Players.GetPlayer(diceTargetPlayer);
        if (p is null) return;

        var context = p.Metadata.TryGetValue("Blackjack.DiceContext", out var ctx) ? ctx as string : "HIT";
        p.Metadata.Remove("Blackjack.DiceContext");

        if (!TryGetCurrentHand(p, out var hand)) return;
        hand.Add(card);

        var (score, _) = BlackjackScoring.Score(hand);
        LogRound($"{p.Name} draws {card.GetCardDisplay()} | total {score}");
        Msg.QueuePartyMessage($"[BLACKJACK] {p.Name} drew {card.GetCardDisplay()} | Total: {score}");

        if (context == "DOUBLE")
        {
            Msg.QueuePartyMessage($"[BLACKJACK] {p.Name} doubles: {string.Join(" ", hand.Select(c => c.GetCardDisplay()))} ({DescribeScore(hand)})");
            AdvanceHandOrPlayer(p);
            return;
        }

        if (score > 21)
        {
            Msg.QueuePartyMessage($"[BLACKJACK] {p.Name} -> {string.Join(" ", hand.Select(c => c.GetCardDisplay()))} (BUST {score})");
            Msg.QueuePartyMessage($"[BLACKJACK] {p.Name} busts this hand.");
            AdvanceHandOrPlayer(p);
            return;
        }

        AnnounceTurn();
    }

    // -----------------------------------------------------------------------
    // Dice helpers
    // -----------------------------------------------------------------------
    private static Card DiceToCard(int roll)
    {
        var value = roll switch { 1 => "A", 11 => "J", 12 => "Q", 13 => "K", _ => roll.ToString() };
        return new Card("*", value);
    }

    private void RequestDiceForPlayer(Player p, string context)
    {
        p.Metadata["Blackjack.DiceContext"] = context;
        diceTargetPlayer = p.Name;
        diceForDealer = false;
        awaitingDice = true;
        Msg.QueuePartyMessage($"[BLACKJACK] {p.Name}");
        Msg.QueuePartyMessage("/dice party 13");
    }

    private void RequestNextDealerDice()
    {
        diceForDealer = true;
        awaitingDice = true;
        Msg.QueuePartyMessage("/dice party 13");
    }

    private CmdResult ToggleDiceMode()
    {
        CasinoUI.BlackjackDiceMode = !CasinoUI.BlackjackDiceMode;
        var state = CasinoUI.BlackjackDiceMode ? "ON" : "OFF";
        Msg.QueueAdminEcho($"Blackjack dice mode: {state}");
        return CmdResult.Ok($"Dice mode {state}.");
    }

    // -----------------------------------------------------------------------
    // Dice deal sequence
    // Sequence: p1-c1, p2-c1 ... p1-c2, p2-c2 ... dealer-c1, dealer-c2
    // -----------------------------------------------------------------------
    private void StartDiceDealSequence()
    {
        diceDealSequenceIndex = 0;
        foreach (var name in turnOrder)
        {
            var p = Players.GetPlayer(name);
            if (p is not null) p.Metadata["Blackjack.Hands"] = new List<List<Card>> { new() };
        }
        StatusText = "Dice deal in progress...";
        ContinueDiceDealSequence();
    }

    private void ContinueDiceDealSequence()
    {
        var n = turnOrder.Count;
        var idx = diceDealSequenceIndex;

        if (idx < n * 2)
        {
            var name = idx < n ? turnOrder[idx] : turnOrder[idx - n];
            diceTargetPlayer = $"__DEAL_{idx}_{name}__";
            diceForDealer = false;
            awaitingDice = true;
            Msg.QueuePartyMessage($"[BLACKJACK] {name}");
            Msg.QueuePartyMessage("/dice party 13");
        }
        else if (idx < n * 2 + 2)
        {
            diceTargetPlayer = "__DEALER_DEAL__";
            diceForDealer = false;
            awaitingDice = true;
            Msg.QueuePartyMessage(idx == n * 2 ? "[BLACKJACK] Dealer (up card)" : "[BLACKJACK] Dealer (hidden)");
            Msg.QueuePartyMessage("/dice party 13");
        }
        else
        {
            FinalizeDiceDeal();
        }
    }

    private void HandleDiceDealSlot(Card card)
    {
        var n = turnOrder.Count;
        var idx = diceDealSequenceIndex;

        if (diceTargetPlayer == "__DEALER_DEAL__")
        {
            dealerHand.Add(card);
            if (dealerHand.Count == 1)
            {
                Msg.QueuePartyMessage($"[BLACKJACK] Dealer: {card.GetCardDisplay()} [Hidden]");
                LogRound($"Dealer up: {card.GetCardDisplay()}");
            }
        }
        else
        {
            var parts = diceTargetPlayer.Split('_');
            var pName = string.Join("_", parts.Skip(3)).TrimEnd('_');
            var p = Players.GetPlayer(pName);
            if (p is not null)
            {
                var hands = GetHands(p);
                if (hands.Count == 0)
                {
                    p.Metadata["Blackjack.Hands"] = new List<List<Card>> { new() };
                    hands = GetHands(p);
                }
                hands[0].Add(card);
                var (sc, _) = BlackjackScoring.Score(hands[0]);
                Msg.QueuePartyMessage($"[BLACKJACK] {pName}: {card.GetCardDisplay()} | Total: {sc}");
                LogRound($"{pName} {(idx < n ? "card 1" : "card 2")}: {card.GetCardDisplay()}");
            }
        }

        diceDealSequenceIndex++;
        diceTargetPlayer = string.Empty;
        awaitingDice = false;
        ContinueDiceDealSequence();
    }

    private void FinalizeDiceDeal()
    {
        if (dealerHand.Count > 0 && dealerHand[0].Value == "A")
        {
            insuranceOpen = true;
            Msg.QueuePartyMessage("[BLACKJACK] Dealer shows an Ace. Insurance available: >INSURANCE");
        }
        StatusText = $"In Round - {turnOrder[currentTurnIndex]}'s turn";
        SkipNaturalsAtStart();
        if (!roundActive) return;
        AnnounceTurn();
    }

    // -----------------------------------------------------------------------
    // Round management
    // -----------------------------------------------------------------------
    private CmdResult StartRound()
    {
        if (roundActive) return CmdResult.Fail("Round already active.");

        var players = Players.GetAllActivePlayers();
        if (players.Count == 0) return CmdResult.Fail("No active players.");

        var anyBets = players.Any(p => p.Metadata.TryGetValue("Blackjack.Bet", out var b) && b is int bet && bet > 0);
        if (!anyBets) return CmdResult.Fail("No bets placed. Use BET [amount] before dealing.");

        dealerHand.Clear();
        turnOrder.Clear();
        currentTurnIndex = 0;
        insuranceOpen = false;
        awaitingDice = false;
        BeginRoundRecord();

        foreach (var p in players)
        {
            if (!p.Metadata.TryGetValue("Blackjack.Bet", out var betObj) || betObj is not int bet || bet <= 0) continue;
            if (bank.Deduct(p, bet, "Blackjack bet") != TransactionResult.Success) continue;
            p.Metadata["Blackjack.Bets"] = new List<int> { bet };
            p.Metadata["Blackjack.ActiveHand"] = 0;
            p.Metadata["Blackjack.Done"] = false;
            p.Metadata["Blackjack.InsuranceBet"] = 0;
            p.Metadata["Blackjack.CalledInsurance"] = false;
            turnOrder.Add(p.Name);
        }

        if (turnOrder.Count == 0) return CmdResult.Fail("No valid bets placed.");

        roundActive = true;
        currentTurnStartedUtc = DateTime.UtcNow;
        currentTurnWarningSent = false;

        if (CasinoUI.BlackjackDiceMode)
        {
            StartDiceDealSequence();
            return CmdResult.Ok("Round started (dice mode).");
        }

        // Deal all hands internally but only announce the current player's hand on their turn
        foreach (var name in turnOrder)
        {
            var p = Players.GetPlayer(name);
            if (p is null) continue;
            var initialHand = new List<Card> { shoe.Draw(), shoe.Draw() };
            p.Metadata["Blackjack.Hands"] = new List<List<Card>> { initialHand };
            LogRound($"{name}: {string.Join(" ", initialHand.Select(c => c.GetCardDisplay()))}");
        }

        dealerHand.Add(shoe.Draw());
        dealerHand.Add(shoe.Draw());

        if (dealerHand[0].Value == "A")
        {
            insuranceOpen = true;
            Msg.QueuePartyMessage("[BLACKJACK] Dealer shows an Ace. Insurance available: >INSURANCE");
        }

        Msg.QueuePartyMessage($"[BLACKJACK] Dealer shows {dealerHand[0].GetCardDisplay()} [Hidden]");
        // Do NOT announce player hands here; they are revealed when each player's turn begins

        StatusText = $"In Round - {turnOrder[currentTurnIndex]}'s turn";
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

        if (CasinoUI.BlackjackDiceMode) { RequestDiceForPlayer(p, "HIT"); return CmdResult.Ok("Roll incoming."); }

        hand.Add(shoe.Draw());
        var (score, _) = BlackjackScoring.Score(hand);
        LogRound($"{p.Name} hits {hand[^1].GetCardDisplay()} | total {score}");

        if (score > 21)
        {
            Msg.QueuePartyMessage($"[BLACKJACK] {p.Name} -> {string.Join(" ", hand.Select(c => c.GetCardDisplay()))} (BUST {score})");
            Msg.QueuePartyMessage($"[BLACKJACK] {p.Name} busts this hand.");
            AdvanceHandOrPlayer(p);
        }
        else AnnounceTurn();

        return CmdResult.Ok("Hit.");
    }

    private CmdResult Stand(Player p)
    {
        if (!roundActive) return CmdResult.Fail("No active round.");
        if (!IsCurrentPlayer(p.Name)) return CmdResult.Fail($"It's {CurrentPlayerName()}'s turn.");
        LogRound($"{p.Name} stands");
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

        if (CasinoUI.BlackjackDiceMode) { RequestDiceForPlayer(p, "DOUBLE"); return CmdResult.Ok("Roll incoming."); }

        hand.Add(shoe.Draw());
        LogRound($"{p.Name} doubles {hand[^1].GetCardDisplay()} | total {DescribeScore(hand)}");
        Msg.QueuePartyMessage($"[BLACKJACK] {p.Name} doubles: {string.Join(" ", hand.Select(c => c.GetCardDisplay()))} ({DescribeScore(hand)})");
        AdvanceHandOrPlayer(p);
        return CmdResult.Ok("Double.");
    }

    private CmdResult Split(Player p)
    {
        if (!roundActive) return CmdResult.Fail("No active round.");
        if (!IsCurrentPlayer(p.Name)) return CmdResult.Fail($"It's {CurrentPlayerName()}'s turn.");
        if (!TryGetCurrentHand(p, out var hand)) return CmdResult.Fail("No active hand.");
        if (hand.Count != 2 || !CanSplit(hand[0], hand[1])) return CmdResult.Fail("Cannot split this hand.");

        var hands = GetHands(p);
        var bets = GetBets(p);
        var idx = GetActiveHandIndex(p);
        if (idx < 0 || idx >= bets.Count) return CmdResult.Fail("Invalid hand state.");
        if (hands.Count >= 2) return CmdResult.Fail("Already split once.");

        var bet = bets[idx];
        if (bank.Deduct(p, bet, "Blackjack split") != TransactionResult.Success)
            return CmdResult.Fail("Insufficient funds to split.");

        var card2 = hand[1];
        hand.RemoveAt(1);

        if (CasinoUI.BlackjackDiceMode)
        {
            hands.Add(new List<Card> { card2 });
            bets.Add(bet);
            RequestDiceForPlayer(p, "HIT");
            return CmdResult.Ok("Split — roll incoming.");
        }

        hand.Add(shoe.Draw());
        hands.Add(new List<Card> { card2, shoe.Draw() });
        bets.Add(bet);
        Msg.QueuePartyMessage($"[BLACKJACK] {p.Name} splits.");
        LogRound($"{p.Name} splits");
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
        Msg.QueuePartyMessage($"[BLACKJACK] {p.Name} buys insurance ({insurance}\uE049). ");
        return CmdResult.Ok("Insurance purchased.");
    }

    private CmdResult SetBet(Player p, string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[0], out var amount) || amount <= 0)
            return CmdResult.Fail("Usage: BET [amount]");
        if (amount < CasinoUI.GlobalMinBet || amount > CasinoUI.GlobalMaxBet)
            return CmdResult.Fail($"Bet must be between {CasinoUI.GlobalMinBet} and {CasinoUI.GlobalMaxBet}. ");
        if (p.CurrentBank < amount) return CmdResult.Fail("Insufficient funds for that bet.");
        p.Metadata["Blackjack.Bet"] = amount;
        StatusText = "Waiting for Deal";
        return CmdResult.Ok("Bet placed.");
    }

    private CmdResult SetRule(string[] args)
    {
        if (args.Length < 1) return CmdResult.Fail("Usage: RULE H17|S17");
        dealerRule = args[0].Equals("S17", StringComparison.OrdinalIgnoreCase)
            ? new StandsSoft17Strategy() : new HitsSoft17Strategy();
        Msg.QueueAdminEcho($"Blackjack dealer rule set to {dealerRule.Name}");
        return CmdResult.Ok("Rule updated.");
    }

    private CmdResult ToggleRule()
    {
        dealerRule = dealerRule is HitsSoft17Strategy ? new StandsSoft17Strategy() : new HitsSoft17Strategy();
        Msg.QueueAdminEcho($"Blackjack dealer rule set to {dealerRule.Name}");
        return CmdResult.Ok($"Rule is now {dealerRule.Name}");
    }

    // -----------------------------------------------------------------------
    // Tick / Turn management
    // -----------------------------------------------------------------------
    public override void Tick()
    {
        base.Tick();
        if (!roundActive || turnOrder.Count == 0) return;

        var current = Players.GetPlayer(CurrentPlayerName());
        if (current is null) { AdvanceToNextPlayer(); return; }

        if (current.IsAfk || current.IsKicked)
        {
            Msg.QueuePartyMessage($"[BLACKJACK] {current.Name} is unavailable and stands.");
            _ = Stand(current);
            return;
        }

        var limit = Math.Max(10.0, CasinoUI.GlobalTurnTimeLimitSeconds);
        var elapsed = (DateTime.UtcNow - currentTurnStartedUtc).TotalSeconds;

        if (!currentTurnWarningSent && elapsed >= limit * (2.0 / 3.0))
        {
            currentTurnWarningSent = true;
            Msg.QueuePartyMessage($"[BLACKJACK] {current.Name}, time's almost up.");
        }

        if (elapsed >= limit) _ = Stand(current);
    }

    private void AdvanceHandOrPlayer(Player p)
    {
        var idx = GetActiveHandIndex(p) + 1;
        var hands = GetHands(p);
        p.Metadata["Blackjack.ActiveHand"] = idx;

        if (idx < hands.Count) { StatusText = $"In Round - {p.Name}'s hand {idx + 1}"; AnnounceTurn(); return; }

        p.Metadata["Blackjack.Done"] = true;
        AdvanceToNextPlayer();
    }

    private void AdvanceToNextPlayer()
    {
        if (turnOrder.Count == 0) { ResolveDealerAndPayouts(); return; }

        for (var i = 0; i < turnOrder.Count; i++)
        {
            currentTurnIndex = (currentTurnIndex + 1) % turnOrder.Count;
            var p = Players.GetPlayer(turnOrder[currentTurnIndex]);
            if (p is null) continue;
            if (p.Metadata.TryGetValue("Blackjack.Done", out var d) && d is bool done && done) continue;
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
        Msg.QueuePartyMessage($"[BLACKJACK] {name}{handTag} -> {string.Join(" ", hand.Select(c => c.GetCardDisplay()))} ({DescribeScore(hand)})");
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
            if (hand.Count == 2 && CanSplit(hand[0], hand[1]) && idx >= 0 && idx < bets.Count
                && p.CurrentBank >= bets[idx] && GetHands(p).Count < 2)
                commands.Add(">SPLIT");
        }

        if (insuranceOpen && !(p.Metadata.TryGetValue("Blackjack.CalledInsurance", out var c) && c is true))
        {
            var cost = bets.Count > 0 ? Math.Max(1, bets[0] / 2) : 1;
            if (p.CurrentBank >= cost) commands.Add(">INSURANCE");
        }

        return commands;
    }

    // -----------------------------------------------------------------------
    // Dealer resolution
    // -----------------------------------------------------------------------
    private void SkipNaturalsAtStart()
    {
        foreach (var name in turnOrder)
        {
            var p = Players.GetPlayer(name);
            if (p is null) continue;
            var hands = GetHands(p);
            if (hands.Count > 0 && IsNaturalBlackjack(hands[0]))
                p.Metadata["Blackjack.Done"] = true;
        }

        var current = Players.GetPlayer(CurrentPlayerName());
        if (current?.Metadata.TryGetValue("Blackjack.Done", out var d) == true && d is bool done && done)
            AdvanceToNextPlayer();
    }

    private void ResolveDealerAndPayouts()
    {
        var (initialScore, _) = BlackjackScoring.Score(dealerHand);
        Msg.QueuePartyMessage($"[BLACKJACK] Dealer reveals: {string.Join(" ", dealerHand.Select(c => c.GetCardDisplay()))} ({initialScore})");

        if (CasinoUI.BlackjackDiceMode)
        {
            if (dealerRule.ShouldHit(dealerHand)) { RequestNextDealerDice(); return; }
            FinalizeDealerAndPayouts();
            return;
        }

        while (dealerRule.ShouldHit(dealerHand))
        {
            var next = shoe.Draw();
            dealerHand.Add(next);
            var (rs, _) = BlackjackScoring.Score(dealerHand);
            Msg.QueuePartyMessage($"[BLACKJACK] Dealer draws {next.GetCardDisplay()} ({rs})");
        }

        FinalizeDealerAndPayouts();
    }

    private void FinalizeDealerAndPayouts()
    {
        var dealerNatural = dealerHand.Count == 2 && IsNaturalBlackjack(dealerHand);
        var (dealerScore, _) = BlackjackScoring.Score(dealerHand);

        LogRound($"Dealer final: {string.Join(" ", dealerHand.Select(c => c.GetCardDisplay()))} ({dealerScore})");

        foreach (var p in Players.GetAllActivePlayers())
        {
            var hands = GetHands(p);
            var bets  = GetBets(p);
            if (hands.Count == 0 || bets.Count == 0) continue;

            for (var i = 0; i < hands.Count && i < bets.Count; i++)
            {
                var hand = hands[i];
                var bet  = bets[i];
                var (playerScore, _) = BlackjackScoring.Score(hand);
                var playerNatural = IsNaturalBlackjack(hand) && hands.Count == 1;

                if (playerNatural && !dealerNatural)
                {
                    var den = Math.Max(1, CasinoUI.BlackjackNaturalPayoutDenominator);
                    var num = Math.Max(1, CasinoUI.BlackjackNaturalPayoutNumerator);
                    var bonus = (int)Math.Ceiling((double)bet * num / den);
                    bank.Award(p, bet + bonus, "Blackjack natural");
                    LogRound($"{p.Name} BLACKJACK wins {bet + bonus}");
                    continue;
                }

                if (playerScore > 21) { LogRound($"{p.Name} BUST"); continue; }

                if (dealerScore > 21 || playerScore > dealerScore)
                {
                    bank.Award(p, bet * 2, "Blackjack win");
                    LogRound($"{p.Name} wins {bet * 2}");
                }
                else if (playerScore == dealerScore)
                {
                    bank.Award(p, bet, "Blackjack push");
                    LogRound($"{p.Name} push");
                }
                else
                {
                    LogRound($"{p.Name} loses");
                }
            }
        }

        roundActive = false;
        insuranceOpen = false;
        awaitingDice = false;
        StatusText = "Round Complete";

        foreach (var p in Players.GetAllActivePlayers())
        {
            p.Metadata.Remove("Blackjack.Hands");
            p.Metadata.Remove("Blackjack.Bets");
            p.Metadata.Remove("Blackjack.ActiveHand");
            p.Metadata.Remove("Blackjack.Done");
            p.Metadata.Remove("Blackjack.InsuranceBet");
            p.Metadata.Remove("Blackjack.CalledInsurance");
            if (p.CurrentBank <= 0) p.IsAfk = true;
        }

        OnRoundComplete();
    }

    // -----------------------------------------------------------------------
    // Static helpers
    // -----------------------------------------------------------------------
    private string CurrentPlayerName() => turnOrder.Count == 0 ? string.Empty : turnOrder[currentTurnIndex];

    private bool IsCurrentPlayer(string name)
        => !string.IsNullOrWhiteSpace(CurrentPlayerName())
           && CurrentPlayerName().Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool IsNaturalBlackjack(List<Card> hand)
        => hand.Count == 2 && hand.Any(c => c.Value == "A") && hand.Any(c => c.Value is "10" or "J" or "Q" or "K");

    private static bool CanSplit(Card a, Card b)
        => a.Value == b.Value || (IsTenValue(a.Value) && IsTenValue(b.Value));

    private static bool IsTenValue(string v) => v is "10" or "J" or "Q" or "K";

    private static List<List<Card>> GetHands(Player p)
        => p.Metadata.TryGetValue("Blackjack.Hands", out var o) && o is List<List<Card>> h ? h : new();

    private static List<int> GetBets(Player p)
        => p.Metadata.TryGetValue("Blackjack.Bets", out var o) && o is List<int> b ? b : new();

    private static int GetActiveHandIndex(Player p)
        => p.Metadata.TryGetValue("Blackjack.ActiveHand", out var o) && o is int i ? i : 0;

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

    private static List<Card> GetCurrentDisplayHand(Player p) => TryGetCurrentHand(p, out var h) ? h : new();

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
        awaitingDice = false;
        currentTurnStartedUtc = DateTime.MinValue;
        currentTurnWarningSent = false;
        StatusText = "Waiting for Deal";
    }

    public override ICasinoViewModel GetViewModel()
    {
        var seats = new List<PlayerSlotViewModel>();

        // Dealer row
        var dealerCards = dealerHand.Select(c => c.GetCardDisplay()).ToList();
        var dealerDisplay = new List<string>();
        if (dealerCards.Count > 0)
        {
            // Show up-card; hide hole card while round is active
            dealerDisplay.Add(dealerCards[0]);
            if (dealerCards.Count > 1)
                dealerDisplay.Add(roundActive ? "[Hidden]" : dealerCards[1]);
            for (var i = 2; i < dealerCards.Count; i++)
                dealerDisplay.Add(dealerCards[i]);
        }

        var dealerResultText = string.Empty;
        if (!roundActive && dealerHand.Count > 0)
        {
            var (ds, _) = BlackjackScoring.Score(dealerHand);
            dealerResultText = ds > 21 ? $"BUST {ds}" : IsNaturalBlackjack(dealerHand) ? "BLACKJACK" : ds.ToString();
        }

        seats.Add(new PlayerSlotViewModel
        {
            PlayerName = "Dealer",
            IsDealer = true,
            Cards = dealerDisplay,
            ResultText = dealerResultText
        });

        // Player seats
        foreach (var p in Players.GetAllActivePlayers())
        {
            var hands = GetHands(p);
            var bets = GetBets(p);
            var activeIdx = GetActiveHandIndex(p);
            var isActive = roundActive && IsCurrentPlayer(p.Name);
            var isDone = p.Metadata.TryGetValue("Blackjack.Done", out var d) && d is true;

            var slot = new PlayerSlotViewModel
            {
                PlayerName = p.Name,
                Bank = p.CurrentBank,
                BetAmount = bets.Count > 0 ? bets.Sum() : (p.Metadata.TryGetValue("Blackjack.Bet", out var b) && b is int bet ? bet : 0),
                IsAfk = p.IsAfk,
                IsKicked = p.IsKicked,
                IsActiveTurn = isActive && !isDone,
                ActiveHandIndex = activeIdx
            };

            if (hands.Count > 0)
            {
                var (dealerScore, _) = roundActive ? (0, false) : BlackjackScoring.Score(dealerHand);
                for (var i = 0; i < hands.Count; i++)
                {
                    var h = hands[i];
                    slot.HandGroups.Add(h.Select(c => c.GetCardDisplay()).ToList());

                    var (ps, _) = BlackjackScoring.Score(h);
                    string result;
                    if (roundActive)
                        result = DescribeScore(h);
                    else if (ps > 21)
                        result = $"BUST {ps}";
                    else if (!roundActive && dealerHand.Count > 0)
                    {
                        var dealerNatural = IsNaturalBlackjack(dealerHand);
                        var playerNatural = IsNaturalBlackjack(h) && hands.Count == 1;
                        if (playerNatural && !dealerNatural) result = "BLACKJACK ✓";
                        else if (dealerScore > 21 || ps > dealerScore) result = $"WIN {ps}";
                        else if (ps == dealerScore) result = $"PUSH {ps}";
                        else result = $"LOSE {ps}";
                    }
                    else
                        result = DescribeScore(h);

                    slot.HandResultTexts.Add(result);
                }
            }
            else if (p.Metadata.ContainsKey("Blackjack.Bet"))
            {
                // Bet placed but no cards yet (waiting for turn)
                slot.Cards = ["—"];
            }

            seats.Add(slot);
        }

        var vm = new BlackjackViewModel { GameTitle = "Blackjack", GameStatus = StatusText, Seats = seats };
        vm.Actions.AddRange(GetValidCommands());
        return vm;
    }

    private sealed class BlackjackViewModel : BaseViewModel
    {
        public new List<string> Actions { get; set; } = new();
        public override IReadOnlyList<string> GetActionButtons() => Actions;
    }
}
