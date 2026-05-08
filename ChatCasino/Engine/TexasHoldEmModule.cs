using System;
using System.Collections.Generic;
using System.Linq;
using ChatCasino.Models;
using ChatCasino.Services;
using ChatCasino.UI;

namespace ChatCasino.Engine;

public sealed class TexasHoldEmModule : BaseEngine
{
    private readonly IBankService bank;
    private readonly ITimerService timer;
    private readonly PokerEvaluator evaluator;
    private readonly PotManager potManager;
    private readonly DeckShoe<Card> shoe;
    private readonly List<Card> board = new();

    private readonly List<string> turnOrder = new();
    private readonly HashSet<string> actedThisStreet = new(StringComparer.OrdinalIgnoreCase);
    private int dealerIndex = -1;
    private int currentTurnIndex = -1;
    private int currentBet;
    private bool handActive;
    private DateTime currentTurnStartedUtc = DateTime.MinValue;
    private bool currentTurnWarningSent;
    private PokerStreet street = PokerStreet.PreFlop;

    private string dealerButtonName = string.Empty;
    private string smallBlindName = string.Empty;
    private string bigBlindName = string.Empty;

    private Guid autoDealTimerId = Guid.Empty;

    public TexasHoldEmModule(
        IMessageService msg,
        IDeckService decks,
        IPlayerService players,
        IBankService bank,
        ITimerService timer,
        PokerEvaluator evaluator,
        PotManager potManager)
        : base(GameType.TexasHoldEmPvP, msg, decks, players)
    {
        this.bank = bank;
        this.timer = timer;
        this.evaluator = evaluator;
        this.potManager = potManager;
        shoe = decks.GetStandardDeck(1, shuffled: true);
        StatusText = "Waiting for players";
    }

    public override CmdResult Execute(string playerName, string cmd, string[] args)
    {
        var player = Players.GetPlayer(playerName);
        if (player is null) return CmdResult.Fail("Player not found.");

        cmd = cmd.ToUpperInvariant();
        return cmd switch
        {
            "DEAL" => StartHand(),
            "HAND" => SendHand(player),
            "FOLD" => Fold(player),
            "CHECK" => Check(player),
            "CALL" => Call(player),
            "BET" => Bet(player, args),
            "RAISE" => Raise(player, args),
            "ALL" when args.Length > 0 && args[0].Equals("IN", StringComparison.OrdinalIgnoreCase) => AllIn(player),
            _ => CmdResult.Ok(string.Empty)
        };
    }

    public override IEnumerable<string> GetValidCommands()
        => ["DEAL", "HAND", "FOLD", "CHECK", "CALL", "BET", "RAISE", "ALL IN"];

    public override void Tick()
    {
        base.Tick();

        if (!handActive || currentTurnIndex < 0 || currentTurnIndex >= turnOrder.Count)
            return;

        var current = Players.GetPlayer(turnOrder[currentTurnIndex]);
        if (current is null)
        {
            currentTurnIndex = FindNextActionIndex(currentTurnIndex);
            if (currentTurnIndex < 0)
                AdvanceStreet();
            else
                AnnounceTurn();
            return;
        }

        if (current.IsAfk || current.IsKicked)
        {
            Msg.QueuePartyMessage($"[POKER] {current.Name} is unavailable and folds.");
            _ = Fold(current);
            return;
        }

        if (GetBool(current, "Poker.Folded") || GetBool(current, "Poker.AllIn"))
            return;

        var turnLimit = Math.Max(10.0, CasinoUI.GlobalTurnTimeLimitSeconds);
        var elapsed = (DateTime.UtcNow - currentTurnStartedUtc).TotalSeconds;
        var warnAt = turnLimit * (2.0 / 3.0);

        if (!currentTurnWarningSent && elapsed >= warnAt)
        {
            currentTurnWarningSent = true;
            Msg.QueuePartyMessage($"[POKER] {current.Name}, time's almost up.");
        }

        if (elapsed >= turnLimit)
            _ = Fold(current);
    }

