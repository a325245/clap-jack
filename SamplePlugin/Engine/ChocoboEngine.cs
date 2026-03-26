using System;
using System.Collections.Generic;
using System.Linq;
using SamplePlugin.Models;

namespace SamplePlugin.Engine;

// ── Chocobo racer definition ──────────────────────────────────────────────────

public class ChocoboRacer
{
    public int    Number    { get; init; }
    public string Name      { get; init; } = string.Empty;
    public int    Speed     { get; init; }   // 1-100, drives early-race pace
    public int    Endurance { get; init; }   // 1-100, drives late-race pace
    public float  Odds      { get; init; }   // total return multiplier  (bet * Odds back on win)
    public string Color     { get; init; } = string.Empty;
}

// ── Engine ────────────────────────────────────────────────────────────────────

public class ChocoboEngine
{
    public Table    CurrentTable { get; set; }
    public ChatMode ChatMode     { get; set; } = ChatMode.Say;

    public Action<string>?         OnChatMessage { get; set; }
    public Action<string, string>? OnPlayerTell  { get; set; }
    public Action?                 OnUIUpdate    { get; set; }

    private readonly Queue<string> MessageQueue = new();
    private DateTime LastMessage = DateTime.MinValue;
    private static readonly Random Rng = new();

    // ── Racer name pool (24 entries — 8 are drawn per race) ────────────────────
    //   Speed = early-race advantage   Endurance = late-race advantage
    //   Odds and race number are assigned fresh each race by power rank.
    private static readonly (string Name, int Speed, int Endurance)[] RacerPool =
    {
        ("Gilded Gale",        88, 65), ("Thunderplume",       82, 78), ("Crimson Flash",     96, 45),
        ("Dusty Wings",        68, 92), ("Storm Rider",        91, 62), ("Pale Comet",        75, 82),
        ("Sable Wing",         80, 72), ("Golden Talon",       70, 85), ("Marshland Strider", 62, 90),
        ("Cloud Dancer",       78, 68), ("Ember Dash",         85, 52), ("Amber Squall",      66, 78),
        ("Iron Beak",          58, 82), ("Featherfoot",        72, 55), ("Russet Runner",     60, 72),
        ("Duskdown",           52, 84), ("Ivory Sprint",       84, 44), ("Mossy Bolt",        64, 62),
        ("Cobblestone",        50, 68), ("Tattered Brim",      56, 58), ("Muddy Hooves",      54, 60),
        ("Lowland Trot",       46, 65), ("Last Hurrah",        60, 48), ("Plucky Pete",       62, 44),
    };

    private static readonly float[]  OddsByRank  = { 2.0f, 2.5f, 3.0f, 3.5f, 4.5f, 5.0f, 7.0f, 9.0f };
    private static readonly string[] ColorByRank = { "*", "~", "!", ".", "+", "o", "#", "%" };

    // Active 8 racers for the current race — reshuffled each OpenBetting()
    public ChocoboRacer[] Roster { get; private set; }

    // ── Race state ────────────────────────────────────────────────────────────
    // Cumulative progress per racer per segment — [racerIndex, segmentIndex]
    private readonly float[,] _segmentCumProgress = new float[8, TotalSegments];
    private int   _lastAnnouncedSegment = -1;
    public  int   WinnerIndex           { get; private set; } = -1;

    private const double RaceDurationMs = 30_000;
    private const int    TotalSegments  = 6;          // one every 5 s

    public ChocoboEngine(Table table) { CurrentTable = table; ShuffleRoster(); }

    // ── Roster shuffle ────────────────────────────────────────────────────────

    private void ShuffleRoster()
    {
        // Pick 8 at random, then rank by raw power so #1 is always the favourite
        var selected = RacerPool
            .OrderBy(_ => Rng.Next())
            .Take(8)
            .OrderByDescending(r => r.Speed + r.Endurance)
            .ToArray();

        Roster = selected.Select((r, i) => new ChocoboRacer
        {
            Number    = i + 1,
            Name      = r.Name,
            Speed     = r.Speed,
            Endurance = r.Endurance,
            Odds      = OddsByRank[i],
            Color     = ColorByRank[i],
        }).ToArray();
    }

    // ── Messaging ─────────────────────────────────────────────────────────────

    private void SendMessage(string msg)
    {
        string cmd = ChatMode == ChatMode.Party ? $"/party {msg}" : $"/say {msg}";
        OnChatMessage?.Invoke(cmd);
        LastMessage = DateTime.Now;
    }

