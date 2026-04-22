using System.Collections.Generic;
using ChatCasino.Models;

namespace ChatCasino.Services;

public sealed class SideBetService
{
    private readonly IBankService bank;

    public SideBetService(IBankService bank)
    {
        this.bank = bank;
    }

    public void ResolveBlackjackSideBets(Player player)
    {
        if (!player.Metadata.TryGetValue("Blackjack.Hands", out var handsObj) || handsObj is not List<List<Card>> hands || hands.Count == 0)
            return;

        var hand = hands[0];
        if (hand.Count < 2) return;

        if (hand[0].Value == hand[1].Value)
        {
            var amount = GetInt(player, "Blackjack.PerfectPairsBet");
            if (amount > 0)
                bank.Award(player, amount * 11, "Perfect Pairs payout");
        }

        var side = GetInt(player, "Blackjack.21Plus3Bet");
        if (side > 0)
            bank.Award(player, side * 3, "21+3 placeholder payout");
    }

    public void ResolveCrapsPropBets(Player player, int die1, int die2)
    {
        var hardways = GetInt(player, "Craps.HardwaysBet");
        if (hardways <= 0) return;

        if (die1 == die2)
            bank.Award(player, hardways * 8, "Craps hardways payout");
    }

    private static int GetInt(Player p, string key)
        => p.Metadata.TryGetValue(key, out var v) && v is int i ? i : 0;
}
