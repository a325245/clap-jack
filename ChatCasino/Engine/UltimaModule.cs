using System;
using System.Collections.Generic;
using System.Linq;
using ChatCasino.Models;
using ChatCasino.Services;
using ChatCasino.UI;

namespace ChatCasino.Engine;

public sealed class UltimaModule : BaseEngine
{
    private readonly DeckShoe<UltimaDeckCard> deck;
    private readonly ITimerService timer;
    private readonly List<UltimaDeckCard> discard = new();
    private readonly List<string> turnOrder = new();
    private int currentIndex;
    private DateTime currentTurnStartedUtc = DateTime.MinValue;
    private bool currentTurnWarningSent;

    public string ActiveColor { get; private set; } = "WATER";
    public int Direction { get; private set; } = 1;

    public UltimaModule(IMessageService msg, IDeckService decks, IPlayerService players, ITimerService timer)
        : base(GameType.Ultima, msg, decks, players)
    {
        deck = decks.GetUltimaDeck();
        this.timer = timer;
        StatusText = "Waiting for players";
    }

    public override CmdResult Execute(string playerName, string cmd, string[] args)
    {
        var player = Players.GetPlayer(playerName);
        if (player is null) return CmdResult.Fail("Player not found.");

        cmd = cmd.ToUpperInvariant();
        return cmd switch
        {
            "DEAL" => StartGame(),
            "PLAY" => PlayCard(player, args),
            "DRAW" => DrawCard(player),
            "HAND" => SendHand(player),
            _ => CmdResult.Ok(string.Empty)
        };
    }

    public override IEnumerable<string> GetValidCommands()
        => ["DEAL", "PLAY", "HAND"];

    public override void OnRoundComplete()
    {
        // Ultima doesn't need bank-based results
        StatusText = "Round complete";
    }

    public override void OnForceStop()
    {
        turnOrder.Clear();
        discard.Clear();
        currentIndex = 0;
        ActiveColor = "WATER";
        Direction = 1;
        currentTurnStartedUtc = DateTime.MinValue;
        currentTurnWarningSent = false;
        StatusText = "Waiting for players";

        foreach (var p in Players.GetAllActivePlayers())
            p.Metadata.Remove("Ultima.Hand");
    }

    public override void Tick()
    {
        if (turnOrder.Count == 0 || currentTurnStartedUtc == DateTime.MinValue)
            return;

        var current = Players.GetPlayer(turnOrder[currentIndex]);
        if (current is null) return;

        var turnLimit = Math.Max(10.0, CasinoUI.GlobalTurnTimeLimitSeconds);
        var elapsed = (DateTime.UtcNow - currentTurnStartedUtc).TotalSeconds;
        var warnAt = turnLimit * (2.0 / 3.0);

        if (!currentTurnWarningSent && elapsed >= warnAt)
        {
            currentTurnWarningSent = true;
            Msg.QueuePartyMessage($"[ULTIMA] {current.Name}, time's almost up.");
        }

        if (elapsed >= turnLimit)
        {
            // Time's up — force a draw
            var handCount = 0;
            if (current.Metadata.TryGetValue("Ultima.Hand", out var handObj) && handObj is List<UltimaDeckCard> hand)
            {
                hand.Add(deck.Draw());
                handCount = hand.Count;
                SendHandTell(current);
            }

            Msg.QueuePartyMessage($"[ULTIMA] {current.Name} ({handCount}) ran out of time and draws.");
            AdvanceTurn();
            AutoDrawUntilValid();
            StatusText = $"{CurrentPlayerName()}'s turn";
            Msg.QueuePartyMessage($"[ULTIMA] Turn: {CurrentPlayerName()}");
            currentTurnStartedUtc = DateTime.UtcNow;
            currentTurnWarningSent = false;
        }
    }

    public override ICasinoViewModel GetViewModel()
    {
        UltimaDeckCard? top = discard.Count == 0 ? null : discard[^1];
        var seats = new List<PlayerSlotViewModel>
        {
            new()
            {
                PlayerName = "Table",
                IsDealer = true,
                Cards = top.HasValue ? [top.Value.Code] : new List<string>(),
                ResultText = $"Color: {ActiveColor} | Dir: {(Direction > 0 ? "CW" : "CCW")} | Turn: {CurrentPlayerName()}"
            }
        };

        seats.AddRange(Players.GetAllActivePlayers().Select(p =>
        {
            var hand = p.Metadata.TryGetValue("Ultima.Hand", out var h) && h is List<UltimaDeckCard> cards
                ? cards
                : new List<UltimaDeckCard>();

            return new PlayerSlotViewModel
            {
                PlayerName = p.Name,
                Bank = p.CurrentBank,
                Cards = hand.Select(c => c.Code).ToList(),
                ResultText = $"Cards: {hand.Count}",
                IsActiveTurn = IsCurrentTurn(p)
            };
        }));

        return new UltimaViewModel
        {
            GameTitle = "Ultima!",
            GameStatus = StatusText,
            Seats = seats,
            Actions = GetValidCommands().ToList()
        };
    }