    private void QueueMessage(string msg) => MessageQueue.Enqueue(msg);

    public void ProcessMessageQueue()
    {
        if (MessageQueue.Count > 0 &&
            (DateTime.Now - LastMessage).TotalMilliseconds >= CurrentTable.MessageDelayMs)
        {
            SendMessage(MessageQueue.Dequeue());
            LastMessage = DateTime.Now;
        }
    }

    // ── Bet placement ──────────────────────────────────────────────────────────

    public bool PlaceBet(string playerName, int racerNumber, int amount, out string error)
    {
        error = string.Empty;

        if (CurrentTable.ChocoboRacePhase != ChocoboRacePhase.WaitingForBets)
        { error = "Betting is closed — a race is already in progress."; return false; }

        if (racerNumber < 1 || racerNumber > Roster.Length)
        { error = $"Invalid chocobo number. Choose 1-{Roster.Length}."; return false; }

        if (amount < CurrentTable.MinBet || amount > CurrentTable.MaxBet)
        { error = $"Bet must be {CurrentTable.MinBet}-{CurrentTable.MaxBet}G."; return false; }

        var player = GetPlayer(playerName);
        if (player == null) { error = "You are not seated at this table."; return false; }
        if (player.Bank < amount) { error = $"Insufficient funds (have {player.Bank}G)."; return false; }

        string key = playerName.ToUpperInvariant();

        // Refund any previous bet before replacing it
        if (CurrentTable.ChocoboBets.TryGetValue(key, out var existing))
            player.Bank += existing.Amount;

        player.Bank -= amount;
        CurrentTable.ChocoboBets[key] = new ChocoboBet { RacerIndex = racerNumber - 1, Amount = amount };

        var racer = Roster[racerNumber - 1];
        QueueMessage($"{playerName} bets {amount}G on #{racerNumber} {racer.Name} ({racer.Odds:0.0}x odds)");
        LogAction($"{playerName} bet {amount}G on {racer.Name}");
        OnUIUpdate?.Invoke();
        return true;
    }

    // ── Open betting round ────────────────────────────────────────────────────

    public bool OpenBetting(out string error)
    {
        error = string.Empty;

        if (CurrentTable.ChocoboRacePhase == ChocoboRacePhase.Racing)
        { error = "Cannot open betting — a race is already in progress."; return false; }

        CurrentTable.ChocoboBets.Clear();
        CurrentTable.ChocoboRacePhase = ChocoboRacePhase.WaitingForBets;
        CurrentTable.GameState = Models.GameState.Lobby;

        ShuffleRoster();

        QueueMessage("🐦 Chocobo betting is now OPEN! Use >BET [#] [amount] to place your bet.");
        string line1 = string.Join(" | ", Roster.Take(4).Select(r => $"#{r.Number} {r.Name} {r.Odds:0.0}x"));
        string line2 = string.Join(" | ", Roster.Skip(4).Select(r => $"#{r.Number} {r.Name} {r.Odds:0.0}x"));
        QueueMessage($"TODAY'S RACERS: {line1}");
        QueueMessage(line2);

        LogAction("Chocobo betting opened");
        OnUIUpdate?.Invoke();
        return true;
    }

    // ── Start race ────────────────────────────────────────────────────────────

    public bool StartRace(out string error)
    {
        error = string.Empty;

        if (CurrentTable.ChocoboRacePhase != ChocoboRacePhase.WaitingForBets)
        { error = "A race is already underway."; return false; }

        if (!CurrentTable.ChocoboBets.Values.Any(b => b.Amount > 0))
        { error = "No bets placed yet."; return false; }

        SimulateRace();
        _lastAnnouncedSegment = -1;
        WinnerIndex = -1;

        CurrentTable.ChocoboRacePhase = ChocoboRacePhase.Racing;
        CurrentTable.GameState = Models.GameState.Playing;
        CurrentTable.ChocoboRaceStart = DateTime.Now;

        string line1 = string.Join(" | ", Roster.Take(4).Select(r => $"#{r.Number} {r.Name} {r.Odds:0.0}x"));
        string line2 = string.Join(" | ", Roster.Skip(4).Select(r => $"#{r.Number} {r.Name} {r.Odds:0.0}x"));

        QueueMessage("The chocobos are at the gate... AND THEY'RE OFF!");
        LogAction("Chocobo race started");
        OnUIUpdate?.Invoke();
        return true;
    }

    // ── Race simulation (pre-computed upfront) ─────────────────────────────────

