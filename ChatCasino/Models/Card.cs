using ChatCasino.UI;

namespace ChatCasino.Models;

public struct Card
{
    public string Suit { get; set; }
    public string Value { get; set; }

    public Card(string suit, string value)
    {
        Suit = suit;
        Value = value;
    }

    public override string ToString() => GetCardDisplay();

    public string GetCardDisplay()
    {
        var suitSymbol = Suit switch
        {
            "H" => "♥",
            "D" => "♦",
            "S" => "♠",
            "C" => "♣",
            _ => Suit
        };

        var card = $"{Value}{suitSymbol}";
        return CasinoUI.BracketStyle switch
        {
            CardBracketStyle.Square => $"[{card}]",
            CardBracketStyle.Lenticular => $"\u3010{card}\u3011",
            _ => card
        };
    }
}