    private CmdResult StartGame()
    {
        turnOrder.Clear();
        turnOrder.AddRange(Players.GetAllActivePlayers().Select(p => p.Name));
        if (turnOrder.Count < 2) return CmdResult.Fail("Need at least 2 players.");

        currentIndex = 0;
        ActiveColor = "WATER";
        Direction = 1;
        discard.Clear();

        foreach (var p in Players.GetAllActivePlayers())
        {
            var hand = new List<UltimaDeckCard>();
            for (var i = 0; i < 7; i++) hand.Add(deck.Draw());
            p.Metadata["Ultima.Hand"] = hand;
            SendHandTell(p);
        }

        var top = deck.Draw();
        discard.Add(top);
        ActiveColor = top.Color == "WILD" ? ActiveColor : top.Color;

        StatusText = $"{CurrentPlayerName()}'s turn";
        Msg.QueuePartyMessage($"[ULTIMA] Game started. Top card: {top.Code}. Active color: {ActiveColor}");
        Msg.QueuePartyMessage($"[ULTIMA] Seat order: {string.Join(" | ", turnOrder)}");
        Msg.QueuePartyMessage($"[ULTIMA] Turn: {CurrentPlayerName()}");
        currentTurnStartedUtc = DateTime.UtcNow;
        currentTurnWarningSent = false;
        return CmdResult.Ok("Game started.");
    }