    public override ICasinoViewModel GetViewModel()
    {
        var potTotal = 0;
        foreach (var name in turnOrder)
        {
            var p = Players.GetPlayer(name);
            if (p is null) continue;
            potTotal += GetInt(p, "Poker.Committed");
        }

        var potText = $"Pot: {potTotal}\uE049";
        if (handActive)
        {
            var participants = turnOrder.Select(n => Players.GetPlayer(n)).Where(p => p != null).Select(p => p!).ToList();
            var sidePots = potManager.BuildSidePots(participants);
            if (sidePots.Count > 1)
            {
                var potParts = sidePots.Select((sp, i) => $"P{i + 1}:{sp.Amount}\uE049").ToList();
                potText += $" ({string.Join(" ", potParts)})";
            }
        }

        var seats = new List<PlayerSlotViewModel>
        {
            new()
            {
                PlayerName = "Board",
                IsDealer = true,
                Cards = board.Select(c => c.GetCardDisplay()).ToList(),
                ResultText = potText
            }
        };

        foreach (var name in turnOrder)
        {
            var p = Players.GetPlayer(name);
            if (p is null) continue;

            var hole = p.Metadata.TryGetValue("Poker.Hole", out var h) && h is List<Card> hc
                ? hc.Select(c => c.GetCardDisplay()).ToList()
                : new List<string>();

            var status = GetBool(p, "Poker.Folded") ? "FOLDED" : (GetBool(p, "Poker.AllIn") ? "ALL-IN" : string.Empty);
            var role = GetRoleForPlayer(p.Name);

            seats.Add(new PlayerSlotViewModel
            {
                PlayerName = p.Name,
                Bank = p.CurrentBank,
                BetAmount = GetInt(p, "Poker.Committed"),
                Cards = hole,
                ResultText = status,
                HandResultTexts = string.IsNullOrWhiteSpace(role) ? [] : [$"ROLE:{role}"],
                IsActiveTurn = handActive && currentTurnIndex >= 0 && currentTurnIndex < turnOrder.Count && turnOrder[currentTurnIndex].Equals(p.Name, StringComparison.OrdinalIgnoreCase)
            });
        }

        return new PokerViewModel
        {
            GameTitle = "Texas Hold'Em PvP",
            GameStatus = StatusText,
            Seats = seats,
            Actions = GetValidCommands().ToList()
        };
    }

    private CmdResult Bet(Player p, string[] args)
    {
        if (!handActive)
            return CmdResult.Fail("Use DEAL to start a hand.");

        if (currentBet > 0)
            return Raise(p, args);

        if (!IsCurrentTurn(p))
            return CmdResult.Fail($"It's {CurrentPlayerName()}'s turn.");

        if (args.Length < 1 || !int.TryParse(args[0], out var amount) || amount <= 0)
            return CmdResult.Fail("Usage: BET [amount]. Amount must be a positive number.");

        if (amount > p.CurrentBank)
            return CmdResult.Fail($"Insufficient funds. You have {p.CurrentBank}\uE049 but tried to bet {amount}\uE049.");

        if (bank.Deduct(p, amount, "Poker bet") != TransactionResult.Success)
            return CmdResult.Fail($"Insufficient funds ({p.CurrentBank}\uE049 available).");

        SetInt(p, "Poker.StreetCommitted", amount);
        SetInt(p, "Poker.Committed", GetInt(p, "Poker.Committed") + amount);
        currentBet = amount;
        actedThisStreet.Clear();
        actedThisStreet.Add(p.Name);
        Msg.QueuePartyMessage($"[POKER] {p.Name} raises to {currentBet}\uE049 | Pot: {GetPotTotal()}");
        AdvanceAfterAction();
        return CmdResult.Ok("Bet.");
    }

