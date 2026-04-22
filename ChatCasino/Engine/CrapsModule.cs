using System;
using System.Collections.Generic;
using System.Linq;
using ChatCasino.Models;
using ChatCasino.Services;
using ChatCasino.UI;

namespace ChatCasino.Engine;

public sealed class CrapsModule : BaseEngine
{
    private readonly IBankService bank;
    private readonly DeckShoe<Card> rngShoe;
    private int point;
    private int die1;
    private int die2;
    private string shooterName = string.Empty;
    private string lastAnnouncedShooter = string.Empty;
    private DateTime lastRollAtUtc = DateTime.MinValue;
    private int shooterIndex = -1;
    private DateTime shooterTurnStartedUtc = DateTime.MinValue;
    private bool shooterTurnWarningSent;
    private DateTime bettingWindowEndsUtc = DateTime.MinValue;

    private sealed record CrapsBetEntry(string Target, int Amount);

    public CrapsModule(IMessageService msg, IDeckService decks, IPlayerService players, IBankService bank)
        : base(GameType.Craps, msg, decks, players)
    {
        this.bank = bank;
        rngShoe = decks.GetStandardDeck(1, shuffled: true);
        StatusText = "Come-out phase";
    }

    public override CmdResult Execute(string playerName, string cmd, string[] args)
    {
        var p = Players.GetPlayer(playerName);
        if (p is null) return CmdResult.Fail("Player not found.");

        cmd = cmd.ToUpperInvariant();
        return cmd switch
        {
            "BET" => PlaceBet(p, args),
            "ROLL" => Roll(p),
            _ => CmdResult.Ok(string.Empty)
        };
    }

    public override IEnumerable<string> GetValidCommands() => ["BET", "ROLL"];

    public override void Tick()
    {
        base.Tick();

        var active = Players.GetAllActivePlayers().ToList();
        if (active.Count == 0)
        {
            shooterName = string.Empty;
            shooterIndex = -1;
            lastAnnouncedShooter = string.Empty;
            shooterTurnStartedUtc = DateTime.MinValue;
            shooterTurnWarningSent = false;
            bettingWindowEndsUtc = DateTime.MinValue;
            return;
        }

        // If no shooter is set, pick one
        if (string.IsNullOrWhiteSpace(shooterName))
        {
            if (shooterIndex < 0 || shooterIndex >= active.Count)
                shooterIndex = 0;
            shooterName = active[shooterIndex].Name;
        }

        // Announce new shooter if not yet announced
        if (!shooterName.Equals(lastAnnouncedShooter, StringComparison.OrdinalIgnoreCase))
        {
            // Check if this shooter is actually available
            var candidate = Players.GetPlayer(shooterName);
            if (candidate is null || candidate.IsAfk || candidate.IsKicked)
            {
                // Silently skip to next without announcing
                shooterIndex = (shooterIndex + 1) % active.Count;
                shooterName = active[shooterIndex].Name;
                // Let next Tick handle announcement
                return;
            }

            shooterTurnStartedUtc = DateTime.UtcNow;
            shooterTurnWarningSent = false;
            var windowSecs = Math.Max(1, CasinoUI.CrapsBettingDurationSeconds);
            bettingWindowEndsUtc = DateTime.UtcNow.AddSeconds(windowSecs);
            var windowTotal = Math.Max(1, (int)Math.Ceiling(windowSecs));
            StatusText = $"Bets open ({windowTotal}s)";
            Msg.QueuePartyMessage($"[CRAPS] Shooter: {shooterName}");
            Msg.QueuePartyMessage($"[CRAPS] Bets are open ({windowTotal}s/{windowTotal}s). Please wait.");
            lastAnnouncedShooter = shooterName;
            return;
        }

        // Betting window still open
        if (DateTime.UtcNow < bettingWindowEndsUtc)
        {
            StatusText = $"Bets open ({Math.Max(0, (int)Math.Ceiling((bettingWindowEndsUtc - DateTime.UtcNow).TotalSeconds))}s)";
            return;
        }

        // Current shooter went AFK/kicked after being announced
        var shooter = Players.GetPlayer(shooterName);
        if (shooter is null || shooter.IsAfk || shooter.IsKicked)
        {
            Msg.QueuePartyMessage($"[CRAPS] {shooterName} is unavailable and passes the dice.");
            AdvanceShooter();
            // Let next Tick handle announcement of new shooter
            return;
        }

        // Turn timer
        var turnLimit = Math.Max(10.0, CasinoUI.GlobalTurnTimeLimitSeconds);
        var elapsed = (DateTime.UtcNow - shooterTurnStartedUtc).TotalSeconds;
        var warnAt = turnLimit * (2.0 / 3.0);

        if (!shooterTurnWarningSent && elapsed >= warnAt)
        {
            shooterTurnWarningSent = true;
            Msg.QueuePartyMessage($"[CRAPS] {shooterName}, time's almost up.");
        }

        if (elapsed >= turnLimit)
        {
            Msg.QueuePartyMessage($"[CRAPS] {shooterName} timed out and passes the dice.");
            AdvanceShooter();
        }
    }

