using System;
using System.Collections.Generic;
using System.Linq;
using ChatCasino.Models;
using ChatCasino.Services;
using ChatCasino.UI;

namespace ChatCasino.Engine;

public sealed class GameEvent
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public GameType GameType { get; init; } = GameType.None;
    public string Action { get; init; } = string.Empty;
    public string? Player { get; init; }
    public string? Details { get; init; }
}

public static class Logger
{
    private static readonly List<GameEvent> Audit = new();

    public static IReadOnlyList<GameEvent> Events => Audit;

    public static void Log(GameEvent ev)
    {
        Audit.Add(ev);
    }
}

public abstract class BaseEngine : IGameProcessor
{
    protected BaseEngine(
        GameType gameType,
        IMessageService messageService,
        IDeckService deckService,
        IPlayerService playerService)
    {
        GameType = gameType;
        Msg = messageService;
        Decks = deckService;
        Players = playerService;
    }

    protected GameType GameType { get; }
    protected IMessageService Msg { get; }
    protected IDeckService Decks { get; }
    protected IPlayerService Players { get; }

    protected string StatusText { get; set; } = "Idle";

    public event Action<Player>? OnPlayerJoined;
    public event Action<Player>? OnPlayerLeft;

    public virtual void Tick()
    {
    }

    public virtual ICasinoViewModel GetViewModel()
    {
        var seats = Players.GetAllActivePlayers()
            .Select(p => new PlayerSlotViewModel
            {
                PlayerName = p.Name,
                Bank = p.CurrentBank,
                BetAmount = TryGetBet(p),
                IsAfk = p.IsAfk,
                IsKicked = p.IsKicked,
                Cards = TryGetCards(p)
            }).ToList();

        return new EngineViewModel
        {
            GameTitle = GameType.ToString(),
            GameStatus = StatusText,
            Seats = seats,
            Actions = GetValidCommands().ToList()
        };
    }

    public void NotifyPlayerJoined(Player player)
    {
        OnPlayerJoined?.Invoke(player);
        HandlePlayerJoined(player);
    }

    public void NotifyPlayerLeft(Player player)
    {
        OnPlayerLeft?.Invoke(player);
        HandlePlayerLeft(player);
    }

    protected virtual void HandlePlayerJoined(Player player) { }
    protected virtual void HandlePlayerLeft(Player player) { }

    protected void LogAction(string action, string? player = null, string? details = null)
    {
        Logger.Log(new GameEvent
        {
            GameType = GameType,
            Action = action,
            Player = player,
            Details = details
        });
    }

    public virtual void OnPreStart() { }
    public virtual void OnStart() { }
    public virtual void OnRoundComplete()
    {
        var players = Players.GetAllActivePlayers().ToList();
        if (players.Count == 0) return;

        Msg.QueuePartyMessage($"[CASINO] {GameType} round complete. Results:");

        var lines = new List<string>();
        foreach (var p in players)
        {
            var key = $"RoundStartBank:{GameType}";
            int delta = 0;
            if (p.Metadata.TryGetValue(key, out var startObj) && startObj is int startBank)
            {
                delta = p.CurrentBank - startBank;
                p.Metadata.Remove(key);
            }

            var sign = delta >= 0 ? "+" : string.Empty;
            lines.Add($"{p.Name} {sign}{delta}\uE049 (Bank {p.CurrentBank}\uE049)");
        }

        for (var i = 0; i < lines.Count; i += 4)
        {
            var chunk = string.Join(" | ", lines.Skip(i).Take(4));
            Msg.QueuePartyMessage($"[CASINO] {chunk}");
        }
    }
    public virtual void OnForceStop() { }

    public abstract CmdResult Execute(string player, string cmd, string[] args);
    public abstract IEnumerable<string> GetValidCommands();

    private static int TryGetBet(Player p)
    {
        if (p.Metadata.TryGetValue("Blackjack.Bet", out var bj) && bj is int b1) return b1;
        if (p.Metadata.TryGetValue("Baccarat.BetAmount", out var bac) && bac is int b2) return b2;
        if (p.Metadata.TryGetValue("Craps.BetAmount", out var cr) && cr is int b3) return b3;
        if (p.Metadata.TryGetValue("Chocobo.BetAmount", out var ch) && ch is int b4) return b4;
        if (p.Metadata.TryGetValue("Poker.Committed", out var pk) && pk is int b5) return b5;
        if (p.Metadata.TryGetValue("Bet", out var g) && g is int b6) return b6;
        return 0;
    }

    private static List<string> TryGetCards(Player p)
    {
        if (p.Metadata.TryGetValue("Blackjack.Hands", out var bjHandsObj) && bjHandsObj is List<List<Card>> bjHands && bjHands.Count > 0)
        {
            var activeIdx = p.Metadata.TryGetValue("Blackjack.ActiveHand", out var idxObj) && idxObj is int i ? i : 0;
            var parts = new List<string>();
            for (var handIdx = 0; handIdx < bjHands.Count; handIdx++)
            {
                var handCardsText = string.Join(" ", bjHands[handIdx].Select(c => c.ToString()));
                var tag = handIdx == activeIdx ? "*" : string.Empty;
                parts.Add($"H{handIdx + 1}{tag}: {handCardsText}");
            }
            return parts;
        }

        if (p.Metadata.TryGetValue("Poker.Hole", out var holeObj) && holeObj is List<Card> hole)
            return hole.Select(c => c.ToString()).ToList();

        if (p.Metadata.TryGetValue("Cards", out var cardsObj) && cardsObj is List<Card> cards)
            return cards.Select(c => c.ToString()).ToList();

        if (p.Metadata.TryGetValue("Ultima.Hand", out var ultimaObj) && ultimaObj is List<UltimaDeckCard> ultima)
            return ultima.Select(c => c.Code).ToList();

        return new List<string>();
    }

    private sealed class EngineViewModel : BaseViewModel
    {
        public List<string> Actions { get; set; } = new();

        public override IReadOnlyList<string> GetActionButtons() => Actions;
    }
}
