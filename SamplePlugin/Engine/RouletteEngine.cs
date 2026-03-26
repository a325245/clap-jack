using System;
using System.Collections.Generic;
using System.Linq;
using SamplePlugin.Models;

namespace SamplePlugin.Engine;

public class RouletteEngine
{
    public Models.Table CurrentTable { get; set; }
    public Models.ChatMode ChatMode { get; set; } = Models.ChatMode.Say;

    public Action<string>? OnChatMessage { get; set; }
    public Action<string, string>? OnPlayerTell { get; set; }
    public Action? OnUIUpdate { get; set; }

    private Queue<string> MessageQueue { get; set; } = new();
    private DateTime LastMessage { get; set; } = DateTime.MinValue;

    private static readonly Random Rng = new();

    public RouletteEngine(Models.Table table)
    {
        CurrentTable = table;
    }

    // ── Messaging ─────────────────────────────────────────────────────────────

    private void SendMessage(string message)
    {
        string chatCommand = ChatMode == Models.ChatMode.Party ? $"/party {message}" : $"/say {message}";
        OnChatMessage?.Invoke(chatCommand);
        LastMessage = DateTime.Now; // keep queue in sync
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

    // ── Spin Logic ────────────────────────────────────────────────────────────

    public bool StartSpin(string dealerName, out string error)
    {
        error = string.Empty;

        if (CurrentTable.GameState != Models.GameState.Lobby)
        {
            error = "A game is already in progress.";
            return false;
        }

        var bettors = CurrentTable.Players.Values
            .Where(p => !p.IsAfk && p.RouletteBets.Count > 0)
            .ToList();

        if (bettors.Count == 0)
        {
            error = "No players have placed bets.";
            return false;
        }

        CurrentTable.RouletteSpinState = RouletteSpinState.Spinning;
        CurrentTable.GameState = Models.GameState.Playing;
        CurrentTable.RouletteSpinStart = DateTime.Now;

        LogAction("Roulette spin started");
        SendMessage("No more bets! The wheel is spinning...");
        OnUIUpdate?.Invoke();

        return true;
    }

    // Called every frame from the plugin timer - resolves after 4-second delay
    public void ProcessSpin()
    {
        if (CurrentTable.RouletteSpinState != RouletteSpinState.Spinning) return;
        if ((DateTime.Now - CurrentTable.RouletteSpinStart).TotalMilliseconds < 4000) return;

        // Generate result
        int result = Rng.Next(0, 37); // 0-36 inclusive
        CurrentTable.RouletteResult = result;
        CurrentTable.RouletteSpinState = RouletteSpinState.Resolving;

        ResolveSpin(result);
    }

    private void ResolveSpin(int result)
    {
        string color = GetColor(result);
        string colorLabel = color == "GREEN" ? "🟢" : color == "RED" ? "🔴" : "⚫";

        // Queue the result so payouts are naturally separated by MessageDelayMs
        QueueMessage($"The ball lands on: {colorLabel} {result} {color}!");
        LogAction($"Roulette result: {result} ({color})");

        var resultLines = new List<string>();

        foreach (var player in CurrentTable.Players.Values.Where(p => !p.IsAfk && p.RouletteBets.Count > 0))
        {
            int bankBefore = player.Bank; // Bank already had bets deducted at placement time
            int won = 0;
            var betBreakdowns = new List<string>();

            foreach (var bet in player.RouletteBets)
            {
                if (bet.IsWinner(result))
                {
                    int payout = bet.Payout();
                    won += payout;
                    int mult = bet.Type == "INSIDE" ? 36 : 2;
                    betBreakdowns.Add($"{bet.Amount}G\u00d7{bet.Target}(\u00d7{mult})={payout}G");
                }
                // misses are not reported
            }

            int totalRisked = player.RouletteBets.Sum(b => b.Amount);
            int net = won - totalRisked;
            string netStr = net >= 0 ? $"+{net}G" : $"{net}G";
            string breakdown = string.Join(" | ", betBreakdowns);
            int bankStart = bankBefore + totalRisked; // what bank was before bets were placed

            if (won > 0)
                player.Bank += won;

            player.RouletteNetGains += net;
            int bankNow = player.Bank;

            resultLines.Add($"Payouts | {player.Name}: {breakdown} | net {netStr} | Bank: {bankStart}G \u2192 {bankNow}G");

            LogAction($"{player.Name}: won={won}G risked={totalRisked}G net={netStr} bank {bankStart}->{bankNow}");
            player.RouletteBets.Clear();
        }

        // Queue each player's payout as its own line, staggered by the message delay
        foreach (var line in resultLines)
            QueueMessage(line);

        CurrentTable.RouletteSpinState = RouletteSpinState.Idle;
        CurrentTable.GameState = Models.GameState.Lobby;
        OnUIUpdate?.Invoke();
    }

    // ── Bet Placement ─────────────────────────────────────────────────────────

    public bool PlaceBet(string playerName, int amount, string targetsRaw, out string error)
    {
        error = string.Empty;

        if (CurrentTable.GameState != Models.GameState.Lobby)
        {
            error = "Bets can only be placed before the spin.";
            return false;
        }

        var player = GetPlayer(playerName);
        if (player == null) { error = $"{playerName} is not at the table."; return false; }

        if (amount < CurrentTable.MinBet || amount > CurrentTable.MaxBet)
        {
            error = $"Bet must be between {CurrentTable.MinBet} and {CurrentTable.MaxBet}.";
            return false;
        }

        // Parse targets
        var rawTargets = targetsRaw.Split(',')
            .Select(t => t.Trim().ToUpperInvariant())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        var outsideTargets = rawTargets.Where(t => t == "RED" || t == "BLACK" || t == "EVEN" || t == "ODD").ToList();
        var insideTargets = rawTargets
            .Where(t => int.TryParse(t, out int n) && n >= 0 && n <= 36 && t.TrimStart('0').Length <= t.Length && !(t.Length > 1 && t.All(c => c == '0')))
            .ToList();

        if (outsideTargets.Count == 0 && insideTargets.Count == 0)
        {
            error = "Invalid targets. Use numbers 0-36, RED, BLACK, EVEN, or ODD.";
            return false;
        }

        if (insideTargets.Count > 6)
        {
            error = "Maximum of 6 inside number bets allowed per spin.";
            return false;
        }

        int totalCost = amount * (outsideTargets.Count + insideTargets.Count);

        if (player.Bank < totalCost)
        {
            error = $"Insufficient funds. Need {totalCost}G, have {player.Bank}G.";
            return false;
        }

        // Deduct and place bets
        player.Bank -= totalCost;
        player.IsAfk = false;

        foreach (var t in outsideTargets)
            player.RouletteBets.Add(new RouletteBet { Type = "OUTSIDE", Target = t, Amount = amount });

        foreach (var t in insideTargets)
            player.RouletteBets.Add(new RouletteBet { Type = "INSIDE", Target = t, Amount = amount });

        var allTargets = outsideTargets.Concat(insideTargets);
        SendMessage($"{playerName} risks {totalCost}G on {string.Join(", ", allTargets)}.");
        LogAction($"{playerName} bet {totalCost}G on {string.Join(", ", allTargets)}");
        OnUIUpdate?.Invoke();
        return true;
    }

    public void ClearPlayerBets(string playerName)
    {
        var player = GetPlayer(playerName);
        if (player == null || CurrentTable.GameState != Models.GameState.Lobby) return;

        // Refund all bets
        int refund = player.RouletteBets.Sum(b => b.Amount);
        player.Bank += refund;
        player.RouletteBets.Clear();

        SendMessage($"{playerName} bets cleared. {refund}G refunded.");
        OnUIUpdate?.Invoke();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    public static string GetColor(int number)
    {
        if (number == 0) return "GREEN";
        return RouletteBet.IsRed(number) ? "RED" : "BLACK";
    }

    public Models.Player? GetPlayer(string name) =>
        CurrentTable.Players.Values.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void ForceStop()
    {
        MessageQueue.Clear();
        QueueMessage("Game force stopped by dealer. All bets refunded.");
        LogAction("Game force stopped - refunding all bets");

        foreach (var player in CurrentTable.Players.Values)
        {
            int refund = player.RouletteBets.Sum(b => b.Amount);
            if (refund > 0)
            {
                player.Bank += refund;
                QueueMessage($"{player.Name}: {refund}G refunded \u2192 Bank: {player.Bank}G");
            }
            player.RouletteBets.Clear();
        }

        CurrentTable.RouletteSpinState = RouletteSpinState.Idle;
        CurrentTable.GameState = Models.GameState.Lobby;
        OnUIUpdate?.Invoke();
    }

    private void LogAction(string action)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        CurrentTable.GameLog.Add($"[{timestamp}] [ROULETTE] {action}");
    }
}