    public override ICasinoViewModel GetViewModel()
    {
        var seats = Players.GetAllActivePlayers().Select(p =>
        {
            var bets = GetBets(p)
                .GroupBy(b => b.Target, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CrapsBetEntry(g.Key, g.Sum(x => x.Amount)))
                .ToList();

            var total = bets.Sum(b => b.Amount);
            var first = bets.FirstOrDefault();
            return new PlayerSlotViewModel
            {
                PlayerName = p.Name,
                Bank = p.CurrentBank,
                BetAmount = total,
                ResultText = first is null ? string.Empty : $"{first.Target} {first.Amount}\uE049",
                HandResultTexts = bets.Select(b => $"{b.Target}:{b.Amount}").ToList(),
                IsActiveTurn = !string.IsNullOrWhiteSpace(shooterName) && p.Name.Equals(shooterName, StringComparison.OrdinalIgnoreCase)
            };
        }).ToList();

        seats.Insert(0, new PlayerSlotViewModel
        {
            PlayerName = "Table",
            IsDealer = true,
            ResultText = $"Dice: {die1}-{die2} | Point: {(point == 0 ? "OFF" : point)} | RolledAt: {lastRollAtUtc.Ticks}"
        });

        return new CrapsViewModel
        {
            GameTitle = "Craps",
            GameStatus = StatusText,
            Seats = seats,
            Actions = GetValidCommands().ToList()
        };
    }

    private CmdResult PlaceBet(Player p, string[] args)
    {
        // Support reversed order: BET PASS 100 as well as BET 100 PASS
        if (args.Length >= 2 && !int.TryParse(args[0], out _) && int.TryParse(args[^1], out _))
            args = [args[^1], .. args[..^1]];

        if (args.Length < 2 || !int.TryParse(args[0], out var amount) || amount <= 0)
            return CmdResult.Fail("Usage: BET [amount] [target]");

        if (amount < CasinoUI.GlobalMinBet || amount > CasinoUI.GlobalMaxBet)
            return CmdResult.Fail($"Bet must be between {CasinoUI.GlobalMinBet} and {CasinoUI.GlobalMaxBet}. ");

        var target = args[1].ToUpperInvariant();
        if (!IsSupportedTarget(target))
            return CmdResult.Fail("Supported bets: PASS, DONTPASS, FIELD, SEVEN, ANYCRAPS, PLACE4/5/6/8/9/10");

        if (bank.Deduct(p, amount, $"Craps bet {target}") != TransactionResult.Success)
            return CmdResult.Fail("Insufficient funds.");

        var bets = GetBets(p);
        bets.Add(new CrapsBetEntry(target, amount));
        Msg.QueuePartyMessage($"[CRAPS] {p.Name} bets {StandardizedFormatting.FormatCurrency(amount)} on {target}");
        return CmdResult.Ok("Bet accepted.");
    }

