using System.Collections.Generic;
using ChatCasino.Models;

namespace ChatCasino.Engine;

public interface IDealerRuleStrategy
{
    string Name { get; }
    bool ShouldHit(IReadOnlyList<Card> hand);
}

public sealed class HitsSoft17Strategy : IDealerRuleStrategy
{
    public string Name => "H17";

    public bool ShouldHit(IReadOnlyList<Card> hand)
    {
        var (score, isSoft) = BlackjackScoring.Score(hand);
        if (score < 17) return true;
        return score == 17 && isSoft;
    }
}

public sealed class StandsSoft17Strategy : IDealerRuleStrategy
{
    public string Name => "S17";

    public bool ShouldHit(IReadOnlyList<Card> hand)
    {
        var (score, _) = BlackjackScoring.Score(hand);
        return score < 17;
    }
}

internal static class BlackjackScoring
{
    public static (int score, bool isSoft) Score(IReadOnlyList<Card> hand)
    {
        var score = 0;
        var aces = 0;

        foreach (var card in hand)
        {
            if (card.Value == "A")
            {
                aces++;
                score += 11;
            }
            else if (card.Value is "K" or "Q" or "J")
            {
                score += 10;
            }
            else
            {
                score += int.TryParse(card.Value, out var n) ? n : 0;
            }
        }

        var soft = aces > 0;
        while (score > 21 && aces > 0)
        {
            score -= 10;
            aces--;
        }

        return (score, soft && aces > 0);
    }
}