    private void SimulateRace()
    {
        // Pre-determine the winner by odds-weighted probability so any racer can
        // win, with frequency that matches their odds (lower odds = wins more often)
        float totalWeight = Roster.Sum(r => 1.0f / r.Odds);
        float pick        = (float)Rng.NextDouble() * totalWeight;
        int   predWinner  = Roster.Length - 1;
        float cumW        = 0f;
        for (int r = 0; r < Roster.Length; r++)
        {
            cumW += 1.0f / Roster[r].Odds;
            if (pick <= cumW) { predWinner = r; break; }
        }

        // Simulate each racer — wide random base keeps mid-race exciting,
        // stats contribute a small but consistent edge (~15% of total)
        for (int r = 0; r < Roster.Length; r++)
        {
            var   racer      = Roster[r];
            float cumulative = 0f;
            for (int s = 0; s < TotalSegments; s++)
            {
                float segFraction = (float)s / (TotalSegments - 1);
                float statContrib = racer.Speed     * (1.0f - segFraction * 0.4f)
                                  + racer.Endurance * (segFraction * 0.5f);
                float basePace    = (float)(Rng.NextDouble() * 120 + 40); // 40-160 wide random
                float segment     = basePace + statContrib * 0.18f;       // stats ~15% influence
                cumulative       += MathF.Max(0, segment);
                _segmentCumProgress[r, s] = cumulative;
            }
        }

        // Guarantee the predetermined winner finishes first
        float winnerFinal = _segmentCumProgress[predWinner, TotalSegments - 1];
        for (int r = 0; r < Roster.Length; r++)
        {
            if (r == predWinner) continue;
            if (_segmentCumProgress[r, TotalSegments - 1] >= winnerFinal)
            {
                float boost = _segmentCumProgress[r, TotalSegments - 1]
                            - winnerFinal
                            + (float)(Rng.NextDouble() * 8 + 2);
                _segmentCumProgress[predWinner, TotalSegments - 1] += boost;
                winnerFinal = _segmentCumProgress[predWinner, TotalSegments - 1];
            }
        }
    }

    // ── Race tick (call every frame from DrawUI) ───────────────────────────────

    public void ProcessRace()
    {
        if (CurrentTable.ChocoboRacePhase != ChocoboRacePhase.Racing) return;

        double elapsedMs  = (DateTime.Now - CurrentTable.ChocoboRaceStart).TotalMilliseconds;
        double elapsedSec = elapsedMs / 1000.0;

        // Queue mid-race announcements at 5, 10, 15, 20, 25 seconds (segments 0-4)
        int dueSegment = Math.Clamp((int)(elapsedSec / 5.0) - 1, -1, TotalSegments - 2);
        while (_lastAnnouncedSegment < dueSegment)
        {
            _lastAnnouncedSegment++;
            AnnounceSegment(_lastAnnouncedSegment);
        }

        // Finish at 30 s
        if (elapsedMs >= RaceDurationMs)
            FinishRace();

        OnUIUpdate?.Invoke();
    }

    private void AnnounceSegment(int segment)
    {
        // Current leader by cumulative progress through this segment
        int leader = 0, second = -1;
        float leadProg = _segmentCumProgress[0, segment], secondProg = float.MinValue;

        for (int r = 1; r < Roster.Length; r++)
        {
            float p = _segmentCumProgress[r, segment];
            if (p > leadProg)
            { second = leader; secondProg = leadProg; leader = r; leadProg = p; }
            else if (p > secondProg)
            { second = r; secondProg = p; }
        }

        string l = Roster[leader].Name;
        string s = second >= 0 ? Roster[second].Name : "the pack";

        string msg = segment switch
        {
            0 => Rng.Next(2) == 0 ? $"{l} shoots to the front!" : $"Early lead: {l}!",
            1 => Rng.Next(2) == 0 ? $"{l} leads! {s} pressing close!" : $"It's {l} at the quarter mark!",
            2 => Rng.Next(2) == 0 ? $"Halfway! {l} holds the lead!" : $"{l} in front, {s} charging hard!",
            3 => Rng.Next(2) == 0 ? $"{l} still ahead — {s} closing the gap!" : $"Three-quarters done! {l} pushes for glory!",
            4 => Rng.Next(2) == 0 ? $"Final stretch! Can anyone catch {l}?!" : $"{l} vs {s} — the finish line is CLOSE!",
            _ => $"{l} is out front!"
        };

        QueueMessage(msg);
        LogAction($"Segment {segment + 1}: {l} leads");
    }

