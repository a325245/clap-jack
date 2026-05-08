using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ChatCasino.Models;
using Dalamud.Bindings.ImGui;

namespace ChatCasino.UI;

public enum CardDisplaySize
{
    Big,
    Little,
    Mini
}

public enum CardDisplayStyle
{
    Graphic,
    Text
}

public enum CardDisplayTheme
{
    Light,
    Dark
}

public enum CardBracketStyle
{
    None,
    Square,
    Lenticular
}

public static class CasinoUI
{
    public static CardDisplaySize CardSize = CardDisplaySize.Little;
    public static CardDisplayStyle CardStyle = CardDisplayStyle.Graphic;
    public static CardDisplayTheme CardTheme = CardDisplayTheme.Light;
    public static bool ShowOtherPlayerHands = false;
    public static CardBracketStyle BracketStyle = CardBracketStyle.Square;
    public static bool DealerManualMode = false;

    public static float GlobalTurnTimeLimitSeconds = 60f;
    public static int GlobalMinBet = 10;
    public static int GlobalMaxBet = 10_000;
    public static float CrapsBettingDurationSeconds = 5f;
    public static float RouletteSpinSeconds = 4f;
    public static float CrapsRollDelaySeconds = 1.2f;
    public static bool BaccaratCommissionEnabled = false;
    public static bool PokerAutoPlayEnabled = true;
    public static bool PrivateBanks = false;
    public static int PokerSmallBlind = 50;
    public static int PokerBigBlind = 100;
    public static int BlackjackNaturalPayoutNumerator = 3;
    public static int BlackjackNaturalPayoutDenominator = 2;
    public static bool RandomizeDealerChatDelay = true;
    public static int DealerChatDelayMinMs = 500;
    public static int DealerChatDelayMaxMs = 1000;
    public static bool NaturalChatOutput = true;
    public static bool ErrorMessagesToEcho = true;
    public static int PlayerChatChannelIndex = 1; // 0 = /say, 1 = /party

    public static bool BlackjackDiceMode = false;
    public static bool BaccaratDiceMode = false;

    // Bingo settings
    public static int BingoCardPrice = 100;
    public static int BingoCatchupLimit = 1;
    public static BingoWinCondition BingoWinCondition = BingoWinCondition.OneLine;
    public static BingoGameMode BingoGameMode = BingoGameMode.Standard;
    // Progressive: list of rounds with win condition + payout %
    public static List<BingoProgressiveRound> BingoProgressiveRounds = new()
    {
        new() { WinCondition = BingoWinCondition.OneLine, PayoutPercent = 30 },
        new() { WinCondition = BingoWinCondition.TwoLine, PayoutPercent = 30 },
        new() { WinCondition = BingoWinCondition.Blackout, PayoutPercent = 40 }
    };

    public static void DrawCardStack(Vector2 pos, IEnumerable<Card> cards)
    {
        var idx = 0;
        foreach (var card in cards)
        {
            var p = new Vector2(pos.X + (idx * 16f), pos.Y + (idx * 4f));
            ImGui.SetCursorScreenPos(p);
            ImGui.Button($"{card.Value}{card.Suit}##card{idx}", new Vector2(48, 64));
            idx++;
        }
    }

