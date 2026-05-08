using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ChatCasino.Models;

namespace ChatCasino.Services;

public enum FlavorEventType
{
    DealStarted,
    BetPlaced,
    HitCalled,
    StandCalled,
    DoubleDownCalled,
    SplitCalled,
    RouletteSpin,
    CrapsRoll,
    PokerAction,
    UltimaPlay
}

public sealed class FlavorTextService
{
    private const string FileName = "flavor-text.json";
    private readonly string filePath;
    private readonly Random rng = new();
    private readonly Dictionary<FlavorEventType, List<string>> linesByEvent = new();

    public FlavorTextService(string configDirectory)
    {
        filePath = Path.Combine(configDirectory, FileName);
        Load();
    }

    public IReadOnlyList<FlavorEventType> OrderedEvents { get; } =
    [
        FlavorEventType.DealStarted,
        FlavorEventType.BetPlaced,
        FlavorEventType.HitCalled,
        FlavorEventType.StandCalled,
        FlavorEventType.DoubleDownCalled,
        FlavorEventType.SplitCalled,
        FlavorEventType.RouletteSpin,
        FlavorEventType.CrapsRoll,
        FlavorEventType.PokerAction,
        FlavorEventType.UltimaPlay
    ];

    public IReadOnlyList<string> GetLines(FlavorEventType eventType)
    {
        EnsureEvent(eventType);
        return linesByEvent[eventType];
    }

    public void SetLine(FlavorEventType eventType, int index, string text)
    {
        EnsureEvent(eventType);
        if (index < 0 || index >= linesByEvent[eventType].Count)
            return;

        linesByEvent[eventType][index] = text ?? string.Empty;
        Save();
    }

    public void AddLine(FlavorEventType eventType)
    {
        EnsureEvent(eventType);
        linesByEvent[eventType].Add(string.Empty);
        Save();
    }

    public void RemoveLine(FlavorEventType eventType, int index)
    {
        EnsureEvent(eventType);
        if (index < 0 || index >= linesByEvent[eventType].Count)
            return;

        linesByEvent[eventType].RemoveAt(index);
        Save();
    }

    public string GetEventLabel(FlavorEventType eventType)
    {
        return eventType switch
        {
            FlavorEventType.DealStarted => "Deal Started",
            FlavorEventType.BetPlaced => "Bet Placed",
            FlavorEventType.HitCalled => "Hit",
            FlavorEventType.StandCalled => "Stand",
            FlavorEventType.DoubleDownCalled => "Double Down",
            FlavorEventType.SplitCalled => "Split",
            FlavorEventType.RouletteSpin => "Roulette Spin",
            FlavorEventType.CrapsRoll => "Craps Roll",
            FlavorEventType.PokerAction => "Poker Action",
            FlavorEventType.UltimaPlay => "Ultima Play",
            _ => eventType.ToString()
        };
    }