    private CmdResult StartHand()
    {
        if (autoDealTimerId != Guid.Empty)
        {
            _ = timer.Cancel(autoDealTimerId);
            autoDealTimerId = Guid.Empty;
        }

        var active = Players.GetAllActivePlayers().Where(p => p.CurrentBank > 0).ToList();
        if (active.Count < 2) return CmdResult.Fail("Need at least 2 active players with funds.");

        handActive = true;
        board.Clear();
        actedThisStreet.Clear();
        street = PokerStreet.PreFlop;
        StatusText = "Pre-flop";

        turnOrder.Clear();
        turnOrder.AddRange(active.Select(p => p.Name));

        dealerIndex = (dealerIndex + 1 + turnOrder.Count) % turnOrder.Count;

        foreach (var p in active)
        {
            p.Metadata["Poker.Folded"] = false;
            p.Metadata["Poker.AllIn"] = false;
            p.Metadata["Poker.Committed"] = 0;
            p.Metadata["Poker.StreetCommitted"] = 0;
            p.Metadata["Poker.Hole"] = new List<Card> { shoe.Draw(), shoe.Draw() };
            SendHoleTell(p);
        }

        var sb = Math.Max(1, CasinoUI.PokerSmallBlind);
        var bb = Math.Max(sb, CasinoUI.PokerBigBlind);

        var smallBlindIndex = turnOrder.Count == 2 ? dealerIndex : (dealerIndex + 1) % turnOrder.Count;
        var bigBlindIndex = turnOrder.Count == 2 ? (dealerIndex + 1) % turnOrder.Count : (dealerIndex + 2) % turnOrder.Count;

        dealerButtonName = turnOrder[dealerIndex];
        smallBlindName = turnOrder[smallBlindIndex];
        bigBlindName = turnOrder[bigBlindIndex];

        var sbPosted = PostBlind(smallBlindName, sb);
        var bbPosted = PostBlind(bigBlindName, bb);

        Msg.QueuePartyMessage($"[POKER] Dealer={dealerButtonName} | SB {smallBlindName} {sbPosted}\uE049 | BB {bigBlindName} {bbPosted}\uE049 | Pot: {GetPotTotal()}");

        currentBet = bb;
        currentTurnIndex = turnOrder.Count == 2 ? dealerIndex : (bigBlindIndex + 1) % turnOrder.Count;

        if (!AnyCanAct())
        {
            RunOutBoardAndShowdown();
            return CmdResult.Ok("Hand started.");
        }

        if (!CanPlayerAct(turnOrder[currentTurnIndex]))
            currentTurnIndex = FindNextActionIndex(currentTurnIndex);

        AnnounceTurn();
        return CmdResult.Ok("Hand started.");
    }

    private int PostBlind(string playerName, int targetAmount)
    {
        var p = Players.GetPlayer(playerName);
        if (p is null) return 0;

        var amount = Math.Max(0, Math.Min(targetAmount, p.CurrentBank));
        if (amount <= 0)
        {
            p.Metadata["Poker.AllIn"] = true;
            return 0;
        }

        if (bank.Deduct(p, amount, "Poker blind") != TransactionResult.Success)
            return 0;

        SetInt(p, "Poker.StreetCommitted", amount);
        SetInt(p, "Poker.Committed", amount);
        if (p.CurrentBank == 0)
            p.Metadata["Poker.AllIn"] = true;

        return amount;
    }

    private CmdResult Fold(Player p)
    {
        if (!handActive) return CmdResult.Fail("No active hand.");
        if (!IsCurrentTurn(p)) return CmdResult.Fail($"It's {CurrentPlayerName()}'s turn.");

        p.Metadata["Poker.Folded"] = true;
        actedThisStreet.Add(p.Name);
        Msg.QueuePartyMessage($"[POKER] {p.Name} folds.");
        AdvanceAfterAction();
        return CmdResult.Ok("Fold.");
    }

    private CmdResult Check(Player p)
    {
        if (!handActive) return CmdResult.Fail("No active hand.");
        if (!IsCurrentTurn(p)) return CmdResult.Fail($"It's {CurrentPlayerName()}'s turn.");

        if (GetInt(p, "Poker.StreetCommitted") != currentBet)
            return CmdResult.Fail("Cannot check, call or fold.");

        actedThisStreet.Add(p.Name);
        Msg.QueuePartyMessage($"[POKER] {p.Name} checks.");
        AdvanceAfterAction();
        return CmdResult.Ok("Check.");
    }

