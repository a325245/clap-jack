using System;
using System.Collections.Generic;
using System.Linq;
using ChatCasino.Models;
using ChatCasino.Services;
using ChatCasino.UI;

namespace ChatCasino.Engine;

public sealed class RouletteModule : BaseEngine
{
    private readonly IBankService bank;
    private readonly ITimerService timer;
    private readonly DeckShoe<Card> rngShoe;
    private bool spinning;
    private int? lastResult;
    private Guid spinTimerId = Guid.Empty;
    private DateTime lastBetReceivedUtc = DateTime.MinValue;

    public RouletteModule(IMessageService msg, IDeckService decks, IPlayerService players, IBankService bank, ITimerService timer)
        : base(GameType.Roulette, msg, decks, players)
    {
        this.bank = bank;
        this.timer = timer;
        rngShoe = decks.GetStandardDeck(2, shuffled: true);
        StatusText = "Waiting for bets";
    }

    public override CmdResult Execute(string player, string cmd, string[] args)
    {
        var p = Players.GetPlayer(player);
        if (p is null) return CmdResult.Fail("Player not found.");

        cmd = cmd.ToUpperInvariant();
        return cmd switch
        {
            "BET" => PlaceBet(p, args),
            "SPIN" => StartSpin(),
            _ => CmdResult.Ok(string.Empty)
        };
    }

    public override IEnumerable<string> GetValidCommands() => ["BET", "SPIN"];

    public override ICasinoViewModel GetViewModel()
    {
        var seats = Players.GetAllActivePlayers().Select(p =>
        {
            var bets = GetBets(p);
            var groupedBets = bets
                .GroupBy(b => b.Target, StringComparer.OrdinalIgnoreCase)
                .Select(g => new RouletteBetEntry(g.Key, g.Sum(x => x.Amount)))
                .ToList();
            var amount = groupedBets.Sum(b => b.Amount);
            var targets = string.Join(", ", groupedBets.Select(b => b.Target));
            return new PlayerSlotViewModel
            {
                PlayerName = p.Name,
                Bank = p.CurrentBank,
                BetAmount = amount,
                ResultText = string.IsNullOrWhiteSpace(targets) ? string.Empty : $"Bets: {targets}",
                HandResultTexts = groupedBets.Select(b => $"{b.Target}:{b.Amount}").ToList()
            };
        }).ToList();

        if (lastResult.HasValue)
        {
            seats.Insert(0, new PlayerSlotViewModel
            {
                PlayerName = "Wheel",
                IsDealer = true,
                ResultText = $"Last: {lastResult.Value} ({(lastResult.Value == 0 ? "Green" : RouletteUtils.IsRed(lastResult.Value) ? "Red" : "Black")})"
            });
        }

        return new RouletteViewModel
        {
            GameTitle = "Roulette",
            GameStatus = StatusText,
            Seats = seats,
            Actions = GetValidCommands().ToList()
        };
    }

