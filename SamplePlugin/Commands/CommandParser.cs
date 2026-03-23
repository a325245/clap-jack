using System;
using System.Collections.Generic;
using System.Linq;
using SamplePlugin.Engine;
using SamplePlugin.Models;

namespace SamplePlugin.Commands;

public class CommandParser
{
    private BlackjackEngine engine;
    public Action<string>? OnChatMessage { get; set; }
    public Action<string, string>? OnPlayerTell { get; set; }  // (name@server, message)
    public Action<string>? OnAdminEcho { get; set; }

    public CommandParser(BlackjackEngine engine)
    {
        this.engine = engine;
    }

    public void Parse(string senderName, string text, string adminName, DealerMode mode)
    {
        if (!text.StartsWith(">")) return;

        var parts = text.Substring(1).Trim().Split(' ');
        if (parts.Length == 0) return;

        string command = parts[0].ToUpper();

        // Admin commands
        if (senderName.Equals(adminName, StringComparison.OrdinalIgnoreCase))
        {
            HandleAdminCommand(senderName, command, parts, text, mode);
        }
        else
        {
            HandlePlayerCommand(senderName, command, parts, text, mode);
        }
    }

    private void HandleAdminCommand(string admin, string command, string[] parts, string fullText, DealerMode mode)
    {
        string broadcastTarget = mode == DealerMode.Manual ? "/party" : "/echo";

        switch (command)
        {
            case "GAME":
                if (parts.Length > 1 && parts[1].ToUpper() == "LOBBY")
                {
                    engine.CurrentTable.GameState = Models.GameState.Lobby;
                    engine.CurrentTable.Players.Clear();
                    foreach (var p in engine.CurrentTable.Players.Values)
                    {
                        p.Hands.Clear();
                        p.CurrentBets.Clear();
                    }
                    engine.CurrentTable.DealerHand.Clear();
                    BroadcastAdmin($"Game reset to Lobby.", broadcastTarget);
                }
                break;

            case "DEAL":
                engine.StartGame();
                break;

            case "UNDO":
                engine.Undo();
                BroadcastAdmin("Game state undone.", broadcastTarget);
                break;

            case "TABLE":
                if (parts.Length > 1 && parts[1].ToUpper() == "STATUS")
                {
                    DisplayTableStatus();
                }
                break;

            case "POT":
                if (parts.Length > 2 && parts[1].ToUpper() == "LIMIT")
                {
                    if (int.TryParse(parts[2], out int min) && parts.Length > 3 && int.TryParse(parts[3], out int max))
                    {
                        engine.CurrentTable.MinBet = min;
                        engine.CurrentTable.MaxBet = max;
                        BroadcastAdmin($"Pot limits set: {min}-{max}", broadcastTarget);
                    }
                }
                break;

            case "TIME":
                if (parts.Length > 2 && parts[1].ToUpper() == "LIMIT")
                {
                    if (int.TryParse(parts[2], out int seconds))
                    {
                        engine.CurrentTable.TurnTimeLimit = seconds;
                        BroadcastAdmin($"Turn timer set to {seconds}s", broadcastTarget);
                    }
                }
                break;

            case "PLAYER":
                HandlePlayerManagement(admin, parts, fullText, broadcastTarget);
                break;

            case "KICK":
                if (parts.Length > 1)
                {
                    string playerName = string.Join(" ", parts.Skip(1));
                    engine.RemovePlayer(playerName);
                    BroadcastAdmin($"Kicked {playerName}", broadcastTarget);
                }
                break;

            case "AFK":
                if (parts.Length > 1)
                {
                    string playerName = string.Join(" ", parts.Skip(1));
                    engine.ToggleAFK(playerName);
                    BroadcastAdmin($"Toggled AFK for {playerName}", broadcastTarget);
                }
                break;

            case "HIT":
            case "STAND":
            case "DOUBLE":
            case "SPLIT":
                if (engine.CurrentTable.GameState == Models.GameState.Playing)
                {
                    HandleDealerAction(command);
                }
                break;
        }
    }