    private CmdResult Call(Player p)
    {
        if (!handActive) return CmdResult.Fail("No active hand.");
        if (!IsCurrentTurn(p)) return CmdResult.Fail($"It's {CurrentPlayerName()}'s turn.");

        var mine = GetInt(p, "Poker.StreetCommitted");
        var diff = currentBet - mine;
        if (diff <= 0) return Check(p);

        if (p.CurrentBank < diff)
            return AllIn(p);

        if (bank.Deduct(p, diff, "Poker call") != TransactionResult.Success)
            return CmdResult.Fail("Insufficient funds.");

        SetInt(p, "Poker.StreetCommitted", currentBet);
        SetInt(p, "Poker.Committed", GetInt(p, "Poker.Committed") + diff);
        if (p.CurrentBank == 0)
            p.Metadata["Poker.AllIn"] = true;

        actedThisStreet.Add(p.Name);
        Msg.QueuePartyMessage($"[POKER] {p.Name} calls. | Pot: {GetPotTotal()}");
        AdvanceAfterAction();
        return CmdResult.Ok("Call.");
    }

    private CmdResult Raise(Player p, string[] args)
    {
        if (!handActive) return CmdResult.Fail("No active hand.");
        if (!IsCurrentTurn(p)) return CmdResult.Fail($"It's {CurrentPlayerName()}'s turn.");

        if (args.Length < 1 || !int.TryParse(args[0], out var raise) || raise <= 0)
            return CmdResult.Fail("Usage: RAISE [amount]. Amount is how much to add on top of the current bet.");

        var target = currentBet + raise;
        var mine = GetInt(p, "Poker.StreetCommitted");
        var cost = target - mine;
        if (cost <= 0)
            return CmdResult.Fail($"Invalid raise. You've already committed {mine}\uE049 this street; raise must exceed current bet of {currentBet}\uE049.");

        if (cost > p.CurrentBank)
            return CmdResult.Fail($"Insufficient funds. Raise costs {cost}\uE049 (current bet {currentBet}\uE049 + raise {raise}\uE049 − your committed {mine}\uE049) but you only have {p.CurrentBank}\uE049. Try ALL IN instead.");

        if (bank.Deduct(p, cost, "Poker raise") != TransactionResult.Success)
            return CmdResult.Fail($"Insufficient funds ({p.CurrentBank}\uE049 available).");

        SetInt(p, "Poker.StreetCommitted", target);
        SetInt(p, "Poker.Committed", GetInt(p, "Poker.Committed") + cost);
        if (p.CurrentBank == 0)
            p.Metadata["Poker.AllIn"] = true;

        currentBet = target;
        actedThisStreet.Clear();
        actedThisStreet.Add(p.Name);

        Msg.QueuePartyMessage($"[POKER] {p.Name} raises to {target}\uE049 | Pot: {GetPotTotal()}");
        AdvanceAfterAction();
        return CmdResult.Ok("Raise.");
    }

    private CmdResult AllIn(Player p)
    {
        if (!handActive) return CmdResult.Fail("No active hand.");
        if (!IsCurrentTurn(p)) return CmdResult.Fail($"It's {CurrentPlayerName()}'s turn.");

        var amount = p.CurrentBank;
        if (amount <= 0) return CmdResult.Fail("No funds.");

        if (bank.Deduct(p, amount, "Poker all-in") != TransactionResult.Success)
            return CmdResult.Fail("Insufficient funds.");

        var mine = GetInt(p, "Poker.StreetCommitted");
        var target = mine + amount;
        SetInt(p, "Poker.StreetCommitted", target);
        SetInt(p, "Poker.Committed", GetInt(p, "Poker.Committed") + amount);
        p.Metadata["Poker.AllIn"] = true;

        if (target > currentBet)
        {
            currentBet = target;
            actedThisStreet.Clear();
        }

        actedThisStreet.Add(p.Name);
        Msg.QueuePartyMessage($"[POKER] {p.Name} is all-in. | Pot: {GetPotTotal()}");
        AdvanceAfterAction();
        return CmdResult.Ok("All-in.");
    }

