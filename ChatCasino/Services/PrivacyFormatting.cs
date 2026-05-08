using ChatCasino.UI;

namespace ChatCasino.Services;

public static class PrivacyFormatting
{
    public static string FormatRoundDelta(int delta)
    {
        if (!CasinoUI.PrivateBanks)
        {
            var sign = delta >= 0 ? "+" : string.Empty;
            return $"{sign}{delta}\uE049";
        }

        if (delta > 0) return "WIN";
        if (delta < 0) return "LOSE";
        return "PUSH";
    }

    public static string FormatBankLabel(int bank)
        => CasinoUI.PrivateBanks ? "Bank Hidden" : $"Bank {bank}\uE049";
}