    private void FinishRace()
    {
        var order = Enumerable.Range(0, Roster.Length)
            .OrderByDescending(r => _segmentCumProgress[r, TotalSegments - 1])
            .ToList();

        WinnerIndex = order[0];
        var winner = Roster[WinnerIndex];

        string podium = string.Join("  ", order.Take(3).Select((r, i) => $"{i + 1}.{Roster[r].Name}"));
        QueueMessage($"FINISH! {podium}");
        LogAction($"Race finished. Winner: {winner.Name}");

        ResolvePayouts(winner);

        CurrentTable.ChocoboRacePhase = ChocoboRacePhase.Complete;
        CurrentTable.GameState = Models.GameState.Lobby;
        OnUIUpdate?.Invoke();
    }

    private void ResolvePayouts(ChocoboRacer winner)
    {
        foreach (var kvp in CurrentTable.ChocoboBets)
        {
            var bet = kvp.Value;
            var player = CurrentTable.Players.Values.FirstOrDefault(p =>
                p.Name.ToUpperInvariant() == kvp.Key);
            if (player == null) continue;

            if (bet.RacerIndex == WinnerIndex)
            {
                int payout = (int)(bet.Amount * winner.Odds);
                int profit = payout - bet.Amount;
                player.Bank += payout;
                player.ChocoboNetGains += profit;
                QueueMessage($"{player.Name}: {winner.Name} WINS! +{profit}G -> Bank: {player.Bank}G");
                LogAction($"{player.Name} won {profit}G on {winner.Name}");
            }
            else
            {
                player.ChocoboNetGains -= bet.Amount;
                QueueMessage($"{player.Name}: {Roster[bet.RacerIndex].Name} lost. -{bet.Amount}G -> Bank: {player.Bank}G");
                LogAction($"{player.Name} lost {bet.Amount}G on {Roster[bet.RacerIndex].Name}");
            }
        }

        CurrentTable.ChocoboBets.Clear();
    }

    // ── UI progress helpers ────────────────────────────────────────────────────

    /// <summary>Returns smoothly interpolated progress for a racer for UI bar display.</summary>
    public float GetRacerProgress(int racerIndex)
    {
        if (CurrentTable.ChocoboRacePhase == ChocoboRacePhase.WaitingForBets) return 0f;
        if (CurrentTable.ChocoboRacePhase == ChocoboRacePhase.Complete)
            return _segmentCumProgress[racerIndex, TotalSegments - 1];

        double elapsed      = (DateTime.Now - CurrentTable.ChocoboRaceStart).TotalMilliseconds;
        double totalFraction = Math.Clamp(elapsed / RaceDurationMs, 0.0, 1.0);

        // Interpolate between segment checkpoints for a realistic animation
        double segFloat = totalFraction * TotalSegments;
        int    seg      = (int)Math.Min(segFloat, TotalSegments - 1);
        float  segFrac  = (float)(segFloat - seg);

        float prev = seg > 0 ? _segmentCumProgress[racerIndex, seg - 1] : 0f;
        float next = _segmentCumProgress[racerIndex, seg];
        return prev + (next - prev) * segFrac;
    }

    public float GetMaxTotalProgress()
    {
        float max = 0;
        for (int r = 0; r < Roster.Length; r++)
            max = MathF.Max(max, _segmentCumProgress[r, TotalSegments - 1]);
        return MathF.Max(max, 1f);
    }

    // ── Force stop ────────────────────────────────────────────────────────────

    public void ForceStop()
    {
        MessageQueue.Clear();
        QueueMessage("Race force stopped by dealer. All bets refunded.");
        LogAction("Race force stopped - refunding all bets");

        foreach (var kvp in CurrentTable.ChocoboBets)
        {
            var bet = kvp.Value;
            var player = CurrentTable.Players.Values.FirstOrDefault(p =>
                p.Name.ToUpperInvariant() == kvp.Key);
            if (player == null) continue;

            player.Bank += bet.Amount;
            QueueMessage($"{player.Name}: {bet.Amount}G refunded -> Bank: {player.Bank}G");
        }

        CurrentTable.ChocoboBets.Clear();
        CurrentTable.ChocoboRacePhase = ChocoboRacePhase.WaitingForBets;
        CurrentTable.GameState = Models.GameState.Lobby;
        OnUIUpdate?.Invoke();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public Player? GetPlayer(string name) =>
        CurrentTable.Players.Values.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void LogAction(string action) =>
        CurrentTable.GameLog.Add($"[{DateTime.Now:HH:mm:ss}] [CHOCOBO] {action}");
}