    private bool TryAwardSingleRemaining()
    {
        var contenders = turnOrder
            .Select(n => Players.GetPlayer(n))
            .Where(p => p != null && !GetBool(p, "Poker.Folded"))
            .Select(p => p!)
            .ToList();

        if (contenders.Count != 1)
            return false;

        var winner = contenders[0];

        var pot = turnOrder.Select(n => Players.GetPlayer(n)).Where(p => p != null).Sum(p => GetInt(p!, "Poker.Committed"));
        if (pot > 0)
            bank.Award(winner, pot, "Poker pot payout");

        Msg.QueuePartyMessage($"[POKER] {winner.Name} wins {pot}\uE049 (all others folded).");
        Msg.QueuePartyMessage($"[POKER] Seat order: {winner.Name}");
        EndHand("Hand complete");
        return true;
    }

    private void ResolveShowdown()
    {
        var contenders = turnOrder
            .Select(n => Players.GetPlayer(n))
            .Where(p => p != null && !GetBool(p, "Poker.Folded"))
            .Select(p => p!)
            .ToList();

        if (contenders.Count == 0)
        {
            EndHand("Hand complete");
            return;
        }

        var ranks = contenders.ToDictionary(p => p, p =>
        {
            var hole = p.Metadata["Poker.Hole"] as List<Card> ?? [];
            return evaluator.Evaluate(hole.Concat(board).ToList());
        });

        var payoutOrder = new List<string>();

        var participants = turnOrder.Select(n => Players.GetPlayer(n)).Where(p => p != null).Select(p => p!).ToList();
        var pots = potManager.BuildSidePots(participants);
        var potIndex = 1;
        foreach (var pot in pots)
        {
            var eligible = contenders.Where(p => pot.EligiblePlayers.Contains(p.Name)).ToList();
            if (eligible.Count == 0) continue;

            var best = eligible[0];
            foreach (var p in eligible.Skip(1))
                if (HandRank.Compare(ranks[p], ranks[best]) > 0)
                    best = p;

            var winners = eligible.Where(p => HandRank.Compare(ranks[p], ranks[best]) == 0).ToList();
            if (winners.Count == 0) continue;

            var split = pot.Amount / winners.Count;
            var remainder = pot.Amount % winners.Count;

            var parts = new List<string>();
            for (var i = 0; i < winners.Count; i++)
            {
                var award = split + (i < remainder ? 1 : 0);
                if (award <= 0) continue;
                bank.Award(winners[i], award, "Poker side pot payout");
                payoutOrder.Add(winners[i].Name);
                parts.Add($"{winners[i].Name} {award}\uE049");
            }

            if (parts.Count > 0)
            {
                var potLabel = pots.Count > 1 ? $"Pot {potIndex}: " : string.Empty;
                if (winners.Count > 1)
                    Msg.QueuePartyMessage($"[POKER] {potLabel}Split: {string.Join(" | ", parts)} ({ranks[best].Description})");
                else
                {
                    var singleAward = split + remainder;
                    Msg.QueuePartyMessage($"[POKER] {potLabel}{winners[0].Name} wins {singleAward}\uE049 ({ranks[best].Description})");
                }
            }

            potIndex++;
        }

        if (payoutOrder.Count == 0)
            payoutOrder.AddRange(turnOrder);

        var ordered = payoutOrder
            .Concat(turnOrder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Msg.QueuePartyMessage($"[POKER] Seat order: {string.Join(" | ", ordered)}");
        EndHand("Showdown");
    }

    private void AdvanceStreet()
    {
        if (!handActive)
            return;

        if (street == PokerStreet.River)
        {
            ResolveShowdown();
            return;
        }

        Burn();
        if (street == PokerStreet.PreFlop)
        {
            board.Add(shoe.Draw());
            board.Add(shoe.Draw());
            board.Add(shoe.Draw());
            street = PokerStreet.Flop;
            StatusText = "Flop";
        }
        else if (street == PokerStreet.Flop)
        {
            board.Add(shoe.Draw());
            street = PokerStreet.Turn;
            StatusText = "Turn";
        }
        else
        {
            board.Add(shoe.Draw());
            street = PokerStreet.River;
            StatusText = "River";
        }

        Msg.QueuePartyMessage($"[POKER] Board: {string.Join(" ", board.Select(c => c.GetCardDisplay()))} | Pot: {GetPotTotal()}");

        currentBet = 0;
        actedThisStreet.Clear();
        foreach (var name in turnOrder)
        {
            var p = Players.GetPlayer(name);
            if (p is null) continue;
            p.Metadata["Poker.StreetCommitted"] = 0;
        }

        if (!AnyCanAct())
        {
            if (street == PokerStreet.River)
                ResolveShowdown();
            else
                AdvanceStreet();
            return;
        }

        currentTurnIndex = FindFirstPostFlopActionIndex();
        if (currentTurnIndex < 0)
        {
            ResolveShowdown();
            return;
        }

        AnnounceTurn();
    }

    private int GetPotTotal()
        => turnOrder.Select(n => Players.GetPlayer(n)).Where(p => p != null).Sum(p => GetInt(p!, "Poker.Committed"));

    private void RunOutBoardAndShowdown()
    {
        while (street != PokerStreet.River)
            AdvanceStreet();

        ResolveShowdown();
    }

    private void EndHand(string status)
    {
        handActive = false;
        currentTurnIndex = -1;
        currentTurnStartedUtc = DateTime.MinValue;
        currentTurnWarningSent = false;
        currentBet = 0;
        actedThisStreet.Clear();
        dealerButtonName = string.Empty;
        smallBlindName = string.Empty;
        bigBlindName = string.Empty;
        StatusText = status;
        OnRoundComplete();

        if (autoDealTimerId != Guid.Empty)
            _ = timer.Cancel(autoDealTimerId);

        if (!CasinoUI.PokerAutoPlayEnabled)
        {
            autoDealTimerId = Guid.Empty;
            return;
        }

        autoDealTimerId = timer.Schedule(TimeSpan.FromSeconds(10), () =>
        {
            autoDealTimerId = Guid.Empty;
            if (handActive)
                return;
            _ = StartHand();
        });
    }

    public override void OnForceStop()
    {
        if (autoDealTimerId != Guid.Empty)
        {
            _ = timer.Cancel(autoDealTimerId);
            autoDealTimerId = Guid.Empty;
        }

        handActive = false;
        currentTurnIndex = -1;
        currentTurnStartedUtc = DateTime.MinValue;
        currentTurnWarningSent = false;
        currentBet = 0;
        actedThisStreet.Clear();
        turnOrder.Clear();
        board.Clear();
        dealerButtonName = string.Empty;
        smallBlindName = string.Empty;
        bigBlindName = string.Empty;
        StatusText = "Waiting for players";
    }

    private List<string> GetLegalCommands(Player p)
    {
        var legal = new List<string>();
        var mine = GetInt(p, "Poker.StreetCommitted");
        if (mine == currentBet)
        {
            legal.Add(">CHECK");
        }
        else
        {
            legal.Add(">CALL");
        }

        if (p.CurrentBank > 0)
        {
            legal.Add(">RAISE");
            legal.Add(">ALL IN");
        }

        legal.Add(">FOLD");
        return legal;
    }

    private bool IsCurrentTurn(Player p)
        => handActive
           && currentTurnIndex >= 0
           && currentTurnIndex < turnOrder.Count
           && turnOrder[currentTurnIndex].Equals(p.Name, StringComparison.OrdinalIgnoreCase);

    private string CurrentPlayerName()
        => currentTurnIndex >= 0 && currentTurnIndex < turnOrder.Count ? turnOrder[currentTurnIndex] : string.Empty;

    private bool AnyCanAct() => turnOrder.Any(CanPlayerAct);

    private bool CanPlayerAct(string name)
    {
        var p = Players.GetPlayer(name);
        if (p is null) return false;
        return !GetBool(p, "Poker.Folded") && !GetBool(p, "Poker.AllIn");
    }

    private int FindNextActionIndex(int fromIndex)
    {
        if (turnOrder.Count == 0) return -1;

        for (var i = 1; i <= turnOrder.Count; i++)
        {
            var idx = (fromIndex + i + turnOrder.Count) % turnOrder.Count;
            if (CanPlayerAct(turnOrder[idx]))
                return idx;
        }

        return -1;
    }

    private int FindFirstPostFlopActionIndex()
    {
        if (turnOrder.Count == 0) return -1;
        for (var i = 1; i <= turnOrder.Count; i++)
        {
            var idx = (dealerIndex + i) % turnOrder.Count;
            if (CanPlayerAct(turnOrder[idx]))
                return idx;
        }

        return -1;
    }

    private CmdResult SendHand(Player player)
    {
        SendHoleTell(player);
        return CmdResult.Ok("Hand sent via tell.");
    }

    private void AnnounceTurn()
    {
        if (currentTurnIndex < 0 || currentTurnIndex >= turnOrder.Count)
            return;

        var current = Players.GetPlayer(turnOrder[currentTurnIndex]);
        if (current is null)
            return;

        currentTurnStartedUtc = DateTime.UtcNow;
        currentTurnWarningSent = false;

        var legal = GetLegalCommands(current);
        Msg.QueuePartyMessage($"[POKER] Turn: {current.Name}");
        Msg.QueuePartyMessage($"[POKER] {current.Name}: {string.Join(" / ", legal)}");
    }

    private string GetRoleForPlayer(string name)
    {
        if (name.Equals(dealerButtonName, StringComparison.OrdinalIgnoreCase)) return "DEALER";
        if (name.Equals(smallBlindName, StringComparison.OrdinalIgnoreCase)) return "SB";
        if (name.Equals(bigBlindName, StringComparison.OrdinalIgnoreCase)) return "BB";
        return string.Empty;
    }

    private void SendHoleTell(Player p)
    {
        var hole = p.Metadata.TryGetValue("Poker.Hole", out var h) && h is List<Card> cards ? cards : [];
        if (hole.Count == 0)
            return;

        var message = $"[POKER HAND] {string.Join(" ", hole.Select(c => c.GetCardDisplay()))}";
        Msg.QueueTell(p.Name, string.IsNullOrWhiteSpace(p.HomeWorld) ? "Unknown" : p.HomeWorld, message);
    }

    private void Burn() => _ = shoe.Draw();

    private static int GetInt(Player p, string key)
        => p.Metadata.TryGetValue(key, out var v) && v is int i ? i : 0;

    private static void SetInt(Player p, string key, int value)
        => p.Metadata[key] = value;

    private static bool GetBool(Player p, string key)
        => p.Metadata.TryGetValue(key, out var v) && v is bool b && b;

    private enum PokerStreet
    {
        PreFlop,
        Flop,
        Turn,
        River
    }

    private sealed class PokerViewModel : BaseViewModel
    {
        public List<string> Actions { get; set; } = new();
        public override IReadOnlyList<string> GetActionButtons() => Actions;
    }

    private void AdvanceAfterAction()
    {
        if (TryAwardSingleRemaining())
            return;

        if (IsBettingRoundComplete())
        {
            AdvanceStreet();
            return;
        }

        currentTurnIndex = FindNextActionIndex(currentTurnIndex);
        if (currentTurnIndex < 0)
        {
            AdvanceStreet();
            return;
        }

        AnnounceTurn();
    }

    private bool IsBettingRoundComplete()
    {
        foreach (var name in turnOrder)
        {
            var p = Players.GetPlayer(name);
            if (p is null) continue;
            if (GetBool(p, "Poker.Folded") || GetBool(p, "Poker.AllIn")) continue;

            if (GetInt(p, "Poker.StreetCommitted") != currentBet)
                return false;
            if (!actedThisStreet.Contains(name))
                return false;
        }

        return true;
    }
}
