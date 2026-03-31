using System;
using System.Collections.Generic;
using System.Linq;
using SamplePlugin.Models;

namespace SamplePlugin.Engine;

public class CrapsEngine
{
    public Table CurrentTable { get; set; }
    public ChatMode ChatMode { get; set; } = ChatMode.Party;

    public Action<string>? OnChatMessage { get; set; }
    public Action<string, string>? OnPlayerTell { get; set; }
    public Action? OnUIUpdate { get; set; }

    private Queue<string> MessageQueue { get; } = new();
    private DateTime LastMessage { get; set; } = DateTime.MinValue;
    private static readonly Random Rng = new();
    private const double RollDurationMs = 2000.0;
    private long _lastAnimFrame = -1;
    private bool _betTimerWarned = false;
    private Dictionary<string, List<string>> _rollResults = new();

    public CrapsEngine(Table table) { CurrentTable = table; }

    // ── Messaging ──────────────────────────────────────────────────────────────

    private void SendMessage(string message)
    {
        string cmd = ChatMode == ChatMode.Party ? $"/party {message}" : $"/say {message}";
        OnChatMessage?.Invoke(cmd);
        LastMessage = DateTime.Now;
    }

    private void QueueMessage(string message) => MessageQueue.Enqueue(message);

    public void ProcessMessageQueue()
    {
        if (MessageQueue.Count > 0 &&
            (DateTime.Now - LastMessage).TotalMilliseconds >= CurrentTable.MessageDelayMs)
        {
            SendMessage(MessageQueue.Dequeue());
            LastMessage = DateTime.Now;
        }
    }

    private string DN(string name) => CurrentTable.GetDisplayName(name);

    // ── Shooter ────────────────────────────────────────────────────────────────

    public bool IsCurrentShooter(string playerName) =>
        !string.IsNullOrEmpty(CurrentTable.CrapsShooterName) &&
        CurrentTable.CrapsShooterName.Equals(playerName, StringComparison.OrdinalIgnoreCase);

    private void AdvanceShooter()
    {
        var active = CurrentTable.Players.Values.Where(p => !p.IsAfk).ToList();
        if (active.Count == 0) { CurrentTable.CrapsShooterName = string.Empty; return; }
        if (string.IsNullOrEmpty(CurrentTable.CrapsShooterName))
        {
            CurrentTable.CrapsShooterName = active[0].Name;
            return;
        }
        int idx = active.FindIndex(p =>
            p.Name.Equals(CurrentTable.CrapsShooterName, StringComparison.OrdinalIgnoreCase));
        CurrentTable.CrapsShooterName = active[(idx + 1) % active.Count].Name;
    }

    // ── Betting Phase ──────────────────────────────────────────────────────────

    public void StartBettingPhase()
    {
        if (string.IsNullOrEmpty(CurrentTable.CrapsShooterName)) AdvanceShooter();
        CurrentTable.CrapsBettingPhase = true;
        CurrentTable.CrapsBettingStart = DateTime.Now;
        _betTimerWarned = false;

        string betHint = CurrentTable.CrapsPhase == CrapsPhase.WaitingForBets
            ? ">BET PASS [amt]  >BET DONTPASS [amt]"
            : ">BET FIELD [amt]  >BET BIG6/BIG8 [amt]  >BET PLACE [4/5/6/8/9/10] [amt]";
        string shooter = string.IsNullOrEmpty(CurrentTable.CrapsShooterName)
            ? "Dealer"
            : CurrentTable.CrapsShooterName;
        QueueMessage($"[CRAPS] Bets open ({CurrentTable.TurnTimeLimit}s) — {betHint} | Shooter: {shooter} (>ROLL)");
    }

    public void ProcessBettingTimer()
    {
        if (!CurrentTable.CrapsBettingPhase || CurrentTable.CrapsRolling) return;
        double elapsed = (DateTime.Now - CurrentTable.CrapsBettingStart).TotalMilliseconds;
        double limitMs = CurrentTable.TurnTimeLimit * 1000.0;

        if (!_betTimerWarned && elapsed >= limitMs * 0.6)
        {
            _betTimerWarned = true;
            int secs = Math.Max(1, (int)Math.Ceiling((limitMs - elapsed) / 1000.0));
            QueueMessage($"[CRAPS] {secs}s left — {CurrentTable.CrapsShooterName} shooting soon!");
        }

        if (elapsed >= limitMs)
        {
            CurrentTable.CrapsBettingPhase = false;
            StartRoll(out _);
        }
    }

