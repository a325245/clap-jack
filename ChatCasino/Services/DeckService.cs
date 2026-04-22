using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using ChatCasino.Models;

namespace ChatCasino.Services;

public sealed class DeckShoe<T>
{
    private readonly Queue<T> cards;
    private readonly IReadOnlyList<T> seed;

    public DeckShoe(IEnumerable<T> cards, bool persistent)
    {
        var cardList = cards.ToList();
        seed = cardList;
        this.cards = new Queue<T>(cardList);
        Persistent = persistent;
    }

    public bool Persistent { get; }
    public int Remaining => cards.Count;

    public T Draw()
    {
        if (cards.Count == 0)
            Refill();

        return cards.Dequeue();
    }

    public IReadOnlyCollection<T> Snapshot() => cards.ToArray();

    private void Refill()
    {
        var list = seed.ToList();
        DeckService.ShuffleInPlace(list);
        cards.Clear();
        foreach (var item in list)
            cards.Enqueue(item);
    }
}

public sealed class DeckService : IDeckService
{
    public DeckShoe<Card> GetStandardDeck(int deckCount, bool shuffled)
    {
        var cards = new List<Card>(deckCount * 52);
        for (var i = 0; i < Math.Max(1, deckCount); i++)
        {
            foreach (var suit in new[] { "S", "H", "D", "C" })
            foreach (var value in new[] { "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K", "A" })
                cards.Add(new Card(suit, value));
        }

        if (shuffled)
            ShuffleInPlace(cards);

        return new DeckShoe<Card>(cards, persistent: true);
    }

    public DeckShoe<UltimaDeckCard> GetUltimaDeck()
    {
        var cards = new List<UltimaDeckCard>();
        var colors = new[] { "WATER", "FIRE", "GRASS", "LIGHT" };

        foreach (var color in colors)
        {
            // One 0 per color, two of each 1-9 = 19 per color = 76 total number cards
            cards.Add(new UltimaDeckCard(color, "NUMBER", $"{color[0]}0"));
            for (var n = 1; n <= 9; n++)
            {
                cards.Add(new UltimaDeckCard(color, "NUMBER", $"{color[0]}{n}"));
                cards.Add(new UltimaDeckCard(color, "NUMBER", $"{color[0]}{n}"));
            }

            // Two of each action per color = 24 total action cards
            cards.Add(new UltimaDeckCard(color, "COUNTERSPELL", $"{color[0]}CS"));
            cards.Add(new UltimaDeckCard(color, "COUNTERSPELL", $"{color[0]}CS"));
            cards.Add(new UltimaDeckCard(color, "REWIND", $"{color[0]}RW"));
            cards.Add(new UltimaDeckCard(color, "REWIND", $"{color[0]}RW"));
            cards.Add(new UltimaDeckCard(color, "SUMMON2", $"{color[0]}S2"));
            cards.Add(new UltimaDeckCard(color, "SUMMON2", $"{color[0]}S2"));
        }

        // 4 Polymorph + 4 Polymorph Draw Four = 8 wild cards
        cards.AddRange(Enumerable.Repeat(new UltimaDeckCard("WILD", "POLYMORPH", "PL"), 4));
        cards.AddRange(Enumerable.Repeat(new UltimaDeckCard("WILD", "POLYMORPH4", "PL4"), 4));

        // Total: 76 + 24 + 4 + 4 = 108 cards
        ShuffleInPlace(cards);
        return new DeckShoe<UltimaDeckCard>(cards, persistent: true);
    }

    internal static void ShuffleInPlace<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
