using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SamplePlugin.Models;

// ── Enumerations ──────────────────────────────────────────────────────────────

/// <summary>The four elemental suits of Ultima!, plus Wild for Polymorph cards.</summary>
public enum UltimaColor { Water, Fire, Grass, Love, Wild }

/// <summary>All Ultima! card types. Numbers are 0-9; values ≥10 are action/wild cards.</summary>
public enum UltimaCardType
{
    N0 = 0, N1, N2, N3, N4, N5, N6, N7, N8, N9,
    Counterspell    = 10,   // Skip
    Rewind          = 11,   // Reverse
    Summon          = 12,   // Draw +2
    Polymorph       = 13,   // Wild (choose color)
    PolymorphSummon = 14    // Wild Draw +4
}

// ── Card class ────────────────────────────────────────────────────────────────

public class UltimaCard
{
    public UltimaColor    Color { get; set; }
    public UltimaCardType Type  { get; set; }

    public bool IsWild   => Type is UltimaCardType.Polymorph or UltimaCardType.PolymorphSummon;
    public bool IsNumber => (int)Type <= 9;
    public bool IsAction => Type is UltimaCardType.Counterspell
                             or UltimaCardType.Rewind or UltimaCardType.Summon;

    // ── Short code used in chat commands (e.g. "W3", "FCS", "G+2", "PL", "PL4") ──
    public string Code
    {
        get
        {
            string c = Color switch
            {
                UltimaColor.Water => "W",
                UltimaColor.Fire  => "F",
                UltimaColor.Grass => "G",
                UltimaColor.Love  => "L",
                _                 => ""
            };
            string t = Type switch
            {
                UltimaCardType.N0             => "0",
                UltimaCardType.N1             => "1",
                UltimaCardType.N2             => "2",
                UltimaCardType.N3             => "3",
                UltimaCardType.N4             => "4",
                UltimaCardType.N5             => "5",
                UltimaCardType.N6             => "6",
                UltimaCardType.N7             => "7",
                UltimaCardType.N8             => "8",
                UltimaCardType.N9             => "9",
                UltimaCardType.Counterspell   => "CS",
                UltimaCardType.Rewind         => "RW",
                UltimaCardType.Summon         => "+2",
                UltimaCardType.Polymorph      => "PL",
                UltimaCardType.PolymorphSummon=> "PL4",
                _                             => "?"
            };
            return c + t;
        }
    }

    // ── Full display name for chat announcements ───────────────────────────────
    public string DisplayName
    {
        get
        {
            string colorName = Color switch
            {
                UltimaColor.Water => "Water",
                UltimaColor.Fire  => "Fire",
                UltimaColor.Grass => "Grass",
                UltimaColor.Love  => "Light",
                _                 => ""
            };
            string typeName = Type switch
            {
                UltimaCardType.Counterspell   => "Counterspell",
                UltimaCardType.Rewind         => "Rewind",
                UltimaCardType.Summon         => "Summon+2",
                UltimaCardType.Polymorph      => "Polymorph",
                UltimaCardType.PolymorphSummon=> "Polymorph+4",
                _                             => ((int)Type).ToString()
            };
            return IsWild ? typeName : $"{colorName} {typeName}";
        }
    }

    // ── Symbol shown on card face in UI ───────────────────────────────────────
    public string Symbol => Type switch
    {
        UltimaCardType.Counterspell   => "\u2298", // ⊘
        UltimaCardType.Rewind         => "\u21BA", // ↺
        UltimaCardType.Summon         => "+2",
        UltimaCardType.Polymorph      => "\u2605", // ★
        UltimaCardType.PolymorphSummon=> "\u2605+4",
        _                             => ((int)Type).ToString()
    };

    // ── Background/foreground colors for UI rendering ─────────────────────────
    public static Vector4 ColorVec(UltimaColor c) => c switch
    {
        UltimaColor.Water => new Vector4(0.14f, 0.38f, 0.82f, 1f),
        UltimaColor.Fire  => new Vector4(0.82f, 0.14f, 0.14f, 1f),
        UltimaColor.Grass => new Vector4(0.12f, 0.64f, 0.22f, 1f),
        UltimaColor.Love  => new Vector4(0.84f, 0.76f, 0.08f, 1f),
        _                 => new Vector4(0.22f, 0.16f, 0.32f, 1f)   // wild = dark purple
    };

