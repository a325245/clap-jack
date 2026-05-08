using ChatCasino.Models;

namespace ChatCasino.Services;

public static class StandardizedFormatting
{
    private const string GilSuffix = "\uE049";

    public static string FormatCurrency(int amount) => $"{amount}{GilSuffix}";

    public static string GetDealerTag(GameType game) => game switch
    {
        GameType.Blackjack => "[BLACKJACK]",
        GameType.Roulette => "[ROULETTE]",
        GameType.Craps => "[CRAPS]",
        GameType.Baccarat => "[BACCARAT]",
        GameType.ChocoboRacing => "[CHOCOBO]",
        GameType.TexasHoldEmPvP => "[POKER]",
        GameType.TexasHoldEmPvD => "[PVD]",
        GameType.Ultima => "[ULTIMA]",
        GameType.Bingo => "[BINGO]",
        _ => "[CASINO]"
    };

    public static string FormatCard(Card card)
    {
        var symbol = card.Suit switch
        {
            "S" => "♠",
            "H" => "♥",
            "D" => "♦",
            "C" => "♣",
            _ => card.Suit
        };

        return $"【{card.Value}{symbol}】";
    }
}
