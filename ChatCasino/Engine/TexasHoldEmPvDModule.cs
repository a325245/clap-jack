using System;
using System.Collections.Generic;
using System.Linq;
using ChatCasino.Models;
using ChatCasino.Services;
using ChatCasino.UI;

namespace ChatCasino.Engine;

/// <summary>
/// Texas Hold'Em Player-vs-Dealer (Ultimate Texas Hold'Em style).
///
/// Betting structure (turn-by-turn):
///   Preflop  — each player in order: bet 1x / 2x / 3x ante, or CHECK
///   Flop     — only players who checked preflop, in order: bet 1x / 2x, or CHECK
///   River    — only players who checked both streets, in order: bet 1x, or FOLD (no check)
///
/// Board: Flop = 3 cards, River = 2 cards together (no separate turn).
/// Dealer advances board stages with DEAL (only when all betting is complete).
/// </summary>
public sealed class TexasHoldEmPvDModule : BaseEngine
{
    // -------------------------------------------------------------------------
    // Payout table
    // -------------------------------------------------------------------------
    private static readonly Dictionary<PokerRankCategory, (int Num, int Den)> PayoutOdds = new()
    {
        [PokerRankCategory.Pair]          = (1, 1),
        [PokerRankCategory.TwoPair]       = (3, 2),
        [PokerRankCategory.Trips]         = (3, 2),
        [PokerRankCategory.Straight]      = (2, 1),
        [PokerRankCategory.Flush]         = (5, 2),
        [PokerRankCategory.FullHouse]     = (3, 1),
        [PokerRankCategory.Quads]         = (10, 1),
        [PokerRankCategory.StraightFlush] = (50, 1),
    };
    private const int RoyalFlushPayout = 250;

    // -------------------------------------------------------------------------
    // Stage
    // -------------------------------------------------------------------------
    private enum Stage { Idle, Preflop, Flop, River, AwaitingResolve }

    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------
    private readonly IBankService bank;
    private readonly PokerEvaluator evaluator;
    private readonly IDeckService decks;

    private DeckShoe<Card> shoe = null!;
    private bool roundActive;
    private Stage stage = Stage.Idle;
    private readonly List<Card> dealerHole = new();
    private readonly List<Card> board = new();
    private readonly List<Card> preDraw = new();

    // Turn-by-turn betting state
    private readonly List<string> bettingOrder = new();
    private int bettingIdx;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------
    public TexasHoldEmPvDModule(
        IMessageService msg,
        IDeckService decks,
        IPlayerService players,
        IBankService bank,
        PokerEvaluator evaluator)
        : base(GameType.TexasHoldEmPvD, msg, decks, players)
    {
        this.bank      = bank;
        this.evaluator = evaluator;
        this.decks     = decks;
        StatusText     = "Place antes — BET [amount]";
    }

    // -------------------------------------------------------------------------
    // Command routing
    // -------------------------------------------------------------------------
    public override CmdResult Execute(string playerName, string cmd, string[] args)
    {
        var player = Players.GetPlayer(playerName);
        if (player is null) return CmdResult.Fail("Player not found.");

        var upper = cmd.ToUpperInvariant();

        if (TryParseMultiplier(upper, out var mult))
            return DispatchMultiplier(player, mult);

        return upper switch
        {
            "BET"   => PlaceAnte(player, args),
            "DEAL"  => HandleDeal(playerName),
            "CHECK" => CheckBet(player),
            "FOLD"  => FoldPlayer(player),
            "HAND"  => AnnounceHand(player),
            _       => CmdResult.Ok(string.Empty)
        };
    }

    public override IEnumerable<string> GetValidCommands()
        => ["BET [ante]", "DEAL", "1 / 2 / 3", "CHECK", "FOLD", "HAND"];