    public int GetBettingSecondsRemaining()
    {
        if (!CurrentTable.CrapsBettingPhase) return 0;
        double elapsed = (DateTime.Now - CurrentTable.CrapsBettingStart).TotalMilliseconds;
        return Math.Max(0, (int)Math.Ceiling((CurrentTable.TurnTimeLimit * 1000.0 - elapsed) / 1000.0));
    }

    // ── Bet Placement ──────────────────────────────────────────────────────────

    public bool PlaceBet(string playerName, string betType, int amount, out string error, int placeNumber = 0)
    {
        error = string.Empty;
        if (CurrentTable.CrapsRolling) { error = "No bets while dice are rolling."; return false; }

        var player = GetPlayer(playerName);
        if (player == null) { error = $"{playerName} is not at the table."; return false; }

        if (amount < CurrentTable.MinBet || amount > CurrentTable.MaxBet)
        { error = $"Bet must be {CurrentTable.MinBet}–{CurrentTable.MaxBet}\uE049."; return false; }

        if (player.Bank < amount) { error = $"Insufficient funds ({player.Bank}\uE049)."; return false; }

        string key = playerName.ToUpperInvariant();
        if (!CurrentTable.CrapsBets.TryGetValue(key, out var bets))
        { bets = new CrapsPlayerBets(); CurrentTable.CrapsBets[key] = bets; }

        switch (betType)
        {
            case "PASS":
                if (CurrentTable.CrapsPhase != CrapsPhase.WaitingForBets)
                { error = "Pass Line only before the come-out roll."; return false; }
                if (bets.PassLineBet > 0) { error = "Already have a Pass Line bet."; return false; }
                bets.PassLineBet = amount;
                break;
            case "DONTPASS":
                if (CurrentTable.CrapsPhase != CrapsPhase.WaitingForBets)
                { error = "Don't Pass only before the come-out roll."; return false; }
                if (bets.DontPassBet > 0) { error = "Already have a Don't Pass bet."; return false; }
                bets.DontPassBet = amount;
                break;
            case "FIELD":
                if (bets.FieldBet > 0) { error = "Already have a Field bet this roll."; return false; }
                bets.FieldBet = amount;
                break;
            case "BIG6":
                if (bets.Big6Bet > 0) { error = "Already have a Big 6 bet."; return false; }
                bets.Big6Bet = amount;
                break;
            case "BIG8":
                if (bets.Big8Bet > 0) { error = "Already have a Big 8 bet."; return false; }
                bets.Big8Bet = amount;
                break;
            case "PLACE":
                if (CurrentTable.CrapsPhase != CrapsPhase.PointEstablished)
                { error = "Place bets only after the point is established."; return false; }
                if (!new[] { 4, 5, 6, 8, 9, 10 }.Contains(placeNumber))
                { error = "Place bets must be on 4, 5, 6, 8, 9, or 10."; return false; }
                if (bets.PlaceBets.ContainsKey(placeNumber))
                { error = $"Already have a Place bet on {placeNumber}."; return false; }
                bets.PlaceBets[placeNumber] = amount;
                break;
            default:
                error = "Unknown bet. Use: PASS, DONTPASS, FIELD, BIG6, BIG8, PLACE [num].";
                return false;
        }

        player.Bank -= amount;
        player.IsAfk = false;
        string label = betType == "PLACE" ? $"Place {placeNumber}" : betType;
        SendMessage($"{DN(playerName)} bets {amount}\uE049 on {label}.");
        LogAction($"{playerName}: {label} {amount}\uE049");
        OnUIUpdate?.Invoke();
        return true;
    }

    // ── Roll ───────────────────────────────────────────────────────────────────

