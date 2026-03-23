namespace SamplePlugin.Models;

public class RouletteBet
{
    public string Type { get; set; } = "OUTSIDE"; // "INSIDE" or "OUTSIDE"
    public string Target { get; set; } = string.Empty; // "14", "RED", "EVEN", etc.
    public int Amount { get; set; } = 0;

    public static readonly int[] RedNumbers = { 1,3,5,7,9,12,14,16,18,19,21,23,25,27,30,32,34,36 };

    public static bool IsRed(int number) => System.Array.IndexOf(RedNumbers, number) >= 0;
    public static bool IsBlack(int number) => number != 0 && !IsRed(number);

    public bool IsWinner(int result)
    {
        if (Type == "INSIDE")
            return int.TryParse(Target, out int n) && n == result;

        return Target switch
        {
            "RED"   => IsRed(result),
            "BLACK" => IsBlack(result),
            "EVEN"  => result != 0 && result % 2 == 0,
            "ODD"   => result != 0 && result % 2 != 0,
            _       => false
        };
    }

    public int Payout() => Type == "INSIDE" ? Amount * 36 : Amount * 2;

    public override string ToString() => $"{Amount}G on {Target}";
}