    // -------------------------------------------------------------------------
    // Pre-round: ante
    // -------------------------------------------------------------------------
    private CmdResult PlaceAnte(Player p, string[] args)
    {
        if (roundActive) return CmdResult.Fail("Round in progress.");

        if (args.Length < 1 || !int.TryParse(args[0], out var ante) || ante <= 0)
            return CmdResult.Fail("Usage: BET [ante amount].");

        var limit = CasinoUI.GlobalMaxBet;
        if (limit > 0 && ante > limit)
            return CmdResult.Fail($"Ante exceeds table max of {limit}\uE049.");
        if (ante > p.CurrentBank)
            return CmdResult.Fail($"Insufficient funds ({p.CurrentBank}\uE049).");
        if (bank.Deduct(p, ante, "PvD ante") != TransactionResult.Success)
            return CmdResult.Fail("Could not place ante.");

        p.Metadata["PvD.Ante"]         = ante;
        p.Metadata["PvD.PlayBet"]      = 0;
        p.Metadata["PvD.Folded"]       = false;
        p.Metadata["PvD.PreflopActed"] = false;
        p.Metadata["PvD.FlopActed"]    = false;
        p.Metadata["PvD.RiverActed"]   = false;
        p.Metadata["PvD.Hole"]         = new List<Card>();

        Msg.QueuePartyMessage($"[PVD] {p.Name} antes {ante}\uE049.");
        return CmdResult.Ok("Ante placed.");
    }

    // -------------------------------------------------------------------------
    // Stage advancement — dealer DEAL command
    // -------------------------------------------------------------------------
    private CmdResult HandleDeal(string senderName)
    {
        return stage switch
        {
            Stage.Idle           => DealHoles(),
            Stage.Preflop        => BettingInProgress(),
            Stage.Flop           => BettingInProgress(),
            Stage.River          => BettingInProgress(),
            Stage.AwaitingResolve => Resolve(),
            _                   => CmdResult.Fail("Unexpected stage.")
        };
    }

    private static CmdResult BettingInProgress()
        => CmdResult.Fail("Betting is in progress — wait for all players to act.");

    // -------------------------------------------------------------------------
    // DealHoles — start round, open preflop betting
    // -------------------------------------------------------------------------
    private CmdResult DealHoles()
    {
        var betters = ActiveBetters();
        if (betters.Count == 0) return CmdResult.Fail("No antes placed. Use BET [amount] first.");

        roundActive = true;
        stage = Stage.Preflop;
        shoe = decks.GetStandardDeck(1, shuffled: true);
        board.Clear();
        preDraw.Clear();
        dealerHole.Clear();
        bettingOrder.Clear();
        bettingIdx = 0;
        BeginRoundRecord();

        foreach (var p in betters)
        {
            // Snapshot bank *before* this round's transactions so the summary delta is accurate.
            // Note: ante was already deducted, so add it back to get true pre-round balance.
            p.Metadata[$"RoundStartBank:{GameType}"] = p.CurrentBank + PvdInt(p, "PvD.Ante");

            p.Metadata["PvD.Hole"]         = new List<Card> { shoe.Draw(), shoe.Draw() };
            p.Metadata["PvD.PlayBet"]      = 0;
            p.Metadata["PvD.Folded"]       = false;
            p.Metadata["PvD.PreflopActed"] = false;
            p.Metadata["PvD.FlopActed"]    = false;
            p.Metadata["PvD.RiverActed"]   = false;
            bettingOrder.Add(p.Name);
        }

        dealerHole.Add(shoe.Draw());
        dealerHole.Add(shoe.Draw());

        for (var i = 0; i < 5; i++)
            preDraw.Add(shoe.Draw());

        // Announce hole cards for all players
        foreach (var p in betters)
        {
            var holeStr = HoleStr(p);
            Msg.QueuePartyMessage($"[PVD] {p.Name}: {holeStr}");
            LogRound($"{p.Name} hole: {holeStr}");
        }

        AnnounceBettingTurn();
        return CmdResult.Ok("Hole cards dealt.");
    }

