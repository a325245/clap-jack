using System;
using System.Collections.Generic;
using System.Linq;
using SamplePlugin.Models;
using C = SamplePlugin.Models.Card;

namespace SamplePlugin.Engine;

// ── Hand evaluation types ─────────────────────────────────────────────────────

public enum HandRank
{
    HighCard      = 0,
    OnePair       = 1,
    TwoPair       = 2,
    ThreeOfAKind  = 3,
    Straight      = 4,
    Flush         = 5,
    FullHouse     = 6,
    FourOfAKind   = 7,
    StraightFlush = 8,
    RoyalFlush    = 9
}

public readonly struct HandResult
{
    public HandRank Rank        { get; init; }
    public int[]    Tiebreaker  { get; init; }
    public string   Description { get; init; }
}

// ── Engine ────────────────────────────────────────────────────────────────────

public class PokerEngine
{
    public const int MaxSeats = 8;

    public Table    CurrentTable { get; set; }
    public ChatMode ChatMode     { get; set; } = ChatMode.Party;

    public Action<string>?         OnChatMessage { get; set; }
    public Action<string, string>? OnPlayerTell  { get; set; }
    public Action?                 OnUIUpdate    { get; set; }

    public PokerSeat[] Seats { get; } = new PokerSeat[MaxSeats];

    private readonly Queue<(bool isTell, string target, string msg)> _msgQueue = new();
    private          DateTime            _lastMsg    = DateTime.MinValue;
    private static readonly Random       Rng         = new();
    private readonly Dictionary<string, int> _startBanks = new();
    private bool _pokerTimerWarned = false;

    public PokerEngine(Table table)
    {
        CurrentTable = table;
        for (int i = 0; i < MaxSeats; i++)
            Seats[i] = new PokerSeat();
    }

    // ── Messaging ─────────────────────────────────────────────────────────────

    private void QueueMessage(string msg)               => _msgQueue.Enqueue((false, string.Empty, msg));
    private void QueueTell(string target, string msg)    => _msgQueue.Enqueue((true, target, msg));
    public void ClearQueue() => _msgQueue.Clear();

    private void SendMessage(string msg)
    {
        string cmd = ChatMode == ChatMode.Party ? $"/party {msg}" : $"/say {msg}";
        OnChatMessage?.Invoke(cmd);
    }

    public void ProcessMessageQueue()
    {
        if (_msgQueue.Count > 0 &&
            (DateTime.Now - _lastMsg).TotalMilliseconds >= CurrentTable.MessageDelayMs)
        {
            var (isTell, target, msg) = _msgQueue.Dequeue();
            if (isTell)
                OnPlayerTell?.Invoke(target, msg);
            else
                SendMessage(msg);
            _lastMsg = DateTime.Now;
        }
    }

    private void TellPlayer(int seatIndex, string msg)
    {
        var player = GetPlayer(Seats[seatIndex].PlayerName);
        if (player == null) return;
        string server = string.IsNullOrEmpty(player.Server) ? "Ultros" : player.Server;
        OnPlayerTell?.Invoke($"{player.Name}@{server}", msg);
    }

    private void Log(string action) =>
        CurrentTable.GameLog.Add($"[{DateTime.Now:HH:mm:ss}] [POKER] {action}");

    private string DN(string name) => CurrentTable.GetDisplayName(name);

    // ── Card dealing

    private C Deal() => CurrentTable.DrawCard();

    // ── Seat helpers ─────────────────────────────────────────────────────────

    public int NextOccupiedSeat(int from)
    {
        for (int i = 1; i <= MaxSeats; i++)
        {
            int idx = (from + i) % MaxSeats;
            if (Seats[idx].IsOccupied) return idx;
        }
        return -1;
    }

    private int OccupiedCount()  => Seats.Count(s => s.IsOccupied);
    private int ActiveCount()    => Seats.Count(s => s.IsActive);
    private int NonFoldedCount() => Seats.Count(s => s.IsActive || s.IsAllIn);

    private Player? GetPlayer(string name) =>
        CurrentTable.Players.Values.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // ── Deal hand ─────────────────────────────────────────────────────────────

    public bool DealHand(out string error)
    {
        error = string.Empty;

        if (CurrentTable.PokerPhase != PokerPhase.WaitingForPlayers &&
            CurrentTable.PokerPhase != PokerPhase.Complete)
        { error = "A hand is already in progress."; return false; }

        // Seat all non-standing, non-AFK, solvent players (up to MaxSeats)
        int seated = 0;
        int minToPlay = CurrentTable.PokerSmallBlind * 2 + CurrentTable.PokerAnte;
        var seatedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in CurrentTable.Players.Values)
        {
            if (seated >= MaxSeats) break;
            if (player.IsKicked || player.IsAfk) continue;
            if (seatedNames.Contains(player.Name)) continue; // prevent duplicates
            if (player.Bank < minToPlay)
            {
                player.IsAfk = true;
                QueueMessage($"{DN(player.Name)} cannot afford the ante ({minToPlay}\uE049) and is set to AFK.");
                Log($"{player.Name} forced AFK - insufficient funds ({player.Bank}\uE049)");
                continue;
            }
            Seats[seated].Clear();
            Seats[seated].PlayerName = player.Name;
            Seats[seated].Status = PokerPlayerStatus.Active;
            seatedNames.Add(player.Name);
            seated++;
        }
        for (int i = seated; i < MaxSeats; i++) Seats[i].Clear();

