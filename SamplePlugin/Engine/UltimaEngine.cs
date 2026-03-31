using System;
using System.Collections.Generic;
using System.Linq;
using SamplePlugin.Models;

namespace SamplePlugin.Engine;

/// <summary>
/// Ultima! — a no-bet, no-dealer card game for 2-8 players.
/// Rules clone of Uno with custom FFXIV-themed card names.
/// </summary>
public class UltimaEngine
{
    private readonly Table _table;

    // ── Events (wired up in Plugin.cs just like other engines) ───────────────
    public event Action<string>?         OnChatMessage;
    public event Action<string, string>? OnPlayerTell;
    public event Action?                 OnUIUpdate;

    public ChatMode ChatMode { get; set; } = ChatMode.Party;

    private DateTime _turnStart        = DateTime.MinValue;
    private bool     _turnTimerWarned  = false;
    private int      _startGameMsgCount = 0; // queued messages before first turn
    private int      _pendingDrawCount  = 0; // set by DrawCard for combined announcement

    public UltimaEngine(Table table) => _table = table;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string DN(string name) => _table.GetDisplayName(name);

    private void Send(string msg)
    {
        string prefix = ChatMode == ChatMode.Party ? "/party " : "/say ";
        OnChatMessage?.Invoke(prefix + msg);
    }