    private void HandlePlayerManagement(string admin, string[] parts, string fullText, string broadcastTarget)
    {
        if (parts.Length < 2) return;

        string subcommand = parts[1].ToUpper();

        switch (subcommand)
        {
            case "ADD":
                if (parts.Length > 2)
                {
                    string playerName = string.Join(" ", parts.Skip(2));
                    engine.AddPlayer(playerName);
                    BroadcastAdmin($"Added player: {playerName}", broadcastTarget);
                }
                break;

            case "REMOVE":
                if (parts.Length > 2)
                {
                    string playerName = string.Join(" ", parts.Skip(2));
                    engine.RemovePlayer(playerName);
                    BroadcastAdmin($"Removed player: {playerName}", broadcastTarget);
                }
                break;

            case "BANK":
                if (parts.Length > 3)
                {
                    // Extract player name and amount
                    var allParts = parts.Skip(2).ToArray();
                    if (allParts.Length >= 2 && int.TryParse(allParts[allParts.Length - 1], out int amount))
                    {
                        string playerName = string.Join(" ", allParts.Take(allParts.Length - 1));
                        engine.AddPlayerBank(playerName, amount);
                        BroadcastAdmin($"Added {amount} to {playerName}'s bank", broadcastTarget);
                    }
                }
                break;

            case "SETBANK":
                if (parts.Length > 3)
                {
                    var allParts = parts.Skip(2).ToArray();
                    if (allParts.Length >= 2 && int.TryParse(allParts[allParts.Length - 1], out int amount))
                    {
                        string playerName = string.Join(" ", allParts.Take(allParts.Length - 1));
                        engine.SetPlayerBank(playerName, amount);
                        BroadcastAdmin($"Set {playerName}'s bank to {amount}", broadcastTarget);
                    }
                }
                break;

            case "SETBET":
                if (parts.Length > 3)
                {
                    var allParts = parts.Skip(2).ToArray();
                    if (allParts.Length >= 2 && int.TryParse(allParts[allParts.Length - 1], out int amount))
                    {
                        string playerName = string.Join(" ", allParts.Take(allParts.Length - 1));
                        var player = engine.CurrentTable.Players.Values.FirstOrDefault(p => 
                            p.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
                        if (player != null)
                        {
                            player.PersistentBet = amount;
                            BroadcastAdmin($"Set {playerName}'s bet to {amount}", broadcastTarget);
                        }
                    }
                }
                break;

            case "RENAME":
                if (parts.Length > 3)
                {
                    var allParts = parts.Skip(2).ToArray();
                    string oldName = string.Join(" ", allParts.Take(allParts.Length - 1));
                    string newName = allParts[allParts.Length - 1];
                    RenamePlayer(oldName, newName);
                    BroadcastAdmin($"Renamed {oldName} to {newName}", broadcastTarget);
                }
                break;

            case "SERVER":
                if (parts.Length > 3)
                {
                    var allParts = parts.Skip(2).ToArray();
                    string playerName = string.Join(" ", allParts.Take(allParts.Length - 1));
                    string server = allParts[allParts.Length - 1];
                    var player = engine.CurrentTable.Players.Values.FirstOrDefault(p => 
                        p.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
                    if (player != null)
                    {
                        player.Server = server;
                        BroadcastAdmin($"Set {playerName}'s server to {server}", broadcastTarget);
                    }
                }
                break;
        }
    }

    private void HandlePlayerCommand(string playerName, string command, string[] parts, string fullText, DealerMode mode)
    {
        switch (command)
        {
            case "HELP":
            case "RULES":
                var player = engine.CurrentTable.Players.Values.FirstOrDefault(p => 
                    p.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
                if (player != null)
                {
                    string rules = GetRulesText(engine.CurrentTable);
                    OnPlayerTell?.Invoke($"{playerName}@{player.Server}", rules);
                }
                break;

            case "BANK":
                player = engine.CurrentTable.Players.Values.FirstOrDefault(p => 
                    p.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
                if (player != null)
                {
                    string betInfo = player.PersistentBet > 0 ? $"Bet: {player.PersistentBet}" : "No bet placed";
                    OnPlayerTell?.Invoke($"{playerName}@{player.Server}", $"Bank: {player.Bank} | {betInfo}");
                }
                break;

            case "AFK":
                engine.ToggleAFK(playerName);
                OnChatMessage?.Invoke($"[BLACKJACK] {playerName} is now {(engine.CurrentTable.Players.Values.FirstOrDefault(p => p.Name == playerName)?.IsAfk == true ? "AFK" : "ACTIVE")}");
                break;

            case "BET":
                if (engine.CurrentTable.GameState == Models.GameState.Lobby && parts.Length > 1)
                {
                    if (int.TryParse(parts[1], out int amount))
                    {
                        engine.SetPlayerBet(playerName, amount);
                        OnChatMessage?.Invoke($"[BLACKJACK] {playerName} bets {amount}");
                    }
                }
                break;

            case "HIT":
                if (mode == DealerMode.Manual)
                {
                    OnPlayerTell?.Invoke($"{playerName}@Local", "Table is in Manual Mode. Dealer will handle actions.");
                }
                else if (engine.CurrentTable.GameState == Models.GameState.Playing)
                {
                    engine.PlayerHit(playerName);
                }
                break;

            case "STAND":
                if (mode == DealerMode.Manual)
                {
                    OnPlayerTell?.Invoke($"{playerName}@Local", "Table is in Manual Mode. Dealer will handle actions.");
                }
                else if (engine.CurrentTable.GameState == Models.GameState.Playing)
                {
                    engine.PlayerStand(playerName);
                }
                break;

            case "DOUBLE":
                if (mode == DealerMode.Manual)
                {
                    OnPlayerTell?.Invoke($"{playerName}@Local", "Table is in Manual Mode. Dealer will handle actions.");
                }
                else if (engine.CurrentTable.GameState == Models.GameState.Playing)
                {
                    engine.PlayerDouble(playerName);
                }
                break;

            case "SPLIT":
                if (mode == DealerMode.Manual)
                {
                    OnPlayerTell?.Invoke($"{playerName}@Local", "Table is in Manual Mode. Dealer will handle actions.");
                }
                else if (engine.CurrentTable.GameState == Models.GameState.Playing)
                {
                    engine.PlayerSplit(playerName);
                }
                break;

            case "INSURANCE":
                if (engine.CurrentTable.GameState == Models.GameState.Playing)
                {
                    engine.PlayerInsurance(playerName);
                }
                break;
        }
    }

    private void HandleDealerAction(string action)
    {
        if (engine.CurrentTable.CurrentTurnIndex >= engine.CurrentTable.TurnOrder.Count) return;

        string currentPlayer = engine.CurrentTable.TurnOrder[engine.CurrentTable.CurrentTurnIndex];

        switch (action)
        {
            case "HIT":
                engine.PlayerHit(currentPlayer);
                break;
            case "STAND":
                engine.PlayerStand(currentPlayer);
                break;
            case "DOUBLE":
                engine.PlayerDouble(currentPlayer);
                break;
            case "SPLIT":
                engine.PlayerSplit(currentPlayer);
                break;
        }
    }

    private void RenamePlayer(string oldName, string newName)
    {
        string oldNameUpper = oldName.ToUpper();
        if (engine.CurrentTable.Players.ContainsKey(oldNameUpper))
        {
            var player = engine.CurrentTable.Players[oldNameUpper];
            engine.CurrentTable.Players.Remove(oldNameUpper);
            player.Name = newName;
            engine.CurrentTable.Players[newName.ToUpper()] = player;
        }
    }

    private void DisplayTableStatus()
    {
        var status = "[BLACKJACK] TABLE STATUS:\n";
        foreach (var player in engine.CurrentTable.Players.Values)
        {
            status += $"  {player.Name} ({player.Server}) - Bank: {player.Bank}, AFK: {player.IsAfk}\n";
        }
        OnChatMessage?.Invoke(status);
    }

    private void BroadcastAdmin(string message, string target)
    {
        if (target == "/party")
        {
            OnChatMessage?.Invoke($"/party [ADMIN] {message}");
        }
        else
        {
            OnAdminEcho?.Invoke(message);
        }
    }

    private string GetRulesText(Table table)
    {
        return $@"[BLACKJACK] RULES:
• Goal: Beat dealer without busting
• 21 = Blackjack (3:2 payout)
• Dealer hits on 16, stands on 17+
• Min Bet: {table.MinBet} | Max Bet: {table.MaxBet}";
    }
}