        if (seated < 2)
        { error = "Need at least 2 players to start a hand."; QueueMessage(error); return false; }

        // Advance dealer button
        AdvanceDealerButton();

        // Prepare all occupied seats
        for (int i = 0; i < MaxSeats; i++)
            if (Seats[i].IsOccupied) Seats[i].PrepareForHand();

        // Record start banks for net-gain tracking
        _startBanks.Clear();
        foreach (var p in CurrentTable.Players.Values)
            _startBanks[p.Name.ToUpperInvariant()] = p.Bank;

        // Fresh deck
        CurrentTable.BuildDeck();
        CurrentTable.PokerCommunity.Clear();
        CurrentTable.PokerPot           = 0;
        CurrentTable.PokerStreetBet     = 0;
        CurrentTable.PokerLastAggressor = -1;

        // Collect antes
        if (CurrentTable.PokerAnte > 0)
        {
            for (int i = 0; i < MaxSeats; i++)
            {
                if (!Seats[i].IsOccupied) continue;
                var antePlayer = GetPlayer(Seats[i].PlayerName);
                if (antePlayer == null) continue;
                int anteAmt = Math.Min(CurrentTable.PokerAnte, antePlayer.Bank);
                antePlayer.Bank -= anteAmt;
                CurrentTable.PokerPot += anteAmt;
            }
        }

        // Post blinds
        int dealerSeat = CurrentTable.PokerDealerSeat;
        int sbSeat, bbSeat, utgSeat;
        int sb = CurrentTable.PokerSmallBlind;
        int bb = sb * 2;

        if (OccupiedCount() == 2)
        {
            sbSeat  = dealerSeat;
            bbSeat  = NextOccupiedSeat(sbSeat);
            utgSeat = sbSeat;
        }
        else
        {
            sbSeat  = NextOccupiedSeat(dealerSeat);
            bbSeat  = NextOccupiedSeat(sbSeat);
            utgSeat = NextOccupiedSeat(bbSeat);
        }

        PostBlind(sbSeat, sb);
        PostBlind(bbSeat, bb);
        Seats[bbSeat].HasActed      = false; // BB gets the option
        CurrentTable.PokerStreetBet     = bb;
        CurrentTable.PokerLastAggressor = bbSeat;

        // Deal 2 hole cards each, starting left of dealer
        for (int round = 0; round < 2; round++)
        {
            int seat = NextOccupiedSeat(dealerSeat);
            for (int i = 0; i < OccupiedCount(); i++)
            {
                if (round == 0) Seats[seat].HoleCard1 = Deal();
                else            Seats[seat].HoleCard2 = Deal();
                seat = NextOccupiedSeat(seat);
            }
        }

        // /tell each player their hole cards (queued with the same delay as all other messages)
        for (int i = 0; i < MaxSeats; i++)
        {
            if (!Seats[i].IsOccupied) continue;
            var p = GetPlayer(Seats[i].PlayerName);
            if (p == null) continue;
            string srv = string.IsNullOrEmpty(p.Server) ? "Ultros" : p.Server;
            QueueTell($"{p.Name}@{srv}", $"Your hole cards: {Seats[i].HoleCard1.GetCardDisplay()} {Seats[i].HoleCard2.GetCardDisplay()}");
        }

        QueueMessage($"\u2660 New hand! Dealer: {DN(Seats[dealerSeat].PlayerName)}  SB: {DN(Seats[sbSeat].PlayerName)} ({sb}\uE049)  BB: {DN(Seats[bbSeat].PlayerName)} ({bb}\uE049)");
        int    utgOwed  = CurrentTable.PokerStreetBet - Seats[utgSeat].Bet;
        string utgOpts  = utgOwed > 0
            ? $">CALL ({utgOwed}\uE049)  >RAISE [+amt]  >FOLD"
            : ">CHECK  >RAISE [+amt]  >FOLD";
        QueueMessage($"Pot: {CurrentTable.PokerPot}\uE049 | First to act: {DN(Seats[utgSeat].PlayerName)} (Bank: {GetPlayer(Seats[utgSeat].PlayerName)?.Bank ?? 0}\uE049) - {utgOpts}");

