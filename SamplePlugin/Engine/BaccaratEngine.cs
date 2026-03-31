using System;
using System.Collections.Generic;
using System.Linq;
using SamplePlugin.Models;

namespace SamplePlugin.Engine;

public class BaccaratEngine
{
    public Table CurrentTable { get; set; }
    public ChatMode ChatMode { get; set; } = ChatMode.Party;

    public Action<string>? OnChatMessage { get; set; }
    public Action<string, string>? OnPlayerTell { get; set; }
    public Action? OnUIUpdate { get; set; }

    private Queue<string> MessageQueue { get; } = new();
    private DateTime LastMessage { get; set; } = DateTime.MinValue;

    public BaccaratEngine(Table table)
    {
        CurrentTable = table;
    }

    // ── Messaging ─────────────────────────────────────────────────────────────

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

    // ── Bet Placement ─────────────────────────────────────────────────────────

    public bool PlaceBet(string playerName, string betType, int amount, out string error)
    {
        error = string.Empty;

        if (CurrentTable.BaccaratPhase != BaccaratPhase.WaitingForBets)
        {
            error = "Bets can only be placed before the deal.";
            return false;
        }

        var player = GetPlayer(playerName);
        if (player == null) { error = $"{playerName} is not at the table."; return false; }

        if (amount < CurrentTable.MinBet || amount > CurrentTable.MaxBet)
        {
            error = $"Bet must be between {CurrentTable.MinBet} and {CurrentTable.MaxBet}.";
            return false;
        }

        if (player.Bank < amount)
        {
            error = $"Insufficient funds. Need {amount}\uE049, have {player.Bank}\uE049.";
            return false;
        }

        string key = playerName.ToUpperInvariant();
        if (!CurrentTable.BaccaratBets.ContainsKey(key))
            CurrentTable.BaccaratBets[key] = new BaccaratBet();

        var bet = CurrentTable.BaccaratBets[key];

        switch (betType)
        {
            case "PLAYER":
                if (bet.PlayerBet > 0) { error = "You already have a Player bet."; return false; }
                bet.PlayerBet = amount;
                break;
            case "BANKER":
                if (bet.BankerBet > 0) { error = "You already have a Banker bet."; return false; }
                bet.BankerBet = amount;
                break;
            case "TIE":
                if (bet.TieBet > 0) { error = "You already have a Tie bet."; return false; }
                bet.TieBet = amount;
                break;
            default:
                error = "Invalid bet type. Use PLAYER, BANKER, or TIE.";
                return false;
        }

        player.Bank -= amount;
        player.IsAfk = false;
        SendMessage($"{DN(playerName)} bets {amount}\uE049 on {betType}.");
        LogAction($"{playerName} bet {amount}\uE049 on {betType}");
        OnUIUpdate?.Invoke();
        return true;
    }

    // ── Deal ──────────────────────────────────────────────────────────────────

    public bool Deal(out string error)
    {
        error = string.Empty;

        if (CurrentTable.BaccaratPhase != BaccaratPhase.WaitingForBets)
        {
            error = "A round is already in progress.";
            return false;
        }

        bool hasBets = CurrentTable.BaccaratBets.Values.Any(b =>
            b.PlayerBet > 0 || b.BankerBet > 0 || b.TieBet > 0);
        if (!hasBets) { error = "No bets placed."; return false; }

        CurrentTable.BaccaratPhase = BaccaratPhase.Dealing;
        CurrentTable.GameState = Models.GameState.Playing;
        CurrentTable.BaccaratPlayerHand.Clear();
        CurrentTable.BaccaratBankerHand.Clear();

        // Deal: P B P B
        CurrentTable.BaccaratPlayerHand.Add(CurrentTable.DrawCard());
        CurrentTable.BaccaratBankerHand.Add(CurrentTable.DrawCard());
        CurrentTable.BaccaratPlayerHand.Add(CurrentTable.DrawCard());
        CurrentTable.BaccaratBankerHand.Add(CurrentTable.DrawCard());

        int playerScore = GetBaccaratScore(CurrentTable.BaccaratPlayerHand);
        int bankerScore = GetBaccaratScore(CurrentTable.BaccaratBankerHand);

        string pHand = string.Join(" ", CurrentTable.BaccaratPlayerHand.Select(c => c.GetCardDisplay()));
        string bHand = string.Join(" ", CurrentTable.BaccaratBankerHand.Select(c => c.GetCardDisplay()));

        QueueMessage($"Player: {pHand} ({playerScore}) | Banker: {bHand} ({bankerScore})");
        LogAction($"Deal: Player={pHand}({playerScore}), Banker={bHand}({bankerScore})");

        // Natural: 8 or 9 — no third cards
        if (playerScore >= 8 || bankerScore >= 8)
        {
            QueueMessage("Natural! No third cards drawn.");
            Resolve(playerScore, bankerScore);
            return true;
        }

        // Player draws third card on 0-5
        bool playerDrewThird = false;
        int playerThirdValue = -1;
        if (playerScore <= 5)
        {
            var third = CurrentTable.DrawCard();
            CurrentTable.BaccaratPlayerHand.Add(third);
            playerThirdValue = GetBaccaratCardValue(third);
            playerScore = GetBaccaratScore(CurrentTable.BaccaratPlayerHand);
            QueueMessage($"Player draws {third.GetCardDisplay()} \u2192 {playerScore}");
            playerDrewThird = true;
        }

        // Banker third card based on standard rules
        if (ShouldBankerDraw(bankerScore, playerDrewThird, playerThirdValue))
        {
            var third = CurrentTable.DrawCard();
            CurrentTable.BaccaratBankerHand.Add(third);
            bankerScore = GetBaccaratScore(CurrentTable.BaccaratBankerHand);
            QueueMessage($"Banker draws {third.GetCardDisplay()} \u2192 {bankerScore}");
        }

        Resolve(playerScore, bankerScore);
        return true;
    }

