using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ChatCasino.Commands;
using ChatCasino.Engine;
using ChatCasino.Models;
using ChatCasino.Services;
using ChatCasino.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System.Numerics;

namespace ChatCasino.Windows;

public sealed class PlayerViewWindow : Window
{
    private readonly ITableService tableService;

    private readonly List<string> chatFeed = new();
    private readonly Dictionary<string, int> mirroredBanks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> mirroredBets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> mirroredKnownPlayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<List<string>>> mirroredBlackjackHands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> mirroredBlackjackHandResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> mirroredBlackjackActiveHands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> mirroredSeatStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> mirroredDealerCards = new();
    private readonly List<string> mirroredPlayerCards = new();
    private readonly List<string> mirroredPokerBoardCards = new();
    private readonly List<string> mirroredBaccaratPlayerCards = new();
    private readonly List<string> mirroredBaccaratBankerCards = new();
    private readonly List<string> mirroredActions = new();

    private readonly List<(string Name, string Odds)> mirroredChocoboRoster = new();
    private readonly Dictionary<string, float[]> mirroredChocoboTrackCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> mirroredChocoboBetRacers = new(StringComparer.OrdinalIgnoreCase);

    private string mirroredGame = "Casino";
    private string mirroredStatus = "No active table";
    private string mirroredPlayerResult = string.Empty;
    private string mirroredBaccaratResult = string.Empty;
    private string mirroredChocoboHash = string.Empty;
    private DateTime mirroredChocoboRaceStartUtc = DateTime.MinValue;
    private bool mirroredChocoboRacing;

    private int? mirroredRouletteLast;
    private bool mirroredRouletteSpinning;
    private DateTime mirroredRouletteSpinStartUtc = DateTime.MinValue;

    private int mirroredCrapsD1 = 1;
    private int mirroredCrapsD2 = 1;
    private int mirroredCrapsPoint;
    private DateTime mirroredCrapsRollUtc = DateTime.MinValue;
    private string mirroredCrapsShooter = string.Empty;
    private DateTime mirroredCrapsBetsOpenUntilUtc = DateTime.MinValue;
    private int mirroredCrapsBetWindowSeconds;

    private int wagerInput = 100;
    private string targetInput = "RED";
    private int targetPresetIndex;
    private string ultimaPendingWildCode = string.Empty;

    private readonly Dictionary<string, List<string>> mirroredRouletteBetMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> mirroredCrapsBetMap = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> mirroredPokerRoles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> mirroredPokerSeatOrder = new();
    private int mirroredPokerPot;
    private string mirroredPokerCurrentTurn = string.Empty;

    // Ultima mirrored state
    private string mirroredUltimaTopCard = string.Empty;
    private string mirroredUltimaColor = string.Empty;
    private string mirroredUltimaDirection = "CW";
    private string mirroredUltimaTurn = string.Empty;
    private readonly List<string> mirroredUltimaHand = new();
    private readonly Dictionary<string, int> mirroredUltimaCardCounts = new(StringComparer.OrdinalIgnoreCase);

    private bool autoPopOnCasinoChat = true;
    private bool hasReceivedCasinoChat;

    public string? LocalPlayerName { get; set; }
    public Action<string, bool>? SendChatCommand { get; set; }
    public Action? OpenDealerView { get; set; }

    public PlayerViewWindow(GameManager gameManager, ITableService tableService)
        : base("Chat Casino - Player View###ChatCasinoPlayer")
    {
        this.tableService = tableService;
        Size = new Vector2(520, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void RecordChat(string sender, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        chatFeed.Add($"{sender}: {text}");
        if (chatFeed.Count > 200)
            chatFeed.RemoveAt(0);

        ParseChatForMirror(text);

        // Auto-pop player view when casino chat is detected
        if (!hasReceivedCasinoChat && IsCasinoChatString(text))
        {
            hasReceivedCasinoChat = true;
            if (autoPopOnCasinoChat)
                IsOpen = true;
        }
    }

    private static bool IsCasinoChatString(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.Contains("[BLACKJACK]", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[ROULETTE]", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[CRAPS]", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[BACCARAT]", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[CHOCOBO]", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[POKER", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[ULTIMA", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[CASINO]", StringComparison.OrdinalIgnoreCase))
            return true;

        var plain = StripLeadingTag(text);
        return plain.Contains("Dealer shows", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("No more bets", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("Bets are open", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("Race started", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("Seat order:", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("Turn:", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("calls ULTIMA", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("(Bank", StringComparison.OrdinalIgnoreCase);
    }

    private GameType GetMirroredGameType()
    {
        var key = NormalizeGameKey(mirroredGame);
        return key switch
        {
            "BLACKJACK" => GameType.Blackjack,
            "ROULETTE" => GameType.Roulette,
            "CRAPS" => GameType.Craps,
            "BACCARAT" => GameType.Baccarat,
            "CHOCOBORACING" => GameType.ChocoboRacing,
            "TEXASHOLDEM" => GameType.TexasHoldEm,
            "ULTIMA" => GameType.Ultima,
            _ => GameType.None
        };
    }

    private static string NormalizeGameKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToUpperInvariant();
    }

    private void DrawUltimaTableVisual()
    {
        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.2f, 1f), "Ultima Table");
        var draw = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        float w = 480f, h = 190f;
        var center = new Vector2(pos.X + w / 2f, pos.Y + h / 2f);
        var tableMin = new Vector2(center.X - 180f, center.Y - 65f);
        var tableMax = new Vector2(center.X + 180f, center.Y + 65f);
        draw.AddRectFilled(tableMin, tableMax, ImGui.GetColorU32(new Vector4(0.1f, 0.35f, 0.2f, 1f)), 60f);
        draw.AddRect(tableMin, tableMax, ImGui.GetColorU32(new Vector4(0.75f, 0.65f, 0.3f, 1f)), 60f, ImDrawFlags.None, 2f);

        if (!string.IsNullOrWhiteSpace(mirroredUltimaColor))
            draw.AddText(new Vector2(center.X - 50f, center.Y - 38f), ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.8f, 1f)), $"Color: {mirroredUltimaColor}");

        // Direction indicator - positioned above the top card
        var isFwd = !mirroredUltimaDirection.Equals("CCW", StringComparison.OrdinalIgnoreCase);
        var dirText = isFwd ? "Direction: CW >>" : "<< CCW :Direction";
        var dirColor = isFwd ? new Vector4(0.5f, 0.9f, 0.5f, 1f) : new Vector4(1f, 0.5f, 0.3f, 1f);
        draw.AddText(new Vector2(center.X - 55f, center.Y - 55f), ImGui.GetColorU32(dirColor), dirText);
        if (!string.IsNullOrWhiteSpace(mirroredUltimaTopCard))
        {
            var cursor = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(new Vector2(center.X - 20f, center.Y - 14f));
            CasinoUI.DrawCardTokens([mirroredUltimaTopCard]);
            ImGui.SetCursorScreenPos(cursor);
        }

        var seatNames = GetMirroredPlayerNames().ToList();
        for (var i = 0; i < seatNames.Count; i++)
        {
            var a = (float)(Math.PI * 2 * i / seatNames.Count) - (float)Math.PI / 2;
            var p = new Vector2(center.X + MathF.Cos(a) * 210f, center.Y + MathF.Sin(a) * 85f);
            var isTurn = mirroredUltimaTurn.Equals(seatNames[i], StringComparison.OrdinalIgnoreCase);
            var color = isTurn ? new Vector4(1f, 0.88f, 0.25f, 1f) : new Vector4(0.82f, 0.82f, 0.82f, 1f);
            draw.AddCircleFilled(p, 10f, ImGui.GetColorU32(color), 16);
            var cardCount = mirroredUltimaCardCounts.GetValueOrDefault(seatNames[i], 7);
            draw.AddText(new Vector2(p.X + 14f, p.Y - 9f), ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.95f, 1f)), $"{seatNames[i]} ({cardCount})");
        }

        ImGui.Dummy(new Vector2(w, h));
        ImGui.Separator();
    }

    private void DrawUltimaHandControls()
    {
        var isMyTurn = !string.IsNullOrWhiteSpace(LocalPlayerName)
            && mirroredUltimaTurn.Equals(LocalPlayerName, StringComparison.OrdinalIgnoreCase);

        if (mirroredUltimaHand.Count > 0)
        {
            ImGui.TextColored(new Vector4(0.85f, 0.8f, 1f, 1f), "YOUR HAND (click to play)");

            var openColorPicker = false;

            for (var i = 0; i < mirroredUltimaHand.Count; i++)
            {
                var code = mirroredUltimaHand[i];
                ImGui.PushID($"ultima-card-{i}");

                var isWild = code.StartsWith("PL", StringComparison.OrdinalIgnoreCase);
                var lead = isWild ? 'P' : (code.Length > 0 ? char.ToUpperInvariant(code[0]) : ' ');
                var cardColor = lead switch
                {
                    'W' => new Vector4(0.20f, 0.36f, 0.82f, 1f),
                    'F' => new Vector4(0.74f, 0.20f, 0.18f, 1f),
                    'G' => new Vector4(0.14f, 0.55f, 0.24f, 1f),
                    'L' => new Vector4(0.78f, 0.66f, 0.10f, 1f),
                    _ => new Vector4(0.46f, 0.24f, 0.62f, 1f)
                };

                if (!isMyTurn) ImGui.BeginDisabled();
                ImGui.PushStyleColor(ImGuiCol.Button, cardColor);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, cardColor + new Vector4(0.1f, 0.1f, 0.1f, 0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, cardColor - new Vector4(0.1f, 0.1f, 0.1f, 0f));
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));

                if (ImGui.Button(code, new Vector2(50, 36)))
                {
                    if (isWild)
                    {
                        ultimaPendingWildCode = code;
                        openColorPicker = true;
                    }
                    else
                    {
                        SendChatCommand?.Invoke($">PLAY {code}", CasinoUI.PlayerChatChannelIndex == 1);
                    }
                }

                ImGui.PopStyleColor(4);
                if (!isMyTurn) ImGui.EndDisabled();

                ImGui.SameLine();
                ImGui.PopID();
            }

            ImGui.NewLine();

            // Open the popup outside the PushID loop so the ID scope matches
            if (openColorPicker)
                ImGui.OpenPopup("UltimaColorPicker");
        }

        // Wild color picker popup — must be at the same ID level as OpenPopup
        if (ImGui.BeginPopup("UltimaColorPicker"))
        {
            ImGui.TextUnformatted("Choose a color:");
            ImGui.Separator();

            (string Name, Vector4 Color)[] colors =
            [
                ("WATER", new Vector4(0.20f, 0.36f, 0.82f, 1f)),
                ("FIRE", new Vector4(0.74f, 0.20f, 0.18f, 1f)),
                ("GRASS", new Vector4(0.14f, 0.55f, 0.24f, 1f)),
                ("LIGHT", new Vector4(0.78f, 0.66f, 0.10f, 1f))
            ];

            foreach (var (name, color) in colors)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, color);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color + new Vector4(0.12f, 0.12f, 0.12f, 0f));
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));

                if (ImGui.Button(name, new Vector2(80, 30)))
                {
                    SendChatCommand?.Invoke($">PLAY {ultimaPendingWildCode} {name}", CasinoUI.PlayerChatChannelIndex == 1);
                    ultimaPendingWildCode = string.Empty;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.PopStyleColor(3);
            }