    private CmdResult PlayCard(Player player, string[] args)
    {
        if (args.Length < 1) return CmdResult.Fail("Usage: PLAY [code] [color?]");
        if (!IsCurrentTurn(player)) return CmdResult.Fail("Not your turn.");

        if (!player.Metadata.TryGetValue("Ultima.Hand", out var handObj) || handObj is not List<UltimaDeckCard> hand)
            return CmdResult.Fail("No hand.");

        var code = args[0].ToUpperInvariant();
        var cardIndex = hand.FindIndex(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (cardIndex < 0) return CmdResult.Fail("Card not in hand.");

        var card = hand[cardIndex];

        // Validate card is playable: must match active color, same number, or be WILD
        if (card.Color != "WILD" && !IsCardPlayable(card))
            return CmdResult.Fail($"Card doesn't match. Play a matching color ({ActiveColor}), same number, or a WILD.");

        // Validate wild color before removing the card from hand
        if (card.Color == "WILD")
        {
            if (args.Length < 2)
                return CmdResult.Fail("Wild cards require a color. Usage: PLAY [code] [WATER|FIRE|GRASS|LIGHT]");
            var chosenColor = args[1].ToUpperInvariant();
            if (chosenColor is not ("WATER" or "FIRE" or "GRASS" or "LIGHT"))
                return CmdResult.Fail("Invalid color. Choose WATER, FIRE, GRASS, or LIGHT.");
        }

        hand.RemoveAt(cardIndex);
        discard.Add(card);

        if (card.Color == "WILD")
            ActiveColor = args[1].ToUpperInvariant();
        else
            ActiveColor = card.Color;

        HandleActionCard(card);

        if (hand.Count == 0)
        {
            StatusText = $"{player.Name} wins";
            Msg.QueuePartyMessage($"[ULTIMA] {player.Name} played {card.Code}. Color: {ActiveColor}. Dir: {(Direction > 0 ? "CW" : "CCW")}.");
            Msg.QueuePartyMessage($"[ULTIMA] {player.Name} wins!");
            currentTurnStartedUtc = DateTime.MinValue;
            turnOrder.Clear();
            OnRoundComplete();
            return CmdResult.Ok("Win.");
        }

        if (hand.Count == 1)
            Msg.QueuePartyMessage($"[ULTIMA] {player.Name} calls ULTIMA! <se.10>");

        AdvanceTurn();
        AutoDrawUntilValid();
        StatusText = $"{CurrentPlayerName()}'s turn";
        Msg.QueuePartyMessage($"[ULTIMA] {player.Name} ({hand.Count}) played {card.Code}. Color: {ActiveColor}. Dir: {(Direction > 0 ? "CW" : "CCW")}. Turn: {CurrentPlayerName()}");
        SendHandTell(player);
        currentTurnStartedUtc = DateTime.UtcNow;
        currentTurnWarningSent = false;
        return CmdResult.Ok("Card played.");
    }

    private CmdResult DrawCard(Player player)
    {
        if (!IsCurrentTurn(player)) return CmdResult.Fail("Not your turn.");
        if (!player.Metadata.TryGetValue("Ultima.Hand", out var handObj) || handObj is not List<UltimaDeckCard> hand)
            return CmdResult.Fail("No hand.");

        hand.Add(deck.Draw());
        AdvanceTurn();
        AutoDrawUntilValid();
        StatusText = $"{CurrentPlayerName()}'s turn";
        Msg.QueuePartyMessage($"[ULTIMA] {player.Name} ({hand.Count}) draws. Turn: {CurrentPlayerName()}");
        SendHandTell(player);
        currentTurnStartedUtc = DateTime.UtcNow;
        currentTurnWarningSent = false;
        return CmdResult.Ok("Draw.");
    }

    private void HandleActionCard(UltimaDeckCard card)
    {
        switch (card.Type)
        {
            case "COUNTERSPELL":
                // Skip next player's turn
                AdvanceTurn();
                break;
            case "REWIND":
                Direction *= -1;
                break;
            case "SUMMON2":
                // Next player draws 2 and their turn is skipped
                GiveCardsToNext(2);
                break;
            case "POLYMORPH4":
                // Next player draws 4 and their turn is skipped
                GiveCardsToNext(4);
                break;
        }
    }

    /// <summary>
    /// Advances to the next player, gives them cards, and leaves the index on them.
    /// PlayCard's subsequent AdvanceTurn() will skip past them.
    /// </summary>
    private void GiveCardsToNext(int amount)
    {
        AdvanceTurn(); // move to the victim
        var victim = Players.GetPlayer(turnOrder[currentIndex]);
        if (victim is null) return;
        if (!victim.Metadata.TryGetValue("Ultima.Hand", out var handObj) || handObj is not List<UltimaDeckCard> hand)
            return;

        for (var i = 0; i < amount; i++) hand.Add(deck.Draw());
        SendHandTell(victim);
        Msg.QueuePartyMessage($"[ULTIMA] {victim.Name} ({hand.Count}) draws {amount} cards.");
    }

    private bool IsCurrentTurn(Player player)
        => turnOrder.Count > 0 && turnOrder[currentIndex].Equals(player.Name, StringComparison.OrdinalIgnoreCase);

    private string CurrentPlayerName() => turnOrder.Count == 0 ? string.Empty : turnOrder[currentIndex];

    private void AdvanceTurn()
    {
        if (turnOrder.Count == 0) return;
        currentIndex = (currentIndex + Direction + turnOrder.Count) % turnOrder.Count;
    }

    /// <summary>If the current player has no valid cards, auto-draw until they get one.</summary>
    private void AutoDrawUntilValid()
    {
        var player = Players.GetPlayer(CurrentPlayerName());
        if (player is null) return;
        if (!player.Metadata.TryGetValue("Ultima.Hand", out var handObj) || handObj is not List<UltimaDeckCard> hand)
            return;

        var draws = 0;
        while (!HasValidCard(hand) && draws < 10)
        {
            hand.Add(deck.Draw());
            draws++;
        }

        if (draws > 0)
        {
            // Announce draws before sending the updated hand
            Msg.QueuePartyMessage($"[ULTIMA] {player.Name} ({hand.Count}) had no valid cards and drew {draws}.");
            SendHandTell(player);
        }
    }

    private bool HasValidCard(List<UltimaDeckCard> hand)
    {
        return hand.Any(c => c.Color == "WILD" || IsCardPlayable(c));
    }

    /// <summary>A card is playable if it matches the active color or has the same number/type as the top discard.</summary>
    private bool IsCardPlayable(UltimaDeckCard card)
    {
        if (card.Color.Equals(ActiveColor, StringComparison.OrdinalIgnoreCase))
            return true;

        // Same number/type matching: F9 can be played on W9, etc.
        if (discard.Count > 0)
        {
            var top = discard[^1];
            // Extract the suffix (number or type portion) from the code
            var cardSuffix = card.Code.Length > 1 ? card.Code[1..] : string.Empty;
            var topSuffix = top.Code.Length > 1 ? top.Code[1..] : string.Empty;
            if (!string.IsNullOrEmpty(cardSuffix) && cardSuffix.Equals(topSuffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private CmdResult SendHand(Player player)
    {
        SendHandTell(player);
        return CmdResult.Ok("Hand sent via tell.");
    }

    private void SendHandTell(Player player)
    {
        if (!player.Metadata.TryGetValue("Ultima.Hand", out var handObj) || handObj is not List<UltimaDeckCard> hand)
            return;

        var payload = string.Join(" ", hand.Select(c => c.Code));
        Msg.QueueTell(player.Name, string.IsNullOrWhiteSpace(player.HomeWorld) ? "Unknown" : player.HomeWorld, $"[ULTIMA HAND] {payload}");
    }

    private sealed class UltimaViewModel : BaseViewModel
    {
        public List<string> Actions { get; set; } = new();
        public override IReadOnlyList<string> GetActionButtons() => Actions;
    }
}