    public static void DrawCardTokens(IEnumerable<string> tokens)
    {
        var idx = 0;
        foreach (var rawToken in tokens)
        {
            // Strip bracket characters — brackets are for chat output only, not drawn cards
            var token = rawToken.Trim('[', ']', '\u3010', '\u3011');
            bool isHidden = token.Contains("Hidden", StringComparison.OrdinalIgnoreCase);
            bool isRed = token.Contains('♥') || token.Contains('♦');
            var isUltima = token.Length >= 1 && !token.Contains('♥') && !token.Contains('♦') && !token.Contains('♣') && !token.Contains('♠');

            if (CardStyle == CardDisplayStyle.Text)
            {
                var textToken = isHidden ? "[]" : token;
                var txtColor = isHidden
                    ? new Vector4(0.7f, 0.7f, 0.8f, 1f)
                    : isUltima
                        ? new Vector4(0.85f, 0.8f, 1f, 1f)
                        : isRed
                            ? new Vector4(0.95f, 0.35f, 0.35f, 1f)
                            : new Vector4(0.9f, 0.9f, 0.9f, 1f);
                ImGui.TextColored(txtColor, textToken);
                ImGui.SameLine();
                idx++;
                continue;
            }

            var cardSize = CardSize switch
            {
                CardDisplaySize.Big => new Vector2(54, 76),
                CardDisplaySize.Mini => new Vector2(40, 38),
                _ => new Vector2(40, 56)
            };
            var cardLabel = isHidden ? string.Empty : token;

            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);

            if (isHidden)
            {
                if (CardTheme == CardDisplayTheme.Dark)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.12f, 0.15f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.18f, 0.22f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.82f, 0.82f, 0.9f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.32f, 0.32f, 0.42f, 1f));
                }
                else
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.15f, 0.22f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.2f, 0.2f, 0.3f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.86f, 0.86f, 0.95f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.3f, 0.3f, 0.45f, 1f));
                }
            }
            else if (isUltima)
            {
                var lead = char.ToUpperInvariant(token[0]);
                var (bg, hover, txt, border) = lead switch
                {
                    'W' => (new Vector4(0.20f, 0.36f, 0.82f, 1f), new Vector4(0.24f, 0.42f, 0.90f, 1f), new Vector4(0.95f, 0.97f, 1f, 1f), new Vector4(0.45f, 0.65f, 1f, 1f)),
                    'G' => (new Vector4(0.14f, 0.55f, 0.24f, 1f), new Vector4(0.18f, 0.62f, 0.30f, 1f), new Vector4(0.94f, 1f, 0.94f, 1f), new Vector4(0.45f, 0.9f, 0.55f, 1f)),
                    'L' => (new Vector4(0.78f, 0.66f, 0.10f, 1f), new Vector4(0.86f, 0.74f, 0.14f, 1f), new Vector4(0.13f, 0.12f, 0.08f, 1f), new Vector4(0.98f, 0.88f, 0.35f, 1f)),
                    'F' => (new Vector4(0.74f, 0.20f, 0.18f, 1f), new Vector4(0.82f, 0.24f, 0.22f, 1f), new Vector4(1f, 0.95f, 0.95f, 1f), new Vector4(0.98f, 0.45f, 0.4f, 1f)),
                    _ => (new Vector4(0.46f, 0.24f, 0.62f, 1f), new Vector4(0.52f, 0.28f, 0.70f, 1f), new Vector4(0.98f, 0.95f, 1f, 1f), new Vector4(0.74f, 0.58f, 0.92f, 1f))
                };

                ImGui.PushStyleColor(ImGuiCol.Button, bg);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hover);
                ImGui.PushStyleColor(ImGuiCol.Text, txt);
                ImGui.PushStyleColor(ImGuiCol.Border, border);
            }
            else
            {
                if (CardTheme == CardDisplayTheme.Dark)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.13f, 0.13f, 0.14f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.18f, 0.20f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.Text, isRed ? new Vector4(0.95f, 0.35f, 0.35f, 1f) : new Vector4(0.92f, 0.92f, 0.92f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.Border, isRed ? new Vector4(0.85f, 0.25f, 0.25f, 1f) : new Vector4(0.55f, 0.55f, 0.55f, 1f));
                }
                else
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.97f, 0.97f, 0.97f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.Text, isRed ? new Vector4(0.9f, 0.2f, 0.2f, 1f) : new Vector4(0.1f, 0.1f, 0.1f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.Border, isRed ? new Vector4(0.9f, 0.25f, 0.25f, 1f) : new Vector4(0.25f, 0.25f, 0.25f, 1f));
                }
            }

            ImGui.Button($"{cardLabel}##tok{idx}", cardSize);

            ImGui.PopStyleColor(4);
            ImGui.PopStyleVar(2);

            ImGui.SameLine();
            idx++;
        }

        if (idx > 0)
            ImGui.NewLine();
    }

    public static void DrawRouletteWheel(int? result)
    {
        var size = new Vector2(180, 180);
        var pos = ImGui.GetCursorScreenPos();
        var center = new Vector2(pos.X + size.X / 2, pos.Y + size.Y / 2);
        var draw = ImGui.GetWindowDrawList();

        var outer = ImGui.GetColorU32(new Vector4(0.18f, 0.18f, 0.22f, 1f));
        var inner = ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.09f, 1f));
        var accent = ImGui.GetColorU32(new Vector4(0.8f, 0.65f, 0.1f, 1f));

        draw.AddCircleFilled(center, 82, outer, 64);
        draw.AddCircleFilled(center, 58, inner, 64);
        draw.AddCircle(center, 82, accent, 64, 2f);

        var txt = result.HasValue ? result.Value.ToString() : "-";
        var txtSize = ImGui.CalcTextSize(txt);
        draw.AddText(new Vector2(center.X - txtSize.X / 2, center.Y - txtSize.Y / 2), accent, txt);

        ImGui.Dummy(size);
    }

    public static void DrawRouletteTablePreview()
    {
        ImGui.TextUnformatted("Roulette Table");
        for (var row = 0; row < 12; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                var n = row * 3 + (3 - col);
                bool red = RouletteRedNumbers.Contains(n);
                ImGui.PushStyleColor(ImGuiCol.Button, red ? new Vector4(0.45f, 0.12f, 0.12f, 1f) : new Vector4(0.12f, 0.12f, 0.12f, 1f));
                ImGui.Button($"{n}##rt{row}_{col}", new Vector2(34, 22));
                ImGui.PopStyleColor();
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.1f, 0.35f, 0.1f, 1f));
        ImGui.Button("0##rt0", new Vector2(108, 22));
        ImGui.PopStyleColor();
    }

    public static void DrawBetChip(Vector2 pos, int amount)
    {
        ImGui.SetCursorScreenPos(pos);
        ImGui.Button($"● {amount}\uE049", new Vector2(96, 24));
    }

    public static void DrawTimerBar(float percent, string label)
    {
        percent = Math.Clamp(percent, 0f, 1f);
        ImGui.ProgressBar(percent, new Vector2(-1, 16), label);
    }

    public static void DrawGameHeader(string title, string phase)
    {
        ImGui.TextUnformatted(title);
        ImGui.SameLine();
        ImGui.TextDisabled($"- {phase}");
        ImGui.Separator();
    }

    private static readonly HashSet<int> RouletteRedNumbers = [1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36];
}


