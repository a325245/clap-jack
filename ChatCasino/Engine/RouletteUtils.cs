using System;

namespace ChatCasino.Engine;

public static class RouletteUtils
{
    private static readonly int[] RedNumbers =
    {
        1,3,5,7,9,12,14,16,18,19,21,23,25,27,30,32,34,36
    };

    public static bool IsRed(int n) => Array.IndexOf(RedNumbers, n) >= 0;
    public static bool IsBlack(int n) => n is >= 1 and <= 36 && !IsRed(n);

    public static bool IsValidTarget(string target)
    {
        target = target.ToUpperInvariant();
        if (int.TryParse(target, out var n))
            return n is >= 0 and <= 36;

        return target is "RED" or "BLACK" or "EVEN" or "ODD";
    }

    public static int PayoutMultiplier(string target)
    {
        if (int.TryParse(target, out var n) && n is >= 0 and <= 36)
            return 36;
        return 2;
    }

    public static bool IsWinningTarget(int result, string target)
    {
        target = target.ToUpperInvariant();

        if (int.TryParse(target, out var n))
            return result == n;

        return target switch
        {
            "RED" => IsRed(result),
            "BLACK" => IsBlack(result),
            "EVEN" => result != 0 && result % 2 == 0,
            "ODD" => result % 2 == 1,
            _ => false
        };
    }
}