    public bool StartRoll(out string error)
    {
        error = string.Empty;
        if (CurrentTable.CrapsRolling) { error = "Already rolling."; return false; }

        CurrentTable.CrapsBettingPhase = false;

        if (CurrentTable.CrapsPhase == CrapsPhase.WaitingForBets)
        {
            bool hasBets = CurrentTable.CrapsBets.Values.Any(b => b.PassLineBet > 0 || b.DontPassBet > 0);
            if (!hasBets) { error = "At least one Pass/Don't Pass bet required to start."; return false; }
        }

        if (string.IsNullOrEmpty(CurrentTable.CrapsShooterName)) AdvanceShooter();

        _lastAnimFrame = -1;
        CurrentTable.CrapsRolling = true;
        CurrentTable.CrapsRollStart = DateTime.Now;
        CurrentTable.GameState = Models.GameState.Playing;

        string shooter = string.IsNullOrEmpty(CurrentTable.CrapsShooterName)
            ? "Dealer"
            : CurrentTable.CrapsShooterName;
        SendMessage($"{shooter} is rolling!");
        OnUIUpdate?.Invoke();
        return true;
    }

    public void ProcessRoll()
    {
        if (!CurrentTable.CrapsRolling) return;
        double elapsed = (DateTime.Now - CurrentTable.CrapsRollStart).TotalMilliseconds;

        long frame = (long)(elapsed / 150);
        if (frame != _lastAnimFrame)
        {
            _lastAnimFrame = frame;
            CurrentTable.CrapsDie1 = Rng.Next(1, 7);
            CurrentTable.CrapsDie2 = Rng.Next(1, 7);
            OnUIUpdate?.Invoke();
        }

        if (elapsed < RollDurationMs) return;

        int d1 = Rng.Next(1, 7), d2 = Rng.Next(1, 7);
        CurrentTable.CrapsDie1 = d1;
        CurrentTable.CrapsDie2 = d2;
        CurrentTable.CrapsRolling = false;
        CurrentTable.GameState = Models.GameState.Lobby;
        OnUIUpdate?.Invoke();
        Resolve(d1, d2);
    }

    // ── Resolution ─────────────────────────────────────────────────────────────

    private void Resolve(int d1, int d2)
    {
        int total = d1 + d2;
        _rollResults.Clear();
        QueueMessage($"Dice: {d1} + {d2} = {total}!");
        LogAction($"Dice: {d1}+{d2}={total}, phase={CurrentTable.CrapsPhase}");

        ResolveField(total);

        if (CurrentTable.CrapsPhase == CrapsPhase.WaitingForBets)
            ResolveComeOut(total);
        else
            ResolvePoint(total);

        SendPlayerSummaries();
    }

    private void ResolveField(int total)
    {
        bool wins = total is 2 or 3 or 4 or 9 or 10 or 11 or 12;
        bool doublesPay = total is 2 or 12;

        foreach (var kvp in CurrentTable.CrapsBets)
        {
            var bets = kvp.Value;
            if (bets.FieldBet <= 0) continue;
            var player = GetPlayerByKey(kvp.Key);
            if (player == null) continue;
            int bet = bets.FieldBet;
            bets.FieldBet = 0;
            if (wins)
            {
                int payout = doublesPay ? bet * 2 : bet;
                player.Bank += bet + payout;
                player.CrapsNetGains += payout;
                AddResult(kvp.Key, $"Field +{payout}\uE049");
            }
            else
            {
                player.CrapsNetGains -= bet;
                AddResult(kvp.Key, $"Field -{bet}\uE049");
            }
        }
    }

    private void ResolveComeOut(int total)
    {
        if (total is 7 or 11)
        {
            QueueMessage($"{total} — Natural! Pass Line wins, Don't Pass loses.");
            ResolvePassLine(true, false);
            ResetForNewComeOut();
            StartBettingPhase();
        }
        else if (total is 2 or 3)
        {
            QueueMessage($"{total} — Craps! Don't Pass wins, Pass Line loses.");
            ResolvePassLine(false, false);
            ResetForNewComeOut();
            StartBettingPhase();
        }
        else if (total == 12)
        {
            QueueMessage("12 — Craps! Pass Line loses, Don't Pass pushes.");
            ResolvePassLine(false, true);
            ResetForNewComeOut();
            StartBettingPhase();
        }
        else
        {
            CurrentTable.CrapsPoint = total;
            CurrentTable.CrapsPhase = CrapsPhase.PointEstablished;
            QueueMessage($"Point is {total}! Roll {total} to win, 7 to lose. Field/Big6/Big8/Place bets now open.");
            StartBettingPhase();
        }
    }

