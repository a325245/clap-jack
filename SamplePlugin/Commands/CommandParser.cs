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
    private CrapsEngine crapsEngine;
    private BaccaratEngine baccaratEngine;
    private ChocoboEngine chocoboEngine;
    private PokerEngine pokerEngine;

    public Action<string>? OnChatMessage { get; set; }
    public Action<string, string>? OnPlayerTell { get; set; }
    public Action<string>? OnAdminEcho { get; set; }

    public CommandParser(BlackjackEngine engine, RouletteEngine rouletteEngine,
                         CrapsEngine crapsEngine, BaccaratEngine baccaratEngine,
                         ChocoboEngine chocoboEngine, PokerEngine pokerEngine)
    {
        this.engine = engine;
        this.rouletteEngine = rouletteEngine;
        this.crapsEngine = crapsEngine;
        this.baccaratEngine = baccaratEngine;
        this.chocoboEngine = chocoboEngine;
        this.pokerEngine = pokerEngine;
    }

    public void Parse(string senderName, string text, string adminName, DealerMode mode, ChatChannel sourceChannel)
    {
        if (!text.StartsWith(">")) return;

        var parts = text.Substring(1).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        // No game active — ignore all commands
        if (engine.CurrentTable.GameType == GameType.None) return;

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
        // Always reply in the dealer's configured chat mode, not the player's incoming channel
        string cmd = engine.ChatMode == Models.ChatMode.Party ? $"/party {message}" : $"/say {message}";
        OnChatMessage?.Invoke(cmd);
    }

    // ── Player Commands ────────────────────────────────────────────────────────

    private void HandlePlayerCommand(string playerName, string command, string[] parts, DealerMode mode, ChatChannel sourceChannel)
    {
        // ── Texas Hold'Em player commands ─────────────────────────────────────
        if (engine.CurrentTable.GameType == GameType.TexasHoldEm)
        {
            switch (command)
            {
                case "FOLD":
                    if (!pokerEngine.PlayerFold(playerName, out string fErr))
                        SendResponse(fErr, sourceChannel);
                    return;
                case "CHECK":
                    if (!pokerEngine.PlayerCheck(playerName, out string chErr))
                        SendResponse(chErr, sourceChannel);
                    return;
                case "CALL":
                    if (!pokerEngine.PlayerCall(playerName, out string caErr))
                        SendResponse(caErr, sourceChannel);
                    return;
                case "RAISE":
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int raiseAmt))
                    {
                        if (!pokerEngine.PlayerRaise(playerName, raiseAmt, out string rErr))
                            SendResponse(rErr, sourceChannel);
                    }
                    else SendResponse("Usage: >RAISE [amount]", sourceChannel);
                    return;
                case "ALL":
                    if (parts.Length >= 2 && parts[1].ToUpperInvariant() == "IN")
                    {
                        if (!pokerEngine.PlayerAllIn(playerName, out string aiErr))
                            SendResponse(aiErr, sourceChannel);
                    }
                    return;
            }
        }

        // ── Chocobo: > BET [#|name...] [amt] ─────────────────────────────────
        if (command == "BET" && engine.CurrentTable.GameType == GameType.ChocoboRacing)
        {
            // >BET 3 100  (number form)
            if (parts.Length >= 3 &&
                int.TryParse(parts[1], out int chocoNum) &&
                int.TryParse(parts[2], out int chocoAmt))
            {
                if (!chocoboEngine.PlaceBet(playerName, chocoNum, chocoAmt, out string err))
                    SendResponse(err, sourceChannel);
                return;
            }
            // >BET Crimson Flash 100  (name form — last token is amount)
            if (parts.Length >= 3 && int.TryParse(parts[parts.Length - 1], out int nameAmt))
            {
                string racerName = string.Join(" ", parts.Skip(1).Take(parts.Length - 2));
                var racer = chocoboEngine.Roster.FirstOrDefault(r =>
                    r.Name.Equals(racerName, StringComparison.OrdinalIgnoreCase));
                if (racer != null)
                {
                    if (!chocoboEngine.PlaceBet(playerName, racer.Number, nameAmt, out string err))
                        SendResponse(err, sourceChannel);
                }
                else
                    SendResponse($"Unknown chocobo '{racerName}'. Use >BET [1-8] [amount] or full name.", sourceChannel);
                return;
            }
        }

        // ── Craps: > BET <TYPE> [num] <amt> ──────────────────────────────────
        if (command == "BET" && parts.Length >= 3 &&
            engine.CurrentTable.GameType == GameType.Craps)
        {
            string crapsType = parts[1].ToUpperInvariant();
            // PLACE bet: > BET PLACE <number> <amount>
            if (crapsType == "PLACE" && parts.Length >= 4 &&
                int.TryParse(parts[2], out int placeNum) &&
                int.TryParse(parts[3], out int placeAmt))
            {
                if (!crapsEngine.PlaceBet(playerName, "PLACE", placeAmt, out string placeErr, placeNum))
                    SendResponse(placeErr, sourceChannel);
                return;
            }
            // Standard bets: > BET PASS/DONTPASS/FIELD/BIG6/BIG8 <amount>
            if (crapsType is "PASS" or "DONTPASS" or "FIELD" or "BIG6" or "BIG8" &&
                int.TryParse(parts[2], out int crapsAmt))
            {
                if (!crapsEngine.PlaceBet(playerName, crapsType, crapsAmt, out string crapsErr))
                    SendResponse(crapsErr, sourceChannel);
                return;
            }
        }

        // ── Baccarat: > BET PLAYER/BANKER/TIE [amt] ──────────────────────────
        if (command == "BET" && parts.Length >= 3 &&
            engine.CurrentTable.GameType == GameType.Baccarat)
        {
            string bacType = parts[1].ToUpperInvariant();
            if ((bacType == "PLAYER" || bacType == "BANKER" || bacType == "TIE") &&
                int.TryParse(parts[2], out int bacAmt))
            {
                if (!baccaratEngine.PlaceBet(playerName, bacType, bacAmt, out string bacErr))
                    SendResponse(bacErr, sourceChannel);
                return;
            }
        }

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
            case "RULES":
                var player = engine.CurrentTable.Players.Values.FirstOrDefault(p =>
                    p.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
                if (player != null)
                    OnPlayerTell?.Invoke($"{playerName}@{player.Server}", GetRulesText(engine.CurrentTable));
                break;

            case "HELP":
                player = engine.CurrentTable.Players.Values.FirstOrDefault(p =>
                    p.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
                if (player != null)
                    OnPlayerTell?.Invoke($"{playerName}@{player.Server}", GetHelpText(engine.CurrentTable));
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
                    SendResponse("No commands needed \u2014 the dealer will control the game.", sourceChannel);
                else if (engine.CurrentTable.GameState == Models.GameState.Playing)
                    engine.PlayerHit(playerName);
                break;

            case "STAND":
                if (mode == DealerMode.Manual)
                    SendResponse("No commands needed \u2014 the dealer will control the game.", sourceChannel);
                else if (engine.CurrentTable.GameState == Models.GameState.Playing)
                    engine.PlayerStand(playerName);
                break;

            case "DOUBLE":
                if (mode == DealerMode.Manual)
                    SendResponse("No commands needed \u2014 the dealer will control the game.", sourceChannel);
                else if (engine.CurrentTable.GameState == Models.GameState.Playing)
                    engine.PlayerDouble(playerName);
                break;

            case "SPLIT":
                if (mode == DealerMode.Manual)
                    SendResponse("No commands needed \u2014 the dealer will control the game.", sourceChannel);
                else if (engine.CurrentTable.GameState == Models.GameState.Playing)
                    engine.PlayerSplit(playerName);
                break;

            case "INSURANCE":
                if (engine.CurrentTable.GameState == Models.GameState.Playing)
                    engine.PlayerInsurance(playerName);
                break;

            case "ROLL":
                if (engine.CurrentTable.GameType == GameType.Craps)
                {
                    if (crapsEngine.IsCurrentShooter(playerName))
                    {
                        if (!crapsEngine.StartRoll(out string rollErr))
                            SendResponse(rollErr, sourceChannel);
                    }
                    else
                    {
                        string shooter = engine.CurrentTable.CrapsShooterName;
                        SendResponse(string.IsNullOrEmpty(shooter)
                            ? "No active shooter — wait for bets to open."
                            : $"Only {shooter} can roll right now.", sourceChannel);
                    }
                }
                break;
        }
    }

    // ── Admin Commands ─────────────────────────────────────────────────────────

    private void HandleAdminCommand(string admin, string command, string[] parts, DealerMode mode, ChatChannel sourceChannel)
    {
        // ── Texas Hold'Em admin: > DEAL ───────────────────────────────────────
        if (command == "DEAL" && engine.CurrentTable.GameType == GameType.TexasHoldEm)
        {
            if (!pokerEngine.DealHand(out string err))
                SendResponse(err, sourceChannel);
            return;
        }

        // ── Texas Hold'Em admin: > TABLE ──────────────────────────────────────
        if (command == "TABLE" && engine.CurrentTable.GameType == GameType.TexasHoldEm)
        {
            pokerEngine.AnnounceTable();
            return;
        }

        // ── Craps admin: > ROLL ───────────────────────────────────────────────
        if (command == "ROLL" && engine.CurrentTable.GameType == GameType.Craps)
        {
            if (!crapsEngine.StartRoll(out string err))
                SendResponse(err, sourceChannel);
            return;
        }

        // ── Baccarat admin: > DEAL ────────────────────────────────────────────
        if (command == "DEAL" && engine.CurrentTable.GameType == GameType.Baccarat)
        {
            if (!baccaratEngine.Deal(out string err))
                SendResponse(err, sourceChannel);
            return;
        }

        // ── Chocobo admin: > OPEN ─────────────────────────────────────────────
        if (command == "OPEN" && engine.CurrentTable.GameType == GameType.ChocoboRacing)
        {
            if (!chocoboEngine.OpenBetting(out string err))
                SendResponse(err, sourceChannel);
            return;
        }

        // ── Chocobo admin: > START ────────────────────────────────────────────
        if (command == "START" && engine.CurrentTable.GameType == GameType.ChocoboRacing)
        {
            if (!chocoboEngine.StartRace(out string err))
                SendResponse(err, sourceChannel);
            return;
        }

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

    private string GetRulesText(Table table)
    {
        return table.GameType switch
        {
            GameType.Craps         => $"[CRAPS RULES] Come-out: 7/11=Natural (Pass wins, DP loses). 2/3=Craps (DP wins, Pass loses). 12=Pass loses, DP push. 4-10 sets the POINT. Point round: roll Point=Pass wins; 7-out=DP wins, Pass/Place/Big6/Big8 lose. FIELD: 2/12 pay 2:1, 3/4/9/10/11=1:1, 5/6/7/8=lose. BIG 6/8: pays 1:1 before 7. PLACE: 4/10=9:5, 5/9=7:5, 6/8=7:6. Limits: {table.MinBet}-{table.MaxBet}G.",
            GameType.Baccarat      => $"[BACCARAT RULES] Closest to 9 wins. Ace=1, 2-9=face, 10/J/Q/K=0. Score=sum mod 10. Natural: 8 or 9 on first two cards ends the round. Player draws on 0-5, stands 6-7. Banker follows standard third-card rules. Payouts: Player 1:1, Banker 1:1, Tie 8:1. Limits: {table.MinBet}-{table.MaxBet}G.",
            GameType.Roulette      => $"[ROULETTE RULES] Wheel has 37 slots (0-36). 0=green, others alternate red/black. Straight numbers pay 35:1. RED/BLACK, EVEN/ODD, 1-18/19-36 pay 1:1. 1ST/2ND/3RD dozen pay 2:1. COL1/COL2/COL3 pay 2:1. Limits: {table.MinBet}-{table.MaxBet}G.",
            GameType.ChocoboRacing => $"[CHOCOBO RULES] 8 racers run a 30-second race. Each has Speed (early), Endurance (late), and X-Factor (randomness). Winning bet pays your stake × the racer's odds. Limits: {table.MinBet}-{table.MaxBet}G.",
            GameType.TexasHoldEm  => $"[POKER RULES] Texas Hold'Em. Each player gets 2 hole cards + 5 community cards over 4 betting rounds (Pre-Flop, Flop, Turn, River). Best 5-card hand wins the pot. SB={table.PokerSmallBlind}G, BB={table.PokerSmallBlind * 2}G. Hands: Royal Flush > Straight Flush > 4-of-a-kind > Full House > Flush > Straight > 3-of-a-kind > Two Pair > Pair > High Card.",
            _                      => $"[BLACKJACK RULES] Beat the dealer without exceeding 21. Ace=1 or 11, face cards=10. Blackjack (Ace+10) pays 1.5x bet. Dealer hits on soft 16 or less. Split matching pairs into two hands. Double Down: double your bet, receive exactly one more card. Insurance: when dealer shows Ace, pays 2:1 if dealer has Blackjack. Limits: {table.MinBet}-{table.MaxBet}G."
        };
    }

    private string GetHelpText(Table table)
    {
        return table.GameType switch
        {
            GameType.Craps         => "[CRAPS COMMANDS] >BET PASS [amt]  >BET DONTPASS [amt]  >BET FIELD [amt]  >BET BIG6 [amt]  >BET BIG8 [amt]  >BET PLACE [4/5/6/8/9/10] [amt]  >ROLL (shooter only)  | >BANK  >AFK  >RULES",
            GameType.Baccarat      => "[BACCARAT COMMANDS] >BET PLAYER [amt]  >BET BANKER [amt]  >BET TIE [amt]  | >BANK  >AFK  >RULES",
            GameType.Roulette      => "[ROULETTE COMMANDS] >BET [amt] ON [target]  — targets: RED BLACK EVEN ODD 1-18 19-36 1ST 2ND 3RD COL1 COL2 COL3 or any number 0-36  | >BANK  >AFK  >RULES",
            GameType.ChocoboRacing => "[CHOCOBO COMMANDS] >BET [#1-8 or racer name] [amt]  | >BANK  >AFK  >RULES",
            GameType.TexasHoldEm  => "[POKER COMMANDS] >CALL  >CHECK  >RAISE [amt]  >FOLD  >ALL IN  | >BANK  >AFK  >RULES",
            _                      => "[BLACKJACK COMMANDS] >HIT  >STAND  >DOUBLE  >SPLIT  >INSURANCE  >BET [amt]  | >BANK  >AFK  >RULES"
        };
    }
}