        CurrentTable.PokerPhase       = PokerPhase.PreFlop;
        CurrentTable.PokerCurrentSeat = utgSeat;
        CurrentTable.GameState        = Models.GameState.Playing;
        ResetTurnTimer();
        Log("Hand dealt");
        OnUIUpdate?.Invoke();
        return true;
    }

    private void AdvanceDealerButton()
    {
        int prev = CurrentTable.PokerDealerSeat;
        if (prev < 0)
        {
            var occ = Enumerable.Range(0, MaxSeats).Where(i => Seats[i].IsOccupied).ToList();
            CurrentTable.PokerDealerSeat = occ.Count > 0 ? occ[Rng.Next(occ.Count)] : 0;
        }
        else
        {
            CurrentTable.PokerDealerSeat = NextOccupiedSeat(prev);
        }
    }

    private void PostBlind(int seatIndex, int amount)
    {
        var seat   = Seats[seatIndex];
        var player = GetPlayer(seat.PlayerName);
        if (player == null) return;

        int actual            = Math.Min(amount, player.Bank);
        player.Bank          -= actual;
        seat.Bet             += actual;
        seat.TotalBet        += actual;
        seat.HasActed         = true;
        CurrentTable.PokerPot += actual;

        if (player.Bank == 0)
            seat.Status = PokerPlayerStatus.AllIn;
    }

    // ── Player actions ────────────────────────────────────────────────────────

    public bool PlayerFold(string playerName, out string error)
    {
        error = string.Empty;
        if (!IsPlayerTurn(playerName, out int seatIndex, out error)) return false;

        Seats[seatIndex].Status   = PokerPlayerStatus.Folded;
        Seats[seatIndex].HasActed = true;
        QueueMessage($"{DN(playerName)} folds.");
        Log($"{playerName} folded");
        AdvanceTurn();
        return true;
    }

    public bool PlayerCheck(string playerName, out string error)
    {
        error = string.Empty;
        if (!IsPlayerTurn(playerName, out int seatIndex, out error)) return false;

        int owed = CurrentTable.PokerStreetBet - Seats[seatIndex].Bet;
        if (owed > 0)
        { error = $"Cannot check - there is a {CurrentTable.PokerStreetBet}\uE049 bet. Use >CALL or >RAISE."; return false; }

        Seats[seatIndex].HasActed = true;
        QueueMessage($"{DN(playerName)} checks.");
        Log($"{playerName} checked");
        AdvanceTurn();
        return true;
    }

    public bool PlayerCall(string playerName, out string error)
    {
        error = string.Empty;
        if (!IsPlayerTurn(playerName, out int seatIndex, out error)) return false;

        var player = GetPlayer(playerName);
        if (player == null) { error = "Player not found."; return false; }

        int owed = CurrentTable.PokerStreetBet - Seats[seatIndex].Bet;
        if (owed <= 0) { error = "Nothing to call - use >CHECK."; return false; }

        int actual                 = Math.Min(owed, player.Bank);
        player.Bank               -= actual;
        Seats[seatIndex].Bet      += actual;
        Seats[seatIndex].TotalBet += actual;
        CurrentTable.PokerPot     += actual;
        Seats[seatIndex].HasActed  = true;

        if (player.Bank == 0)
        {
            Seats[seatIndex].Status = PokerPlayerStatus.AllIn;
            QueueMessage($"{DN(playerName)} calls {actual}\uE049 and is ALL IN! Pot: {CurrentTable.PokerPot}\uE049");
        }
        else
        {
            QueueMessage($"{DN(playerName)} calls {actual}\uE049. Pot: {CurrentTable.PokerPot}\uE049");
        }

        Log($"{playerName} called {actual}\uE049");
        AdvanceTurn();
        return true;
    }

    public bool PlayerRaise(string playerName, int raiseBy, out string error)
    {
        error = string.Empty;
        if (!IsPlayerTurn(playerName, out int seatIndex, out error)) return false;

        var player = GetPlayer(playerName);
        if (player == null) { error = "Player not found."; return false; }

        int minRaise = CurrentTable.PokerSmallBlind * 2;
        if (raiseBy < minRaise)
        { error = $"Minimum raise is {minRaise}\uE049."; return false; }

        int callAmount  = CurrentTable.PokerStreetBet - Seats[seatIndex].Bet;
        int totalNeeded = callAmount + raiseBy;

        if (totalNeeded > player.Bank)
        { error = "Not enough chips. Use >ALL IN to go all-in."; return false; }

        player.Bank                     -= totalNeeded;
        Seats[seatIndex].Bet            += totalNeeded;
        Seats[seatIndex].TotalBet       += totalNeeded;
        CurrentTable.PokerPot           += totalNeeded;
        CurrentTable.PokerStreetBet      = Seats[seatIndex].Bet;
        CurrentTable.PokerLastAggressor  = seatIndex;
        Seats[seatIndex].HasActed        = true;

        // Everyone else must respond
        for (int i = 0; i < MaxSeats; i++)
            if (i != seatIndex && Seats[i].IsActive) Seats[i].HasActed = false;

        QueueMessage($"{DN(playerName)} raises to {CurrentTable.PokerStreetBet}\uE049. Pot: {CurrentTable.PokerPot}\uE049");
        Log($"{playerName} raised to {CurrentTable.PokerStreetBet}\uE049");
        AdvanceTurn();
        return true;
    }

    public bool PlayerAllIn(string playerName, out string error)
    {
        error = string.Empty;
        if (!IsPlayerTurn(playerName, out int seatIndex, out error)) return false;

        var player = GetPlayer(playerName);
        if (player == null) { error = "Player not found."; return false; }
        if (player.Bank == 0) { error = "You have no chips left."; return false; }

        int amount                 = player.Bank;
        player.Bank                = 0;
        Seats[seatIndex].Bet      += amount;
        Seats[seatIndex].TotalBet += amount;
        CurrentTable.PokerPot     += amount;
        Seats[seatIndex].Status    = PokerPlayerStatus.AllIn;
        Seats[seatIndex].HasActed  = true;

        if (Seats[seatIndex].Bet > CurrentTable.PokerStreetBet)
        {
            CurrentTable.PokerStreetBet     = Seats[seatIndex].Bet;
            CurrentTable.PokerLastAggressor = seatIndex;
            for (int i = 0; i < MaxSeats; i++)
                if (i != seatIndex && Seats[i].IsActive) Seats[i].HasActed = false;
        }

        QueueMessage($"{DN(playerName)} is ALL IN for {Seats[seatIndex].TotalBet}\uE049! Pot: {CurrentTable.PokerPot}\uE049");
        Log($"{playerName} all-in for {Seats[seatIndex].TotalBet}\uE049");
        AdvanceTurn();
        return true;
    }

    private bool IsPlayerTurn(string playerName, out int seatIndex, out string error)
    {
        seatIndex = -1;
        error     = string.Empty;

        if (CurrentTable.PokerPhase == PokerPhase.WaitingForPlayers ||
            CurrentTable.PokerPhase == PokerPhase.Complete)
        { error = "No hand in progress."; return false; }

        for (int i = 0; i < MaxSeats; i++)
        {
            if (Seats[i].PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            { seatIndex = i; break; }
        }

        if (seatIndex < 0)                          { error = "You are not seated at this table."; return false; }
        if (seatIndex != CurrentTable.PokerCurrentSeat) { error = "It is not your turn."; return false; }
        if (!Seats[seatIndex].IsActive)             { error = "You cannot act - you are folded or all-in."; return false; }

        return true;
    }

    // ── Turn advancement ─────────────────────────────────────────────────────

    private void AdvanceTurn()
    {
        if (NonFoldedCount() == 1) { EndHandUncontested(); return; }
        SetNextActor(CurrentTable.PokerCurrentSeat);
    }

    // Walks forward from `from`, auto-folding any AFK players it encounters, then
    // sets the first non-AFK active player as the current actor and prompts them.
    private void SetNextActor(int from, bool isStreetStart = false)
    {
        for (int guard = 0; guard <= MaxSeats; guard++)
        {
            if (NonFoldedCount() <= 1) { EndHandUncontested(); return; }

            int next = FindNextSeatToAct(from);
            if (next == -1) { AdvanceStreet(); return; }

            var nextPlayer = GetPlayer(Seats[next].PlayerName);
            if (nextPlayer != null && nextPlayer.IsAfk)
            {
                QueueMessage($"{DN(Seats[next].PlayerName)} (AFK) auto-folds.");
                Log($"{Seats[next].PlayerName} auto-folded (AFK)");
                Seats[next].Status   = PokerPlayerStatus.Folded;
                Seats[next].HasActed = true;
                from = next;
                continue;
            }

            CurrentTable.PokerCurrentSeat = next;
            if (isStreetStart) CurrentTable.PokerLastAggressor = next;
            ResetTurnTimer();
            var   name   = Seats[next].PlayerName;
            var   player = GetPlayer(name);
            int   owed   = CurrentTable.PokerStreetBet - Seats[next].Bet;
            string opts  = owed > 0
                ? $">CALL ({owed}\uE049)  >RAISE [+amt]  >FOLD"
                : ">CHECK  >RAISE [+amt]  >FOLD";
            QueueMessage($"Action to {DN(name)} (Bank: {player?.Bank ?? 0}\uE049) - {opts}");
            OnUIUpdate?.Invoke();
            return;
        }
        AdvanceStreet();
    }

    private int FindNextSeatToAct(int from)
    {
        for (int i = 1; i <= MaxSeats; i++)
        {
            int idx = (from + i) % MaxSeats;
            var s   = Seats[idx];
            if (!s.IsActive) continue;
            if (!s.HasActed || s.Bet < CurrentTable.PokerStreetBet)
                return idx;
        }
        return -1;
    }

    // ── Street advancement ───────────────────────────────────────────────────

    private void AdvanceStreet()
    {
        for (int i = 0; i < MaxSeats; i++)
        {
            Seats[i].Bet     = 0;
            Seats[i].HasActed = false;
        }
        CurrentTable.PokerStreetBet = 0;

        switch (CurrentTable.PokerPhase)
        {
            case PokerPhase.PreFlop: DealFlop();    break;
            case PokerPhase.Flop:   DealTurnCard(); break;
            case PokerPhase.Turn:   DealRiver();    break;
            case PokerPhase.River:  StartShowdown();break;
        }
    }

    private void DealFlop()
    {
        Deal(); // burn
        var c1 = Deal(); var c2 = Deal(); var c3 = Deal();
        CurrentTable.PokerCommunity.Add(c1);
        CurrentTable.PokerCommunity.Add(c2);
        CurrentTable.PokerCommunity.Add(c3);
        CurrentTable.PokerPhase = PokerPhase.Flop;
        string board = $"{c1.GetCardDisplay()} {c2.GetCardDisplay()} {c3.GetCardDisplay()}";
        QueueMessage($"*** FLOP *** [{board}]  Pot: {CurrentTable.PokerPot}\uE049");
        Log($"Flop: {board}");
        StartStreetBetting();
    }

    private void DealTurnCard()
    {
        Deal(); // burn
        var c = Deal();
        CurrentTable.PokerCommunity.Add(c);
        CurrentTable.PokerPhase = PokerPhase.Turn;
        string board = string.Join(" ", CurrentTable.PokerCommunity.Select(x => x.GetCardDisplay()));
        QueueMessage($"*** TURN *** [{board}]  Pot: {CurrentTable.PokerPot}\uE049");
        Log($"Turn: {c.GetCardDisplay()}");
        StartStreetBetting();
    }

    private void DealRiver()
    {
        Deal(); // burn
        var c = Deal();
        CurrentTable.PokerCommunity.Add(c);
        CurrentTable.PokerPhase = PokerPhase.River;
        string board = string.Join(" ", CurrentTable.PokerCommunity.Select(x => x.GetCardDisplay()));
        QueueMessage($"*** RIVER *** [{board}]  Pot: {CurrentTable.PokerPot}\uE049");
        Log($"River: {c.GetCardDisplay()}");
        StartStreetBetting();
    }

    private void StartStreetBetting()
    {
        if (ActiveCount() <= 1) { AdvanceStreet(); return; }
        SetNextActor(CurrentTable.PokerDealerSeat, isStreetStart: true);
    }

    // ── Showdown ─────────────────────────────────────────────────────────────

    private void StartShowdown()
    {
        CurrentTable.PokerPhase = PokerPhase.Showdown;
        Log("Showdown");

        string board = string.Join(" ", CurrentTable.PokerCommunity.Select(c => c.GetCardDisplay()));
        QueueMessage($"*** SHOWDOWN *** Board: [{board}]");

        for (int i = 0; i < MaxSeats; i++)
        {
            if (!Seats[i].IsOccupied || Seats[i].IsFolded) continue;
            var all = new C[] { Seats[i].HoleCard1, Seats[i].HoleCard2 }
                .Concat(CurrentTable.PokerCommunity)
                .ToArray();
            var hr = BestHandFromCards(all);
            QueueMessage($"{DN(Seats[i].PlayerName)}: {Seats[i].HoleCard1.GetCardDisplay()} {Seats[i].HoleCard2.GetCardDisplay()} - {hr.Description}");
        }

        AwardPots();
    }

    private void EndHandUncontested()
    {
        int winnerSeat = -1;
        for (int i = 0; i < MaxSeats; i++)
            if (Seats[i].IsActive || Seats[i].IsAllIn) { winnerSeat = i; break; }

        if (winnerSeat >= 0)
        {
            var player = GetPlayer(Seats[winnerSeat].PlayerName);
            if (player != null)
            {
                player.Bank               += CurrentTable.PokerPot;
                QueueMessage($"{DN(Seats[winnerSeat].PlayerName)} wins {CurrentTable.PokerPot}\uE049 (uncontested). Bank: {player.Bank}\uE049");
                Log($"{Seats[winnerSeat].PlayerName} wins uncontested {CurrentTable.PokerPot}\uE049");
            }
        }

        CurrentTable.PokerPot = 0;
        RecordNetGains();
        EndHand();
    }

    // ── Pot award (with side pots) ────────────────────────────────────────────

    private void AwardPots()
    {
        var levels = Enumerable.Range(0, MaxSeats)
            .Where(i => Seats[i].IsOccupied && !Seats[i].IsFolded && Seats[i].TotalBet > 0)
            .Select(i => Seats[i].TotalBet)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        int remaining = CurrentTable.PokerPot;
        int prevLevel = 0;

        foreach (int level in levels)
        {
            int delta        = level - prevLevel;
            int contributors = Enumerable.Range(0, MaxSeats)
                .Count(i => Seats[i].IsOccupied && Seats[i].TotalBet >= level);
            int potSize      = Math.Min(delta * contributors, remaining);
            remaining       -= potSize;
            prevLevel        = level;

            var eligible = Enumerable.Range(0, MaxSeats)
                .Where(i => !Seats[i].IsFolded && Seats[i].IsOccupied && Seats[i].TotalBet >= level)
                .ToList();
            if (eligible.Count == 0) continue;

            var ranked = eligible
                .Select(i =>
                {
                    var all = new C[] { Seats[i].HoleCard1, Seats[i].HoleCard2 }
                        .Concat(CurrentTable.PokerCommunity).ToArray();
                    return (seat: i, hand: BestHandFromCards(all));
                })
                .OrderByDescending(x => x.hand.Rank)
                .ThenByDescending(x => x.hand.Tiebreaker, new IntArrayComparer())
                .ToList();

            var winners = new List<int> { ranked[0].seat };
            for (int k = 1; k < ranked.Count; k++)
            {
                if (ranked[k].hand.Rank == ranked[0].hand.Rank &&
                    ranked[k].hand.Tiebreaker.SequenceEqual(ranked[0].hand.Tiebreaker))
                    winners.Add(ranked[k].seat);
                else break;
            }

            int share     = potSize / winners.Count;
            int remainder = potSize - share * winners.Count;

            foreach (int w in winners)
            {
                var wp = GetPlayer(Seats[w].PlayerName);
                if (wp == null) continue;
                wp.Bank += share;
                var desc = ranked.First(x => x.seat == w).hand.Description;
                QueueMessage($"{DN(Seats[w].PlayerName)} wins {share}\uE049 with {desc}! Bank: {wp.Bank}\uE049");
                Log($"{Seats[w].PlayerName} wins {share}\uE049");
            }

            if (remainder > 0)
            {
                var wp = GetPlayer(Seats[winners[0]].PlayerName);
                if (wp != null) wp.Bank += remainder;
            }
        }

        // Any leftover (from folds) to last aggressor
        if (remaining > 0)
        {
            int seat = CurrentTable.PokerLastAggressor >= 0 &&
                       !Seats[CurrentTable.PokerLastAggressor].IsFolded
                ? CurrentTable.PokerLastAggressor
                : Enumerable.Range(0, MaxSeats)
                    .FirstOrDefault(i => !Seats[i].IsFolded && Seats[i].IsOccupied, -1);
            if (seat >= 0)
            {
                var wp = GetPlayer(Seats[seat].PlayerName);
                if (wp != null) wp.Bank += remaining;
            }
        }

        CurrentTable.PokerPot = 0;
        RecordNetGains();
        EndHand();
    }

    private void RecordNetGains()
    {
        foreach (var p in CurrentTable.Players.Values)
        {
            string key = p.Name.ToUpperInvariant();
            if (_startBanks.TryGetValue(key, out int start))
                p.PokerNetGains += (p.Bank - start);
        }
    }

    private void EndHand()
    {
        Log("Hand complete");
        CurrentTable.PokerPhase       = PokerPhase.Complete;
        CurrentTable.GameState        = Models.GameState.Lobby;
        CurrentTable.PokerCurrentSeat = -1;
        OnUIUpdate?.Invoke();
    }

    // ── Timer ─────────────────────────────────────────────────────────────────

    private void ResetTurnTimer()
    {
        CurrentTable.PokerTurnStart = DateTime.Now;
        _pokerTimerWarned = false;
    }

    public void ProcessTick()
    {
        ProcessMessageQueue();

        if (CurrentTable.PokerPhase == PokerPhase.WaitingForPlayers ||
            CurrentTable.PokerPhase == PokerPhase.Complete) return;

        int seat = CurrentTable.PokerCurrentSeat;
        if (seat < 0 || !Seats[seat].IsActive) return;

        string seatName   = Seats[seat].PlayerName;
        var    seatPlayer = GetPlayer(seatName);

        // If the current player went AFK, immediately fold them
        if (seatPlayer != null && seatPlayer.IsAfk)
        {
            QueueMessage($"{DN(seatName)} is AFK and is auto-folded.");
            Log($"{seatName} auto-folded (AFK)");
            Seats[seat].Status   = PokerPlayerStatus.Folded;
            Seats[seat].HasActed = true;
            AdvanceTurn();
            return;
        }

        double elapsed = (DateTime.Now - CurrentTable.PokerTurnStart).TotalSeconds;

        // 1/3 time remaining — warn the acting player via tell
        if (!_pokerTimerWarned && elapsed >= CurrentTable.TurnTimeLimit * 2.0 / 3.0)
        {
            _pokerTimerWarned = true;
            TellPlayer(seat, $"⏰ Hurry up! You have {Math.Max(0, (int)(CurrentTable.TurnTimeLimit - elapsed))}s left.");
        }

        if (elapsed >= CurrentTable.TurnTimeLimit)
        {
            int owed = CurrentTable.PokerStreetBet - Seats[seat].Bet;
            if (owed <= 0)
            {
                // Nothing to call — auto-check costs nothing
                QueueMessage($"{DN(seatName)} ran out of time and auto-checks.");
                Log($"{seatName} auto-checked on timeout");
                Seats[seat].HasActed = true;
            }
            else
            {
                QueueMessage($"{DN(seatName)} ran out of time and is auto-folded.");
                Log($"{seatName} auto-folded on timeout");
                Seats[seat].Status = PokerPlayerStatus.Folded;
                Seats[seat].HasActed = true;
                if (seatPlayer != null) seatPlayer.IsAfk = true;
            }
            AdvanceTurn();
        }
    }

    // ── Force stop ────────────────────────────────────────────────────────────

    public void ForceStop()
    {
        _msgQueue.Clear();

        for (int i = 0; i < MaxSeats; i++)
        {
            if (!Seats[i].IsOccupied) continue;
            var player = GetPlayer(Seats[i].PlayerName);
            if (player != null && Seats[i].TotalBet > 0)
            {
                player.Bank += Seats[i].TotalBet;
                QueueMessage($"{DN(Seats[i].PlayerName)}: {Seats[i].TotalBet}\uE049 refunded -> Bank: {player.Bank}\uE049");
            }
        }

        QueueMessage("Poker hand force stopped by dealer. All bets refunded.");
        Log("Hand force stopped - refunding all bets");

        for (int i = 0; i < MaxSeats; i++) Seats[i].Clear();
        CurrentTable.PokerCommunity.Clear();
        CurrentTable.PokerPot           = 0;
        CurrentTable.PokerStreetBet     = 0;
        CurrentTable.PokerCurrentSeat   = -1;
        CurrentTable.PokerPhase         = PokerPhase.WaitingForPlayers;
        CurrentTable.GameState          = Models.GameState.Lobby;
        OnUIUpdate?.Invoke();
    }

    // ── Table announcement ────────────────────────────────────────────────────

    public void AnnounceTable()
    {
        var occupied = Enumerable.Range(0, MaxSeats)
            .Where(i => Seats[i].IsOccupied).ToList();

        if (occupied.Count == 0)
        { QueueMessage("No players seated."); return; }

        string order = string.Join(" -> ", occupied.Select(i => $"Seat {i + 1}: {DN(Seats[i].PlayerName)}"));
        int    dealer = CurrentTable.PokerDealerSeat;
        string dealerName = dealer >= 0 && dealer < MaxSeats && Seats[dealer].IsOccupied
            ? DN(Seats[dealer].PlayerName) : "TBD";

        string anteStr = CurrentTable.PokerAnte > 0 ? $"  Ante: {CurrentTable.PokerAnte}\uE049" : string.Empty;
        QueueMessage($"Seat order: {order}");
        QueueMessage($"Dealer: {dealerName}  SB: {CurrentTable.PokerSmallBlind}\uE049  BB: {CurrentTable.PokerSmallBlind * 2}\uE049{anteStr}");
    }

    // ── Hand evaluator ────────────────────────────────────────────────────────

    public HandResult BestHandFromCards(C[] cards)
    {
        if (cards.Length < 5)
        {
            int[] ranks = cards.Select(CardRank).OrderByDescending(x => x).ToArray();
            return new HandResult { Rank = HandRank.HighCard, Tiebreaker = ranks, Description = "High Card" };
        }

        HandResult best = default;
        bool first = true;

        for (int a = 0; a < cards.Length - 4; a++)
        for (int b = a + 1; b < cards.Length - 3; b++)
        for (int cc = b + 1; cc < cards.Length - 2; cc++)
        for (int d = cc + 1; d < cards.Length - 1; d++)
        for (int e = d + 1; e < cards.Length;     e++)
        {
            var hr = Evaluate5(cards[a], cards[b], cards[cc], cards[d], cards[e]);
            if (first || CompareHands(hr, best) > 0)
            { best = hr; first = false; }
        }

        return best;
    }

    private static HandResult Evaluate5(C a, C b, C cc, C d, C e)
    {
        int[] ranks = new[] { CardRank(a), CardRank(b), CardRank(cc), CardRank(d), CardRank(e) };
        Array.Sort(ranks);
        Array.Reverse(ranks);

        bool isFlush    = a.Suit == b.Suit && b.Suit == cc.Suit && cc.Suit == d.Suit && d.Suit == e.Suit;
        bool isStraight = IsStraight(ranks, out int highCard);

        if (isFlush && isStraight)
        {
            if (highCard == 14)
                return new HandResult { Rank = HandRank.RoyalFlush,    Tiebreaker = new[] { 14 },      Description = "Royal Flush" };
            return     new HandResult { Rank = HandRank.StraightFlush, Tiebreaker = new[] { highCard }, Description = $"Straight Flush, {RankName(highCard)} high" };
        }

        var groups = ranks.GroupBy(r => r)
            .Select(g => (Rank: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.Rank)
            .ToArray();

        if (groups[0].Count == 4)
        {
            int quad = groups[0].Rank, kick = groups[1].Rank;
            return new HandResult { Rank = HandRank.FourOfAKind, Tiebreaker = new[] { quad, kick }, Description = $"Four of a Kind, {RankName(quad)}s" };
        }

        if (groups[0].Count == 3 && groups[1].Count == 2)
            return new HandResult { Rank = HandRank.FullHouse, Tiebreaker = new[] { groups[0].Rank, groups[1].Rank }, Description = $"Full House, {RankName(groups[0].Rank)}s full of {RankName(groups[1].Rank)}s" };

        if (isFlush)
            return new HandResult { Rank = HandRank.Flush, Tiebreaker = ranks, Description = $"Flush, {RankName(ranks[0])} high" };

        if (isStraight)
            return new HandResult { Rank = HandRank.Straight, Tiebreaker = new[] { highCard }, Description = $"Straight, {RankName(highCard)} high" };

        if (groups[0].Count == 3)
        {
            int   trips   = groups[0].Rank;
            int[] kickers = groups.Skip(1).Select(g => g.Rank).ToArray();
            return new HandResult { Rank = HandRank.ThreeOfAKind, Tiebreaker = new[] { trips }.Concat(kickers).ToArray(), Description = $"Three of a Kind, {RankName(trips)}s" };
        }

        if (groups[0].Count == 2 && groups[1].Count == 2)
        {
            int high2 = Math.Max(groups[0].Rank, groups[1].Rank);
            int low2  = Math.Min(groups[0].Rank, groups[1].Rank);
            int kick  = groups[2].Rank;
            return new HandResult { Rank = HandRank.TwoPair, Tiebreaker = new[] { high2, low2, kick }, Description = $"Two Pair, {RankName(high2)}s and {RankName(low2)}s" };
        }

        if (groups[0].Count == 2)
        {
            int   pair    = groups[0].Rank;
            int[] kickers = groups.Skip(1).Select(g => g.Rank).ToArray();
            return new HandResult { Rank = HandRank.OnePair, Tiebreaker = new[] { pair }.Concat(kickers).ToArray(), Description = $"Pair of {RankName(pair)}s" };
        }

        return new HandResult { Rank = HandRank.HighCard, Tiebreaker = ranks, Description = $"{RankName(ranks[0])} high" };
    }

    private static bool IsStraight(int[] sortedDesc, out int highCard)
    {
        highCard = sortedDesc[0];
        bool normal = true;
        for (int i = 0; i < 4; i++)
            if (sortedDesc[i] - sortedDesc[i + 1] != 1) { normal = false; break; }
        if (normal) return true;

        // Wheel: A-2-3-4-5
        if (sortedDesc[0] == 14 && sortedDesc[1] == 5 && sortedDesc[2] == 4 &&
            sortedDesc[3] == 3  && sortedDesc[4] == 2)
        { highCard = 5; return true; }

        return false;
    }

    private static int CompareHands(HandResult a, HandResult b)
    {
        if (a.Rank != b.Rank) return a.Rank.CompareTo(b.Rank);
        int len = Math.Min(a.Tiebreaker.Length, b.Tiebreaker.Length);
        for (int i = 0; i < len; i++)
        {
            int cmp = a.Tiebreaker[i].CompareTo(b.Tiebreaker[i]);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    private static int CardRank(C c) => c.Value switch
    {
        "A"  => 14,
        "K"  => 13,
        "Q"  => 12,
        "J"  => 11,
        "10" => 10,
        _    => int.TryParse(c.Value, out int n) ? n : 0
    };

    private static string RankName(int r) => r switch
    {
        14 => "Ace", 13 => "King", 12 => "Queen", 11 => "Jack",
        10 => "Ten",  9 => "Nine",  8 => "Eight",  7 => "Seven",
         6 => "Six",  5 => "Five",  4 => "Four",   3 => "Three",
         2 => "Two",  _ => r.ToString()
    };

    private sealed class IntArrayComparer : IComparer<int[]>
    {
        public int Compare(int[]? x, int[]? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            int len = Math.Min(x.Length, y.Length);
            for (int i = 0; i < len; i++)
            {
                int cmp = x[i].CompareTo(y[i]);
                if (cmp != 0) return cmp;
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}
