using System;
using System.Collections.Concurrent;
using System.Linq;
using ChatCasino.Models;

namespace ChatCasino.Services;

public sealed class BankTransaction
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string Player { get; init; } = string.Empty;
    public int Amount { get; init; }
    public string Reason { get; init; } = string.Empty;
    public TransactionResult Result { get; init; }
    public int BalanceAfter { get; init; }
}

public sealed class BankService : IBankService
{
    private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.OrdinalIgnoreCase);

    public Action<BankTransaction>? OnTransactionLogged { get; set; }

    public TransactionResult VerifyFunds(Player player, int amount)
    {
        if (player is null) return TransactionResult.PlayerNotFound;
        if (amount <= 0) return TransactionResult.InvalidAmount;
        return player.CurrentBank >= amount ? TransactionResult.Success : TransactionResult.InsufficientFunds;
    }

    public TransactionResult Deduct(Player player, int amount, string reason)
    {
        return Mutate(player, -amount, reason);
    }

    public TransactionResult Credit(Player player, int amount, string reason)
    {
        return Mutate(player, amount, reason);
    }

    public TransactionResult Award(Player player, int amount, string reason)
    {
        return Credit(player, amount, reason);
    }

    private TransactionResult Mutate(Player player, int delta, string reason)
    {
        if (player is null) return TransactionResult.PlayerNotFound;
        if (delta == 0) return TransactionResult.InvalidAmount;

        var gate = locks.GetOrAdd(player.Name, _ => new object());

        lock (gate)
        {
            if (delta < 0)
            {
                int cost = Math.Abs(delta);
                if (player.CurrentBank < cost)
                {
                    Log(player.Name, cost, reason, TransactionResult.InsufficientFunds, player.CurrentBank);
                    return TransactionResult.InsufficientFunds;
                }

                if (IsWagerReason(reason))
                {
                    var gameKey = ResolveRoundStartGameKey(reason);
                    var key = $"RoundStartBank:{gameKey}";
                    if (!player.Metadata.ContainsKey(key))
                        player.Metadata[key] = player.CurrentBank;
                }
            }

            checked
            {
                player.CurrentBank += delta;
            }

            Log(player.Name, Math.Abs(delta), reason, TransactionResult.Success, player.CurrentBank);
            return TransactionResult.Success;
        }
    }

    private void Log(string playerName, int amount, string reason, TransactionResult result, int balanceAfter)
    {
        OnTransactionLogged?.Invoke(new BankTransaction
        {
            Player = playerName,
            Amount = amount,
            Reason = reason,
            Result = result,
            BalanceAfter = balanceAfter
        });
    }

    private static bool IsWagerReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return false;

        return reason.Contains("bet", StringComparison.OrdinalIgnoreCase)
               || reason.Contains("blind", StringComparison.OrdinalIgnoreCase)
               || reason.Contains("call", StringComparison.OrdinalIgnoreCase)
               || reason.Contains("raise", StringComparison.OrdinalIgnoreCase)
               || reason.Contains("all-in", StringComparison.OrdinalIgnoreCase)
               || reason.Contains("all in", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveRoundStartGameKey(string reason)
    {
        var token = reason.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return token.ToUpperInvariant() switch
        {
            "POKER" => "TexasHoldEm",
            "CHOCOBO" => "ChocoboRacing",
            _ => token
        };
    }
}