    private static bool ShouldBankerDraw(int bankerScore, bool playerDrewThird, int playerThirdValue)
    {
        if (!playerDrewThird)
            return bankerScore <= 5;

        return bankerScore switch
        {
            0 or 1 or 2 => true,
            3 => playerThirdValue != 8,
            4 => playerThirdValue is >= 2 and <= 7,
            5 => playerThirdValue is >= 4 and <= 7,
            6 => playerThirdValue is 6 or 7,
            _ => false   // 7: stand
        };
    }

    private void Resolve(int playerScore, int bankerScore)
    {
        string winner = playerScore > bankerScore ? "PLAYER"
                      : bankerScore > playerScore ? "BANKER"
                      : "TIE";

        QueueMessage($"Final — Player: {playerScore} | Banker: {bankerScore} | {winner} wins!");
        LogAction($"Result: Player={playerScore}, Banker={bankerScore}, Winner={winner}");

        foreach (var kvp in CurrentTable.BaccaratBets.ToList())
        {
            var bet = kvp.Value;
            var player = CurrentTable.Players.Values.FirstOrDefault(p =>
                p.Name.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase));
            if (player == null) continue;

            var lines = new List<string>();
            int net = 0;

            // Player bet pays 1:1; pushed on TIE
            if (bet.PlayerBet > 0)
            {
                if (winner == "PLAYER")
                { player.Bank += bet.PlayerBet * 2; net += bet.PlayerBet; lines.Add($"Player +{bet.PlayerBet}\uE049"); }
                else if (winner == "TIE")
                { player.Bank += bet.PlayerBet; lines.Add("Player PUSH"); }
                else
                { net -= bet.PlayerBet; lines.Add($"Player -{bet.PlayerBet}\uE049"); }
            }

            // Banker bet pays 1:1 (no commission); pushed on TIE
            if (bet.BankerBet > 0)
            {
                if (winner == "BANKER")
                { player.Bank += bet.BankerBet * 2; net += bet.BankerBet; lines.Add($"Banker +{bet.BankerBet}\uE049"); }
                else if (winner == "TIE")
                { player.Bank += bet.BankerBet; lines.Add("Banker PUSH"); }
                else
                { net -= bet.BankerBet; lines.Add($"Banker -{bet.BankerBet}\uE049"); }
            }

            // Tie bet pays 8:1
            if (bet.TieBet > 0)
            {
                if (winner == "TIE")
                { player.Bank += bet.TieBet * 9; net += bet.TieBet * 8; lines.Add($"Tie +{bet.TieBet * 8}\uE049"); }
                else
                { net -= bet.TieBet; lines.Add($"Tie -{bet.TieBet}\uE049"); }
            }

            player.BaccaratNetGains += net;
            QueueMessage($"{DN(player.Name)}: {string.Join(" | ", lines)} \u2192 Bank: {player.Bank}\uE049");
        }

        CurrentTable.BaccaratPhase = BaccaratPhase.WaitingForBets;
        CurrentTable.GameState = Models.GameState.Lobby;
        CurrentTable.BaccaratBets.Clear();
        OnUIUpdate?.Invoke();
    }

    // ── Card helpers ──────────────────────────────────────────────────────────

    public static int GetBaccaratCardValue(Models.Card card) => card.Value switch
    {
        "A"                            => 1,
        "10" or "J" or "Q" or "K" => 0,
        _                              => int.Parse(card.Value)
    };

    public static int GetBaccaratScore(IEnumerable<Models.Card> hand)
        => hand.Sum(GetBaccaratCardValue) % 10;

    public Player? GetPlayer(string name) =>
        CurrentTable.Players.Values.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void ForceStop()
    {
        MessageQueue.Clear();
        QueueMessage("Game force stopped by dealer. All bets refunded.");
        LogAction("Game force stopped - refunding all bets");

        foreach (var kvp in CurrentTable.BaccaratBets)
        {
            var bet = kvp.Value;
            var player = CurrentTable.Players.Values.FirstOrDefault(p =>
                p.Name.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase));
            if (player == null) continue;

            int refund = bet.PlayerBet + bet.BankerBet + bet.TieBet;
            if (refund > 0)
            {
                player.Bank += refund;
                QueueMessage($"{player.Name}: {refund}\uE049 refunded \u2192 Bank: {player.Bank}\uE049");
            }
        }

        CurrentTable.BaccaratPhase = BaccaratPhase.WaitingForBets;
        CurrentTable.BaccaratPlayerHand.Clear();
        CurrentTable.BaccaratBankerHand.Clear();
        CurrentTable.BaccaratBets.Clear();
        CurrentTable.GameState = Models.GameState.Lobby;
        OnUIUpdate?.Invoke();
    }

    private void LogAction(string action)
    {
        string ts = DateTime.Now.ToString("HH:mm:ss");
        CurrentTable.GameLog.Add($"[{ts}] [BACCARAT] {action}");
    }
}
