using System;
using System.Collections.Generic;
using System.Linq;
using SamplePlugin.Engine;
using SamplePlugin.Models;
using SamplePlugin.Chat;

namespace SamplePlugin.Commands;

public class CommandParser
{
    private BlackjackEngine engine;
    private RouletteEngine rouletteEngine;

    public Action<string>? OnChatMessage { get; set; }
    public Action<string, string>? OnPlayerTell { get; set; }
    public Action<string>? OnAdminEcho { get; set; }

    public CommandParser(BlackjackEngine engine, RouletteEngine rouletteEngine)
    {
        this.engine = engine;
        this.rouletteEngine = rouletteEngine;
    }

    public void Parse(string senderName, string text, string adminName, DealerMode mode, ChatChannel sourceChannel)
    {
        if (!text.StartsWith(">")) return;

        var parts = text.Substring(1).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        string command = parts[0].ToUpperInvariant();
        bool isAdmin = senderName.Equals(adminName, StringComparison.OrdinalIgnoreCase);

        if (isAdmin)
            HandleAdminCommand(senderName, command, parts, mode, sourceChannel);
        else
            HandlePlayerCommand(senderName, command, parts, mode, sourceChannel);
    }

    public void Parse(string senderName, string text, string adminName, DealerMode mode)
        => Parse(senderName, text, adminName, mode, ChatChannel.Say);

    private void SendResponse(string message, ChatChannel channel)
    {
        string cmd = channel == ChatChannel.Party ? $"/party {message}" : $"/say {message}";
        OnChatMessage?.Invoke(cmd);
    }

    // ── Player Commands ────────────────────────────────────────────────────────

    private void HandlePlayerCommand(string playerName, string command, string[] parts, DealerMode mode, ChatChannel sourceChannel)
    {
        // ── Roulette: > BET [amt] ON [targets] ────────────────────────────────
        if (command == "BET" && parts.Length >= 4 && parts[1].ToUpperInvariant() == "ON" == false)
        {
            // Format: BET 50 ON RED, EVEN
            if (parts.Length >= 3 &&
                int.TryParse(parts[1], out int betAmt) &&
                parts[2].Equals("ON", StringComparison.OrdinalIgnoreCase))
            {
                if (engine.CurrentTable.GameType == GameType.Roulette)
                {
                    string targets = string.Join(",", parts.Skip(3));
                    if (rouletteEngine.PlaceBet(playerName, betAmt, targets, out string err))
                        return;
                    else
                        SendResponse(err, sourceChannel);
                    return;
                }

                // Blackjack bet change: > BET 50 ON is invalid for blackjack; fall through to > BET below
            }
        }

        switch (command)
        {
            case "HELP":
            case "RULES":
                var player = engine.CurrentTable.Players.Values.FirstOrDefault(p =>
                    p.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
                if (player != null)
                    OnPlayerTell?.Invoke($"{playerName}@{player.Server}", GetRulesText(engine.CurrentTable));
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
                var afkP = engine.CurrentTable.Players.Values.FirstOrDefault(p =>
                    p.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
                SendResponse($"{playerName} is now {(afkP?.IsAfk == true ? "AFK" : "ACTIVE")}", sourceChannel);
                break;

            case "BET":
                // Blackjack: > BET [amount]
                if (engine.CurrentTable.GameType == GameType.Blackjack &&
                    engine.CurrentTable.GameState == Models.GameState.Lobby &&
                    parts.Length > 1 && int.TryParse(parts[1], out int bjAmt))
                {
                    engine.SetPlayerBet(playerName, bjAmt, sourceChannel);
                }
                break;

            case "HIT":
                if (mode == DealerMode.Manual)
                    OnPlayerTell?.Invoke($"{playerName}@Local", "Table is in Manual Mode.");
                else if (engine.CurrentTable.GameState == Models.GameState.Playing)
                    engine.PlayerHit(playerName);
                break;

            case "STAND":
                if (mode == DealerMode.Manual)
                    OnPlayerTell?.Invoke($"{playerName}@Local", "Table is in Manual Mode.");
                else if (engine.CurrentTable.GameState == Models.GameState.Playing)
                    engine.PlayerStand(playerName);
                break;

            case "DOUBLE":
                if (mode == DealerMode.Manual)
                    OnPlayerTell?.Invoke($"{playerName}@Local", "Table is in Manual Mode.");
                else if (engine.CurrentTable.GameState == Models.GameState.Playing)
                    engine.PlayerDouble(playerName);
                break;

            case "SPLIT":
                if (mode == DealerMode.Manual)
                    OnPlayerTell?.Invoke($"{playerName}@Local", "Table is in Manual Mode.");
                else if (engine.CurrentTable.GameState == Models.GameState.Playing)
                    engine.PlayerSplit(playerName);
                break;

            case "INSURANCE":
                if (engine.CurrentTable.GameState == Models.GameState.Playing)
                    engine.PlayerInsurance(playerName);
                break;
        }
    }

    // ── Admin Commands ─────────────────────────────────────────────────────────

    private void HandleAdminCommand(string admin, string command, string[] parts, DealerMode mode, ChatChannel sourceChannel)
    {
        // ── Roulette admin: > SPIN ─────────────────────────────────────────────
        if (command == "SPIN" && engine.CurrentTable.GameType == GameType.Roulette)
        {
            if (rouletteEngine.StartSpin(admin, out string err))
                return;
            else
                SendResponse(err, sourceChannel);
            return;
        }

        // ── Roulette admin: > PLAYER [name] BET [amt] ON [targets] ────────────
        if (command == "PLAYER" && parts.Length >= 6 && engine.CurrentTable.GameType == GameType.Roulette)
        {
            // Find where "BET" is in parts to split name from bet
            int betIdx = Array.FindIndex(parts, 1, p => p.Equals("BET", StringComparison.OrdinalIgnoreCase));
            if (betIdx > 1 &&
                betIdx + 3 < parts.Length &&
                int.TryParse(parts[betIdx + 1], out int amt) &&
                parts[betIdx + 2].Equals("ON", StringComparison.OrdinalIgnoreCase))
            {
                string pName = string.Join(" ", parts.Skip(1).Take(betIdx - 1));
                string targets = string.Join(",", parts.Skip(betIdx + 3));
                if (!rouletteEngine.PlaceBet(pName, amt, targets, out string err))
                    SendResponse(err, sourceChannel);
                return;
            }
        }

        // ── General admin commands ─────────────────────────────────────────────
        switch (command)
        {
            case "GAME":
                if (parts.Length > 1 && parts[1].ToUpperInvariant() == "LOBBY")
                {
                    engine.CurrentTable.GameState = Models.GameState.Lobby;
                    SendResponse("Game reset to lobby", sourceChannel);
                }
                break;
        }
    }

    private string GetRulesText(Table table) =>
        $"Min Bet: {table.MinBet}, Max Bet: {table.MaxBet}. BJ: >HIT >STAND >DOUBLE >SPLIT >INSURANCE >BET [amt]. Roulette: >BET [amt] ON [targets] >BANK >AFK >HELP";
}