    public bool TryBuildFlavorLine(string player, string cmd, string[] args, GameType game, out string line)
    {
        line = string.Empty;
        if (!TryMapEvent(cmd, out var eventType))
            return false;

        EnsureEvent(eventType);
        var options = linesByEvent[eventType]
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (options.Count == 0)
            return false;

        var template = options[rng.Next(options.Count)];
        var target = args.Length > 0 ? string.Join(' ', args) : string.Empty;
        var amount = args.Length > 0 && int.TryParse(args[0], out var parsedAmount)
            ? StandardizedFormatting.FormatCurrency(parsedAmount)
            : string.Empty;

        line = template
            .Replace("{player}", player ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{cmd}", cmd ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{game}", game.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{target}", target, StringComparison.OrdinalIgnoreCase)
            .Replace("{amount}", amount, StringComparison.OrdinalIgnoreCase);

        return !string.IsNullOrWhiteSpace(line);
    }

    private bool TryMapEvent(string cmd, out FlavorEventType eventType)
    {
        eventType = FlavorEventType.BetPlaced;
        var normalized = (cmd ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        switch (normalized)
        {
            case "DEAL":
                eventType = FlavorEventType.DealStarted;
                return true;
            case "BET":
                eventType = FlavorEventType.BetPlaced;
                return true;
            case "HIT":
                eventType = FlavorEventType.HitCalled;
                return true;
            case "STAND":
                eventType = FlavorEventType.StandCalled;
                return true;
            case "DOUBLE":
                eventType = FlavorEventType.DoubleDownCalled;
                return true;
            case "SPLIT":
                eventType = FlavorEventType.SplitCalled;
                return true;
            case "SPIN":
                eventType = FlavorEventType.RouletteSpin;
                return true;
            case "ROLL":
                eventType = FlavorEventType.CrapsRoll;
                return true;
            case "CHECK":
            case "CALL":
            case "RAISE":
            case "FOLD":
            case "ALL":
                eventType = FlavorEventType.PokerAction;
                return true;
            case "PLAY":
                eventType = FlavorEventType.UltimaPlay;
                return true;
            default:
                return false;
        }
    }

    private void EnsureEvent(FlavorEventType eventType)
    {
        if (linesByEvent.ContainsKey(eventType))
            return;

        linesByEvent[eventType] = GetDefaultLines(eventType).ToList();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var model = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? new();
                foreach (var evt in OrderedEvents)
                {
                    if (model.TryGetValue(evt.ToString(), out var lines) && lines is { Count: > 0 })
                        linesByEvent[evt] = lines;
                    else
                        linesByEvent[evt] = GetDefaultLines(evt).ToList();
                }
                return;
            }
        }
        catch
        {
        }

        foreach (var evt in OrderedEvents)
            linesByEvent[evt] = GetDefaultLines(evt).ToList();

        Save();
    }

    private void Save()
    {
        try
        {
            var payload = linesByEvent.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value);
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
        catch
        {
        }
    }

    private static IEnumerable<string> GetDefaultLines(FlavorEventType eventType)
    {
        return eventType switch
        {
            FlavorEventType.DealStarted =>
            [
                "Cards up! Let's see where luck lands this hand.",
                "New round in {game} — eyes up, dealers and dreamers.",
                "Fresh deal on the table. {player} is setting the pace."
            ],
            FlavorEventType.BetPlaced =>
            [
                "{player} puts action on the table: {amount}.",
                "Bet locked in by {player} for {amount}.",
                "{player} is in for {amount} — let's play it out."
            ],
            FlavorEventType.HitCalled =>
            [
                "{player} asks for one more.",
                "Hit for {player} — dealing the next card.",
                "{player} presses their luck with a hit."
            ],
            FlavorEventType.StandCalled =>
            [
                "{player} stands pat.",
                "Stand from {player} — score is locked.",
                "{player} holds."
            ],
            FlavorEventType.DoubleDownCalled =>
            [
                "{player} doubles down — confidence play!",
                "Double down called by {player}.",
                "{player} goes big with the double."
            ],
            FlavorEventType.SplitCalled =>
            [
                "{player} splits the hand — two paths now.",
                "Split from {player}; let's run both hands.",
                "{player} makes the split call."
            ],
            FlavorEventType.RouletteSpin =>
            [
                "Wheel is live. No more bets.",
                "Roulette spin incoming — good luck all.",
                "The wheel turns; fate picks a number."
            ],
            FlavorEventType.CrapsRoll =>
            [
                "Dice are out — here we go.",
                "Craps roll incoming. Shooter's up.",
                "Dice in motion. Let's see that total."
            ],
            FlavorEventType.PokerAction =>
            [
                "{player} chooses {cmd}.",
                "Poker action from {player}: {cmd}.",
                "{player} makes the {cmd} play."
            ],
            FlavorEventType.UltimaPlay =>
            [
                "{player} plays {target}.",
                "Ultima move by {player}.",
                "{player} commits a card to the table."
            ],
            _ => ["The table moves forward."]
        };
    }
}