    /// <summary>Text color on a card of this color (black on Love, white on others).</summary>
    public static Vector4 TextColor(UltimaColor c) =>
        c == UltimaColor.Love ? new Vector4(0f, 0f, 0f, 1f) : new Vector4(1f, 1f, 1f, 1f);

    public static string ColorDisplayName(UltimaColor c) => c switch
    {
        UltimaColor.Water => "Water",
        UltimaColor.Fire  => "Fire",
        UltimaColor.Grass => "Grass",
        UltimaColor.Love  => "Light",
        _                 => "Wild"
    };

    // ── Deck factory ──────────────────────────────────────────────────────────
    /// <summary>Creates the standard 108-card Ultima! deck.</summary>
    public static List<UltimaCard> CreateDeck()
    {
        var deck = new List<UltimaCard>(108);
        foreach (var color in new[] { UltimaColor.Water, UltimaColor.Fire, UltimaColor.Grass, UltimaColor.Love })
        {
            deck.Add(new UltimaCard { Color = color, Type = UltimaCardType.N0 });
            for (int n = 1; n <= 9; n++)
            {
                deck.Add(new UltimaCard { Color = color, Type = (UltimaCardType)n });
                deck.Add(new UltimaCard { Color = color, Type = (UltimaCardType)n });
            }
            for (int i = 0; i < 2; i++)
            {
                deck.Add(new UltimaCard { Color = color, Type = UltimaCardType.Counterspell });
                deck.Add(new UltimaCard { Color = color, Type = UltimaCardType.Rewind });
                deck.Add(new UltimaCard { Color = color, Type = UltimaCardType.Summon });
            }
        }
        for (int i = 0; i < 4; i++)
        {
            deck.Add(new UltimaCard { Color = UltimaColor.Wild, Type = UltimaCardType.Polymorph });
            deck.Add(new UltimaCard { Color = UltimaColor.Wild, Type = UltimaCardType.PolymorphSummon });
        }
        return deck;
    }

    public static void Shuffle(List<UltimaCard> deck)
    {
        var rng = new Random();
        for (int n = deck.Count - 1; n > 0; n--)
        {
            int k = rng.Next(n + 1);
            (deck[k], deck[n]) = (deck[n], deck[k]);
        }
    }

    // ── Parsing ───────────────────────────────────────────────────────────────
    /// <summary>Parse a card code (e.g. "W3", "FCS", "PL4") into an UltimaCard. Returns null on failure.</summary>
    public static UltimaCard? Parse(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        code = code.Trim().ToUpperInvariant();
        if (code == "PL4") return new UltimaCard { Color = UltimaColor.Wild, Type = UltimaCardType.PolymorphSummon };
        if (code == "PL")  return new UltimaCard { Color = UltimaColor.Wild, Type = UltimaCardType.Polymorph };
        if (code.Length < 2) return null;
        UltimaColor color = code[0] switch
        {
            'W' => UltimaColor.Water,
            'F' => UltimaColor.Fire,
            'G' => UltimaColor.Grass,
            'L' => UltimaColor.Love,
            _   => (UltimaColor)99
        };
        if ((int)color == 99) return null;
        string rest = code[1..];
        UltimaCardType type;
        if      (rest == "CS")  type = UltimaCardType.Counterspell;
        else if (rest == "RW")  type = UltimaCardType.Rewind;
        else if (rest == "+2")  type = UltimaCardType.Summon;
        else if (rest.Length == 1 && char.IsDigit(rest[0])) type = (UltimaCardType)int.Parse(rest);
        else return null;
        return new UltimaCard { Color = color, Type = type };
    }

    /// <summary>Parse a color name ("WATER", "W", "BLUE", etc.). Returns null on failure.</summary>
    public static UltimaColor? ParseColor(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.Trim().ToUpperInvariant() switch
        {
            "WATER" or "W" or "BLUE"   => UltimaColor.Water,
            "FIRE"  or "F" or "RED"    => UltimaColor.Fire,
            "GRASS" or "G" or "GREEN"  => UltimaColor.Grass,
            "LOVE"  or "LIGHT" or "L" or "YELLOW" => UltimaColor.Love,
            _                          => null
        };
    }

    /// <summary>Check whether <paramref name="card"/> can legally be played on top of <paramref name="top"/>
    /// given the current active color.</summary>
    public static bool CanPlay(UltimaCard card, UltimaCard top, UltimaColor activeColor)
    {
        if (card.IsWild) return true;
        if (card.Color == activeColor) return true;
        if (card.Type  == top.Type)    return true;
        return false;
    }
}
