using System;
using System.Collections.Generic;
using System.Linq;
using ChatCasino.Models;

namespace ChatCasino.Services;

public enum PokerRankCategory
{
    HighCard = 1,
    Pair = 2,
    TwoPair = 3,
    Trips = 4,
    Straight = 5,
    Flush = 6,
    FullHouse = 7,
    Quads = 8,
    StraightFlush = 9
}

public readonly record struct HandRank(PokerRankCategory Category, IReadOnlyList<int> Kickers, string Description)
{
    public static int Compare(HandRank a, HandRank b)
    {
        var cat = a.Category.CompareTo(b.Category);
        if (cat != 0) return cat;

        var len = Math.Min(a.Kickers.Count, b.Kickers.Count);
        for (var i = 0; i < len; i++)
        {
            if (a.Kickers[i] == b.Kickers[i]) continue;
            return a.Kickers[i].CompareTo(b.Kickers[i]);
        }

        return a.Kickers.Count.CompareTo(b.Kickers.Count);
    }
}

public sealed class PokerEvaluator
{
    public HandRank Evaluate(List<Card> cards)
    {
        if (cards.Count < 5) throw new ArgumentException("Need at least 5 cards.");

        var best = EvaluateFive(cards.Take(5).ToList());
        foreach (var combo in Combinations(cards, 5))
        {
            var next = EvaluateFive(combo);
            if (HandRank.Compare(next, best) > 0) best = next;
        }

        return best;
    }

    private static IEnumerable<List<Card>> Combinations(List<Card> cards, int choose)
    {
        var n = cards.Count;
        var idx = Enumerable.Range(0, choose).ToArray();

        while (true)
        {
            yield return idx.Select(i => cards[i]).ToList();

            var t = choose - 1;
            while (t >= 0 && idx[t] == n - choose + t) t--;
            if (t < 0) yield break;
            idx[t]++;
            for (var i = t + 1; i < choose; i++) idx[i] = idx[i - 1] + 1;
        }
    }

    private static HandRank EvaluateFive(List<Card> hand)
    {
        var ranks = hand.Select(GetRank).OrderByDescending(x => x).ToList();
        var groups = ranks.GroupBy(x => x).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).ToList();
        var flush = hand.Select(c => c.Suit).Distinct().Count() == 1;
        var flushRanks = hand
            .GroupBy(c => c.Suit)
            .Where(g => g.Count() >= 5)
            .Select(g => g.Select(GetRank).OrderByDescending(x => x).Take(5).ToList())
            .OrderByDescending(g => g[0])
            .FirstOrDefault();
        var straight = IsStraight(ranks, out var highStraight);

        if (flush && straight)
            return new HandRank(PokerRankCategory.StraightFlush, new[] { highStraight }, $"Straight Flush ({RankName(highStraight)} high)");

        if (groups[0].Count() == 4)
            return new HandRank(PokerRankCategory.Quads, new[] { groups[0].Key, groups[1].Key }, $"Four of a Kind ({PluralRankName(groups[0].Key)})");

        if (groups[0].Count() == 3 && groups[1].Count() == 2)
            return new HandRank(PokerRankCategory.FullHouse, new[] { groups[0].Key, groups[1].Key }, $"Full House ({PluralRankName(groups[0].Key)} over {PluralRankName(groups[1].Key)})");

        if (flush && flushRanks is not null)
            return new HandRank(PokerRankCategory.Flush, flushRanks, $"Flush ({RankName(flushRanks[0])} high)");

        if (straight)
            return new HandRank(PokerRankCategory.Straight, new[] { highStraight }, $"Straight ({RankName(highStraight)} high)");

        if (groups[0].Count() == 3)
            return new HandRank(PokerRankCategory.Trips, groups.SelectMany(g => Enumerable.Repeat(g.Key, g.Count())).ToList(), $"Three of a Kind ({PluralRankName(groups[0].Key)})");

        if (groups[0].Count() == 2 && groups[1].Count() == 2)
            return new HandRank(PokerRankCategory.TwoPair, new[] { groups[0].Key, groups[1].Key, groups[2].Key }, $"Two Pair ({PluralRankName(groups[0].Key)} and {PluralRankName(groups[1].Key)})");

        if (groups[0].Count() == 2)
            return new HandRank(PokerRankCategory.Pair, groups.SelectMany(g => Enumerable.Repeat(g.Key, g.Count())).ToList(), $"Pair of {PluralRankName(groups[0].Key)}");

        return new HandRank(PokerRankCategory.HighCard, ranks, $"High Card ({RankName(ranks[0])})");
    }

    private static bool IsStraight(List<int> ranks, out int high)
    {
        var distinct = ranks.Distinct().OrderByDescending(x => x).ToList();
        if (distinct.Count < 5)
        {
            high = 0;
            return false;
        }

        if (distinct.SequenceEqual(new[] { 14, 5, 4, 3, 2 }))
        {
            high = 5;
            return true;
        }

        for (var i = 0; i < distinct.Count - 4; i++)
        {
            if (distinct[i] - 1 == distinct[i + 1] && distinct[i + 1] - 1 == distinct[i + 2] &&
                distinct[i + 2] - 1 == distinct[i + 3] && distinct[i + 3] - 1 == distinct[i + 4])
            {
                high = distinct[i];
                return true;
            }
        }

        high = 0;
        return false;
    }

    private static int GetRank(Card c) => c.Value switch
    {
        "A" => 14,
        "K" => 13,
        "Q" => 12,
        "J" => 11,
        _ => int.TryParse(c.Value, out var n) ? n : 0
    };

    private static string RankName(int rank) => rank switch
    {
        14 => "Ace",
        13 => "King",
        12 => "Queen",
        11 => "Jack",
        10 => "Ten",
        9 => "Nine",
        8 => "Eight",
        7 => "Seven",
        6 => "Six",
        5 => "Five",
        4 => "Four",
        3 => "Three",
        2 => "Two",
        _ => rank.ToString()
    };

    private static string PluralRankName(int rank) => rank switch
    {
        6 => "Sixes",
        _ => $"{RankName(rank)}s"
    };
}
