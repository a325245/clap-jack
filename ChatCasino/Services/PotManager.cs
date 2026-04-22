using System;
using System.Collections.Generic;
using System.Linq;
using ChatCasino.Models;

namespace ChatCasino.Services;

public sealed record SidePot(int Amount, IReadOnlyCollection<string> EligiblePlayers);

public sealed class PotManager
{
    public IReadOnlyList<SidePot> BuildSidePots(IEnumerable<Player> players)
    {
        var committed = players
            .Select(p => new
            {
                p.Name,
                Bet = GetInt(p, "Poker.Committed"),
                Folded = GetBool(p, "Poker.Folded")
            })
            .Where(x => x.Bet > 0)
            .OrderBy(x => x.Bet)
            .ToList();

        var pots = new List<SidePot>();
        var last = 0;

        for (var i = 0; i < committed.Count; i++)
        {
            var level = committed[i].Bet;
            var contributors = committed.Count - i;
            var tranche = (level - last) * contributors;
            if (tranche <= 0) continue;

            var eligible = committed
                .Skip(i)
                .Where(x => !x.Folded)
                .Select(x => x.Name)
                .ToArray();

            pots.Add(new SidePot(tranche, eligible));
            last = level;
        }

        return pots;
    }

    private static int GetInt(Player p, string key)
        => p.Metadata.TryGetValue(key, out var v) && v is int i ? i : 0;

    private static bool GetBool(Player p, string key)
        => p.Metadata.TryGetValue(key, out var v) && v is bool b && b;
}