    /// <summary>Send a private tell using the correct Name@Server format required by FFXIV.</summary>
    private void Tell(string playerName, string msg)
    {
        var p = _table.Players.Values.FirstOrDefault(x =>
            x.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
        if (p == null) return;
        string server = string.IsNullOrEmpty(p.Server) ? "Ultros" : p.Server;
        OnPlayerTell?.Invoke($"{p.Name}@{server}", msg);
    }

    private string CurrentPlayer => _table.UltimaPlayerOrder[_table.UltimaCurrentIndex];

    private void AdvanceTurn()
    {
        int count = _table.UltimaPlayerOrder.Count;
        _table.UltimaCurrentIndex = _table.UltimaClockwise
            ? (_table.UltimaCurrentIndex + 1) % count
            : ((_table.UltimaCurrentIndex - 1) + count) % count;
    }

    private UltimaCard DrawFromPile()
    {
        if (_table.UltimaDrawPile.Count == 0)
            ReshuffleDiscard();

        var card = _table.UltimaDrawPile[^1];
        _table.UltimaDrawPile.RemoveAt(_table.UltimaDrawPile.Count - 1);
        return card;
    }

    private void ReshuffleDiscard()
    {
        if (_table.UltimaDiscardPile.Count <= 1)
        {
            _table.UltimaDrawPile = UltimaCard.CreateDeck();
            UltimaCard.Shuffle(_table.UltimaDrawPile);
            Send("The draw pile was exhausted — a fresh deck was shuffled in!");
            return;
        }
        var top = _table.UltimaDiscardPile[^1];
        _table.UltimaDrawPile = _table.UltimaDiscardPile.GetRange(0, _table.UltimaDiscardPile.Count - 1);
        _table.UltimaDiscardPile.Clear();
        _table.UltimaDiscardPile.Add(top);
        UltimaCard.Shuffle(_table.UltimaDrawPile);
        Send("The discard pile was reshuffled into the draw pile!");
    }

    private void DealCardsTo(string player, int count)
    {
        if (!_table.UltimaHands.TryGetValue(player, out var hand))
            _table.UltimaHands[player] = hand = new List<UltimaCard>();

        var drawn = new List<UltimaCard>(count);
        for (int i = 0; i < count; i++)
            drawn.Add(DrawFromPile());

        hand.AddRange(drawn);
        Tell(player, $"You drew {count} card{(count > 1 ? "s" : "")}: {string.Join(" ", drawn.Select(c => c.Code))}  [Hand: {HandStr(player)}]");
    }

    private string HandStr(string player)
    {
        if (!_table.UltimaHands.TryGetValue(player, out var h)) return "(none)";
        return string.Join(" ", h.Select(c => c.Code));
    }

    private void AnnounceTopCard()
    {
        // Delay the first turn's timer so it starts AFTER all queued hand-tells
        // have actually been delivered (one message per player + start messages).
        if (_startGameMsgCount > 0)
        {
            _turnStart = DateTime.Now.AddMilliseconds(_startGameMsgCount * (double)_table.MessageDelayMs);
            _startGameMsgCount = 0;
        }
        else
        {
            _turnStart = DateTime.Now;
        }
        _turnTimerWarned = false;
        var tc = _table.UltimaTopCard!;
        string colorInfo = tc.IsWild
            ? $"Color: {UltimaCard.ColorDisplayName(_table.UltimaActiveColor)}"
            : string.Empty;
        // Use display name so chat is readable; myTurn comparison uses StartsWith to handle first-name-only display.
        Send($"Top card: [{tc.Code}] {tc.DisplayName} {colorInfo}  \u25ba {DN(CurrentPlayer)}'s turn!");
    }

    private void CheckUltima(string player)
    {
        if (_table.UltimaHands.TryGetValue(player, out var h) && h.Count == 1)
        {
            Send($"{DN(player)} — ULTIMA!");
            _table.UltimaCalled.Add(player);
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Start a new Ultima! game. Any player (2-8) can trigger this.</summary>
    public bool StartGame(string callerName, out string error)
    {
        error = string.Empty;
        if (_table.UltimaPhase == UltimaPhase.Playing)
        { error = "A game is already in progress!"; return false; }

        var players = _table.Players.Values.Select(p => p.Name).ToList();
        if (players.Count < 2)
        { error = "Ultima! needs at least 2 players at the table!"; return false; }
        if (players.Count > 8)
        { error = "Ultima! supports up to 8 players!"; return false; }

        // ── Reset state ───────────────────────────────────────────────────────
        _table.UltimaHands = new Dictionary<string, List<UltimaCard>>(StringComparer.OrdinalIgnoreCase);
        _table.UltimaCalled.Clear();
        _table.UltimaWinner      = string.Empty;
        _table.UltimaClockwise   = true;
        _table.UltimaActiveColor = UltimaColor.Wild;
        _table.UltimaTopCard     = null;

        // Shuffle player order (randomise who goes first)
        _table.UltimaPlayerOrder = players.OrderBy(_ => Guid.NewGuid()).ToList();
        _table.UltimaCurrentIndex = 0;
        _turnStart       = DateTime.MaxValue;
        _turnTimerWarned = false;

        _table.GameType = GameType.Ultima;
        _table.UltimaPhase = UltimaPhase.Playing;

        // ── Build and shuffle deck ─────────────────────────────────────────────
        _table.UltimaDrawPile = UltimaCard.CreateDeck();
        UltimaCard.Shuffle(_table.UltimaDrawPile);
        _table.UltimaDiscardPile.Clear();

        // ── Deal 7 cards to every player ──────────────────────────────────────
        foreach (var p in _table.UltimaPlayerOrder)
        {
            _table.UltimaHands[p] = new List<UltimaCard>();
            for (int i = 0; i < 7; i++)
                _table.UltimaHands[p].Add(DrawFromPile());
        }

        // ── Flip first card; re-draw if wild ──────────────────────────────────
        UltimaCard startCard;
        do { startCard = DrawFromPile(); }
        while (startCard.IsWild); // wilds may not start the game

        _table.UltimaTopCard     = startCard;
        _table.UltimaActiveColor = startCard.Color;
        _table.UltimaDiscardPile.Add(startCard);

        // ── Apply starting card effects ───────────────────────────────────────
        string openingEffect = string.Empty;
        if (startCard.Type == UltimaCardType.Rewind)
        {
            _table.UltimaClockwise = false;
            openingEffect = "  Rewind — play starts counter-clockwise!";
        }
        else if (startCard.Type == UltimaCardType.Counterspell)
        {
            AdvanceTurn(); // skip first player
            openingEffect = $"  Counterspell — {DN(_table.UltimaPlayerOrder[0])} is skipped!";
        }
        else if (startCard.Type == UltimaCardType.Summon)
        {
            // First player draws 2 and their turn is skipped
            string first = CurrentPlayer;
            DealCardsTo(first, 2);
            Send($"Summon+2 on the opening! {DN(first)} draws 2 cards and is skipped!");
            AdvanceTurn();
        }

        // ── Send private hand tells ───────────────────────────────────────────
        foreach (var p in _table.UltimaPlayerOrder)
            Tell(p, $"Your Ultima! hand: {HandStr(p)} (7 cards)");

        // ── Announce game start ───────────────────────────────────────────────
        // Use display names in chat so the game is readable.
        string order = string.Join(" \u2192 ", _table.UltimaPlayerOrder.Select(DN));
        // Account for all queued messages so first-turn timer only starts after
        // every player has received their hand tell.
        _startGameMsgCount = _table.UltimaPlayerOrder.Count + 2; // n tells + begins + order
        Send($"\u2756 Ultima! begins! \u2756  [{startCard.Code}] {startCard.DisplayName}{openingEffect}");
        Send($"Turn order: {order}");
        AnnounceTopCard();

        _table.GameType = GameType.Ultima;
        OnUIUpdate?.Invoke();
        return true;
    }

    /// <summary>A player plays a card. <paramref name="chosenColor"/> is required for Polymorph cards.</summary>
    public bool PlayCard(string playerName, string cardCode, string? chosenColor, out string error)
    {
        error = string.Empty;
        if (_table.UltimaPhase != UltimaPhase.Playing)
        { error = "No Ultima! game in progress."; return false; }
        if (!playerName.Equals(CurrentPlayer, StringComparison.OrdinalIgnoreCase))
        { error = $"It's {DN(CurrentPlayer)}'s turn, not yours!"; return false; }

        var card = UltimaCard.Parse(cardCode);
        if (card == null)
        { error = $"Unknown card '{cardCode}'. Usage: >PLAY W3  or  >PLAY PL WATER"; return false; }

        // ── Find card in hand (match by Code) ─────────────────────────────────
        if (!_table.UltimaHands.TryGetValue(playerName, out var hand))
        { error = "You have no cards!"; return false; }

        int idx = hand.FindIndex(c => c.Code.Equals(card.Code, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        { error = $"You don't have [{card.Code}] in your hand."; return false; }

        // ── Validate play against top card ────────────────────────────────────
        if (_table.UltimaTopCard != null &&
            !UltimaCard.CanPlay(card, _table.UltimaTopCard, _table.UltimaActiveColor))
        { error = $"[{card.Code}] can't be played on [{_table.UltimaTopCard.Code}] (active color: {UltimaCard.ColorDisplayName(_table.UltimaActiveColor)})."; return false; }

        // ── Wild cards require a chosen color ─────────────────────────────────
        UltimaColor newColor = UltimaColor.Wild;
        if (card.IsWild)
        {
            if (string.IsNullOrWhiteSpace(chosenColor))
            { error = "Polymorph requires a color: >PLAY PL WATER  (Water/Fire/Grass/Love)"; return false; }
            var parsed = UltimaCard.ParseColor(chosenColor);
            if (parsed == null)
            { error = $"Unknown color '{chosenColor}'. Use Water, Fire, Grass, or Love."; return false; }
            newColor = parsed.Value;
        }

        // ── Execute: remove from hand, place on discard ───────────────────────
        hand.RemoveAt(idx);
        _table.UltimaTopCard = card;
        _table.UltimaDiscardPile.Add(card);
        _table.UltimaActiveColor = card.IsWild ? newColor : card.Color;

        // ── Build play announcement (combined draw-and-play when called from DrawCard) ──
        {
            string colorEx = card.IsWild ? $" ({UltimaCard.ColorDisplayName(_table.UltimaActiveColor)})" : string.Empty;
            string countTag = $" ({hand.Count} cards)";
            string playMsg;
            if (_pendingDrawCount > 0)
            {
                int n = _pendingDrawCount;
                _pendingDrawCount = 0;
                playMsg = n == 1
                    ? $"{DN(playerName)} drew a card and plays [{card.Code}] {card.DisplayName}{colorEx}!{countTag}"
                    : $"{DN(playerName)} drew {n} cards until they could play \u2014 [{card.Code}] {card.DisplayName}{colorEx}!{countTag}";
            }
            else
            {
                playMsg = $"{DN(playerName)} plays [{card.Code}] {card.DisplayName}!{countTag}";
            }
            Send(playMsg);
        }

        // ── Win check ─────────────────────────────────────────────────────────
        if (hand.Count == 0)
        {
            _table.UltimaPhase  = UltimaPhase.Complete;
            _table.UltimaWinner = playerName;
            Send($"\u2756 {DN(playerName)} wins Ultima! GG!");
            foreach (var p in _table.Players.Values)
            {
                if (p.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase)) p.UltimaWins++;
                else if (!p.IsKicked) p.UltimaLosses++;
            }
            _table.GameType = GameType.None;
            OnUIUpdate?.Invoke();
            return true;
        }

        CheckUltima(playerName);

        // ── Apply card effects ─────────────────────────────────────────────────
        ApplyEffect(card, playerName, newColor);

        Tell(playerName, $"Hand: {HandStr(playerName)}");
        OnUIUpdate?.Invoke();
        return true;
    }

    private void ApplyEffect(UltimaCard card, string playedBy, UltimaColor newColor)
    {
        switch (card.Type)
        {
            case UltimaCardType.Counterspell:
                AdvanceTurn();
                Send($"Counterspell! {DN(CurrentPlayer)} is skipped!");
                AdvanceTurn();
                break;

            case UltimaCardType.Rewind:
                _table.UltimaClockwise = !_table.UltimaClockwise;
                string dir = _table.UltimaClockwise ? "clockwise" : "counter-clockwise";
                Send($"Rewind! Direction reversed — now {dir}!");
                if (_table.UltimaPlayerOrder.Count == 2)
                    AdvanceTurn(); // 2-player: reverse acts like skip
                else
                    AdvanceTurn();
                break;

            case UltimaCardType.Summon:
                AdvanceTurn();
                string victim2 = CurrentPlayer;
                DealCardsTo(victim2, 2);
                {
                    int vc = _table.UltimaHands.TryGetValue(victim2, out var vh2) ? vh2.Count : 0;
                    Send($"Summon+2! {DN(victim2)} draws 2 cards and is skipped! ({vc} cards)");
                }
                AdvanceTurn();
                break;

            case UltimaCardType.PolymorphSummon:
                Send($"Polymorph+4! Color changed to {UltimaCard.ColorDisplayName(newColor)}!");
                AdvanceTurn();
                string victim4 = CurrentPlayer;
                DealCardsTo(victim4, 4);
                {
                    int vc = _table.UltimaHands.TryGetValue(victim4, out var vh4) ? vh4.Count : 0;
                    Send($"{DN(victim4)} draws 4 cards and is skipped! ({vc} cards)");
                }
                AdvanceTurn();
                break;

            case UltimaCardType.Polymorph:
                Send($"Polymorph! Color changed to {UltimaCard.ColorDisplayName(newColor)}!");
                AdvanceTurn();
                break;

            default:
                AdvanceTurn();
                break;
        }

        if (_table.UltimaPhase == UltimaPhase.Playing)
            AnnounceTopCard();
    }

    /// <summary>A player draws a card. Draws repeatedly until a playable card is found.</summary>
    public bool DrawCard(string playerName, out string error)
    {
        error = string.Empty;
        if (_table.UltimaPhase != UltimaPhase.Playing)
        { error = "No Ultima! game in progress."; return false; }
        if (!playerName.Equals(CurrentPlayer, StringComparison.OrdinalIgnoreCase))
        { error = $"It's {DN(CurrentPlayer)}'s turn, not yours!"; return false; }

        if (!_table.UltimaHands.TryGetValue(playerName, out var hand))
            _table.UltimaHands[playerName] = hand = new List<UltimaCard>();

        // If the player already has a valid card they must play it, not draw.
        if (_table.UltimaTopCard != null &&
            hand.Any(c => UltimaCard.CanPlay(c, _table.UltimaTopCard, _table.UltimaActiveColor)))
        {
            error = "You have a valid card to play! Use >PLAY to play it.";
            return false;
        }

        // Draw until a playable card is found, then auto-play it.
        int totalDrawn = 0;
        UltimaCard? playable = null;
        const int safetyLimit = 20;
        while (totalDrawn < safetyLimit)
        {
            var drawn = DrawFromPile();
            totalDrawn++;
            if (_table.UltimaTopCard == null ||
                UltimaCard.CanPlay(drawn, _table.UltimaTopCard, _table.UltimaActiveColor))
            {
                playable = drawn;
                break;
            }
            hand.Add(drawn); // non-playable: goes into hand
        }

        if (playable == null)
        {
            // Edge case: couldn't find a playable card in safetyLimit draws; just end turn
            Send($"{DN(playerName)} draws {totalDrawn} cards. ({hand.Count} cards)");
            Tell(playerName, $"[Hand: {HandStr(playerName)}]");
            AdvanceTurn();
            AnnounceTopCard();
            OnUIUpdate?.Invoke();
            return true;
        }

        // Auto-choose colour for wilds (most common colour in remaining hand)
        string? chosenColor = null;
        if (playable.IsWild)
        {
            var freq = hand.Where(c => !c.IsWild)
                .GroupBy(c => c.Color)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();
            chosenColor = UltimaCard.ColorDisplayName(freq?.Key ?? UltimaColor.Water).ToUpperInvariant();
        }

        // Add the playable card to hand so PlayCard can locate and remove it
        hand.Add(playable);
        // Tell PlayCard how many cards were drawn so it formats the combined announcement
        _pendingDrawCount = totalDrawn;
        PlayCard(playerName, playable.Code, chosenColor, out _);

        return true;
    }

    /// <summary>A player announces "Ultima" (has 1 card left).</summary>
    public void CallUltima(string playerName)
    {
        if (_table.UltimaPhase != UltimaPhase.Playing) return;
        if (!_table.UltimaHands.TryGetValue(playerName, out var h) || h.Count != 1)
        {
            Tell(playerName, "You can only call ULTIMA when you have exactly 1 card left.");
            return;
        }
        _table.UltimaCalled.Add(playerName);
        Send($"{DN(playerName)} calls ULTIMA!");
    }

    /// <summary>Sort a player's hand and send them the updated tell.</summary>
    public void SortHand(string playerName, bool byColor)
    {
        if (!_table.UltimaHands.TryGetValue(playerName, out var hand)) return;

        if (byColor)
            hand.Sort((a, b) => a.Color != b.Color
                ? ((int)a.Color).CompareTo((int)b.Color)
                : ((int)a.Type).CompareTo((int)b.Type));
        else
            hand.Sort((a, b) => a.Type != b.Type
                ? ((int)a.Type).CompareTo((int)b.Type)
                : ((int)a.Color).CompareTo((int)b.Color));

        string mode = byColor ? "color" : "rank";
        Tell(playerName, $"Hand sorted by {mode}: {HandStr(playerName)}");
    }

    /// <summary>Admin: force-end the current game.</summary>
    public void ForceEnd()
    {
        _table.UltimaPhase = UltimaPhase.WaitingForPlayers;
        _table.GameType    = GameType.None;
        Send("Ultima! game ended by the host.");
        OnUIUpdate?.Invoke();
    }

    /// <summary>Admin: re-send a player's hand as a tell.</summary>
    public void ResendHand(string playerName)
    {
        if (_table.UltimaHands.TryGetValue(playerName, out var h))
            Tell(playerName, $"Your Ultima! hand: {HandStr(playerName)} ({h.Count} cards)");
    }

    /// <summary>Called when a player times out. Draws one card and ends their turn.</summary>
    private void ForceTimeoutAction(string playerName)
    {
        if (_table.UltimaPhase != UltimaPhase.Playing) return;
        if (!_table.UltimaHands.TryGetValue(playerName, out var hand))
            _table.UltimaHands[playerName] = hand = new List<UltimaCard>();

        var c = DrawFromPile();
        hand.Add(c);
        Send($"{DN(playerName)} ran out of time and draws a card. ({hand.Count} cards)");
        Tell(playerName, $"You drew: [{c.Code}] {c.DisplayName}  [Hand: {HandStr(playerName)}]");
        AdvanceTurn();
        AnnounceTopCard();
        OnUIUpdate?.Invoke();
    }

    /// <summary>Called every frame from Plugin.DrawUI. Auto-draws for a player who times out.</summary>
    public void ProcessTick()
    {
        if (_table.UltimaPhase != UltimaPhase.Playing) return;
        if (_table.UltimaPlayerOrder.Count == 0) return;

        double elapsed = (DateTime.Now - _turnStart).TotalSeconds;
        int    limit   = _table.TurnTimeLimit;

        if (!_turnTimerWarned && elapsed >= limit * 0.66)
        {
            _turnTimerWarned = true;
            Send($"!! {DN(CurrentPlayer)} has {limit - (int)elapsed}s left \u2014 play a card or >DRAW!");
        }

        if (elapsed >= limit)
        {
            ForceTimeoutAction(CurrentPlayer);
        }
    }
}
