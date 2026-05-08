using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ChatCasino.Engine;
using ChatCasino.Models;

namespace ChatCasino.Services;

public sealed class TestBotService
{
    private static readonly string[] AllBotNames =
    [
        "TestBot1", "TestBot2", "TestBot3", "TestBot4", "TestBot5", "TestBot6", "TestBot7"
    ];

    private static readonly string[] RouletteTargets =
    [
        "RED", "BLACK", "EVEN", "ODD", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20", "21", "22", "23", "24", "25", "26", "27", "28", "29", "30", "31", "32", "33", "34", "35", "36"
    ];

    private static readonly string[] CrapsTargets = ["PASS", "DONTPASS", "FIELD", "SEVEN", "ANYCRAPS", "PLACE4", "PLACE5", "PLACE6", "PLACE8", "PLACE9", "PLACE10"];
    private static readonly string[] BaccaratTargets = ["PLAYER", "BANKER", "TIE"];
    private static readonly string[] UltimaTargets = ["WATER", "FIRE", "GRASS", "LIGHT"];

    private readonly GameManager gameManager;
    private readonly ITimerService timer;
    private readonly Random rng = new();

    private readonly HashSet<string> activeBots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> roundBetPlaced = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> shooterLineBetPlaced = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> chocoboTargets = new();
    private readonly Dictionary<string, int> blackjackTotals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> ultimaHands = new(StringComparer.OrdinalIgnoreCase);

    private bool enabled;
    private GameType currentGame = GameType.None;
    private string currentShooter = string.Empty;

    public TestBotService(GameManager gameManager, ITimerService timer)
    {
        this.gameManager = gameManager;
        this.timer = timer;
    }

    public void Configure(bool enabled, int botCount)
    {
        botCount = Math.Clamp(botCount, 1, 7);

        if (!enabled)
        {
            foreach (var b in activeBots.ToList())
                _ = gameManager.RouteCommand(b, "LEAVE", Array.Empty<string>());
            activeBots.Clear();
            roundBetPlaced.Clear();
            shooterLineBetPlaced.Clear();
            chocoboTargets.Clear();
            this.enabled = false;
            return;
        }

        this.enabled = true;

        var desired = AllBotNames.Take(botCount).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var b in activeBots.Where(b => !desired.Contains(b)).ToList())
        {
            _ = gameManager.RouteCommand(b, "LEAVE", Array.Empty<string>());
            activeBots.Remove(b);
        }