    // -------------------------------------------------------------------------
    // Flop / River deal (called internally when all betting is done)
    // -------------------------------------------------------------------------
    private void DealFlop()
    {
        board.Add(preDraw[0]);
        board.Add(preDraw[1]);
        board.Add(preDraw[2]);

        var boardStr = string.Join(" ", board.Select(c => c.GetCardDisplay()));
        Msg.QueuePartyMessage($"[PVD] *** FLOP: {boardStr} ***");
        LogRound($"Flop: {boardStr}");

        // Build betting order: only players who checked preflop
        bettingOrder.Clear();
        bettingIdx = 0;
        foreach (var p in ActiveBetters().Where(p => WasCheckedPreflop(p)))
            bettingOrder.Add(p.Name);

        stage = Stage.Flop;

        if (bettingOrder.Count == 0)
        {
            // Everyone already bet preflop — skip to river
            DealRiver();
        }
        else
        {
            AnnounceBettingTurn();
        }
    }

    private void DealRiver()
    {
        board.Add(preDraw[3]);
        board.Add(preDraw[4]);

        var riverCards = $"{preDraw[3].GetCardDisplay()} {preDraw[4].GetCardDisplay()}";
        var boardStr   = string.Join(" ", board.Select(c => c.GetCardDisplay()));
        Msg.QueuePartyMessage($"[PVD] *** RIVER: {riverCards} | Board: {boardStr} ***");
        LogRound($"River: {riverCards}");

        // Build betting order: only players who checked both preflop and flop
        bettingOrder.Clear();
        bettingIdx = 0;
        foreach (var p in ActiveBetters().Where(p => WasCheckedPreflop(p) && WasCheckedFlop(p)))
            bettingOrder.Add(p.Name);

        stage = Stage.River;

        if (bettingOrder.Count == 0)
        {
            // Nobody left to act — go straight to resolve
            stage = Stage.AwaitingResolve;
            Msg.QueuePartyMessage("[PVD] All players have acted. Dealer: DEAL to resolve.");
        }
        else
        {
            AnnounceBettingTurn();
        }
    }