    private void ResolvePoint(int total)
    {
        if (total == CurrentTable.CrapsPoint)
        {
            QueueMessage($"{total} — Point made! Pass Line wins, Don't Pass loses.");
            ResolvePassLine(true, false);
            ResolveBig6Big8(false, total);
            ResolvePlaceBets(false, total);
            ResetForNewComeOut();
            StartBettingPhase();
        }
        else if (total == 7)
        {
            QueueMessage("Seven out! Don't Pass wins. Pass, Big 6/8, and Place bets lose.");
            ResolvePassLine(false, false);
            ResolveBig6Big8(true, total);
            ResolvePlaceBets(true, total);
            ResetRound();
            AdvanceShooter();
            string nextShooter = string.IsNullOrEmpty(CurrentTable.CrapsShooterName)
                ? "Dealer"
                : CurrentTable.CrapsShooterName;
            QueueMessage($"New shooter: {nextShooter}!");
            StartBettingPhase();
        }
        else
        {
            ResolveBig6Big8(false, total);
            ResolvePlaceBets(false, total);
            QueueMessage($"{total} — No decision. Point is still {CurrentTable.CrapsPoint}. Roll again!");
            StartBettingPhase();
        }
    }

    private void ResolvePassLine(bool passWins, bool dontPassPush)
    {
        foreach (var kvp in CurrentTable.CrapsBets)
        {
            var bets = kvp.Value;
            var player = GetPlayerByKey(kvp.Key);
            if (player == null) continue;

            if (bets.PassLineBet > 0)
            {
                if (passWins)
                { player.Bank += bets.PassLineBet * 2; player.CrapsNetGains += bets.PassLineBet; AddResult(kvp.Key, $"Pass +{bets.PassLineBet}\uE049"); }
                else
                { player.CrapsNetGains -= bets.PassLineBet; AddResult(kvp.Key, $"Pass -{bets.PassLineBet}\uE049"); }
            }

            if (bets.DontPassBet > 0)
            {
                if (dontPassPush)
                { player.Bank += bets.DontPassBet; AddResult(kvp.Key, "DP push"); }
                else if (!passWins)
                { player.Bank += bets.DontPassBet * 2; player.CrapsNetGains += bets.DontPassBet; AddResult(kvp.Key, $"DP +{bets.DontPassBet}\uE049"); }
                else
                { player.CrapsNetGains -= bets.DontPassBet; AddResult(kvp.Key, $"DP -{bets.DontPassBet}\uE049"); }
            }
        }
    }

    private void ResolveBig6Big8(bool sevenOut, int rolledNumber)
    {
        foreach (var kvp in CurrentTable.CrapsBets)
        {
            var bets = kvp.Value;
            var player = GetPlayerByKey(kvp.Key);
            if (player == null) continue;

            if (bets.Big6Bet > 0)
            {
                if (sevenOut)
                { player.CrapsNetGains -= bets.Big6Bet; AddResult(kvp.Key, $"Big6 -{bets.Big6Bet}\uE049"); bets.Big6Bet = 0; }
                else if (rolledNumber == 6)
                { player.Bank += bets.Big6Bet * 2; player.CrapsNetGains += bets.Big6Bet; AddResult(kvp.Key, $"Big6 +{bets.Big6Bet}\uE049"); bets.Big6Bet = 0; }
            }

            if (bets.Big8Bet > 0)
            {
                if (sevenOut)
                { player.CrapsNetGains -= bets.Big8Bet; AddResult(kvp.Key, $"Big8 -{bets.Big8Bet}\uE049"); bets.Big8Bet = 0; }
                else if (rolledNumber == 8)
                { player.Bank += bets.Big8Bet * 2; player.CrapsNetGains += bets.Big8Bet; AddResult(kvp.Key, $"Big8 +{bets.Big8Bet}\uE049"); bets.Big8Bet = 0; }
            }
        }
    }