    private CmdResult Roll(Player shooter)
    {
        if (string.IsNullOrWhiteSpace(shooterName))
            return CmdResult.Fail("No shooter assigned yet. Place a bet first.");

        if (!shooter.Name.Equals(shooterName, StringComparison.OrdinalIgnoreCase))
            return CmdResult.Fail($"Only shooter {shooterName} may roll.");

        if (DateTime.UtcNow < bettingWindowEndsUtc)
        {
            var left = Math.Max(1, (int)Math.Ceiling((bettingWindowEndsUtc - DateTime.UtcNow).TotalSeconds));
            var windowTotal = Math.Max(1, (int)Math.Ceiling(CasinoUI.CrapsBettingDurationSeconds));
            Msg.QueuePartyMessage($"[CRAPS] Bets are open ({left}s/{windowTotal}s). Please wait.");
            return CmdResult.Fail($"Bets are open ({left}s/{windowTotal}s).");
        }

        if (!HasRequiredShooterLineBet(shooter))
        {
            Msg.QueuePartyMessage($"[CRAPS] {shooter.Name} must bet PASS or DONTPASS before rolling.");
            return CmdResult.Fail("Shooter must have a PASS or DONTPASS bet to roll.");
        }

        MarkShooter(shooter);
        die1 = RollDie();
        die2 = RollDie();
        lastRollAtUtc = DateTime.UtcNow;
        shooterTurnStartedUtc = DateTime.UtcNow;
        shooterTurnWarningSent = false;
        var total = die1 + die2;

        Msg.QueuePartyMessage($"[CRAPS] {shooter.Name} rolls {die1}+{die2}={total}");

        ResolveOneRollBets(total);

        if (point == 0)
        {
            if (total is 7 or 11)
            {
                StatusText = "Come-out win";
                Msg.QueuePartyMessage("[CRAPS] Come-out win.");
                ResolveLineAndPlaceBets(passWins: true, total);
            }
            else if (total is 2 or 3 or 12)
            {
                StatusText = "Come-out loss";
                Msg.QueuePartyMessage("[CRAPS] Come-out loss.");
                ResolveLineAndPlaceBets(passWins: false, total);
            }
            else
            {
                point = total;
                StatusText = $"Point is {point}";
                Msg.QueuePartyMessage($"[CRAPS] Point established: {point}");
            }

            bettingWindowEndsUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, CasinoUI.CrapsBettingDurationSeconds));
            return CmdResult.Ok("Roll resolved.");
        }

        if (total == point)
        {
            point = 0;
            StatusText = "Point made";
            Msg.QueuePartyMessage("[CRAPS] Point made.");
            ResolveLineAndPlaceBets(passWins: true, total);
        }
        else if (total == 7)
        {
            point = 0;
            StatusText = "Seven-out";
            Msg.QueuePartyMessage("[CRAPS] Seven out.");
            ResolveLineAndPlaceBets(passWins: false, total);
        }
        else
        {
            ResolvePlaceBetsOnNonLineRoll(total);
            StatusText = $"Point is {point}";
        }

        bettingWindowEndsUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, CasinoUI.CrapsBettingDurationSeconds));
        return CmdResult.Ok("Roll resolved.");
    }

    private void ResolveOneRollBets(int total)
    {
        foreach (var player in Players.GetAllActivePlayers())
        {
            var bets = GetBets(player);
            if (bets.Count == 0) continue;

            var keep = new List<CrapsBetEntry>();
            foreach (var bet in bets)
            {
                if (bet.Target is not ("FIELD" or "SEVEN" or "ANYCRAPS"))
                {
                    keep.Add(bet);
                    continue;
                }

                var won = false;
                var payoutMultiplier = 0;

                if (bet.Target == "FIELD")
                {
                    won = total is 2 or 3 or 4 or 9 or 10 or 11 or 12;
                    payoutMultiplier = total is 2 or 12 ? 3 : 2;
                }
                else if (bet.Target == "SEVEN")
                {
                    won = total == 7;
                    payoutMultiplier = 6;
                }
                else if (bet.Target == "ANYCRAPS")
                {
                    won = total is 2 or 3 or 12;
                    payoutMultiplier = 9;
                }

                if (won)
                {
                    var payout = bet.Amount * payoutMultiplier;
                    bank.Award(player, payout, $"Craps {bet.Target} win");
                    Msg.QueuePartyMessage($"[CRAPS] {player.Name} wins {StandardizedFormatting.FormatCurrency(payout)} on {bet.Target}");
                }
            }

            SetBets(player, keep);
        }
    }

    private void ResolveLineAndPlaceBets(bool passWins, int total)
    {
        foreach (var player in Players.GetAllActivePlayers())
        {
            var bets = GetBets(player);
            if (bets.Count == 0) continue;

            var keep = new List<CrapsBetEntry>();
            foreach (var bet in bets)
            {
                var won = false;

                if (bet.Target is "PASS" or "DONTPASS")
                {
                    won = (bet.Target == "PASS" && passWins) || (bet.Target == "DONTPASS" && !passWins);
                }
                else if (bet.Target.StartsWith("PLACE", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(bet.Target.AsSpan(5), out var placeNum) && total == placeNum)
                    {
                        won = true;
                    }
                }
                else
                {
                    keep.Add(bet);
                    continue;
                }

                if (won)
                {
                    int payout;
                    if (bet.Target is "PASS" or "DONTPASS")
                    {
                        payout = bet.Amount * 2;
                    }
                    else if (bet.Target.StartsWith("PLACE", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(bet.Target.AsSpan(5), out var pn))
                    {
                        payout = pn switch
                        {
                            4 or 10 => bet.Amount + (int)Math.Ceiling(bet.Amount * 9.0 / 5.0),
                            5 or 9 => bet.Amount + (int)Math.Ceiling(bet.Amount * 7.0 / 5.0),
                            6 or 8 => bet.Amount + (int)Math.Ceiling(bet.Amount * 7.0 / 6.0),
                            _ => bet.Amount * 2
                        };
                    }
                    else
                    {
                        payout = bet.Amount * 2;
                    }
                    bank.Award(player, payout, $"Craps {bet.Target} win");
                    Msg.QueuePartyMessage($"[CRAPS] {player.Name} wins {StandardizedFormatting.FormatCurrency(payout)} on {bet.Target}");
                }
            }

            SetBets(player, keep);
        }

        OnRoundComplete();
        AdvanceShooter();
    }

    private void ResolvePlaceBetsOnNonLineRoll(int total)
    {
        foreach (var player in Players.GetAllActivePlayers())
        {
            var bets = GetBets(player);
            if (bets.Count == 0) continue;

            var keep = new List<CrapsBetEntry>();
            foreach (var bet in bets)
            {
                if (!bet.Target.StartsWith("PLACE", StringComparison.OrdinalIgnoreCase))
                {
                    keep.Add(bet);
                    continue;
                }

                if (int.TryParse(bet.Target.AsSpan(5), out var placeNum) && total == placeNum)
                {
                    var payout = placeNum switch
                    {
                        4 or 10 => bet.Amount + (int)Math.Ceiling(bet.Amount * 9.0 / 5.0),
                        5 or 9 => bet.Amount + (int)Math.Ceiling(bet.Amount * 7.0 / 5.0),
                        6 or 8 => bet.Amount + (int)Math.Ceiling(bet.Amount * 7.0 / 6.0),
                        _ => bet.Amount * 2
                    };
                    bank.Award(player, payout, $"Craps {bet.Target} win");
                    Msg.QueuePartyMessage($"[CRAPS] {player.Name} wins {StandardizedFormatting.FormatCurrency(payout)} on {bet.Target}");
                }
                else
                {
                    keep.Add(bet);
                }
            }

            SetBets(player, keep);
        }
    }

    private bool HasRequiredShooterLineBet(Player shooter)
    {
        return GetBets(shooter)
            .Where(b => b.Target is "PASS" or "DONTPASS")
            .Sum(b => b.Amount) >= CasinoUI.GlobalMinBet;
    }

    private static bool IsSupportedTarget(string target)
    {
        if (target is "PASS" or "DONTPASS" or "FIELD" or "SEVEN" or "ANYCRAPS")
            return true;

        if (!target.StartsWith("PLACE", StringComparison.OrdinalIgnoreCase))
            return false;

        return int.TryParse(target.AsSpan(5), out var n) && (n is 4 or 5 or 6 or 8 or 9 or 10);
    }

    private void MarkShooter(Player shooter)
    {
        shooterName = shooter.Name;
        shooter.Metadata["IsShooter"] = true;
        shooterTurnStartedUtc = DateTime.UtcNow;
        shooterTurnWarningSent = false;
    }

    private void AdvanceShooter()
    {
        var active = Players.GetAllActivePlayers().ToList();
        if (active.Count == 0)
        {
            shooterName = string.Empty;
            shooterIndex = -1;
            lastAnnouncedShooter = string.Empty;
            shooterTurnStartedUtc = DateTime.MinValue;
            shooterTurnWarningSent = false;
            bettingWindowEndsUtc = DateTime.MinValue;
            return;
        }

        if (shooterIndex < 0)
            shooterIndex = 0;
        else
            shooterIndex = (shooterIndex + 1) % active.Count;

        // Set the new shooter name but don't announce — Tick will handle announcement
        shooterName = active[shooterIndex].Name;
        lastAnnouncedShooter = string.Empty;
    }

    private int RollDie()
    {
        var card = rngShoe.Draw();
        var seed = HashCode.Combine(card.Suit, card.Value, DateTime.UtcNow.Ticks);
        return Math.Abs(seed % 6) + 1;
    }

    private static List<CrapsBetEntry> GetBets(Player player)
    {
        if (player.Metadata.TryGetValue("Craps.Bets", out var obj) && obj is List<CrapsBetEntry> bets)
            return bets;

        var fallback = new List<CrapsBetEntry>();
        if (player.Metadata.TryGetValue("Craps.BetType", out var tObj)
            && tObj is string target
            && player.Metadata.TryGetValue("Craps.BetAmount", out var aObj)
            && aObj is int amount
            && amount > 0)
        {
            fallback.Add(new CrapsBetEntry(target.ToUpperInvariant(), amount));
        }

        player.Metadata.Remove("Craps.BetType");
        player.Metadata.Remove("Craps.BetAmount");
        player.Metadata["Craps.Bets"] = fallback;
        return fallback;
    }

    private static void SetBets(Player player, List<CrapsBetEntry> bets)
    {
        player.Metadata["Craps.Bets"] = bets;
    }

    private sealed class CrapsViewModel : BaseViewModel
    {
        public List<string> Actions { get; set; } = new();
        public override IReadOnlyList<string> GetActionButtons() => Actions;
    }

    public override void OnForceStop()
    {
        point = 0;
        die1 = 1;
        die2 = 1;
        shooterName = string.Empty;
        lastAnnouncedShooter = string.Empty;
        lastRollAtUtc = DateTime.MinValue;
        shooterIndex = -1;
        shooterTurnStartedUtc = DateTime.MinValue;
        shooterTurnWarningSent = false;
        bettingWindowEndsUtc = DateTime.MinValue;
        StatusText = "Come-out phase";
    }
}