    // -------------------------------------------------------------------------
    // Announce whose turn it is to bet
    // -------------------------------------------------------------------------
    private void AnnounceBettingTurn()
    {
        if (bettingIdx >= bettingOrder.Count)
        {
            OnAllBettingComplete();
            return;
        }

        var name = bettingOrder[bettingIdx];
        StatusText = $"{stage} — {name}'s turn";

        switch (stage)
        {
            case Stage.Preflop:
                var ante = PvdIntByName(name, "PvD.Ante");
                Msg.QueuePartyMessage($"[PVD] {name}: Preflop — >1 ({ante}\uE049) / >2 ({ante * 2}\uE049) / >3 ({ante * 3}\uE049) — or >CHECK");
                break;
            case Stage.Flop:
                var anteF = PvdIntByName(name, "PvD.Ante");
                Msg.QueuePartyMessage($"[PVD] {name}: Post-flop — >1 ({anteF}\uE049) / >2 ({anteF * 2}\uE049) — or >CHECK");
                break;
            case Stage.River:
                var anteR = PvdIntByName(name, "PvD.Ante");
                Msg.QueuePartyMessage($"[PVD] {name}: River — >1 ({anteR}\uE049) to play — or >FOLD");
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Player betting actions
    // -------------------------------------------------------------------------
    private CmdResult PlacePlayBet(Player p, int multiplier)
    {
        if (!roundActive) return CmdResult.Fail("No round in progress.");
        if (!p.Metadata.ContainsKey("PvD.Ante")) return CmdResult.Fail("No ante in this round.");
        if (PvdBool(p, "PvD.Folded")) return CmdResult.Fail("You have folded.");
        if (PvdInt(p, "PvD.PlayBet") > 0) return CmdResult.Fail("You have already placed your play bet.");

        if (!IsCurrentBettingPlayer(p.Name))
            return CmdResult.Fail($"It's {CurrentBettingPlayerName()}'s turn.");

        int maxMult = stage switch
        {
            Stage.Preflop => 3,
            Stage.Flop    => 2,
            Stage.River   => 1,
            _             => 0
        };

        if (maxMult == 0) return CmdResult.Fail("Betting is not open right now.");
        if (stage == Stage.River) return CmdResult.Fail("On the river, use >1 to play or >FOLD.");
        if (multiplier < 1 || multiplier > maxMult)
            return CmdResult.Fail($"Valid bets this street: 1–{maxMult}x.");

        var ante    = PvdInt(p, "PvD.Ante");
        var playBet = ante * multiplier;

        if (playBet > p.CurrentBank) return CmdResult.Fail($"Insufficient funds ({p.CurrentBank}\uE049).");
        if (bank.Deduct(p, playBet, "PvD play bet") != TransactionResult.Success)
            return CmdResult.Fail("Could not place play bet.");

        p.Metadata["PvD.PlayBet"] = playBet;
        MarkActed(p);

        var stageName = stage == Stage.Preflop ? "preflop" : "flop";
        Msg.QueuePartyMessage($"[PVD] {p.Name} bets {multiplier}x ({playBet}\uE049) {stageName}.");
        LogRound($"{p.Name} {stageName} bet {multiplier}x = {playBet}");

        AdvanceBetting();
        return CmdResult.Ok("Bet placed.");
    }

    private CmdResult CheckBet(Player p)
    {
        if (!roundActive) return CmdResult.Fail("No round in progress.");
        if (!p.Metadata.ContainsKey("PvD.Ante")) return CmdResult.Fail("No ante in this round.");
        if (PvdBool(p, "PvD.Folded")) return CmdResult.Fail("You have folded.");
        if (PvdInt(p, "PvD.PlayBet") > 0) return CmdResult.Fail("You have already placed your play bet.");
        if (stage == Stage.River) return CmdResult.Fail("No check on the river — >1 to play or >FOLD.");
        if (stage != Stage.Preflop && stage != Stage.Flop) return CmdResult.Fail("Betting is not open right now.");

        if (!IsCurrentBettingPlayer(p.Name))
            return CmdResult.Fail($"It's {CurrentBettingPlayerName()}'s turn.");

        MarkActed(p);
        Msg.QueuePartyMessage($"[PVD] {p.Name} checks.");
        LogRound($"{p.Name} checks {stage}");

        AdvanceBetting();
        return CmdResult.Ok("Checked.");
    }

    private CmdResult FoldPlayer(Player p)
    {
        if (!roundActive) return CmdResult.Fail("No round in progress.");
        if (!p.Metadata.ContainsKey("PvD.Ante")) return CmdResult.Fail("No ante in this round.");
        if (PvdBool(p, "PvD.Folded")) return CmdResult.Fail("Already folded.");
        if (stage != Stage.River) return CmdResult.Fail("You can only fold on the river.");
        if (PvdInt(p, "PvD.PlayBet") > 0) return CmdResult.Fail("You already placed your play bet.");

        if (!IsCurrentBettingPlayer(p.Name))
            return CmdResult.Fail($"It's {CurrentBettingPlayerName()}'s turn.");

        p.Metadata["PvD.Folded"] = true;
        MarkActed(p);
        Msg.QueuePartyMessage($"[PVD] {p.Name} folds (forfeits ante).");
        LogRound($"{p.Name} folds river");

        AdvanceBetting();
        return CmdResult.Ok("Folded.");
    }

    // River 1x play — accepts "1" multiplier only on the river
    private CmdResult PlayRiver(Player p)
    {
        if (!roundActive) return CmdResult.Fail("No round in progress.");
        if (!p.Metadata.ContainsKey("PvD.Ante")) return CmdResult.Fail("No ante in this round.");
        if (PvdBool(p, "PvD.Folded")) return CmdResult.Fail("You have folded.");
        if (PvdInt(p, "PvD.PlayBet") > 0) return CmdResult.Fail("You have already placed your play bet.");
        if (stage != Stage.River) return CmdResult.Fail("Not on the river.");

        if (!IsCurrentBettingPlayer(p.Name))
            return CmdResult.Fail($"It's {CurrentBettingPlayerName()}'s turn.");

        var ante    = PvdInt(p, "PvD.Ante");
        var playBet = ante;

        if (playBet > p.CurrentBank) return CmdResult.Fail($"Insufficient funds ({p.CurrentBank}\uE049).");
        if (bank.Deduct(p, playBet, "PvD river play") != TransactionResult.Success)
            return CmdResult.Fail("Could not place bet.");

        p.Metadata["PvD.PlayBet"] = playBet;
        MarkActed(p);
        Msg.QueuePartyMessage($"[PVD] {p.Name} plays river ({playBet}\uE049).");
        LogRound($"{p.Name} river play = {playBet}");

        AdvanceBetting();
        return CmdResult.Ok("River bet placed.");
    }

    private CmdResult AnnounceHand(Player p)
    {
        if (!roundActive) return CmdResult.Fail("No round in progress.");
        var holeStr = HoleStr(p);
        if (string.IsNullOrEmpty(holeStr)) return CmdResult.Fail("No hole cards.");
        Msg.QueuePartyMessage($"[PVD] {p.Name} hole cards: {holeStr}");
        return CmdResult.Ok("Hand announced.");
    }

    // -------------------------------------------------------------------------
    // Bet multiplier routing — split river (1x = PlayRiver) vs preflop/flop
    // -------------------------------------------------------------------------
    // Override the multiplier dispatch to handle river separately
    private CmdResult DispatchMultiplier(Player p, int mult)
    {
        if (stage == Stage.River && mult == 1)
            return PlayRiver(p);
        return PlacePlayBet(p, mult);
    }

    // Update Execute to use DispatchMultiplier
    // (we patch Execute to call DispatchMultiplier for numeric commands)

    // -------------------------------------------------------------------------
    // Advance betting turn
    // -------------------------------------------------------------------------
    private void AdvanceBetting()
    {
        bettingIdx++;
        // Skip any player who has already folded (shouldn't happen but safety check)
        while (bettingIdx < bettingOrder.Count)
        {
            var p = Players.GetPlayer(bettingOrder[bettingIdx]);
            if (p is null || PvdBool(p, "PvD.Folded")) bettingIdx++;
            else break;
        }

        if (bettingIdx >= bettingOrder.Count)
            OnAllBettingComplete();
        else
            AnnounceBettingTurn();
    }

    private void OnAllBettingComplete()
    {
        switch (stage)
        {
            case Stage.Preflop:
                DealFlop();
                break;
            case Stage.Flop:
                DealRiver();
                break;
            case Stage.River:
                stage = Stage.AwaitingResolve;
                Msg.QueuePartyMessage("[PVD] All players have acted. Dealer: DEAL to resolve.");
                StatusText = "Awaiting resolve — dealer: DEAL";
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Resolve
    // -------------------------------------------------------------------------
    public CmdResult Resolve()
    {
        if (!roundActive) return CmdResult.Fail("No round in progress.");
        if (stage != Stage.AwaitingResolve) return CmdResult.Fail("Not all players have acted yet.");

        var allBetters = Players.GetAllActivePlayers()
            .Where(p => p.Metadata.ContainsKey("PvD.Ante"))
            .ToList();

        var dealerFull = dealerHole.Concat(board).ToList();
        var dealerBest = evaluator.Evaluate(dealerFull);
        var dealerStr  = string.Join(" ", dealerHole.Select(c => c.GetCardDisplay()));
        var boardStr   = string.Join(" ", board.Select(c => c.GetCardDisplay()));

        Msg.QueuePartyMessage($"[PVD] Board: {boardStr}");
        Msg.QueuePartyMessage($"[PVD] Dealer: {dealerStr} — {dealerBest.Description}");

        var dealerQualifies = dealerBest.Category >= PokerRankCategory.Pair;
        if (!dealerQualifies)
            Msg.QueuePartyMessage("[PVD] Dealer does not qualify (needs at least a pair). Antes pushed.");

        foreach (var p in allBetters)
        {
            var ante    = PvdInt(p, "PvD.Ante");
            var playBet = PvdInt(p, "PvD.PlayBet");
            var folded  = PvdBool(p, "PvD.Folded");

            if (folded) continue;

            var hole       = p.Metadata["PvD.Hole"] as List<Card> ?? [];
            var playerBest = evaluator.Evaluate(hole.Concat(board).ToList());
            var holeStr    = string.Join(" ", hole.Select(c => c.GetCardDisplay()));

            if (!dealerQualifies)
            {
                // Ante always pushes; play bet still wins at odds (UTH rule)
                bank.Award(p, ante, "PvD ante push");
                if (playBet > 0)
                {
                    var playProfit = CalcPayout(playBet, playerBest);
                    bank.Award(p, playBet + playProfit, "PvD play win (dealer no-qualify)");
                    var noQMsg = playProfit > 0
                        ? $"[PVD] {p.Name} ({playerBest.Description}) — dealer no-qualify: ante pushed, play wins +{playProfit}\uE049"
                        : $"[PVD] {p.Name} ({playerBest.Description}) — dealer no-qualify: ante + play returned.";
                    Msg.QueuePartyMessage(noQMsg);
                }
                else
                {
                    Msg.QueuePartyMessage($"[PVD] {p.Name} ({playerBest.Description}) — dealer no-qualify: ante pushed.");
                }
                continue;
            }

            var cmp = HandRank.Compare(playerBest, dealerBest);

            if (cmp > 0)
            {
                var antePayout = ante;
                var playPayout = playBet > 0 ? CalcPayout(playBet, playerBest) : 0;
                var totalReturn = ante + antePayout + playBet + playPayout;
                bank.Award(p, totalReturn, "PvD win");
                Msg.QueuePartyMessage($"[PVD] {p.Name} ({holeStr}: {playerBest.Description}) WINS +{antePayout + playPayout}\uE049");
                LogRound($"{p.Name} wins");
            }
            else if (cmp == 0)
            {
                bank.Award(p, ante + playBet, "PvD push");
                Msg.QueuePartyMessage($"[PVD] {p.Name} ({playerBest.Description}) PUSH — bets returned.");
                LogRound($"{p.Name} push");
            }
            else
            {
                Msg.QueuePartyMessage($"[PVD] {p.Name} ({holeStr}: {playerBest.Description}) loses.");
                LogRound($"{p.Name} loses");
            }
        }

        EndRound();
        return CmdResult.Ok("Round resolved.");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private static int CalcPayout(int betAmount, HandRank rank)
    {
        if (rank.Category == PokerRankCategory.StraightFlush && rank.Kickers.Count > 0 && rank.Kickers[0] == 14)
            return betAmount * RoyalFlushPayout;
        return PayoutOdds.TryGetValue(rank.Category, out var odds) ? betAmount * odds.Num / odds.Den : 0;
    }

    private List<Player> ActiveBetters()
        => Players.GetAllActivePlayers()
            .Where(p => p.Metadata.ContainsKey("PvD.Ante") && !PvdBool(p, "PvD.Folded"))
            .ToList();

    private bool IsCurrentBettingPlayer(string name)
        => bettingIdx < bettingOrder.Count &&
           bettingOrder[bettingIdx].Equals(name, StringComparison.OrdinalIgnoreCase);

    private string CurrentBettingPlayerName()
        => bettingIdx < bettingOrder.Count ? bettingOrder[bettingIdx] : "nobody";

    private static bool WasCheckedPreflop(Player p)
        => PvdInt(p, "PvD.PlayBet") == 0 && ActedPreflop(p);

    private static bool WasCheckedFlop(Player p)
        => PvdInt(p, "PvD.PlayBet") == 0 && ActedFlop(p);

    private static bool ActedPreflop(Player p)
        => p.Metadata.TryGetValue("PvD.PreflopActed", out var v) && v is true;

    private static bool ActedFlop(Player p)
        => p.Metadata.TryGetValue("PvD.FlopActed", out var v) && v is true;

    private void MarkActed(Player p)
    {
        switch (stage)
        {
            case Stage.Preflop: p.Metadata["PvD.PreflopActed"] = true; break;
            case Stage.Flop:    p.Metadata["PvD.FlopActed"]    = true; break;
            case Stage.River:   p.Metadata["PvD.RiverActed"]   = true; break;
        }
    }

    private string HoleStr(Player p)
    {
        var hole = p.Metadata.TryGetValue("PvD.Hole", out var h) && h is List<Card> hc ? hc : new List<Card>();
        return hole.Count == 0 ? string.Empty : string.Join(" ", hole.Select(c => c.GetCardDisplay()));
    }

    private static bool TryParseMultiplier(string cmd, out int mult)
    {
        mult = cmd switch
        {
            "1" or "ONE"   => 1,
            "2" or "TWO"   => 2,
            "3" or "THREE" => 3,
            _              => 0
        };
        return mult > 0;
    }

    private static int PvdInt(Player p, string key)
        => p.Metadata.TryGetValue(key, out var v) && v is int i ? i : 0;

    private int PvdIntByName(string name, string key)
    {
        var p = Players.GetPlayer(name);
        return p is null ? 0 : PvdInt(p, key);
    }

    private static bool PvdBool(Player p, string key)
        => p.Metadata.TryGetValue(key, out var v) && v is true;

    // -------------------------------------------------------------------------
    // Round end / cleanup
    // -------------------------------------------------------------------------
    public override void OnRoundComplete()
    {
        var participants = Players.GetAllActivePlayers()
            .Where(p => p.Metadata.ContainsKey($"RoundStartBank:{GameType}"))
            .ToList();

        if (participants.Count == 0) return;

        Msg.QueuePartyMessage($"[PVD] Round complete.");

        var lines = new List<string>();
        foreach (var p in participants)
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

        for (var i = 0; i < lines.Count; i += 3)
        {
            var chunk = string.Join(" | ", lines.Skip(i).Take(3));
            Msg.QueuePartyMessage($"[PVD] {chunk}");
        }
    }

    private void EndRound()
    {
        roundActive = false;
        stage = Stage.Idle;
        bettingOrder.Clear();
        bettingIdx = 0;
        dealerHole.Clear();
        board.Clear();
        preDraw.Clear();
        StatusText = "Place antes — BET [amount]";

        foreach (var p in Players.GetAllActivePlayers())
        {
            p.Metadata.Remove("PvD.Ante");
            p.Metadata.Remove("PvD.PlayBet");
            p.Metadata.Remove("PvD.Folded");
            p.Metadata.Remove("PvD.PreflopActed");
            p.Metadata.Remove("PvD.FlopActed");
            p.Metadata.Remove("PvD.RiverActed");
            p.Metadata.Remove("PvD.Hole");
        }

        OnRoundComplete();
    }

    public override void OnForceStop()
    {
        roundActive = false;
        stage = Stage.Idle;
        bettingOrder.Clear();
        bettingIdx = 0;
        dealerHole.Clear();
        board.Clear();
        preDraw.Clear();
        StatusText = "Place antes — BET [amount]";

        foreach (var p in Players.GetAllActivePlayers())
        {
            p.Metadata.Remove("PvD.Ante");
            p.Metadata.Remove("PvD.PlayBet");
            p.Metadata.Remove("PvD.Folded");
            p.Metadata.Remove("PvD.PreflopActed");
            p.Metadata.Remove("PvD.FlopActed");
            p.Metadata.Remove("PvD.RiverActed");
            p.Metadata.Remove("PvD.Hole");
        }
    }
}
