namespace SamplePlugin.Models;

public struct Card
{
    public string Suit { get; set; }      // S, H, D, C
    public string Value { get; set; }     // 2-10, J, Q, K, A

    public Card(string suit, string value)
    {
        Suit = suit;
        Value = value;
    }

    public override string ToString() => GetCardDisplay();

    public string GetCardDisplay()
    {
        string suitSymbol = Suit switch
        {
            "H" => "♥", "D" => "♦", 
            "S" => "♠", "C" => "♣",
            _ => Suit
        };

        return $"{Value}{suitSymbol}";
    }

    public string GetImageFileName()
    {
        string suitName = Suit switch
        {
            "H" => "hearts",
            "D" => "diamonds", 
            "S" => "spades",
            "C" => "clubs",
            _ => "spades"
        };

        string valueName = Value switch
        {
            "J" => "jack",
            "Q" => "queen", 
            "K" => "king",
            "A" => "ace",
            _ => Value.ToLower()
        };

        // Face cards have '2' suffix
        string suffix = (Value == "J" || Value == "Q" || Value == "K") ? "2" : "";

        return $"{valueName}_of_{suitName}{suffix}.png";
    }

    public bool IsRed => Suit is "H" or "D";
    public bool IsBlack => Suit is "S" or "C";

    public int GetNumericValue()
    {
        return Value switch
        {
            "J" or "Q" or "K" => 10,
            "A" => 11,
            _ => int.Parse(Value)
        };
    }

    public bool IsAce => Value == "A";
    public bool IsFaceCard => Value is "J" or "Q" or "K";
    public bool IsTenValue => Value is "10" or "J" or "Q" or "K";
}