            ImGui.EndPopup();
        }

        // DRAW is automatic in Ultima - only show DEAL and HAND buttons
        if (ImGui.Button("DEAL"))
            SendChatCommand?.Invoke(">DEAL", CasinoUI.PlayerChatChannelIndex == 1);

        ImGui.SameLine();
        if (ImGui.Button("HAND"))
            SendChatCommand?.Invoke(">HAND", CasinoUI.PlayerChatChannelIndex == 1);
    }

    private string[] GetTargetPresets(GameType game)
        => game switch
        {
            GameType.Roulette => ["RED", "BLACK", "EVEN", "ODD", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20", "21", "22", "23", "24", "25", "26", "27", "28", "29", "30", "31", "32", "33", "34", "35", "36"],
            GameType.Craps => ["PASS", "DONTPASS", "FIELD", "SEVEN", "ANYCRAPS", "PLACE4", "PLACE5", "PLACE6", "PLACE8", "PLACE9", "PLACE10"],
            GameType.Baccarat => ["PLAYER", "BANKER", "TIE"],
            GameType.ChocoboRacing => mirroredChocoboRoster.Select(x => x.Name).ToArray(),
            GameType.Ultima => ["WATER", "FIRE", "GRASS", "LIGHT"],
            _ => Array.Empty<string>()
        };

    private static List<string> FilterPlayerCommands(IEnumerable<string> commands)
    {
        var dealerOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DEAL", "SPIN", "START", "OPENBETS", "RULETOGGLE", "RULE" };
        return commands.Select(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant()).Where(c => !dealerOnly.Contains(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string StripMirrorPrefix(string text)
    {
        var prefixMatch = Regex.Match(text, @"^\[(?:SELF|BOT)-TELL:[^\]]+\]\s*(.+)$", RegexOptions.IgnoreCase);
        return prefixMatch.Success ? prefixMatch.Groups[1].Value : text;
    }

    private static string StripLeadingTag(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return Regex.Replace(text, @"^\[[^\]]+\]\s*", string.Empty).TrimStart();
    }

    private static bool StartsWithTaggedOrPlain(string text, string taggedPrefix)
    {
        if (text.StartsWith(taggedPrefix, StringComparison.OrdinalIgnoreCase))
            return true;

        var plainPrefix = StripLeadingTag(taggedPrefix);
        var plainText = StripLeadingTag(text);
        return !string.IsNullOrWhiteSpace(plainPrefix)
            && plainText.StartsWith(plainPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static Match MatchTaggedOrPlain(string text, string taggedPattern, RegexOptions options = RegexOptions.IgnoreCase)
    {
        var taggedMatch = Regex.Match(text, taggedPattern, options);
        if (taggedMatch.Success)
            return taggedMatch;

        var plainPattern = Regex.Replace(taggedPattern, @"^\^\\\[[^\]]+\\\]\\s\+", "^");
        plainPattern = Regex.Replace(plainPattern, @"^\^\\\[[^\]]+\\\]\\s\*", "^");
        plainPattern = Regex.Replace(plainPattern, @"^\^\\\[[^\]]+\\\]", "^");

        return Regex.Match(StripLeadingTag(text), plainPattern, options);
    }

    private void ResetMirroredRoundStateForGameSwitch()
    {
        mirroredDealerCards.Clear(); mirroredPlayerCards.Clear(); mirroredPokerBoardCards.Clear();
        mirroredBaccaratPlayerCards.Clear(); mirroredBaccaratBankerCards.Clear();
        mirroredBlackjackHands.Clear(); mirroredBlackjackHandResults.Clear(); mirroredBlackjackActiveHands.Clear();
        mirroredSeatStates.Clear(); mirroredBets.Clear(); mirroredChocoboRoster.Clear(); mirroredChocoboTrackCache.Clear();
        mirroredPlayerResult = string.Empty; mirroredBaccaratResult = string.Empty; mirroredChocoboHash = string.Empty;
        mirroredChocoboRacing = false; mirroredChocoboBetRacers.Clear(); mirroredActions.Clear(); mirroredRouletteBetMap.Clear(); mirroredCrapsBetMap.Clear();
        mirroredPokerRoles.Clear(); mirroredPokerSeatOrder.Clear(); mirroredPokerPot = 0;
        mirroredPokerCurrentTurn = string.Empty;
        mirroredUltimaTopCard = string.Empty; mirroredUltimaColor = string.Empty; mirroredUltimaDirection = "CW";
        mirroredUltimaTurn = string.Empty; mirroredUltimaHand.Clear(); mirroredUltimaCardCounts.Clear();
        mirroredCrapsBetsOpenUntilUtc = DateTime.MinValue;
    }

    private void SetDefaultActionsForCurrentGame()
    {
        mirroredActions.Clear();
        switch (GetMirroredGameType())
        {
            case GameType.Blackjack: mirroredActions.Add("BET"); break;
            case GameType.Roulette: mirroredActions.AddRange(["BET", "SPIN"]); break;
            case GameType.Craps: mirroredActions.AddRange(["BET", "ROLL"]); break;
            case GameType.Baccarat: mirroredActions.Add("BET"); break;
            case GameType.ChocoboRacing: mirroredActions.Add("BET"); break;
            case GameType.TexasHoldEm: break;
        }
    }

    private IEnumerable<string> GetMirroredPlayerNames()
    {
        var names = mirroredKnownPlayers
            .Concat(mirroredBanks.Keys)
            .Concat(mirroredBets.Keys)
            .Concat(mirroredBlackjackHands.Keys)
            .Concat(mirroredSeatStates.Keys);

        if (!string.IsNullOrWhiteSpace(LocalPlayerName))
            names = names.Concat([LocalPlayerName]);

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
    }

    public override void Draw()
    {
        var game = GetMirroredGameType();

        var showTableTab = true;
        if (ImGui.BeginTabBar("##playerTabsTop"))
        {
            if (ImGui.BeginTabItem("Table"))
            {
                showTableTab = true;
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Rules"))
            {
                showTableTab = false;
                DrawRulesTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Config"))
            {
                showTableTab = false;
                DrawPlayerConfigTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        if (!showTableTab)
            return;

        ImGui.TextUnformatted($"{mirroredGame} - {mirroredStatus}");
        ImGui.Separator();

        // For poker and ultima, draw table visual above the players section
        if (game == GameType.TexasHoldEm)
            DrawPokerTableVisual();
        else if (game == GameType.Ultima)
            DrawUltimaTableVisual();

        DrawMirroredSeats();

        if (game == GameType.Roulette)
            DrawRouletteBoard();
        else if (game == GameType.Craps)
            DrawCrapsBoard();
        else if (game == GameType.ChocoboRacing)
            DrawChocoboBoard();

        ImGui.Separator();

        if (game == GameType.Ultima)
        {
            DrawUltimaHandControls();
            DrawCommandButtons(game);
            return;
        }

        if (game == GameType.None)
        {
            if (!hasReceivedCasinoChat)
            {
                ImGui.TextWrapped("Waiting for casino activity in party chat. This view will populate automatically when a game starts.");
                ImGui.Spacing();
                if (ImGui.Button("Open Dealer View"))
                    OpenDealerView?.Invoke();
            }
            DrawCommandButtons(game);
            return;
        }

        ImGui.SetNextItemWidth(100);
        ImGui.InputInt("Amt", ref wagerInput);
        if (wagerInput < 1) wagerInput = 1;

        var targetPresets = GetTargetPresets(game);
        if (targetPresets.Length > 0)
        {
            if (game == GameType.ChocoboRacing)
            {
                if (!string.IsNullOrWhiteSpace(targetInput) && !targetPresets.Contains(targetInput, StringComparer.OrdinalIgnoreCase))
                    targetInput = string.Empty;

                ImGui.SameLine();
                ImGui.SetNextItemWidth(150);
                var preview = string.IsNullOrWhiteSpace(targetInput) ? "(Select Racer)" : targetInput;
                if (ImGui.BeginCombo("Target", preview))
                {
                    for (var i = 0; i < targetPresets.Length; i++)
                    {
                        var selected = string.Equals(targetPresets[i], targetInput, StringComparison.OrdinalIgnoreCase);
                        if (ImGui.Selectable(targetPresets[i], selected))
                        {
                            targetPresetIndex = i;
                            targetInput = targetPresets[i];
                        }
                        if (selected) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
            }
            else
            {
                if (targetPresetIndex < 0 || targetPresetIndex >= targetPresets.Length)
                    targetPresetIndex = 0;
                if (string.IsNullOrWhiteSpace(targetInput) || !targetPresets.Contains(targetInput, StringComparer.OrdinalIgnoreCase))
                    targetInput = targetPresets[targetPresetIndex];

                ImGui.SameLine();
                ImGui.SetNextItemWidth(150);
                if (ImGui.BeginCombo("Target", targetInput))
                {
                    for (var i = 0; i < targetPresets.Length; i++)
                    {
                        var selected = targetPresetIndex == i;
                        if (ImGui.Selectable(targetPresets[i], selected))
                        {
                            targetPresetIndex = i;
                            targetInput = targetPresets[i];
                        }
                        if (selected) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
            }
        }

        DrawCommandButtons(game);
    }

    private static void DrawRulesTab()
    {
        ImGui.TextColored(new Vector4(0.8f, 0.95f, 1f, 1f), "RULES & COMMANDS");
        ImGui.Separator();

        foreach (var game in GameRulebook.OrderedGames)
        {
            if (!ImGui.CollapsingHeader(game.ToString(), ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            foreach (var line in GameRulebook.GetRules(game))
            {
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextWrapped(line);
            }

            ImGui.Spacing();
            ImGui.TextDisabled($"Commands: {CommandRegistry.BuildHelp(game)}");
            ImGui.Separator();
        }
    }

    private void DrawPlayerConfigTab()
    {
        ImGui.TextColored(new Vector4(0.8f, 0.95f, 1f, 1f), "CONFIG");
        ImGui.Separator();

        if (ImGui.Button("Open Dealer View"))
            OpenDealerView?.Invoke();
        ImGui.Separator();

        ImGui.TextUnformatted("Chat");
        ImGui.SetNextItemWidth(260f);
        ImGui.Combo("Chat Channel", ref CasinoUI.PlayerChatChannelIndex, ["/say", "/party"], 2);
        ImGui.Separator();

        ImGui.TextUnformatted("Cards");

        ImGui.Checkbox("Auto-open on casino chat", ref autoPopOnCasinoChat);
        ImGui.TextDisabled("Automatically show player view when casino activity is detected in party chat.");

        var sizeIndex = (int)CasinoUI.CardSize;
        if (ImGui.Combo("Card Size", ref sizeIndex, ["Big Cards", "Little Cards", "Mini Cards"], 3))
            CasinoUI.CardSize = (CardDisplaySize)Math.Clamp(sizeIndex, 0, 2);

        var styleIndex = (int)CasinoUI.CardStyle;
        if (ImGui.Combo("Card Style", ref styleIndex, ["Graphic Cards", "Text Cards"], 2))
            CasinoUI.CardStyle = (CardDisplayStyle)Math.Clamp(styleIndex, 0, 1);

        var themeIndex = (int)CasinoUI.CardTheme;
        if (ImGui.Combo("Card Theme", ref themeIndex, ["Light Mode Cards", "Dark Mode Cards"], 2))
            CasinoUI.CardTheme = (CardDisplayTheme)Math.Clamp(themeIndex, 0, 1);

        ImGui.Separator();
        if (ImGui.Button("Reset Player View"))
        {
            CasinoUI.PlayerChatChannelIndex = 1;
            CasinoUI.CardSize = CardDisplaySize.Little;
            CasinoUI.CardStyle = CardDisplayStyle.Graphic;
            CasinoUI.CardTheme = CardDisplayTheme.Light;
            autoPopOnCasinoChat = true;
            hasReceivedCasinoChat = false;
            mirroredGame = "Casino";
            mirroredStatus = "No active table";
            ResetMirroredRoundStateForGameSwitch();
        }
    }

    private void DrawMirroredSeats()
    {
        var game = GetMirroredGameType();

        if (game == GameType.Baccarat)
        {
            if (mirroredBaccaratPlayerCards.Count > 0)
            {
                ImGui.TextUnformatted("Player Hand");
                CasinoUI.DrawCardTokens(mirroredBaccaratPlayerCards);
            }

            if (mirroredBaccaratBankerCards.Count > 0)
            {
                ImGui.TextUnformatted("Banker Hand");
                CasinoUI.DrawCardTokens(mirroredBaccaratBankerCards);
            }

            if (!string.IsNullOrWhiteSpace(mirroredBaccaratResult))
                ImGui.TextDisabled(mirroredBaccaratResult);
        }
        else if (game == GameType.Ultima)
        {
            // Ultima dealer/table cards shown on the table visual, not here
        }
        else if (game == GameType.TexasHoldEm)
        {
            // Board cards shown on the table visual, not here
        }
        else
        {
            if (mirroredDealerCards.Count > 0)
            {
                ImGui.TextUnformatted("Dealer");
                var cardsToDraw = mirroredDealerCards.ToList();
                if (game == GameType.Blackjack && cardsToDraw.Count == 1)
                    cardsToDraw.Add("[Hidden]");
                CasinoUI.DrawCardTokens(cardsToDraw);
            }
        }

        ImGui.TextColored(new Vector4(0.6f, 0.95f, 1f, 1f), "Players");
        foreach (var name in GetMirroredPlayerNames())
        {
            ImGui.PushID($"pv-seat-{name}");
            var me = !string.IsNullOrWhiteSpace(LocalPlayerName) && name.Equals(LocalPlayerName, StringComparison.OrdinalIgnoreCase);
            var hasBank = mirroredBanks.TryGetValue(name, out var bank);
            var bet = mirroredBets.TryGetValue(name, out var b) ? b : 0;
            mirroredBlackjackHands.TryGetValue(name, out var handGroups);
            handGroups ??= [];
            var hasBlackjackHands = game == GameType.Blackjack && handGroups.Count > 0;

            if (me) ImGui.TextColored(new Vector4(0.6f, 1f, 0.6f, 1f), $"{name} (You)");
            else if (game == GameType.TexasHoldEm && mirroredPokerRoles.TryGetValue(name, out var playerRole))
            {
                var roleColor = playerRole switch
                {
                    "DEALER" => new Vector4(1f, 0.88f, 0.25f, 1f),
                    "SB" => new Vector4(0.35f, 0.85f, 1f, 1f),
                    "BB" => new Vector4(0.95f, 0.45f, 0.45f, 1f),
                    _ => new Vector4(0.82f, 0.82f, 0.82f, 1f)
                };
                ImGui.TextColored(roleColor, $"{name} ({playerRole})");
            }
            else ImGui.TextUnformatted(name);

            if (game == GameType.Ultima)
            {
                var cardCount = mirroredUltimaCardCounts.GetValueOrDefault(name, 7);
                ImGui.SameLine();
                ImGui.TextDisabled($"Cards: {cardCount}");
                if (mirroredUltimaTurn.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(1f, 0.88f, 0.25f, 1f), "<< TURN");
                }
            }
            else if (hasBank)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"Bank {bank}\uE049");
            }

            if (hasBlackjackHands)
            {
                var results = mirroredBlackjackHandResults.TryGetValue(name, out var handResultTexts)
                    ? handResultTexts
                    : [];

                for (var i = 0; i < handGroups.Count; i++)
                {
                    ImGui.PushID($"hand-{i}");
                    if (handGroups.Count > 1)
                    {
                        var activeHandIndex = GetMirroredBlackjackActiveHandIndex(name);
                        var handLabel = activeHandIndex == i ? $"Hand {i + 1}*" : $"Hand {i + 1}";
                        ImGui.TextUnformatted(handLabel);
                    }
                    CasinoUI.DrawCardTokens(handGroups[i]);
                    if (i < results.Count && !string.IsNullOrWhiteSpace(results[i]))
                        ImGui.TextDisabled(results[i]);
                    ImGui.PopID();
                }
            }
            else if (game == GameType.TexasHoldEm && me && mirroredPlayerCards.Count > 0)
            {
                CasinoUI.DrawCardTokens(mirroredPlayerCards);
            }
            else if (game == GameType.Ultima && me && mirroredUltimaHand.Count > 0)
            {
                // Hand cards drawn below the table in DrawUltimaHandControls
            }

            if (mirroredSeatStates.TryGetValue(name, out var state) && !string.IsNullOrWhiteSpace(state))
                ImGui.TextDisabled(state);
            else if ((!hasBlackjackHands || game != GameType.Blackjack) && me && !string.IsNullOrWhiteSpace(mirroredPlayerResult))
                ImGui.TextDisabled(mirroredPlayerResult);

            ImGui.Separator();
            ImGui.PopID();
        }
    }

    private void DrawCommandButtons(GameType game)
    {
        var commands = FilterPlayerCommands(mirroredActions);

        if (game == GameType.Blackjack)
        {
            string[] preferred = ["BET", "HIT", "STAND", "DOUBLE", "SPLIT", "INSURANCE"];
            foreach (var cmd in preferred)
            {
                var enabled = commands.Contains(cmd, StringComparer.OrdinalIgnoreCase);
                if (!enabled) ImGui.BeginDisabled();
                if (enabled)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.50f, 0.26f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.62f, 0.32f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.14f, 0.42f, 0.22f, 1f));
                }
                if (ImGui.Button(cmd))
                {
                    var text = BuildCommandText(cmd);
                    if (!string.IsNullOrWhiteSpace(text))
                        SendChatCommand?.Invoke(text, CasinoUI.PlayerChatChannelIndex == 1);
                }
                if (enabled) ImGui.PopStyleColor(3);
                if (!enabled) ImGui.EndDisabled();
                ImGui.SameLine();
            }
            ImGui.NewLine();
            return;
        }

        if (game == GameType.TexasHoldEm)
        {
            var isMyTurn = !string.IsNullOrWhiteSpace(LocalPlayerName)
                && !string.IsNullOrWhiteSpace(mirroredPokerCurrentTurn)
                && mirroredPokerCurrentTurn.Equals(LocalPlayerName, StringComparison.OrdinalIgnoreCase);

            string[] preferred = ["CHECK", "CALL", "RAISE", "FOLD", "ALL"];
            foreach (var cmd in preferred)
            {
                var available = commands.Contains(cmd, StringComparer.OrdinalIgnoreCase);
                var enabled = available && isMyTurn;
                if (!enabled) ImGui.BeginDisabled();
                if (enabled)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.50f, 0.26f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.62f, 0.32f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.14f, 0.42f, 0.22f, 1f));
                }
                if (ImGui.Button(cmd))
                {
                    var text = BuildCommandText(cmd);
                    if (!string.IsNullOrWhiteSpace(text))
                        SendChatCommand?.Invoke(text, CasinoUI.PlayerChatChannelIndex == 1);
                }
                if (enabled) ImGui.PopStyleColor(3);
                if (!enabled) ImGui.EndDisabled();
                ImGui.SameLine();
            }
            ImGui.NewLine();
            return;
        }

        if (game == GameType.Craps)
        {
            var canBet = commands.Contains("BET", StringComparer.OrdinalIgnoreCase) || commands.Count == 0;
            var canRollCommand = commands.Contains("ROLL", StringComparer.OrdinalIgnoreCase) || commands.Count == 0;
            var canRollShooter = !string.IsNullOrWhiteSpace(LocalPlayerName) && mirroredCrapsShooter.Equals(LocalPlayerName, StringComparison.OrdinalIgnoreCase);
            // Apply persistent bet window countdown on every turn
            var rollLocked = DateTime.UtcNow < mirroredCrapsBetsOpenUntilUtc;
            var left = Math.Max(0, (int)Math.Ceiling((mirroredCrapsBetsOpenUntilUtc - DateTime.UtcNow).TotalSeconds));

            if (!canBet) ImGui.BeginDisabled();
            if (ImGui.Button("BET"))
            {
                var text = BuildCommandText("BET");
                if (!string.IsNullOrWhiteSpace(text))
                    SendChatCommand?.Invoke(text, CasinoUI.PlayerChatChannelIndex == 1);
            }
            if (!canBet) ImGui.EndDisabled();
            ImGui.SameLine();

            var rollLabel = rollLocked ? $"ROLL ({left}s)" : "ROLL";
            if (!canRollCommand || !canRollShooter || rollLocked) ImGui.BeginDisabled();
            if (ImGui.Button(rollLabel))
            {
                var text = BuildCommandText("ROLL");
                SendChatCommand?.Invoke(text, CasinoUI.PlayerChatChannelIndex == 1);
            }
            if (!canRollCommand || !canRollShooter || rollLocked) ImGui.EndDisabled();
            ImGui.NewLine();
            return;
        }

        foreach (var action in commands)
        {
            var cmd = action.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
            if (ImGui.Button(cmd))
            {
                var text = BuildCommandText(cmd);
                if (!string.IsNullOrWhiteSpace(text))
                    SendChatCommand?.Invoke(text, CasinoUI.PlayerChatChannelIndex == 1);
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();
    }

    private void DrawChatFeed()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Chat Feed");
        if (ImGui.BeginChild("chatfeed", new Vector2(0, 200), true))
        {
            var stickToBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 8f;
            foreach (var line in chatFeed)
            {
                if (line.Contains(": >", StringComparison.OrdinalIgnoreCase))
                    ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f), line);
                else if (line.Contains("[", StringComparison.OrdinalIgnoreCase))
                    ImGui.TextColored(new Vector4(1f, 0.88f, 0.3f, 1f), line);
                else
                    ImGui.TextUnformatted(line);
            }

            if (stickToBottom)
                ImGui.SetScrollHereY(1f);
        }
        ImGui.EndChild();
    }

    private void DrawRouletteBoard()
    {
        var grid = new[,] { { 3, 6, 9, 12, 15, 18, 21, 24, 27, 30, 33, 36 }, { 2, 5, 8, 11, 14, 17, 20, 23, 26, 29, 32, 35 }, { 1, 4, 7, 10, 13, 16, 19, 22, 25, 28, 31, 34 } };

        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.2f, 1f), "Roulette Table");
        if (mirroredRouletteLast.HasValue)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"Last: {mirroredRouletteLast.Value}");
        }
        ImGui.Columns(2, "pvrouletteColumns", false);
        ImGui.SetColumnWidth(0, 210f);
        DrawRouletteWheelAnimated();
        ImGui.NextColumn();

        float cellW = 34f;
        float cellH = 22f;
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 12; c++)
            {
                var n = grid[r, c];
                var key = n.ToString();
                bool red = Array.IndexOf(new[] { 1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36 }, n) >= 0;
                var p0 = new Vector2(start.X + c * (cellW + 2), start.Y + r * (cellH + 2));
                var p1 = new Vector2(p0.X + cellW, p0.Y + cellH);
                uint bg = red ? ImGui.GetColorU32(new Vector4(0.55f, 0.16f, 0.16f, 1f)) : ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.12f, 1f));
                draw.AddRectFilled(p0, p1, bg, 3f);
                draw.AddRect(p0, p1, ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)), 3f, ImDrawFlags.None, 1.5f);

                var txt = key;
                var ts = ImGui.CalcTextSize(txt);
                draw.AddText(new Vector2(p0.X + cellW / 2 - ts.X / 2, p0.Y + cellH / 2 - ts.Y / 2), 0xFFFFFFFFu, txt);

                if (mirroredRouletteBetMap.TryGetValue(key, out var lines) && lines.Count > 0)
                    draw.AddCircleFilled(new Vector2(p1.X - 6, p0.Y + 6), 4f, GetBetMarkerColor(lines), 10);

                if (ImGui.IsMouseHoveringRect(p0, p1))
                {
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        targetInput = txt;
                        TryPlaceDirectBet(txt);
                    }

                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted($"{key} ({GetRouletteOddsText(key)})");
                    if (lines != null && lines.Count > 0)
                    {
                        ImGui.Separator();
                        foreach (var line in lines)
                            ImGui.TextUnformatted(line);
                    }
                    ImGui.EndTooltip();
                }
            }
        }

        var rowY = start.Y + 3 * (cellH + 2) + 2;
        string[] row = ["0", "RED", "BLACK", "EVEN", "ODD"];
        for (var i = 0; i < row.Length; i++)
        {
            var key = row[i];
            var p0 = new Vector2(start.X + i * (72f + 6f), rowY);
            var p1 = new Vector2(p0.X + 72f, p0.Y + 22f);
            uint bg = key switch
            {
                "0" => ImGui.GetColorU32(new Vector4(0.1f, 0.45f, 0.16f, 1f)),
                "RED" => ImGui.GetColorU32(new Vector4(0.55f, 0.16f, 0.16f, 1f)),
                "BLACK" => ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.12f, 1f)),
                _ => ImGui.GetColorU32(new Vector4(0.2f, 0.2f, 0.2f, 1f))
            };
            draw.AddRectFilled(p0, p1, bg, 3f);
            draw.AddRect(p0, p1, ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)), 3f);
            var ts = ImGui.CalcTextSize(key);
            draw.AddText(new Vector2(p0.X + 36 - ts.X / 2, p0.Y + 11 - ts.Y / 2), 0xFFFFFFFFu, key);

            mirroredRouletteBetMap.TryGetValue(key, out var lines);
            if (lines != null && lines.Count > 0)
                draw.AddCircleFilled(new Vector2(p1.X - 6, p0.Y + 6), 4f, GetBetMarkerColor(lines), 10);

            if (ImGui.IsMouseHoveringRect(p0, p1))
            {
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    targetInput = key;
                    TryPlaceDirectBet(key);
                }

                ImGui.BeginTooltip();
                ImGui.TextUnformatted($"{key} ({GetRouletteOddsText(key)})");
                if (lines != null && lines.Count > 0)
                {
                    ImGui.Separator();
                    foreach (var line in lines)
                        ImGui.TextUnformatted(line);
                }
                ImGui.EndTooltip();
            }
        }

        ImGui.Dummy(new Vector2(430, 132));
        ImGui.Columns(1);
        ImGui.Separator();
    }

    private static string GetRouletteOddsText(string key)
    {
        if (int.TryParse(key, out _))
            return "35:1";

        return key.ToUpperInvariant() switch
        {
            "RED" or "BLACK" or "EVEN" or "ODD" => "1:1",
            _ => "-"
        };
    }

    private void DrawCrapsBoard()
    {
        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.2f, 1f), "Craps Table");
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        float w = 540f;
        float h = 165f;
        var p1 = new Vector2(pos.X + w, pos.Y + h);
        dl.AddRectFilled(pos, p1, 0xFF114422u, 8f);
        dl.AddRect(pos, p1, 0xFF88AA88u, 8f, ImDrawFlags.None, 2f);

        DrawCrapsZone(dl, pos, new Vector2(12, 10), new Vector2(190, 24), "PASS", "Pass line (1:1)");
        DrawCrapsZone(dl, pos, new Vector2(12, 38), new Vector2(190, 24), "DONTPASS", "Don't pass (1:1)");
        DrawCrapsZone(dl, pos, new Vector2(12, 66), new Vector2(190, 24), "FIELD", "Field (1:1, 2/12 pay 2:1)");
        DrawCrapsZone(dl, pos, new Vector2(12, 94), new Vector2(190, 24), "SEVEN", "Seven (5:1)");
        DrawCrapsZone(dl, pos, new Vector2(12, 122), new Vector2(190, 24), "ANYCRAPS", "Any Craps (8:1)");
        DrawCrapsZone(dl, pos, new Vector2(210, 94), new Vector2(80, 24), "PLACE4", "Place 4 (9:5)");
        DrawCrapsZone(dl, pos, new Vector2(296, 94), new Vector2(80, 24), "PLACE5", "Place 5 (7:5)");
        DrawCrapsZone(dl, pos, new Vector2(382, 94), new Vector2(80, 24), "PLACE6", "Place 6 (7:6)");
        DrawCrapsZone(dl, pos, new Vector2(210, 122), new Vector2(80, 24), "PLACE8", "Place 8 (7:6)");
        DrawCrapsZone(dl, pos, new Vector2(296, 122), new Vector2(80, 24), "PLACE9", "Place 9 (7:5)");
        DrawCrapsZone(dl, pos, new Vector2(382, 122), new Vector2(80, 24), "PLACE10", "Place 10 (9:5)");

        var rolling = (DateTime.UtcNow - mirroredCrapsRollUtc).TotalMilliseconds < Math.Max(200.0, CasinoUI.CrapsRollDelaySeconds * 1000.0);
        int d1 = mirroredCrapsD1;
        int d2 = mirroredCrapsD2;
        if (rolling)
        {
            var phase = (int)((DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond) / 70 % 6) + 1;
            d1 = phase;
            d2 = ((phase + 2) % 6) + 1;
        }

        var puckPos = new Vector2(pos.X + 238, pos.Y + 30);
        dl.AddCircleFilled(puckPos, 15f, mirroredCrapsPoint == 0 ? 0xFF333333u : 0xFFAA2222u, 16);
        dl.AddCircle(puckPos, 15f, 0xFFCCCCCCu, 16, 1.5f);
        var puckText = mirroredCrapsPoint == 0 ? "OFF" : "ON";
        var puckSize = ImGui.CalcTextSize(puckText);
        dl.AddText(new Vector2(puckPos.X - puckSize.X / 2, puckPos.Y - puckSize.Y / 2), 0xFFFFFFFFu, puckText);
        var pointTxt = mirroredCrapsPoint == 0 ? "Point: OFF" : $"Point: {mirroredCrapsPoint}";
        dl.AddText(new Vector2(pos.X + 220, pos.Y + 55), 0xFFEEDDAAu, pointTxt);
        var shooterTxt = string.IsNullOrWhiteSpace(mirroredCrapsShooter) ? "Shooter: (pending)" : $"Shooter: {mirroredCrapsShooter}";
        dl.AddText(new Vector2(pos.X + 220, pos.Y + 72), 0xFFC8E8C8u, shooterTxt);

        DrawDieFace(dl, new Vector2(pos.X + 340, pos.Y + 15f), 52, d1, rolling);
        DrawDieFace(dl, new Vector2(pos.X + 406, pos.Y + 15f), 52, d2, rolling);

        ImGui.Dummy(new Vector2(w, h));
        ImGui.Separator();
    }

    private void DrawCrapsZone(ImDrawListPtr dl, Vector2 tablePos, Vector2 relPos, Vector2 size, string key, string tooltip)
    {
        var p0 = tablePos + relPos;
        var p1 = p0 + size;
        dl.AddRect(p0, p1, 0x66FFFFFFu, 3f);
        dl.AddText(new Vector2(p0.X + 6, p0.Y + 4), 0xFFEEDDAAu, key);

        mirroredCrapsBetMap.TryGetValue(key, out var lines);
        if (lines != null && lines.Count > 0)
            dl.AddCircleFilled(new Vector2(p1.X - 6, p0.Y + 6), 4f, GetBetMarkerColor(lines), 10);

        if (ImGui.IsMouseHoveringRect(p0, p1))
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                targetInput = key;
                TryPlaceDirectBet(key);
            }

            ImGui.BeginTooltip();
            ImGui.TextUnformatted(key);
            ImGui.TextDisabled(tooltip);
            if (lines != null && lines.Count > 0)
            {
                ImGui.Separator();
                foreach (var line in lines)
                    ImGui.TextUnformatted(line);
            }
            ImGui.EndTooltip();
        }
    }

    private static readonly Dictionary<int, Vector2[]> PipOffsets = new()
    {
        [1] = [new Vector2(0f, 0f)],
        [2] = [new Vector2(-0.22f, -0.22f), new Vector2(0.22f, 0.22f)],
        [3] = [new Vector2(-0.22f, -0.22f), new Vector2(0f, 0f), new Vector2(0.22f, 0.22f)],
        [4] = [new Vector2(-0.22f, -0.22f), new Vector2(0.22f, -0.22f), new Vector2(-0.22f, 0.22f), new Vector2(0.22f, 0.22f)],
        [5] = [new Vector2(-0.22f, -0.22f), new Vector2(0.22f, -0.22f), new Vector2(0f, 0f), new Vector2(-0.22f, 0.22f), new Vector2(0.22f, 0.22f)],
        [6] = [new Vector2(-0.22f, -0.24f), new Vector2(0.22f, -0.24f), new Vector2(-0.22f, 0f), new Vector2(0.22f, 0f), new Vector2(-0.22f, 0.24f), new Vector2(0.22f, 0.24f)]
    };

    private static void DrawDieFace(ImDrawListPtr dl, Vector2 tl, float sz, int face, bool rolling)
    {
        var br = tl + new Vector2(sz, sz);
        uint bg = rolling ? 0xFF444455u : 0xFFEEEEEEu;
        uint pip = rolling ? 0xFFCCCCFFu : 0xFF111111u;
        uint bdr = rolling ? 0xFF8888CCu : 0xFF555555u;
        dl.AddRectFilled(tl, br, bg, 8f);
        dl.AddRect(tl, br, bdr, 8f, ImDrawFlags.None, 1.5f);
        if (face < 1 || face > 6) return;

        var cx = new Vector2(tl.X + sz * 0.5f, tl.Y + sz * 0.5f);
        float r = sz * 0.085f;
        foreach (var off in PipOffsets[face])
            dl.AddCircleFilled(cx + off * sz, r, pip, 12);
    }

    private void DrawChocoboBoard()
    {
        if (mirroredChocoboRoster.Count == 0)
        {
            ImGui.TextDisabled("Waiting for dealer to open betting…");
            ImGui.Separator();
            return;
        }

        ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), "CHOCOBO ROSTER");
        if (ImGui.BeginTable("##pvchocoboRoster", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Racer", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Odds", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableHeadersRow();
            foreach (var r in mirroredChocoboRoster)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(r.Name);
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(r.Odds);
            }
            ImGui.EndTable();
        }

        if (!string.IsNullOrWhiteSpace(mirroredChocoboHash) && mirroredChocoboRacing)
            DrawChocoboLanes(mirroredChocoboHash, mirroredChocoboRoster.Select(r => r.Name).ToList(), mirroredChocoboRacing);

        ImGui.Separator();
    }

    private static List<string> DecodePodium(string hash, List<string> racers)
    {
        return ChatCasino.Engine.ChocoboRacingModule.BuildPodiumFromHash(hash, racers);
    }

    private string lastPlayerChocoboHash = string.Empty;

    private void DrawChocoboLanes(string hash, List<string> racers, bool racing)
    {
        if (!string.Equals(hash, lastPlayerChocoboHash, StringComparison.Ordinal))
        {
            lastPlayerChocoboHash = hash;
            mirroredChocoboTrackCache.Clear();
        }

        var podium = DecodePodium(hash, racers);
        var elapsed = (DateTime.UtcNow - mirroredChocoboRaceStartUtc).TotalMilliseconds;
        var frac = racing ? Math.Clamp((float)(elapsed / 30000.0), 0f, 1f) : 1f;

        var checkpoints = BuildChocoboCheckpointMap(hash, racers, podium);
        var segFloat = frac * 6f;
        var segLow = Math.Clamp((int)MathF.Floor(segFloat), 0, 5);
        var segT = segFloat - segLow;

        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        float w = Math.Max(ImGui.GetContentRegionAvail().X, 300f);
        float laneH = 22f;
        int n = racers.Count;
        float h = n * laneH + 4f;

        uint trackBg = ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.10f, 0.06f, 0.9f));
        uint laneLine = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.2f, 0.4f));
        uint finishLine = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.3f));
        dl.AddRectFilled(origin, origin + new Vector2(w, h), trackBg, 4f);
        dl.AddLine(origin + new Vector2(w - 2, 0), origin + new Vector2(w - 2, h), finishLine, 2f);

        float labelW = 120f;
        float trackW = w - labelW - 10f;

        for (int i = 0; i < n; i++)
        {
            float y = origin.Y + i * laneH + 2f;
            if (i > 0)
                dl.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + w, y), laneLine);

            var name = racers[i];
            if (!checkpoints.TryGetValue(name, out var cp) || cp.Length < 7)
                continue;

            var pos = cp[segLow] + (cp[segLow + 1] - cp[segLow]) * segT;
            float dotX = origin.X + labelW + pos * trackW;
            float dotY = y + laneH * 0.5f;

            var label = name.Length > 14 ? name[..14] : name;
            dl.AddText(new Vector2(origin.X + 4, y + 2), ImGui.ColorConvertFloat4ToU32(new Vector4(0.75f, 0.75f, 0.6f, 1f)), label);
            var hasBet = mirroredChocoboBetRacers.Contains(name, StringComparer.OrdinalIgnoreCase);
            var dotColor = hasBet
                ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 1f, 0.3f, 1f))
                : ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.84f, 0f, 1f));
            var glowColor = hasBet
                ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 1f, 0.5f, 0.4f))
                : ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.94f, 0.4f, 0.4f));
            dl.AddCircleFilled(new Vector2(dotX, dotY), 6f, dotColor);
            dl.AddCircle(new Vector2(dotX, dotY), 7.5f, glowColor, 12, 1.2f);
        }

        ImGui.Dummy(new Vector2(w, h));
    }

    private Dictionary<string, float[]> BuildChocoboCheckpointMap(string hash, List<string> racers, List<string> podium)
    {
        var profiles = ChatCasino.Engine.ChocoboRacingModule.DecodeRaceProfiles(hash);

        foreach (var racer in racers)
        {
            if (mirroredChocoboTrackCache.ContainsKey(racer))
                continue;

            var idx = racers.FindIndex(r => r.Equals(racer, StringComparison.OrdinalIgnoreCase));
            var rank = podium.FindIndex(p => p.Equals(racer, StringComparison.OrdinalIgnoreCase));
            var profile = idx >= 0 && idx < profiles.Count
                ? profiles[idx]
                : new ChatCasino.Engine.ChocoboRacingModule.RaceProfile(75, 75, 0, 0);
            var pace = ChatCasino.Engine.ChocoboRacingModule.BuildSegmentPace(hash, profile, Math.Max(0, idx));

            mirroredChocoboTrackCache[racer] = BuildRacerCheckpoints(hash, racer, rank, pace);
        }

        return mirroredChocoboTrackCache;
    }

    private static float[] BuildRacerCheckpoints(string hash, string racer, int podiumRank, float[] pace)
    {
        var cp = new float[7];
        cp[0] = 0f;

        var total = MathF.Max(0.001f, pace.Sum());
        var cumulative = 0f;
        for (var s = 1; s <= 6; s++)
        {
            cumulative += MathF.Max(0.001f, pace[s - 1]);
            cp[s] = cumulative / total;
        }

        var seed = StableHash($"{hash}|{racer}");
        var targetFinish = podiumRank switch
        {
            0 => 0.995f,
            1 => 0.978f,
            2 => 0.962f,
            _ => 0.90f + (seed % 7) * 0.008f
        };

        var scale = targetFinish / MathF.Max(0.001f, cp[6]);
        var prev = 0f;
        for (var s = 1; s <= 6; s++)
        {
            var next = Math.Clamp(cp[s] * scale, prev + 0.01f, 1f);
            cp[s] = next;
            prev = next;
        }

        return cp;
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in value)
                hash = (hash ^ c) * 16777619;
            return hash;
        }
    }

    private int GetMirroredBlackjackActiveHandIndex(string playerName)
        => mirroredBlackjackActiveHands.TryGetValue(playerName, out var idx) ? idx : 0;

    private string BuildCommandText(string cmd)
    {
        var args = BuildArgs(cmd);
        return args.Length == 0 ? $">{cmd}" : $">{cmd} {string.Join(' ', args)}";
    }

    private string[] BuildArgs(string cmd)
    {
        var game = GetMirroredGameType();
        return cmd switch
        {
            "BET" => game switch
            {
                GameType.Blackjack => [wagerInput.ToString()],
                GameType.Roulette => [wagerInput.ToString(), string.IsNullOrWhiteSpace(targetInput) ? "RED" : targetInput.ToUpperInvariant()],
                GameType.Craps => [wagerInput.ToString(), string.IsNullOrWhiteSpace(targetInput) ? "PASS" : targetInput.ToUpperInvariant()],
                GameType.Baccarat => [wagerInput.ToString(), string.IsNullOrWhiteSpace(targetInput) ? "PLAYER" : targetInput.ToUpperInvariant()],
                GameType.ChocoboRacing => string.IsNullOrWhiteSpace(targetInput) ? [wagerInput.ToString()] : [wagerInput.ToString(), targetInput],
                _ => [wagerInput.ToString()]
            },
            "RAISE" => [wagerInput.ToString()],
            "ALL" => ["IN"],
            _ => Array.Empty<string>()
        };
    }

    private void TryPlaceDirectBet(string target)
    {
        var game = GetMirroredGameType();
        if (game is not (GameType.Roulette or GameType.Craps))
            return;

        targetInput = target;
        var text = BuildCommandText("BET");
        if (!string.IsNullOrWhiteSpace(text))
            SendChatCommand?.Invoke(text, CasinoUI.PlayerChatChannelIndex == 1);
    }

    private uint GetBetMarkerColor(List<string> lines)
        => ImGui.GetColorU32(!string.IsNullOrWhiteSpace(LocalPlayerName) && lines.Any(line => line.StartsWith($"{LocalPlayerName} ", StringComparison.OrdinalIgnoreCase)) ? new Vector4(1f, 0.9f, 0.25f, 0.9f) : new Vector4(0.25f, 0.9f, 0.4f, 0.85f));

    private void DrawRouletteWheelAnimated()
    {
        var draw = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        float cx = pos.X + 90f;
        float cy = pos.Y + 90f;
        float radius = 76f;
        float seg = (float)(2 * Math.PI / 37f);
        const float twoPi = (float)(2 * Math.PI);
        int[] wheelOrder = [0, 32, 15, 19, 4, 21, 2, 25, 17, 34, 6, 27, 13, 36, 11, 30, 8, 23, 10, 5, 24, 16, 33, 1, 20, 14, 31, 9, 22, 18, 29, 7, 28, 12, 35, 3, 26];
        int[] redNumbers = [1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36];

        float wheelRot;
        float ballAngle;
        var spinDurationMs = Math.Max(500.0, CasinoUI.RouletteSpinSeconds * 1000.0);
        if (mirroredRouletteSpinning)
        {
            var elapsed = Math.Min((DateTime.UtcNow - mirroredRouletteSpinStartUtc).TotalMilliseconds, spinDurationMs);
            var a = 0.95 * elapsed - 0.46 * elapsed * elapsed / spinDurationMs;
            wheelRot = (float)(a * 0.018) % twoPi;
            ballAngle = -(float)(a * 0.04) % twoPi;
        }
        else if (mirroredRouletteLast.HasValue)
        {
            wheelRot = 0f;
            var slot = Array.IndexOf(wheelOrder, mirroredRouletteLast.Value);
            ballAngle = (slot + 0.5f) * seg - (float)(Math.PI / 2);
        }
        else
        {
            wheelRot = 0f;
            ballAngle = -(float)(Math.PI / 2);
        }

        var wheelBg = ImGui.GetColorU32(new Vector4(0.1f, 0.1f, 0.1f, 1f));
        var wheelBorder = ImGui.GetColorU32(new Vector4(1f, 0.84f, 0f, 1f));
        var redSeg = ImGui.GetColorU32(new Vector4(0.66f, 0.13f, 0.13f, 1f));
        var blackSeg = ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.12f, 1f));
        var greenSeg = ImGui.GetColorU32(new Vector4(0.08f, 0.6f, 0.14f, 1f));

        draw.AddCircleFilled(new Vector2(cx, cy), radius, wheelBg, 72);
        draw.AddCircle(new Vector2(cx, cy), radius + 2, wheelBorder, 72, 2.5f);

        for (var slot = 0; slot < 37; slot++)
        {
            int n = wheelOrder[slot];
            float a1 = slot * seg - (float)(Math.PI / 2) + wheelRot;
            float a2 = a1 + seg;
            uint col = n == 0 ? greenSeg : (Array.IndexOf(redNumbers, n) >= 0 ? redSeg : blackSeg);
            var c = new Vector2(cx, cy);
            var rp1 = new Vector2(cx + MathF.Cos(a1) * (radius - 3), cy + MathF.Sin(a1) * (radius - 3));
            var rp2 = new Vector2(cx + MathF.Cos(a2) * (radius - 3), cy + MathF.Sin(a2) * (radius - 3));
            draw.AddTriangleFilled(c, rp1, rp2, col);
        }

        float trackR = radius - 8f;
        var ballPos = new Vector2(cx + MathF.Cos(ballAngle) * trackR, cy + MathF.Sin(ballAngle) * trackR);
        draw.AddCircleFilled(ballPos, 5f, 0xFFFFFFFFu, 12);

        ImGui.Dummy(new Vector2(190, 190));
    }

    private void ParseChatForMirror(string text)
    {
        var normalized = StripMirrorPrefix(text);
        var priorGame = mirroredGame;

        if (!string.IsNullOrWhiteSpace(LocalPlayerName))
            mirroredKnownPlayers.Add(LocalPlayerName);

        var plain = StripLeadingTag(normalized);
        if (normalized.Contains("[BLACKJACK]", StringComparison.OrdinalIgnoreCase) || plain.StartsWith("Dealer shows", StringComparison.OrdinalIgnoreCase)) mirroredGame = "Blackjack";
        else if (normalized.Contains("[ROULETTE]", StringComparison.OrdinalIgnoreCase) || plain.Contains("No more bets", StringComparison.OrdinalIgnoreCase) || plain.Contains("Result:", StringComparison.OrdinalIgnoreCase)) mirroredGame = "Roulette";
        else if (normalized.Contains("[CRAPS]", StringComparison.OrdinalIgnoreCase) || plain.StartsWith("Shooter:", StringComparison.OrdinalIgnoreCase) || plain.Contains(" rolls ", StringComparison.OrdinalIgnoreCase)) mirroredGame = "Craps";
        else if (normalized.Contains("[BACCARAT]", StringComparison.OrdinalIgnoreCase) || plain.StartsWith("Player hand:", StringComparison.OrdinalIgnoreCase) || plain.StartsWith("Banker hand:", StringComparison.OrdinalIgnoreCase)) mirroredGame = "Baccarat";
        else if (normalized.Contains("[CHOCOBO]", StringComparison.OrdinalIgnoreCase) || plain.StartsWith("Roster ", StringComparison.OrdinalIgnoreCase) || plain.StartsWith("Forecast hash:", StringComparison.OrdinalIgnoreCase)) mirroredGame = "Chocobo Racing";
        else if (normalized.Contains("[POKER", StringComparison.OrdinalIgnoreCase) || plain.StartsWith("Dealer=", StringComparison.OrdinalIgnoreCase) || plain.StartsWith("Board:", StringComparison.OrdinalIgnoreCase)) mirroredGame = "Texas Hold'Em";
        else if (normalized.Contains("[ULTIMA", StringComparison.OrdinalIgnoreCase) || plain.StartsWith("Game started.", StringComparison.OrdinalIgnoreCase) || plain.Contains("calls ULTIMA", StringComparison.OrdinalIgnoreCase)) mirroredGame = "Ultima";

        var gameChange = MatchTaggedOrPlain(normalized, @"^\[CASINO\]\s+Game changed to\s+(.+)$");
        if (gameChange.Success)
            mirroredGame = gameChange.Groups[1].Value.Trim();

        if (!string.Equals(priorGame, mirroredGame, StringComparison.OrdinalIgnoreCase))
        {
            ResetMirroredRoundStateForGameSwitch();
            mirroredStatus = "Waiting for bets";
            SetDefaultActionsForCurrentGame();
        }

        var removed = MatchTaggedOrPlain(normalized, @"^\[CASINO\]\s+(.+?)\s+(?:removed from table|has been removed)\.?$");
        if (removed.Success)
        {
            RemoveMirroredPlayer(NormalizeMirroredPlayerName(removed.Groups[1].Value));
            return;
        }

        ParseCasinoBankSummary(normalized);

        var betMatch = MatchTaggedOrPlain(normalized, @"^\[(?<game>[A-Z]+)\]\s+(.+?)\s+bets?\s+(\d+)");
        if (!betMatch.Success)
            betMatch = Regex.Match(plain, @"^(.+?)\s+bets?\s+(\d+)", RegexOptions.IgnoreCase);

        if (betMatch.Success && int.TryParse(betMatch.Groups[3].Success ? betMatch.Groups[3].Value : betMatch.Groups[2].Value, out var bet))
        {
            var playerGroup = betMatch.Groups[2].Success && betMatch.Groups[3].Success ? betMatch.Groups[2].Value : betMatch.Groups[1].Value;
            var playerName = NormalizeMirroredPlayerName(playerGroup.Trim());
            if (!string.IsNullOrWhiteSpace(playerName))
            {
                RememberPlayer(playerName);
                var gameTag = betMatch.Groups["game"].Success
                    ? betMatch.Groups["game"].Value.ToUpperInvariant()
                    : NormalizeGameKey(mirroredGame);
                if (gameTag is "ROULETTE" or "CRAPS")
                    mirroredBets[playerName] = (mirroredBets.TryGetValue(playerName, out var existing) ? existing : 0) + bet;
                else
                    mirroredBets[playerName] = bet;
            }
        }

        if (StartsWithTaggedOrPlain(normalized, "[BLACKJACK] Dealer shows"))
        {
            mirroredBlackjackHands.Clear();
            mirroredBlackjackHandResults.Clear();
            mirroredBlackjackActiveHands.Clear();
            mirroredDealerCards.Clear();
            mirroredDealerCards.AddRange(ExtractCardTokens(normalized));
        }
        else if (StartsWithTaggedOrPlain(normalized, "[BLACKJACK] Dealer:"))
        {
            mirroredDealerCards.Clear();
            mirroredDealerCards.AddRange(ExtractCardTokens(normalized));
        }

        if (StartsWithTaggedOrPlain(normalized, "[BLACKJACK] Round complete")
            || StartsWithTaggedOrPlain(normalized, "[CASINO] Blackjack round complete")
            || StartsWithTaggedOrPlain(normalized, "[BLACKJACK] Dealer:"))
        {
            mirroredStatus = "Waiting for bets";
            mirroredActions.Clear();
            mirroredActions.Add("BET");
        }

        var payoutLine = MatchTaggedOrPlain(normalized, @"^\[CASINO\]\s+(.+?)\s+round complete");
        if (payoutLine.Success)
        {
        }

        if ((normalized.StartsWith("[CASINO]", StringComparison.OrdinalIgnoreCase) || plain.Contains("(Bank", StringComparison.OrdinalIgnoreCase))
            && normalized.Contains("(Bank", StringComparison.OrdinalIgnoreCase)
            && !normalized.Contains("round complete", StringComparison.OrdinalIgnoreCase))
        {
            var payoutNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match pm in Regex.Matches(normalized, @"(?:^\[CASINO\]\s+|^\s*|\|\s*)(?<name>.+?)\s+[+-]?\d+\D*\s*\(Bank\s+(?<bank>\d+)\D*\)", RegexOptions.IgnoreCase))
            {
                var pn = NormalizeMirroredPlayerName(pm.Groups["name"].Value);
                if (!string.IsNullOrWhiteSpace(pn))
                    payoutNames.Add(pn);
            }
            if (payoutNames.Count > 0)
            {
                foreach (var known in mirroredKnownPlayers.ToList())
                {
                    if (!string.IsNullOrWhiteSpace(LocalPlayerName) && known.Equals(LocalPlayerName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!payoutNames.Contains(known))
                        RemoveMirroredPlayer(known);
                }
            }
        }

        var blackjackPrompt = MatchTaggedOrPlain(normalized, @"^\[BLACKJACK\]\s+(.+?):\s*>(.+)$");
        if (blackjackPrompt.Success)
        {
            var promptPlayer = NormalizeMirroredPlayerName(blackjackPrompt.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(LocalPlayerName) && promptPlayer.Equals(LocalPlayerName, StringComparison.OrdinalIgnoreCase))
            {
                mirroredActions.Clear();
                mirroredActions.AddRange(blackjackPrompt.Groups[2].Value
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().TrimStart('>'))
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant()));
                mirroredStatus = "In round";
            }
        }

        var blackjackDouble = MatchTaggedOrPlain(normalized, @"^\[BLACKJACK\]\s+(.+?)\s+doubles:\s+(.+?)\s+\(([^\)]+)\)$");
        if (blackjackDouble.Success)
        {
            var name = NormalizeMirroredPlayerName(blackjackDouble.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (mirroredBets.TryGetValue(name, out var total) && total > 0)
                    mirroredBets[name] = total + Math.Max(1, total / Math.Max(1, mirroredBlackjackHands.TryGetValue(name, out var hg) ? hg.Count : 1));
                var cards = ExtractCardTokens(blackjackDouble.Groups[2].Value);
                if (cards.Count > 0)
                {
                    if (!mirroredBlackjackHands.TryGetValue(name, out var groups)) mirroredBlackjackHands[name] = groups = [];
                    var active = GetMirroredBlackjackActiveHandIndex(name);
                    while (groups.Count <= active) groups.Add([]);
                    groups[active] = cards;
                    if (!mirroredBlackjackHandResults.TryGetValue(name, out var results)) mirroredBlackjackHandResults[name] = results = [];
                    while (results.Count <= active) results.Add(string.Empty);
                    results[active] = blackjackDouble.Groups[3].Value.Trim();
                }
            }
        }

        var blackjackSplit = MatchTaggedOrPlain(normalized, @"^\[BLACKJACK\]\s+(.+?)\s+splits\.");
        if (blackjackSplit.Success)
        {
            var name = NormalizeMirroredPlayerName(blackjackSplit.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (mirroredBets.TryGetValue(name, out var total) && total > 0)
                    mirroredBets[name] = total * 2;
                mirroredBlackjackHands.Remove(name);
                mirroredBlackjackHandResults.Remove(name);
                mirroredBlackjackActiveHands[name] = 0;
            }
        }

        var blackjackSplitHand = MatchTaggedOrPlain(normalized, @"^\[BLACKJACK\]\s+(.+?)\s+H(\d+):\s+(.+?)\s+\(([^\)]+)\)$");
        if (blackjackSplitHand.Success)
        {
            var name = NormalizeMirroredPlayerName(blackjackSplitHand.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(name) && int.TryParse(blackjackSplitHand.Groups[2].Value, out var handNum))
            {
                var handIdx = Math.Max(0, handNum - 1);
                var cards = ExtractCardTokens(blackjackSplitHand.Groups[3].Value);
                if (cards.Count > 0)
                {
                    if (!mirroredBlackjackHands.TryGetValue(name, out var groups)) mirroredBlackjackHands[name] = groups = [];
                    while (groups.Count <= handIdx) groups.Add([]);
                    groups[handIdx] = cards;
                    if (!mirroredBlackjackHandResults.TryGetValue(name, out var results)) mirroredBlackjackHandResults[name] = results = [];
                    while (results.Count <= handIdx) results.Add(string.Empty);
                    results[handIdx] = blackjackSplitHand.Groups[4].Value.Trim();
                }
            }
        }

        if (!blackjackDouble.Success && !blackjackSplitHand.Success)
        {
            var blackjackCards = MatchTaggedOrPlain(normalized, @"^\[BLACKJACK\]\s+(.+?):\s+(.+?)\s+\(([^\)]+)\)$");
            if (blackjackCards.Success)
            {
                var name = NormalizeMirroredPlayerName(blackjackCards.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var cards = ExtractCardTokens(blackjackCards.Groups[2].Value);
                    if (cards.Count > 0)
                    {
                        if (!mirroredBlackjackHands.TryGetValue(name, out var groups)) mirroredBlackjackHands[name] = groups = [];
                        var active = GetMirroredBlackjackActiveHandIndex(name);
                        while (groups.Count <= active) groups.Add([]);
                        groups[active] = cards;
                        if (!mirroredBlackjackHandResults.TryGetValue(name, out var results)) mirroredBlackjackHandResults[name] = results = [];
                        while (results.Count <= active) results.Add(string.Empty);
                        results[active] = blackjackCards.Groups[3].Value.Trim();
                    }
                }
            }
        }

        var blackjackTurn = MatchTaggedOrPlain(normalized, @"^\[BLACKJACK\]\s+Turn:\s+(.+?)(?:\s+H(\d+))?\s+->\s+(.+?)\s+\(([^\)]+)\)$");
        if (blackjackTurn.Success)
        {
            var name = NormalizeMirroredPlayerName(blackjackTurn.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(name))
            {
                RememberPlayer(name);
                mirroredStatus = $"Turn: {name}";
                var handIdx = blackjackTurn.Groups[2].Success && int.TryParse(blackjackTurn.Groups[2].Value, out var h) ? Math.Max(0, h - 1) : 0;
                mirroredBlackjackActiveHands[name] = handIdx;
                var cards = ExtractCardTokens(blackjackTurn.Groups[3].Value);
                if (!mirroredBlackjackHands.TryGetValue(name, out var groups)) mirroredBlackjackHands[name] = groups = [];
                while (groups.Count <= handIdx) groups.Add([]);
                groups[handIdx] = cards;
                if (!mirroredBlackjackHandResults.TryGetValue(name, out var res)) mirroredBlackjackHandResults[name] = res = [];
                while (res.Count <= handIdx) res.Add(string.Empty);
                res[handIdx] = blackjackTurn.Groups[4].Value.Trim();
            }
        }

        var baccaratReveal = MatchTaggedOrPlain(normalized, @"^\[BACCARAT REVEAL\]\s+Player:\s+(.+?)\s+\((\d+)\)\s+\|\s+Banker:\s+(.+?)\s+\((\d+)\)\s+\|\s+Winner:\s+(.+)$");
        if (baccaratReveal.Success)
        {
            mirroredBaccaratPlayerCards.Clear();
            mirroredBaccaratPlayerCards.AddRange(ExtractCardTokens(baccaratReveal.Groups[1].Value));
            mirroredBaccaratBankerCards.Clear();
            mirroredBaccaratBankerCards.AddRange(ExtractCardTokens(baccaratReveal.Groups[3].Value));
            mirroredBaccaratResult = $"Player {baccaratReveal.Groups[2].Value} vs Banker {baccaratReveal.Groups[4].Value} - Winner: {baccaratReveal.Groups[5].Value.Trim()}";
            mirroredStatus = "Round complete";
        }

        var shooterLine = MatchTaggedOrPlain(normalized, @"^\[CRAPS\]\s+Shooter:\s+(.+)$");
        if (shooterLine.Success)
            mirroredCrapsShooter = NormalizeMirroredPlayerName(shooterLine.Groups[1].Value);

        var openLine = MatchTaggedOrPlain(normalized, @"^\[CRAPS\]\s+Bets are open\s*\((\d+)s(?:\/(\d+)s)?\)");
        if (openLine.Success && int.TryParse(openLine.Groups[1].Value, out var openSecs))
        {
            if (openLine.Groups[2].Success && int.TryParse(openLine.Groups[2].Value, out var parsedTotal))
                mirroredCrapsBetWindowSeconds = Math.Max(1, parsedTotal);
            else if (mirroredCrapsBetWindowSeconds <= 0)
                mirroredCrapsBetWindowSeconds = Math.Max(1, (int)Math.Ceiling(CasinoUI.CrapsBettingDurationSeconds));
            mirroredCrapsBetsOpenUntilUtc = DateTime.UtcNow.AddSeconds(Math.Max(0, openSecs));
            mirroredStatus = $"Bets open ({Math.Max(0, openSecs)}/{mirroredCrapsBetWindowSeconds}s)";
        }

        if (shooterLine.Success && mirroredCrapsBetWindowSeconds > 0)
            mirroredCrapsBetsOpenUntilUtc = DateTime.UtcNow.AddSeconds(mirroredCrapsBetWindowSeconds);

        var crapsRoll = MatchTaggedOrPlain(normalized, @"^\[CRAPS\]\s+(.+?)\s+rolls\s*(\d+)\+(\d+)=(\d+)");
        if (crapsRoll.Success)
        {
            mirroredCrapsShooter = NormalizeMirroredPlayerName(crapsRoll.Groups[1].Value);
            int.TryParse(crapsRoll.Groups[2].Value, out mirroredCrapsD1);
            int.TryParse(crapsRoll.Groups[3].Value, out mirroredCrapsD2);
            mirroredCrapsRollUtc = DateTime.UtcNow;
            mirroredCrapsBetMap.Remove("FIELD");
            mirroredCrapsBetMap.Remove("SEVEN");
            mirroredCrapsBetMap.Remove("ANYCRAPS");
            if (mirroredCrapsBetWindowSeconds > 0)
                mirroredCrapsBetsOpenUntilUtc = DateTime.UtcNow.AddSeconds(mirroredCrapsBetWindowSeconds);
        }

        var pointEstablished = MatchTaggedOrPlain(normalized, @"^\[CRAPS\]\s+Point established:\s*(\d+)");
        if (pointEstablished.Success && int.TryParse(pointEstablished.Groups[1].Value, out var point))
            mirroredCrapsPoint = point;

        var crapsBet = MatchTaggedOrPlain(normalized, @"^\[CRAPS\]\s+(.+?)\s+bets\s+(\d+)\D*\s+on\s+(.+)$");
        if (crapsBet.Success && int.TryParse(crapsBet.Groups[2].Value, out var cAmt))
            UpsertMirroredBetLine(mirroredCrapsBetMap, crapsBet.Groups[3].Value.Trim().ToUpperInvariant(), NormalizeMirroredPlayerName(crapsBet.Groups[1].Value), cAmt);

        var crapsWin = MatchTaggedOrPlain(normalized, @"^\[CRAPS\]\s+.+?\s+wins\s+\d+\D*\s+on\s+(\w+)");
        if (crapsWin.Success)
            mirroredCrapsBetMap.Remove(crapsWin.Groups[1].Value.Trim().ToUpperInvariant());

        if (plain.Contains("point off", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("point made", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("seven out", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("come-out win", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("come-out loss", StringComparison.OrdinalIgnoreCase))
        {
            mirroredCrapsBetMap.Clear();
            mirroredCrapsPoint = 0;
            foreach (var key in mirroredBets.Keys.ToList())
                mirroredBets[key] = 0;
        }

        var rouletteBet = MatchTaggedOrPlain(normalized, @"^\[ROULETTE\]\s+(.+?)\s+bets\s+(\d+)\D*\s+on\s+(.+)$");
        if (rouletteBet.Success && int.TryParse(rouletteBet.Groups[2].Value, out var rAmt))
        {
            var rPlayer = NormalizeMirroredPlayerName(rouletteBet.Groups[1].Value);
            var targets = rouletteBet.Groups[3].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var target in targets)
                UpsertMirroredBetLine(mirroredRouletteBetMap, target.ToUpperInvariant(), rPlayer, rAmt);
        }

        var rouletteResult = MatchTaggedOrPlain(normalized, @"^\[ROULETTE\].*Result:\s*(\d+)");
        if (rouletteResult.Success && int.TryParse(rouletteResult.Groups[1].Value, out var rv))
        {
            mirroredRouletteLast = rv;
            mirroredRouletteSpinning = false;
            mirroredRouletteBetMap.Clear();
            foreach (var key in mirroredBets.Keys.ToList())
                mirroredBets[key] = 0;
            var rvColor = rv == 0 ? "Green" : RouletteUtils.IsRed(rv) ? "Red" : "Black";
            mirroredStatus = $"Result {rv} ({rvColor}) - waiting for bets";
        }

        if (plain.Contains("No more bets", StringComparison.OrdinalIgnoreCase) || plain.Contains("Spinning", StringComparison.OrdinalIgnoreCase))
        {
            mirroredRouletteSpinning = true;
            mirroredRouletteSpinStartUtc = DateTime.UtcNow;
            mirroredStatus = "Spinning...";
        }

        if (StartsWithTaggedOrPlain(normalized, "[CHOCOBO] Bets are now OPEN"))
        {
            mirroredChocoboRoster.Clear();
            mirroredChocoboHash = string.Empty;
            lastPlayerChocoboHash = string.Empty;
            mirroredChocoboRacing = false;
            mirroredChocoboBetRacers.Clear();
            mirroredChocoboTrackCache.Clear();
            mirroredStatus = "Bets open";
        }

        var chocoboBet = MatchTaggedOrPlain(normalized, @"^\[CHOCOBO\]\s+(.+?)\s+bets\s+\d+\D*\s+on\s+(.+?)\s*(?:\(\d|$)");
        if (chocoboBet.Success)
        {
            var betPlayer = NormalizeMirroredPlayerName(chocoboBet.Groups[1].Value);
            var racerName = chocoboBet.Groups[2].Value.Trim();
            if (!string.IsNullOrWhiteSpace(LocalPlayerName) && betPlayer.Equals(LocalPlayerName, StringComparison.OrdinalIgnoreCase))
                mirroredChocoboBetRacers.Add(racerName);
        }

        if (StartsWithTaggedOrPlain(normalized, "[CHOCOBO] Roster "))
            MergeChocoboRoster(normalized);

        var chocoboHash = MatchTaggedOrPlain(normalized, @"^\[CHOCOBO\]\s+Forecast hash:\s+(.+)$");
        if (chocoboHash.Success)
            mirroredChocoboHash = chocoboHash.Groups[1].Value.Trim();

        if (StartsWithTaggedOrPlain(normalized, "[CHOCOBO] Race started!"))
        {
            mirroredChocoboRacing = true;
            mirroredChocoboRaceStartUtc = DateTime.UtcNow;
            mirroredStatus = "Race in progress";
        }

        var pokerPot = Regex.Match(normalized, @"\bPot:\s*(\d+)", RegexOptions.IgnoreCase);
        if (pokerPot.Success && int.TryParse(pokerPot.Groups[1].Value, out var pot))
            mirroredPokerPot = pot;

        var pokerBoard = MatchTaggedOrPlain(normalized, @"^\[POKER\]\s+Board:\s+(.+?)(?:\s+\|\s+Pot:\s*(\d+))?$");
        if (pokerBoard.Success)
        {
            mirroredPokerBoardCards.Clear();
            mirroredPokerBoardCards.AddRange(ExtractCardTokens(pokerBoard.Groups[1].Value));
            if (pokerBoard.Groups[2].Success && int.TryParse(pokerBoard.Groups[2].Value, out var p2)) mirroredPokerPot = p2;
            mirroredStatus = "Hand in progress";
        }

        if (normalized.Contains("[POKER HAND]", StringComparison.OrdinalIgnoreCase) || plain.StartsWith("HAND:", StringComparison.OrdinalIgnoreCase))
        {
            mirroredPlayerCards.Clear();
            mirroredPlayerCards.AddRange(ExtractCardTokens(normalized));
        }

        var pokerSeatOrder = MatchTaggedOrPlain(normalized, @"^\[POKER\]\s+Seat order:\s+(.+)$");
        if (pokerSeatOrder.Success)
        {
            var newSeats = new List<string>();
            foreach (var part in pokerSeatOrder.Groups[1].Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var cleaned = Regex.Replace(part, @"\s+\d+[\uE049]*$", string.Empty).Trim();
                var normalized2 = NormalizeMirroredPlayerName(cleaned);
                if (!string.IsNullOrWhiteSpace(normalized2))
                {
                    newSeats.Add(normalized2);
                    RememberPlayer(normalized2);
                }
            }
            if (newSeats.Count >= mirroredPokerSeatOrder.Count || mirroredPokerSeatOrder.Count == 0)
            {
                mirroredPokerSeatOrder.Clear();
                mirroredPokerSeatOrder.AddRange(newSeats);
            }
        }

        var pokerRoles = MatchTaggedOrPlain(normalized, @"^\[POKER\]\s+Dealer=(.+?)\s+\|\s+SB\s+(.+?)\s+\d+.*?\|\s+BB\s+(.+?)\s+\d+");
        if (pokerRoles.Success)
        {
            mirroredPokerRoles.Clear();
            mirroredPokerRoles[NormalizeMirroredPlayerName(pokerRoles.Groups[1].Value)] = "DEALER";
            mirroredPokerRoles[NormalizeMirroredPlayerName(pokerRoles.Groups[2].Value)] = "SB";
            mirroredPokerRoles[NormalizeMirroredPlayerName(pokerRoles.Groups[3].Value)] = "BB";
            mirroredPokerBoardCards.Clear();
            mirroredPokerPot = 0;
        }

        var pokerTurn = MatchTaggedOrPlain(normalized, @"^\[POKER\]\s+Turn:\s+(.+)$");
        if (pokerTurn.Success)
            mirroredPokerCurrentTurn = NormalizeMirroredPlayerName(pokerTurn.Groups[1].Value);

        var pokerPrompt = MatchTaggedOrPlain(normalized, @"^\[POKER[^\]]*\]\s+(.+?):\s*>(.+)$");
        if (pokerPrompt.Success)
        {
            var promptPlayer = NormalizeMirroredPlayerName(pokerPrompt.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(promptPlayer)) mirroredPokerCurrentTurn = promptPlayer;
            if (!string.IsNullOrWhiteSpace(LocalPlayerName) && promptPlayer.Equals(LocalPlayerName, StringComparison.OrdinalIgnoreCase))
            {
                mirroredActions.Clear();
                mirroredActions.AddRange(pokerPrompt.Groups[2].Value
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().TrimStart('>'))
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant()));
            }
        }

        var ultimaStart = MatchTaggedOrPlain(normalized, @"^\[ULTIMA\]\s+Game started\.\s+Top card:\s+(\S+)\.\s+Active color:\s+(\S+)");
        if (ultimaStart.Success)
        {
            mirroredUltimaTopCard = ultimaStart.Groups[1].Value.Trim();
            mirroredUltimaColor = ultimaStart.Groups[2].Value.Trim();
            mirroredUltimaCardCounts.Clear();
            foreach (var p in GetMirroredPlayerNames())
                mirroredUltimaCardCounts[p] = 7;
            mirroredStatus = "Game in progress";
        }

        var ultimaPlayed = MatchTaggedOrPlain(normalized, @"^\[ULTIMA\]\s+(.+?)\s+\((\d+)\)\s+played\s+(\S+)\.\s+Color:\s+(\S+)\.\s+Dir:\s+(\S+)\.\s+Turn:\s+(.+)$");
        if (ultimaPlayed.Success)
        {
            var who = NormalizeMirroredPlayerName(ultimaPlayed.Groups[1].Value);
            var playedCode = ultimaPlayed.Groups[3].Value.Trim();
            mirroredUltimaTopCard = playedCode;
            mirroredUltimaColor = ultimaPlayed.Groups[4].Value.Trim();
            mirroredUltimaDirection = ultimaPlayed.Groups[5].Value.Trim();
            mirroredUltimaTurn = NormalizeMirroredPlayerName(ultimaPlayed.Groups[6].Value);
            if (int.TryParse(ultimaPlayed.Groups[2].Value, out var reportedCount))
                mirroredUltimaCardCounts[who] = reportedCount;
            if (!string.IsNullOrWhiteSpace(LocalPlayerName) && who.Equals(LocalPlayerName, StringComparison.OrdinalIgnoreCase))
            {
                var idx = mirroredUltimaHand.FindIndex(c => c.Equals(playedCode, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) mirroredUltimaHand.RemoveAt(idx);
            }
            RememberPlayer(who);
            mirroredStatus = $"{mirroredUltimaTurn}'s turn";
        }

        var ultimaFinalPlay = MatchTaggedOrPlain(normalized, @"^\[ULTIMA\]\s+(.+?)\s+played\s+(\S+)\.\s+Color:\s+(\S+)\.\s+Dir:\s+(\S+)\.\s*$");
        if (!ultimaPlayed.Success && ultimaFinalPlay.Success)
        {
            var who2 = NormalizeMirroredPlayerName(ultimaFinalPlay.Groups[1].Value);
            mirroredUltimaTopCard = ultimaFinalPlay.Groups[2].Value.Trim();
            mirroredUltimaColor = ultimaFinalPlay.Groups[3].Value.Trim();
            mirroredUltimaDirection = ultimaFinalPlay.Groups[4].Value.Trim();
            if (!string.IsNullOrWhiteSpace(who2))
                mirroredUltimaCardCounts[who2] = 0;
            if (!string.IsNullOrWhiteSpace(LocalPlayerName) && who2.Equals(LocalPlayerName, StringComparison.OrdinalIgnoreCase))
            {
                var idx = mirroredUltimaHand.FindIndex(c => c.Equals(mirroredUltimaTopCard, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) mirroredUltimaHand.RemoveAt(idx);
            }
        }

        var ultimaTurnLine = MatchTaggedOrPlain(normalized, @"^\[ULTIMA\]\s+Turn:\s+(.+)$");
        if (ultimaTurnLine.Success)
            mirroredUltimaTurn = NormalizeMirroredPlayerName(ultimaTurnLine.Groups[1].Value);

        var ultimaWin = MatchTaggedOrPlain(normalized, @"^\[ULTIMA\]\s+(.+?)\s+wins!");
        if (ultimaWin.Success)
        {
            mirroredStatus = $"{NormalizeMirroredPlayerName(ultimaWin.Groups[1].Value)} wins!";
            mirroredUltimaTurn = string.Empty;
        }

        var ultimaCall = MatchTaggedOrPlain(normalized, @"^\[ULTIMA\]\s+(.+?)\s+calls ULTIMA!");
        if (ultimaCall.Success)
            RememberPlayer(NormalizeMirroredPlayerName(ultimaCall.Groups[1].Value));

        var ultimaHand = MatchTaggedOrPlain(normalized, @"^\[ULTIMA HAND\]\s+(.+)$");
        if (ultimaHand.Success)
        {
            mirroredUltimaHand.Clear();
            mirroredUltimaHand.AddRange(ultimaHand.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (!string.IsNullOrWhiteSpace(LocalPlayerName))
                mirroredUltimaCardCounts[LocalPlayerName] = mirroredUltimaHand.Count;
        }
    }

    private void RemoveMirroredPlayer(string? playerName)
    {
        var n = NormalizeMirroredPlayerName(playerName);
        if (string.IsNullOrWhiteSpace(n))
            return;

        mirroredKnownPlayers.Remove(n);
        mirroredBanks.Remove(n);
        mirroredBets.Remove(n);
        mirroredSeatStates.Remove(n);
        mirroredBlackjackHands.Remove(n);
        mirroredBlackjackHandResults.Remove(n);
        mirroredBlackjackActiveHands.Remove(n);
        foreach (var list in mirroredRouletteBetMap.Values)
            list.RemoveAll(line => line.StartsWith($"{n} ", StringComparison.OrdinalIgnoreCase));
        foreach (var list in mirroredCrapsBetMap.Values)
            list.RemoveAll(line => line.StartsWith($"{n} ", StringComparison.OrdinalIgnoreCase));
    }

    private void DrawPokerTableVisual()
    {
        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.2f, 1f), "Texas Hold'Em Table");

        var draw = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        float w = 520f, h = 260f;
        var center = new Vector2(pos.X + w / 2f, pos.Y + h / 2f);
        var tableMin = new Vector2(center.X - 180f, center.Y - 70f);
        var tableMax = new Vector2(center.X + 180f, center.Y + 70f);
        draw.AddRectFilled(tableMin, tableMax, ImGui.GetColorU32(new Vector4(0.1f, 0.35f, 0.2f, 1f)), 70f);
        draw.AddRect(tableMin, tableMax, ImGui.GetColorU32(new Vector4(0.75f, 0.65f, 0.3f, 1f)), 70f, ImDrawFlags.None, 2.2f);
        draw.AddText(new Vector2(center.X - 46f, center.Y - 42f), ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.8f, 1f)), $"Pot: {mirroredPokerPot}\uE049");

        if (mirroredPokerBoardCards.Count > 0)
        {
            var cursor = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(new Vector2(center.X - 120f, center.Y - 18f));
            CasinoUI.DrawCardTokens(mirroredPokerBoardCards);
            ImGui.SetCursorScreenPos(cursor);
        }

        // Seat players around the oval table
        var seatNames = mirroredPokerSeatOrder.Count > 0
            ? mirroredPokerSeatOrder
            : GetMirroredPlayerNames().ToList();

        if (seatNames.Count > 0)
        {
            for (var i = 0; i < seatNames.Count; i++)
            {
                var a = (float)(Math.PI * 2 * i / seatNames.Count) - (float)Math.PI / 2;
                var p = new Vector2(center.X + MathF.Cos(a) * 220f, center.Y + MathF.Sin(a) * 110f);

                mirroredPokerRoles.TryGetValue(seatNames[i], out var role);
                role ??= string.Empty;
                var isTurn = mirroredPokerCurrentTurn.Equals(seatNames[i], StringComparison.OrdinalIgnoreCase);
                var color = role switch
                {
                    "DEALER" => new Vector4(1f, 0.88f, 0.25f, 1f),
                    "SB" => new Vector4(0.35f, 0.85f, 1f, 1f),
                    "BB" => new Vector4(0.95f, 0.45f, 0.45f, 1f),
                    _ => isTurn ? new Vector4(1f, 0.88f, 0.25f, 1f) : new Vector4(0.82f, 0.82f, 0.82f, 1f)
                };

                draw.AddCircleFilled(p, 10f, ImGui.GetColorU32(color), 16);
                var bankStr = mirroredBanks.TryGetValue(seatNames[i], out var bank) ? $"{bank}\uE049" : string.Empty;
                draw.AddText(new Vector2(p.X + 14f, p.Y - 9f), ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.95f, 1f)), seatNames[i]);
                draw.AddText(new Vector2(p.X + 14f, p.Y + 5f), ImGui.GetColorU32(new Vector4(0.7f, 0.7f, 0.7f, 1f)), bankStr);

                if (ImGui.IsMouseHoveringRect(new Vector2(p.X - 12, p.Y - 12), new Vector2(p.X + 12, p.Y + 12)))
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(seatNames[i]);
                    ImGui.TextDisabled(string.IsNullOrWhiteSpace(role) ? "Role: Player" : $"Role: {role}");
                    if (mirroredBanks.TryGetValue(seatNames[i], out var tipBank))
                        ImGui.TextDisabled($"Bank: {tipBank}\uE049");
                    if (isTurn)
                        ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), "CURRENT TURN");
                    ImGui.EndTooltip();
                }
            }
        }

        ImGui.Dummy(new Vector2(w, h));
        ImGui.Separator();
    }

    private void RememberPlayer(string? playerName)
    {
        var n = NormalizeMirroredPlayerName(playerName);
        if (!string.IsNullOrWhiteSpace(n))
            mirroredKnownPlayers.Add(n);
    }

    private static string NormalizeMirroredPlayerName(string? playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return string.Empty;

        var t = Regex.Replace(playerName.Trim(), @"^\[[^\]]+\]\s*", string.Empty);
        if (t.EndsWith(" must", StringComparison.OrdinalIgnoreCase))
            t = t[..^5].TrimEnd();

        if (string.IsNullOrWhiteSpace(t)
            || t.Contains("(Bank", StringComparison.OrdinalIgnoreCase)
            || t.Contains('|')
            || t.Equals("Dealer", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Turn", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Table", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Wheel", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Board", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Player Hand", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Banker Hand", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Dealer shows", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return t;
    }

    private static List<string> ExtractCardTokens(string text)
        => Regex.Matches(text, @"[\[\u3010]?([2-9]|10|[AJQK])[♥♦♣♠][\]\u3011]?").Select(m => m.Value.Trim('[', ']', '\u3010', '\u3011')).ToList();

    private static void UpsertMirroredBetLine(Dictionary<string, List<string>> betMap, string target, string who, int amount)
    {
        if (string.IsNullOrWhiteSpace(who) || string.IsNullOrWhiteSpace(target) || amount <= 0)
            return;

        if (!betMap.TryGetValue(target, out var lines))
            betMap[target] = lines = [];

        var idx = lines.FindIndex(line => line.StartsWith($"{who} ", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            var m = Regex.Match(lines[idx], @"\s(\d+)\D*$");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var existing))
            {
                lines[idx] = $"{who} {existing + amount}\uE049";
                return;
            }
        }

        lines.Add($"{who} {amount}\uE049");
    }

    private void MergeChocoboRoster(string text)
    {
        var idx = text.IndexOf(':');
        if (idx < 0 || idx == text.Length - 1)
            return;

        foreach (var part in text[(idx + 1)..].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Regex.Match(part, @"^(.*?)\s+(\d+(?:\.\d+)?:1)$");
            if (!m.Success)
                continue;

            var name = m.Groups[1].Value.Trim();
            var odds = m.Groups[2].Value.Trim();
            var existing = mirroredChocoboRoster.FindIndex(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
                mirroredChocoboRoster[existing] = (name, odds);
            else
                mirroredChocoboRoster.Add((name, odds));
        }
    }

    private void ParseCasinoBankSummary(string text)
    {
        var plain = StripLeadingTag(text);
        if (!text.StartsWith("[CASINO]", StringComparison.OrdinalIgnoreCase)
            && !plain.Contains("(Bank", StringComparison.OrdinalIgnoreCase))
            return;

        // Tagged format and natural format are both accepted.
        foreach (Match m in Regex.Matches(text, @"(?:^\[CASINO\]\s+|^\s*|\|\s*)(?<name>.+?)\s+[+-]?\d+\D*\s*\(Bank\s+(?<bank>\d+)\D*\)", RegexOptions.IgnoreCase))
        {
            var name = NormalizeMirroredPlayerName(m.Groups["name"].Value);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (int.TryParse(m.Groups["bank"].Value, out var bank))
            {
                mirroredKnownPlayers.Add(name);
                mirroredBanks[name] = bank;
            }
        }

        // Also support single-entry natural lines without [CASINO] or pipe separators.
        if (!text.Contains('|') && !text.StartsWith("[CASINO]", StringComparison.OrdinalIgnoreCase))
        {
            var single = Regex.Match(text, @"^(?<name>.+?)\s+[+-]?\d+\D*\s*\(Bank\s+(?<bank>\d+)\D*\)$", RegexOptions.IgnoreCase);
            if (single.Success)
            {
                var name = NormalizeMirroredPlayerName(single.Groups["name"].Value);
                if (!string.IsNullOrWhiteSpace(name) && int.TryParse(single.Groups["bank"].Value, out var bank))
                {
                    mirroredKnownPlayers.Add(name);
                    mirroredBanks[name] = bank;
                }
            }
        }
    }
}