        foreach (var b in desired)
        {
            if (activeBots.Contains(b)) continue;
            activeBots.Add(b);
            _ = gameManager.RouteCommand(b, "JOIN", ["Ultros"]);
        }
    }

    public void OnChat(string text)
    {
        if (!enabled || string.IsNullOrWhiteSpace(text)) return;

        ParseGameChange(text);
        ParseTaggedGame(text);
        ParseRoundReset(text);
        ParseShooter(text);
        ParseChocoboRoster(text);
        ParseBlackjackTotals(text);
        ParseBotTellEcho(text);

        if (currentGame == GameType.Blackjack && text.Contains("[BLACKJACK]", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("round complete", StringComparison.OrdinalIgnoreCase)
             || text.Contains("waiting", StringComparison.OrdinalIgnoreCase)
             || text.Contains("bets", StringComparison.OrdinalIgnoreCase)
             || text.Contains("Dealer shows", StringComparison.OrdinalIgnoreCase)
             || text.Contains("Turn:", StringComparison.OrdinalIgnoreCase)))
            PlaceOpenBetsForAllBots();

        if (currentGame == GameType.Ultima)
            MaybePlayUltimaTurn(text);

        if (currentGame == GameType.ChocoboRacing && (text.StartsWith("[CHOCOBO] Bets are now OPEN", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[CHOCOBO] Roster A:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[CHOCOBO] Roster B:", StringComparison.OrdinalIgnoreCase)))
            PlaceOpenBetsForAllBots();

        if (currentGame == GameType.Roulette && text.StartsWith("[ROULETTE]", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("No more bets", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("Spinning", StringComparison.OrdinalIgnoreCase))
            PlaceOpenBetsForAllBots();

        if (currentGame == GameType.Craps && text.StartsWith("[CRAPS] Shooter:", StringComparison.OrdinalIgnoreCase))
        {
            PlaceOpenBetsForAllBots();
            EnsureShooterLineBet(currentShooter);
            MaybeScheduleShooterRoll();
        }

        if (currentGame == GameType.Craps && Regex.IsMatch(text, @"^\[CRAPS\]\s+.+?\s+rolls\s*\d+\+\d+=\d+", RegexOptions.IgnoreCase))
            MaybeScheduleShooterRoll();

        var prompt = Regex.Match(text, @"^\[[^\]]+\]\s+(.+?):\s*>(.+)$", RegexOptions.IgnoreCase);
        if (prompt.Success)
        {
            var name = prompt.Groups[1].Value.Trim();
            if (!activeBots.Contains(name)) return;

            var options = prompt.Groups[2].Value
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().TrimStart('>'))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant())
                .Where(c => c is not ("DEAL" or "SPIN" or "START" or "OPENBETS" or "RULE" or "RULETOGGLE"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (options.Count == 0) return;
            var cmd = ChooseCommandForGame(name, options);
            var args = BuildArgs(currentGame, cmd);
            ScheduleCommand(name, cmd, args);
        }
    }

    private void ParseGameChange(string text)
    {
        var gameChange = Regex.Match(text, @"^\[CASINO\]\s+Game changed to\s+(.+)$", RegexOptions.IgnoreCase);
        if (!gameChange.Success) return;

        currentGame = ParseGame(gameChange.Groups[1].Value.Trim());
        roundBetPlaced.Clear();
        shooterLineBetPlaced.Clear();
        chocoboTargets.Clear();

        if (currentGame == GameType.Blackjack)
            PlaceOpenBetsForAllBots();
    }

    private void ParseTaggedGame(string text)
    {
        if (text.Contains("[BLACKJACK]", StringComparison.OrdinalIgnoreCase)) currentGame = GameType.Blackjack;
        else if (text.Contains("[ROULETTE]", StringComparison.OrdinalIgnoreCase)) currentGame = GameType.Roulette;
        else if (text.Contains("[CRAPS]", StringComparison.OrdinalIgnoreCase)) currentGame = GameType.Craps;
        else if (text.Contains("[BACCARAT]", StringComparison.OrdinalIgnoreCase)) currentGame = GameType.Baccarat;
        else if (text.Contains("[CHOCOBO]", StringComparison.OrdinalIgnoreCase)) currentGame = GameType.ChocoboRacing;
        else if (text.Contains("[POKER", StringComparison.OrdinalIgnoreCase)) currentGame = GameType.TexasHoldEmPvP;
        else if (text.Contains("[ULTIMA", StringComparison.OrdinalIgnoreCase)) currentGame = GameType.Ultima;
    }

    private void ParseRoundReset(string text)
    {
        if (text.Contains("round complete", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Result:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Winner:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Bets are now OPEN", StringComparison.OrdinalIgnoreCase))
        {
            roundBetPlaced.Clear();
            shooterLineBetPlaced.Clear();
        }
    }

    private void ParseShooter(string text)
    {
        var shooter = Regex.Match(text, @"^\[CRAPS\]\s+Shooter:\s+(.+)$", RegexOptions.IgnoreCase);
        if (shooter.Success)
            currentShooter = shooter.Groups[1].Value.Trim();
    }

    private void ParseChocoboRoster(string text)
    {
        if (!(text.StartsWith("[CHOCOBO] Roster A:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("[CHOCOBO] Roster B:", StringComparison.OrdinalIgnoreCase)))
            return;

        var idx = text.IndexOf(':');
        if (idx < 0) return;
        var payload = text[(idx + 1)..].Trim();

        foreach (var entry in payload.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Regex.Match(entry, @"^(.*)\s+\d+(?:\.\d+)?:1$");
            if (!m.Success) continue;
            var racer = m.Groups[1].Value.Trim();
            if (!chocoboTargets.Contains(racer, StringComparer.OrdinalIgnoreCase))
                chocoboTargets.Add(racer);
        }
    }

    private void PlaceOpenBetsForAllBots()
    {
        foreach (var b in activeBots)
        {
            var key = $"{currentGame}:{b}:BET";
            if (roundBetPlaced.Contains(key)) continue;

            var args = BuildArgs(currentGame, "BET");
            if (args.Length == 0) continue;

            roundBetPlaced.Add(key);
            ScheduleCommand(b, "BET", args);
        }
    }

    private void MaybeScheduleShooterRoll()
    {
        var shooter = currentShooter;
        if (string.IsNullOrWhiteSpace(shooter) || !activeBots.Contains(shooter))
            return;

        EnsureShooterLineBet(shooter);

        var delayMs = rng.Next(350, 1200);
        timer.Schedule(TimeSpan.FromMilliseconds(delayMs), () =>
        {
            if (!enabled || string.IsNullOrWhiteSpace(shooter) || !activeBots.Contains(shooter))
                return;
            if (!shooter.Equals(currentShooter, StringComparison.OrdinalIgnoreCase))
                return;

            _ = gameManager.RouteCommand(shooter, "ROLL", Array.Empty<string>());
        });
    }

    private void EnsureShooterLineBet(string shooter)
    {
        if (currentGame != GameType.Craps || string.IsNullOrWhiteSpace(shooter) || !activeBots.Contains(shooter))
            return;

        var key = $"{currentGame}:{shooter}:SHOOTERLINE";
        if (shooterLineBetPlaced.Contains(key))
            return;

        shooterLineBetPlaced.Add(key);
        var amount = rng.Next(50, 251).ToString();
        var target = rng.NextDouble() < 0.5 ? "PASS" : "DONTPASS";
        ScheduleCommand(shooter, "BET", [amount, target]);
    }

    private string[] BuildArgs(GameType game, string cmd)
    {
        if (!cmd.Equals("BET", StringComparison.OrdinalIgnoreCase))
            return cmd.Equals("ALL", StringComparison.OrdinalIgnoreCase) ? ["IN"] : Array.Empty<string>();

        var amount = rng.Next(50, 251).ToString();
        return game switch
        {
            GameType.Blackjack => [amount],
            GameType.Roulette => [amount, RouletteTargets[rng.Next(RouletteTargets.Length)]],
            GameType.Craps => [amount, CrapsTargets[rng.Next(CrapsTargets.Length)]],
            GameType.Baccarat => [amount, BaccaratTargets[rng.Next(BaccaratTargets.Length)]],
            GameType.ChocoboRacing => chocoboTargets.Count > 0 ? [amount, chocoboTargets[rng.Next(chocoboTargets.Count)]] : Array.Empty<string>(),
            GameType.TexasHoldEmPvP => [amount],
            GameType.TexasHoldEmPvD => [amount],
            GameType.Ultima => [amount, UltimaTargets[rng.Next(UltimaTargets.Length)]],
            _ => Array.Empty<string>()
        };
    }

    private void ScheduleCommand(string botName, string cmd, string[] args)
    {
        var delayMs = rng.Next(350, 1200);
        timer.Schedule(TimeSpan.FromMilliseconds(delayMs), () =>
        {
            _ = gameManager.RouteCommand(botName, cmd, args);
        });
    }

    private void ParseBlackjackTotals(string text)
    {
        var m = Regex.Match(text, @"^\[BLACKJACK\]\s+(.+?):.*\((\d+)\)", RegexOptions.IgnoreCase);
        if (!m.Success) return;
        var name = m.Groups[1].Value.Trim();
        if (!activeBots.Contains(name)) return;
        if (int.TryParse(m.Groups[2].Value, out var total))
            blackjackTotals[name] = total;
    }

    private string ChooseCommandForGame(string botName, List<string> options)
    {
        if (currentGame == GameType.Blackjack)
        {
            if (options.Contains("BET", StringComparer.OrdinalIgnoreCase)) return "BET";
            blackjackTotals.TryGetValue(botName, out var total);
            if (options.Contains("HIT", StringComparer.OrdinalIgnoreCase) && total <= 11) return "HIT";
            if (options.Contains("STAND", StringComparer.OrdinalIgnoreCase) && total >= 17) return "STAND";
            if (options.Contains("DOUBLE", StringComparer.OrdinalIgnoreCase) && total is 9 or 10 or 11 && rng.NextDouble() < 0.30) return "DOUBLE";
            if (options.Contains("HIT", StringComparer.OrdinalIgnoreCase) && total < 17) return rng.NextDouble() < 0.72 ? "HIT" : "STAND";
            if (options.Contains("STAND", StringComparer.OrdinalIgnoreCase)) return "STAND";
        }

        if (currentGame == GameType.TexasHoldEmPvP)
        {
            if (options.Contains("CHECK", StringComparer.OrdinalIgnoreCase) && rng.NextDouble() < 0.60) return "CHECK";
            if (options.Contains("CALL", StringComparer.OrdinalIgnoreCase) && rng.NextDouble() < 0.70) return "CALL";
            if (options.Contains("RAISE", StringComparer.OrdinalIgnoreCase) && rng.NextDouble() < 0.22) return "RAISE";
            if (options.Contains("ALL", StringComparer.OrdinalIgnoreCase) && rng.NextDouble() < 0.05) return "ALL";
            if (options.Contains("FOLD", StringComparer.OrdinalIgnoreCase) && rng.NextDouble() < 0.10) return "FOLD";
        }

        return options[rng.Next(options.Count)];
    }

    private static GameType ParseGame(string text)
    {
        var key = NormalizeGameKey(text);
        return key switch
        {
            "BLACKJACK" => GameType.Blackjack,
            "ROULETTE" => GameType.Roulette,
            "CRAPS" => GameType.Craps,
            "BACCARAT" => GameType.Baccarat,
            "CHOCOBORACING" => GameType.ChocoboRacing,
            "TEXASHOLDEM" => GameType.TexasHoldEmPvP,
            "ULTIMA" => GameType.Ultima,
            _ => GameType.None
        };
    }

    private static string NormalizeGameKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToUpperInvariant();
    }

    private void ParseBotTellEcho(string text)
    {
        var m = Regex.Match(text, @"^\[BOT-TELL:(.+?)\]\s+(.+)$", RegexOptions.IgnoreCase);
        if (!m.Success) return;

        var bot = m.Groups[1].Value.Trim();
        var payload = m.Groups[2].Value.Trim();
        if (!activeBots.Contains(bot)) return;

        if (payload.StartsWith("[ULTIMA HAND]", StringComparison.OrdinalIgnoreCase))
        {
            var cards = payload.Replace("[ULTIMA HAND]", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToUpperInvariant())
                .ToList();
            ultimaHands[bot] = cards;
        }
    }

    private void MaybePlayUltimaTurn(string text)
    {
        // Match standalone "Turn: X" or embedded "Turn: X" at end of played/draw messages
        var turn = Regex.Match(text, @"Turn:\s+(.+?)$", RegexOptions.IgnoreCase);
        if (!turn.Success) return;

        var who = turn.Groups[1].Value.Trim();
        if (!activeBots.Contains(who)) return;

        // Parse the active color from the message
        var colorMatch = Regex.Match(text, @"Color:\s+(\S+)", RegexOptions.IgnoreCase);
        var activeColor = colorMatch.Success ? colorMatch.Groups[1].Value.Trim().ToUpperInvariant() : string.Empty;

        if (ultimaHands.TryGetValue(who, out var hand) && hand.Count > 0)
        {
            // Try to find a playable card (matching color or wild)
            var playable = hand.Where(c =>
                c.StartsWith("PL", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(activeColor) && char.ToUpperInvariant(c[0]) == activeColor[0])
            ).ToList();

            if (playable.Count > 0)
            {
                var pick = playable[rng.Next(playable.Count)];
                var isWild = pick.StartsWith("PL", StringComparison.OrdinalIgnoreCase);
                var args = isWild ? [pick, UltimaTargets[rng.Next(UltimaTargets.Length)]] : new[] { pick };
                ScheduleCommand(who, "PLAY", args);
            }
            else
            {
                ScheduleCommand(who, "DRAW", Array.Empty<string>());
            }
        }
        else
        {
            ScheduleCommand(who, "DRAW", Array.Empty<string>());
        }
    }
}