    private void ResolvePlaceBets(bool sevenOut, int rolledNumber)
    {
        foreach (var kvp in CurrentTable.CrapsBets)
        {
            var bets = kvp.Value;
            var player = GetPlayerByKey(kvp.Key);
            if (player == null) continue;

            foreach (var num in bets.PlaceBets.Keys.ToList())
            {
                int amt = bets.PlaceBets[num];
                if (sevenOut)
                { player.CrapsNetGains -= amt; AddResult(kvp.Key, $"Place{num} -{amt}\uE049"); bets.PlaceBets.Remove(num); }
                else if (rolledNumber == num)
                {
                    int pay = GetPlacePayout(num, amt);
                    player.Bank += amt + pay;
                    player.CrapsNetGains += pay;
                    AddResult(kvp.Key, $"Place{num} +{pay}\uE049");
                    bets.PlaceBets.Remove(num);
                }
            }
        }
    }

    private static int GetPlacePayout(int n, int amt) => n switch
    {
        4 or 10 => amt * 9 / 5,
        5 or 9  => amt * 7 / 5,
        6 or 8  => amt * 7 / 6,
        _       => amt
    };

    private void AddResult(string key, string result)
    {
        if (!_rollResults.TryGetValue(key, out var list)) { list = new List<string>(); _rollResults[key] = list; }
        list.Add(result);
    }

    private void SendPlayerSummaries()
    {
        foreach (var kvp in _rollResults)
        {
            var p = GetPlayerByKey(kvp.Key);
            if (p == null) continue;
            QueueMessage($"{DN(p.Name)}: {string.Join(" | ", kvp.Value)} → Bank: {p.Bank}\uE049");
        }
    }

    // ── Reset Helpers ──────────────────────────────────────────────────────────

    private void ResetForNewComeOut()
    {
        CurrentTable.CrapsPhase = CrapsPhase.WaitingForBets;
        CurrentTable.CrapsPoint = 0;
        foreach (var b in CurrentTable.CrapsBets.Values)
        { b.PassLineBet = 0; b.DontPassBet = 0; }
    }

    private void ResetRound()
    {
        CurrentTable.CrapsPhase = CrapsPhase.WaitingForBets;
        CurrentTable.CrapsPoint = 0;
        foreach (var b in CurrentTable.CrapsBets.Values)
        { b.PassLineBet = 0; b.DontPassBet = 0; b.FieldBet = 0; b.Big6Bet = 0; b.Big8Bet = 0; b.PlaceBets.Clear(); }
    }

    // ── Player Lookup ──────────────────────────────────────────────────────────

    public Player? GetPlayer(string name) =>
        CurrentTable.Players.Values.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private Player? GetPlayerByKey(string key) =>
        CurrentTable.Players.TryGetValue(key, out var p)
            ? p
            : CurrentTable.Players.Values.FirstOrDefault(v => v.Name.ToUpperInvariant() == key);

    public void ForceStop()
    {
        MessageQueue.Clear();
        QueueMessage("Game force stopped by dealer. All bets refunded.");
        LogAction("Game force stopped - refunding all bets");

        foreach (var kvp in CurrentTable.CrapsBets)
        {
            var bets = kvp.Value;
            var player = GetPlayerByKey(kvp.Key);
            if (player == null) continue;

            int refund = bets.PassLineBet + bets.DontPassBet + bets.FieldBet
                       + bets.Big6Bet + bets.Big8Bet + bets.PlaceBets.Values.Sum();
            if (refund > 0)
            {
                player.Bank += refund;
                QueueMessage($"{DN(player.Name)}: {refund}\uE049 refunded \u2192 Bank: {player.Bank}\uE049");
            }
        }

        foreach (var b in CurrentTable.CrapsBets.Values)
        { b.PassLineBet = 0; b.DontPassBet = 0; b.FieldBet = 0; b.Big6Bet = 0; b.Big8Bet = 0; b.PlaceBets.Clear(); }

        CurrentTable.CrapsPhase = CrapsPhase.WaitingForBets;
        CurrentTable.CrapsPoint = 0;
        CurrentTable.CrapsRolling = false;
        CurrentTable.CrapsBettingPhase = false;
        CurrentTable.GameState = Models.GameState.Lobby;
        OnUIUpdate?.Invoke();
    }

    private void LogAction(string a) =>
        CurrentTable.GameLog.Add($"[{DateTime.Now:HH:mm:ss}] [CRAPS] {a}");
}