    private CmdResult PlaceBet(Player player, string[] args)
    {
        if (spinning) return CmdResult.Fail("Wheel is spinning.");

        // Support reversed order: BET RED 100 as well as BET 100 RED
        if (args.Length >= 2 && !int.TryParse(args[0], out _) && int.TryParse(args[^1], out _))
            args = [args[^1], .. args[..^1]];

        if (args.Length < 2 || !int.TryParse(args[0], out var amt) || amt <= 0)
            return CmdResult.Fail("Usage: BET [amount] [target] or BET [amount] on [t1, t2, ...]");

        if (amt < CasinoUI.GlobalMinBet || amt > CasinoUI.GlobalMaxBet)
            return CmdResult.Fail($"Bet must be between {CasinoUI.GlobalMinBet} and {CasinoUI.GlobalMaxBet}. ");

        var payload = string.Join(' ', args.Skip(1));
        if (payload.StartsWith("on ", StringComparison.OrdinalIgnoreCase))
            payload = payload[3..];

        var targets = payload
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(t => t.ToUpperInvariant())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
            return CmdResult.Fail("At least one target is required.");

        if (targets.Any(t => !RouletteUtils.IsValidTarget(t)))
            return CmdResult.Fail("Target must be RED, BLACK, EVEN, ODD, or 0-36.");

        var totalCost = amt * targets.Count;
        if (player.CurrentBank < totalCost)
            return CmdResult.Fail("Insufficient funds.");

        var bets = GetBets(player);
        foreach (var target in targets)
        {
            if (bank.Deduct(player, amt, $"Roulette bet {target}") != TransactionResult.Success)
                return CmdResult.Fail("Insufficient funds.");
            bets.Add(new RouletteBetEntry(target, amt));
        }

        StatusText = "Waiting for spin";
        lastBetReceivedUtc = DateTime.UtcNow;
        Msg.QueuePartyMessage($"[ROULETTE] {player.Name} bets {StandardizedFormatting.FormatCurrency(amt)} on {string.Join(", ", targets)}");
        return CmdResult.Ok("Bet accepted.");
    }

    private CmdResult StartSpin()
    {
        if (spinning) return CmdResult.Fail("Already spinning.");

        var sinceBet = (DateTime.UtcNow - lastBetReceivedUtc).TotalSeconds;
        if (sinceBet < 2.0)
        {
            var left = Math.Ceiling(2.0 - sinceBet);
            return CmdResult.Fail($"Wait {left}s for last bet to confirm before spinning.");
        }

        spinning = true;
        StatusText = "Spinning...";
        Msg.QueuePartyMessage("[ROULETTE] No more bets. Spinning...");

        spinTimerId = timer.Schedule(TimeSpan.FromSeconds(Math.Max(1, CasinoUI.RouletteSpinSeconds)), () =>
        {
            spinTimerId = Guid.Empty;
            ResolveSpin();
        });
        return CmdResult.Ok("Spin started.");
    }

    public override void OnForceStop()
    {
        if (spinTimerId != Guid.Empty)
        {
            _ = timer.Cancel(spinTimerId);
            spinTimerId = Guid.Empty;
        }

        spinning = false;
        lastResult = null;
        StatusText = "Waiting for bets";

        foreach (var player in Players.GetAllActivePlayers())
            GetBets(player).Clear();
    }

    private void ResolveSpin()
    {
        var result = NextNumber0To36();
        lastResult = result;

        foreach (var player in Players.GetAllActivePlayers())
        {
            var bets = GetBets(player);
            foreach (var bet in bets)
            {
                if (!RouletteUtils.IsWinningTarget(result, bet.Target)) continue;
                var payout = bet.Amount * RouletteUtils.PayoutMultiplier(bet.Target);
                bank.Award(player, payout, $"Roulette win {bet.Target}");
            }
            bets.Clear();
        }

        var color = result == 0 ? "Green" : RouletteUtils.IsRed(result) ? "Red" : "Black";
        Msg.QueuePartyMessage($"[ROULETTE] Result: {result} ({color})");
        spinning = false;
        StatusText = $"Result {result} - waiting for bets";
        OnRoundComplete();
    }

    private int NextNumber0To36()
    {
        var card = rngShoe.Draw();
        var hash = HashCode.Combine(card.Suit, card.Value, DateTime.UtcNow.Ticks);
        return Math.Abs(hash % 37);
    }

    private static List<RouletteBetEntry> GetBets(Player player)
    {
        if (player.Metadata.TryGetValue("Roulette.Bets", out var obj) && obj is List<RouletteBetEntry> bets)
            return bets;

        bets = new List<RouletteBetEntry>();
        player.Metadata["Roulette.Bets"] = bets;
        return bets;
    }

    private sealed record RouletteBetEntry(string Target, int Amount);

    private sealed class RouletteViewModel : BaseViewModel
    {
        public List<string> Actions { get; set; } = new();
        public override IReadOnlyList<string> GetActionButtons() => Actions;
    }
}
