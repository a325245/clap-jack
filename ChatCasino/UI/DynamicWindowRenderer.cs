using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ChatCasino.Engine;
using ChatCasino.Models;
using ChatCasino.Services;
using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace ChatCasino.UI;

public sealed class DynamicWindowRenderer
{
    private static readonly int[] RouletteRedNumbers = [1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36];
    private static readonly int[] RouletteWheelOrder = [0, 32, 15, 19, 4, 21, 2, 25, 17, 34, 6, 27, 13, 36, 11, 30, 8, 23, 10, 5, 24, 16, 33, 1, 20, 14, 31, 9, 22, 18, 29, 7, 28, 12, 35, 3, 26];
    private static readonly int[,] RouletteGrid =
    {
        { 3, 6, 9, 12, 15, 18, 21, 24, 27, 30, 33, 36 },
        { 2, 5, 8, 11, 14, 17, 20, 23, 26, 29, 32, 35 },
        { 1, 4, 7, 10, 13, 16, 19, 22, 25, 28, 31, 34 }
    };

    private readonly GameManager gameManager;
    private readonly ITableService tableService;
    private readonly Dictionary<string, string> worldEditByPlayer = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> bankEditByPlayer = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> bankEditActivePlayers = new(StringComparer.OrdinalIgnoreCase);

    private int wagerInput = 100;
    private string addPlayerName = string.Empty;
    private string addPlayerWorld = "Ultros";
    private string ruleInput = "H17";
    private string targetInput = "RED";
    private int targetPresetIndex;
    private GameType selectedGame = GameType.None;
    private string commandSenderPlayer = string.Empty;

    private DateTime rouletteSpinAnimStartUtc = DateTime.MinValue;
    private bool rouletteWasSpinning;

    private bool testModeEnabled;
    private int testBotCount = 3;
    private bool dealerConfirmed;
    private bool streamerModeEnabled;
    private readonly Dictionary<string, string> streamerNameMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random streamerRng = new();

    private readonly List<string> dealerChatFeed = new();

    public Action? AddPartyRequested { get; set; }
    public Action<bool, int>? TestModeRequested { get; set; }
    public Action<string>? DealerBroadcastRequested { get; set; }
    public Action? FactoryResetRequested { get; set; }

    public DynamicWindowRenderer(GameManager gameManager, ITableService tableService)
    {
        this.gameManager = gameManager;
        this.tableService = tableService;
    }

    public ICasinoViewModel? LastRendered { get; private set; }

    public void SetTestModeState(bool enabled, int botCount)
    {
        testModeEnabled = enabled;
        testBotCount = Math.Clamp(botCount, 1, 7);
    }

    public void RecordDealerChat(string sender, string text, bool isParty)
    {
        if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(text))
            return;

        var isDealer = sender.Equals(gameManager.DealerIdentity, StringComparison.OrdinalIgnoreCase);
        var isCommand = text.StartsWith('>') || text.StartsWith('/');

        if (!isDealer)
        {
            if (!isParty || isCommand)
                return;
        }
        else if (IsGameAnnouncement(text))
        {
            return;
        }

        var displaySender = GetDisplayName(sender);
        var displayText = StripGameTag(text);
        if (streamerModeEnabled)
            displayText = ObscurePlayerMentions(displayText);

        dealerChatFeed.Add($"{displaySender}: {displayText}");
        if (dealerChatFeed.Count > 200)
            dealerChatFeed.RemoveAt(0);
    }

    private string ObscurePlayerMentions(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !streamerModeEnabled)
            return text;

        var result = text;
        var names = gameManager.GetAllPlayers()
            .Select(p => p.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length)
            .ToList();

        foreach (var name in names)
        {
            var alias = GetDisplayName(name);
            result = Regex.Replace(result, $@"\b{Regex.Escape(name)}\b", alias, RegexOptions.IgnoreCase);
        }

        return result;
    }

    public void DrawContents(bool dealerView, string? localPlayerName)
    {
        var vm = ResolveViewModel();
        LastRendered = vm;

        var showTableTab = true;
        if (ImGui.BeginTabBar("##mainTabsTop"))
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
                DrawDealerConfigTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        if (!showTableTab)
            return;

        DrawManagementControls(localPlayerName);

        if (!dealerView)
            DrawPartyStrip(vm);

        CasinoUI.DrawGameHeader(vm.GameTitle.ToUpperInvariant(), vm.GameStatus);

        if (tableService.ActiveGameType == GameType.Roulette)
            DrawRouletteVisual(vm, localPlayerName);
        else if (tableService.ActiveGameType == GameType.Craps)
            DrawCrapsVisual(vm);
        else if (tableService.ActiveGameType == GameType.ChocoboRacing)
            DrawChocoboRaceVisual(vm);
        else if (tableService.ActiveGameType is GameType.TexasHoldEm or GameType.Ultima)
            DrawOvalTableVisual(vm, tableService.ActiveGameType == GameType.TexasHoldEm ? "Texas Hold'Em Table" : "Ultima Table");

        var seats = vm.Seats;

        var dealers = seats.Where(s => s.IsDealer).ToList();
        var players = seats.Where(s => !s.IsDealer).ToList();
        var allPlayers = gameManager.GetAllPlayers().ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        if (dealerView)
        {
            foreach (var p in allPlayers.Values)
            {
                if (players.Any(s => s.PlayerName.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                players.Add(new PlayerSlotViewModel
                {
                    PlayerName = p.Name,
                    Bank = p.CurrentBank,
                    IsAfk = p.IsAfk,
                    IsKicked = p.IsKicked,
                    BetAmount = 0
                });
            }
        }

        if (ImGui.CollapsingHeader("Players", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (dealers.Count > 0 && tableService.ActiveGameType != GameType.ChocoboRacing)
            {
                foreach (var dealerRow in dealers)
                {
                    ImGui.PushID($"dealer-{dealerRow.PlayerName}");
                    ImGui.TextColored(new Vector4(1f, 0.75f, 0.2f, 1f), dealerRow.PlayerName);
                    CasinoUI.DrawCardTokens(dealerRow.Cards);
                    if (!string.IsNullOrWhiteSpace(dealerRow.ResultText))
                        ImGui.TextColored(new Vector4(1f, 0.9f, 0.2f, 1f), dealerRow.ResultText);
                    ImGui.Separator();
                    ImGui.PopID();
                }
            }

            foreach (var seat in players)
            {
                var state = allPlayers.TryGetValue(seat.PlayerName, out var playerState) ? playerState : null;
                var isKicked = state?.IsKicked ?? seat.IsKicked;
                var isAfk = !isKicked && (state?.IsAfk ?? seat.IsAfk);

                ImGui.PushID($"seat-{seat.PlayerName}");
                var displayName = GetDisplayName(seat.PlayerName);
                if (isKicked)
                    ImGui.TextDisabled($"{displayName} (KICKED)");
                else if (isAfk)
                    ImGui.TextDisabled($"{displayName} (AFK)");
                else
                    ImGui.TextUnformatted(displayName);
                ImGui.SameLine();

                if (dealerView)
                {
                    DrawBankEditorForSeat(seat.PlayerName);
                    DrawWorldEditorForSeat(seat.PlayerName);
                    if (!streamerModeEnabled)
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled($"Bet {seat.BetAmount}\uE049");
                        ImGui.SameLine();
                    }
                    else
                    {
                        ImGui.SameLine();
                    }

                    if (!isKicked)
                    {
                        if (ImGui.SmallButton($"{(isAfk ? "UNAFK" : "AFK")}##{seat.PlayerName}"))
                        {
                            if (gameManager.TrySetPlayerAfk(seat.PlayerName, !isAfk))
                                DealerBroadcastRequested?.Invoke($"[CASINO] {seat.PlayerName} is now {(!isAfk ? "AFK" : "back")}. ");
                        }

                        ImGui.SameLine();
                        if (ImGui.SmallButton($"Kick##{seat.PlayerName}"))
                        {
                            var result = gameManager.RouteCommand(seat.PlayerName, "LEAVE", Array.Empty<string>());
                            if (result.Success)
                                DealerBroadcastRequested?.Invoke($"[CASINO] {seat.PlayerName} has been removed.");
                        }
                    }
                    else
                    {
                        if (ImGui.SmallButton($"Remove##{seat.PlayerName}"))
                            _ = gameManager.RouteCommand(seat.PlayerName, "REMOVE", Array.Empty<string>());
                    }
                }
                else
                {
                    ImGui.TextDisabled($"Bank {seat.Bank}\uE049");
                    if (!streamerModeEnabled)
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled($"Bet {seat.BetAmount}\uE049");
                    }
                }

                ImGui.NewLine();

                if (seat.HandGroups.Count > 0)
                {
                    for (var i = 0; i < seat.HandGroups.Count; i++)
                    {
                        ImGui.PushID($"hand-{i}");
                        if (seat.HandGroups.Count > 1)
                        {
                            string handLabel = seat.ActiveHandIndex == i ? $"Hand {i + 1}*" : $"Hand {i + 1}";
                            ImGui.TextUnformatted(handLabel);
                        }

                        var cardsToDraw = seat.HandGroups[i];
                        if (dealerView
                            && tableService.ActiveGameType is GameType.TexasHoldEm or GameType.Ultima
                            && !CasinoUI.ShowOtherPlayerHands
                            && (string.IsNullOrWhiteSpace(localPlayerName) || !seat.PlayerName.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase)))
                        {
                            cardsToDraw = cardsToDraw.Count > 0
                                ? Enumerable.Repeat("[Hidden]", cardsToDraw.Count).ToList()
                                : ["[Hidden]", "[Hidden]"];
                        }

                        CasinoUI.DrawCardTokens(cardsToDraw);

                        if (i < seat.HandResultTexts.Count && !string.IsNullOrWhiteSpace(seat.HandResultTexts[i]))
                        {
                            var handResult = seat.HandResultTexts[i];
                            var color = handResult.Contains("BUST", StringComparison.OrdinalIgnoreCase)
                                ? new Vector4(1f, 0.3f, 0.3f, 1f)
                                : (handResult.Contains("BLACKJACK", StringComparison.OrdinalIgnoreCase)
                                    ? new Vector4(1f, 1f, 0.2f, 1f)
                                    : new Vector4(0.85f, 0.85f, 0.85f, 1f));
                            ImGui.TextColored(color, handResult);
                        }
                        ImGui.PopID();
                    }
                }
                else
                {
                    var cardsToDraw = seat.Cards;
                    if (dealerView
                        && tableService.ActiveGameType is GameType.TexasHoldEm or GameType.Ultima
                        && !CasinoUI.ShowOtherPlayerHands
                        && (string.IsNullOrWhiteSpace(localPlayerName) || !seat.PlayerName.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase)))
                    {
                        cardsToDraw = cardsToDraw.Count > 0
                            ? Enumerable.Repeat("[Hidden]", cardsToDraw.Count).ToList()
                            : ["[Hidden]", "[Hidden]"];
                    }
                    CasinoUI.DrawCardTokens(cardsToDraw);
                }

                if (!string.IsNullOrWhiteSpace(seat.ResultText) && (seat.HandGroups.Count == 0 || seat.HandResultTexts.Count == 0))
                {
                    var color = seat.ResultText.Contains("BUST", StringComparison.OrdinalIgnoreCase)
                        ? new Vector4(1f, 0.3f, 0.3f, 1f)
                        : (seat.ResultText.Contains("BLACKJACK", StringComparison.OrdinalIgnoreCase)
                            ? new Vector4(1f, 1f, 0.2f, 1f)
                            : new Vector4(0.85f, 0.85f, 0.85f, 1f));
                    ImGui.TextColored(color, seat.ResultText);
                }

                if (seat.IsActiveTurn)
                    ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), "YOUR TURN");

                ImGui.Separator();
                ImGui.PopID();
            }
        }

        if (!streamerModeEnabled)
            DrawActionInputs(tableService.ActiveGameType);

        if (dealerView && tableService.ActiveGameType != GameType.Ultima && !streamerModeEnabled)
        {
            var actorOptions = players.Select(p => p.PlayerName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (actorOptions.Count > 0)
            {
                // Auto-track active turn player for turn-based games
                var activeTurnSeat = players.FirstOrDefault(s => s.IsActiveTurn && !s.IsDealer);
                if (activeTurnSeat != null && !string.IsNullOrWhiteSpace(activeTurnSeat.PlayerName)
                    && tableService.ActiveGameType is GameType.Blackjack or GameType.TexasHoldEm or GameType.Craps)
                {
                    commandSenderPlayer = activeTurnSeat.PlayerName;
                }
                else if (string.IsNullOrWhiteSpace(commandSenderPlayer) || !actorOptions.Contains(commandSenderPlayer, StringComparer.OrdinalIgnoreCase))
                {
                    commandSenderPlayer = actorOptions[0];
                }

                ImGui.TextColored(new Vector4(0.8f, 0.95f, 1f, 1f), "ACT AS PLAYER");
                ImGui.SetNextItemWidth(220f);
                if (ImGui.BeginCombo("As Player", commandSenderPlayer))
                {
                    foreach (var name in actorOptions)
                    {
                        var selected = name.Equals(commandSenderPlayer, StringComparison.OrdinalIgnoreCase);
                        if (ImGui.Selectable(name, selected))
                            commandSenderPlayer = name;
                        if (selected) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
            }
        }

        if (ImGui.CollapsingHeader("Commands", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var orderedActions = GetOrderedActions(vm.GetActionButtons(), tableService.ActiveGameType);
            if (orderedActions.Count > 0)
            {
                var localSeat = players.FirstOrDefault(s => !string.IsNullOrWhiteSpace(localPlayerName) && s.PlayerName.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase));
                var localTurn = localSeat?.IsActiveTurn ?? false;

                foreach (var action in orderedActions)
                {
                    var cmd = action.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
                    var label = ToActionLabel(action);
                    bool isTurnSensitive = cmd is "HIT" or "STAND" or "DOUBLE" or "SPLIT" or "INSURANCE" or "PLAY";
                    bool enabled = dealerView || IsActionEnabled(cmd, tableService.ActiveGameType, vm, localTurn);

                    if ((!dealerView && isTurnSensitive && !localTurn) || !enabled)
                        ImGui.BeginDisabled();

                    if (ImGui.Button(label))
                    {
                        if (dealerView && cmd == "OPENBETS" && tableService.ActiveGameType == GameType.ChocoboRacing
                            && vm.GameStatus.Contains("Bets Open", StringComparison.OrdinalIgnoreCase))
                        {
                            ImGui.OpenPopup("ConfirmReopenBets");
                        }
                        else
                        {
                            var args = BuildArgsForCommand(cmd);
                            var sender = dealerView && !string.IsNullOrWhiteSpace(commandSenderPlayer)
                                ? commandSenderPlayer
                                : gameManager.DealerIdentity;
                            _ = gameManager.RouteCommand(sender, cmd, args);
                        }
                    }

                    if ((!dealerView && isTurnSensitive && !localTurn) || !enabled)
                        ImGui.EndDisabled();

                    ImGui.SameLine();
                }
                ImGui.NewLine();
            }
        }

        if (ImGui.BeginPopup("ConfirmReopenBets"))
        {
            ImGui.TextUnformatted("Bets are already open. Re-open with a new roster?");
            if (ImGui.Button("Yes, Re-open"))
            {
                var sender = dealerView && !string.IsNullOrWhiteSpace(commandSenderPlayer)
                    ? commandSenderPlayer
                    : gameManager.DealerIdentity;
                _ = gameManager.RouteCommand(sender, "OPENBETS", []);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (dealerView && streamerModeEnabled && ImGui.CollapsingHeader("Streamer Chat", ImGuiTreeNodeFlags.DefaultOpen))
            DrawDealerChatFeed();
    }

    private void DrawRouletteVisual(ICasinoViewModel vm, string? localPlayerName)
    {
        int? result = null;
        var betMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var seat in vm.Seats)
        {
            if (seat.IsDealer && seat.ResultText.StartsWith("Last:", StringComparison.OrdinalIgnoreCase))
            {
                var v = seat.ResultText[5..].Trim();
                var spaceIdx = v.IndexOf(' ');
                if (spaceIdx > 0) v = v[..spaceIdx];
                if (int.TryParse(v, out var n)) result = n;
            }

            if (seat.IsDealer) continue;
            foreach (var b in seat.HandResultTexts)
            {
                var parts = b.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) continue;
                var key = parts[0].Trim().ToUpperInvariant();
                if (!int.TryParse(parts[1], out var amt)) continue;
                if (!betMap.TryGetValue(key, out var list))
                    betMap[key] = list = new List<string>();
                var line = $"{seat.PlayerName} {amt}\uE049";
                if (!list.Contains(line, StringComparer.OrdinalIgnoreCase))
                    list.Add(line);
            }
        }

        var spinning = vm.GameStatus.Contains("Spinning", StringComparison.OrdinalIgnoreCase);
        if (spinning && !rouletteWasSpinning)
            rouletteSpinAnimStartUtc = DateTime.UtcNow;
        rouletteWasSpinning = spinning;

        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.2f, 1f), "Roulette Table");
        ImGui.Columns(2, "rouletteColumns", false);
        ImGui.SetColumnWidth(0, 220f);
        DrawRouletteWheelAnimated(result, spinning);
        ImGui.NextColumn();
        DrawRouletteGridWithBets(betMap, localPlayerName);
        ImGui.Columns(1);
        ImGui.Separator();
    }

    private void DrawRouletteWheelAnimated(int? result, bool spinning)
    {
        var draw = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        float cx = pos.X + 90f;
        float cy = pos.Y + 90f;
        float radius = 76f;
        float seg = (float)(2 * Math.PI / 37f);
        const float TwoPi = (float)(2 * Math.PI);

        float wheelRot;
        float ballAngle;
        var spinDurationMs = Math.Max(500.0, CasinoUI.RouletteSpinSeconds * 1000.0);

        if (spinning)
        {
            var elapsed = Math.Min((DateTime.UtcNow - rouletteSpinAnimStartUtc).TotalMilliseconds, spinDurationMs);
            var a = 0.95 * elapsed - 0.46 * elapsed * elapsed / spinDurationMs;
            wheelRot = (float)(a * 0.018) % TwoPi;
            ballAngle = -(float)(a * 0.04) % TwoPi;
        }
        else if (result.HasValue)
        {
            wheelRot = 0f;
            var slot = Array.IndexOf(RouletteWheelOrder, result.Value);
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

        for (int slot = 0; slot < 37; slot++)
        {
            int n = RouletteWheelOrder[slot];
            float a1 = slot * seg - (float)(Math.PI / 2) + wheelRot;
            float a2 = a1 + seg;
            uint col = n == 0 ? greenSeg : (Array.IndexOf(RouletteRedNumbers, n) >= 0 ? redSeg : blackSeg);

            var c = new Vector2(cx, cy);
            var p1 = new Vector2(cx + MathF.Cos(a1) * (radius - 3), cy + MathF.Sin(a1) * (radius - 3));
            var p2 = new Vector2(cx + MathF.Cos(a2) * (radius - 3), cy + MathF.Sin(a2) * (radius - 3));
            draw.AddTriangleFilled(c, p1, p2, col);
        }

        float trackR = radius - 8f;
        var ballPos = new Vector2(cx + MathF.Cos(ballAngle) * trackR, cy + MathF.Sin(ballAngle) * trackR);
        draw.AddCircleFilled(ballPos, 5f, 0xFFFFFFFFu, 12);

        ImGui.Dummy(new Vector2(190, 190));
    }

    private void DrawRouletteGridWithBets(Dictionary<string, List<string>> betMap, string? localPlayerName)
    {
        float cellW = 34f;
        float cellH = 22f;
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 12; c++)
            {
                var n = RouletteGrid[r, c];
                bool red = Array.IndexOf(RouletteRedNumbers, n) >= 0;
                var p0 = new Vector2(start.X + c * (cellW + 2), start.Y + r * (cellH + 2));
                var p1 = new Vector2(p0.X + cellW, p0.Y + cellH);
                uint bg = red
                    ? ImGui.GetColorU32(new Vector4(0.55f, 0.16f, 0.16f, 1f))
                    : ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.12f, 1f));
                draw.AddRectFilled(p0, p1, bg, 3f);
                draw.AddRect(p0, p1, ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)), 3f, ImDrawFlags.None, 1.5f);

                var txt = n.ToString();
                var ts = ImGui.CalcTextSize(txt);
                draw.AddText(new Vector2(p0.X + cellW / 2 - ts.X / 2, p0.Y + cellH / 2 - ts.Y / 2), 0xFFFFFFFFu, txt);
                var key = n.ToString();
                if (betMap.TryGetValue(key, out var lines) && lines.Count > 0)
                    draw.AddCircleFilled(new Vector2(p1.X - 6, p0.Y + 6), 4f, GetRouletteMarkerColor(lines), 10);

                if (ImGui.IsMouseHoveringRect(p0, p1))
                {
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        targetInput = key;
                        SubmitRouletteClickBet(key, localPlayerName);
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

            betMap.TryGetValue(key, out var lines);
            if (lines != null && lines.Count > 0)
                draw.AddCircleFilled(new Vector2(p1.X - 6, p0.Y + 6), 4f, GetRouletteMarkerColor(lines), 10);

            if (ImGui.IsMouseHoveringRect(p0, p1))
            {
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    targetInput = key;
                    SubmitRouletteClickBet(key, localPlayerName);
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

        ImGui.Dummy(new Vector2(450, 132));
    }

    private void SubmitRouletteClickBet(string target, string? localPlayerName)
    {
        var sender = !string.IsNullOrWhiteSpace(commandSenderPlayer)
            ? commandSenderPlayer
            : localPlayerName;

        if (string.IsNullOrWhiteSpace(sender))
            return;

        _ = gameManager.RouteCommand(sender, "BET", [wagerInput.ToString(), target]);
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

    private void DrawCrapsVisual(ICasinoViewModel vm)
    {
        var table = vm.Seats.FirstOrDefault(s => s.IsDealer && s.PlayerName.Equals("Table", StringComparison.OrdinalIgnoreCase));
        if (table == null) return;

        var betMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var seat in vm.Seats.Where(s => !s.IsDealer && s.HandResultTexts.Count > 0))
        {
            foreach (var entry in seat.HandResultTexts)
            {
                var parts = entry.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) continue;
                var target = parts[0].Trim().ToUpperInvariant();
                if (!int.TryParse(parts[1], out var amount) || amount <= 0) continue;

                if (!betMap.TryGetValue(target, out var lines))
                    betMap[target] = lines = new List<string>();

                var line = $"{seat.PlayerName} {amount}\uE049";
                if (!lines.Contains(line, StringComparer.OrdinalIgnoreCase))
                    lines.Add(line);
            }
        }

        int d1 = 1, d2 = 1, point = 0;
        bool rolling = false;

        if (!string.IsNullOrWhiteSpace(table.ResultText))
        {
            var parts = table.ResultText.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var dicePart = parts.FirstOrDefault(p => p.StartsWith("Dice:", StringComparison.OrdinalIgnoreCase));
            var pointPart = parts.FirstOrDefault(p => p.StartsWith("Point:", StringComparison.OrdinalIgnoreCase));
            var rolledPart = parts.FirstOrDefault(p => p.StartsWith("RolledAt:", StringComparison.OrdinalIgnoreCase));

            if (dicePart != null)
            {
                var v = dicePart[5..].Trim();
                var d = v.Split('-', StringSplitOptions.RemoveEmptyEntries);
                if (d.Length == 2)
                {
                    int.TryParse(d[0], out d1);
                    int.TryParse(d[1], out d2);
                }
            }

            if (pointPart != null)
            {
                var ptxt = pointPart[6..].Trim();
                if (!ptxt.Equals("OFF", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(ptxt, out point);
            }

            if (rolledPart != null && long.TryParse(rolledPart[9..].Trim(), out var ticks))
                rolling = DateTime.UtcNow.Ticks - ticks < TimeSpan.FromMilliseconds(Math.Max(200.0, CasinoUI.CrapsRollDelaySeconds * 1000.0)).Ticks;
        }

        if (rolling)
        {
            var phase = (int)((DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond) / 70 % 6) + 1;
            d1 = phase;
            d2 = ((phase + 2) % 6) + 1;
        }

        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.2f, 1f), "Craps Table");
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        float w = 540f;
        float h = 165f;
        var p1 = new Vector2(pos.X + w, pos.Y + h);
        dl.AddRectFilled(pos, p1, 0xFF114422u, 8f);
        dl.AddRect(pos, p1, 0xFF88AA88u, 8f, ImDrawFlags.None, 2f);

        DrawCrapsZone(dl, pos, new Vector2(12, 10), new Vector2(190, 24), "PASS", "Pass line (1:1).", betMap);
        DrawCrapsZone(dl, pos, new Vector2(12, 38), new Vector2(190, 24), "DONTPASS", "Don't pass (1:1).", betMap);
        DrawCrapsZone(dl, pos, new Vector2(12, 66), new Vector2(190, 24), "FIELD", "Field (1:1, 2/12 pay 2:1).", betMap);
        DrawCrapsZone(dl, pos, new Vector2(12, 94), new Vector2(190, 24), "SEVEN", "Seven (5:1).", betMap);
        DrawCrapsZone(dl, pos, new Vector2(12, 122), new Vector2(190, 24), "ANYCRAPS", "Any Craps (8:1).", betMap);
        DrawCrapsZone(dl, pos, new Vector2(210, 94), new Vector2(80, 24), "PLACE4", "Place 4 (9:5).", betMap);
        DrawCrapsZone(dl, pos, new Vector2(296, 94), new Vector2(80, 24), "PLACE5", "Place 5 (7:5).", betMap);
        DrawCrapsZone(dl, pos, new Vector2(382, 94), new Vector2(80, 24), "PLACE6", "Place 6 (7:6).", betMap);
        DrawCrapsZone(dl, pos, new Vector2(210, 122), new Vector2(80, 24), "PLACE8", "Place 8 (7:6).", betMap);
        DrawCrapsZone(dl, pos, new Vector2(296, 122), new Vector2(80, 24), "PLACE9", "Place 9 (7:5).", betMap);
        DrawCrapsZone(dl, pos, new Vector2(382, 122), new Vector2(80, 24), "PLACE10", "Place 10 (9:5).", betMap);

        var puckPos = new Vector2(pos.X + 238, pos.Y + 30);
        dl.AddCircleFilled(puckPos, 15f, point == 0 ? 0xFF333333u : 0xFFAA2222u, 16);
        dl.AddCircle(puckPos, 15f, 0xFFCCCCCCu, 16, 1.5f);
        var puckText = point == 0 ? "OFF" : "ON";
        var puckSize = ImGui.CalcTextSize(puckText);
        dl.AddText(new Vector2(puckPos.X - puckSize.X / 2, puckPos.Y - puckSize.Y / 2), 0xFFFFFFFFu, puckText);

        var pointTxt = point == 0 ? "Point: OFF" : $"Point: {point}";
        dl.AddText(new Vector2(pos.X + 220, pos.Y + 55), 0xFFEEDDAAu, pointTxt);

        var dicePos = new Vector2(pos.X + 340, pos.Y + 15f);
        DrawDieFace(dl, dicePos, 52, d1, rolling);
        DrawDieFace(dl, new Vector2(dicePos.X + 66, dicePos.Y), 52, d2, rolling);

        ImGui.Dummy(new Vector2(w, h));
        ImGui.Separator();
    }

    private void DrawCrapsZone(ImDrawListPtr dl, Vector2 tablePos, Vector2 relPos, Vector2 size, string title, string tooltip, Dictionary<string, List<string>> betMap)
    {
        var p0 = tablePos + relPos;
        var p1 = p0 + size;
        dl.AddRect(p0, p1, 0x66FFFFFFu, 3f);
        dl.AddText(new Vector2(p0.X + 6, p0.Y + 4), 0xFFEEDDAAu, title);

        betMap.TryGetValue(title, out var lines);
        if (lines != null && lines.Count > 0)
            dl.AddCircleFilled(new Vector2(p1.X - 6, p0.Y + 6), 4f, GetRouletteMarkerColor(lines), 10);

        if (ImGui.IsMouseHoveringRect(p0, p1))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(title);
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

    private uint GetRouletteMarkerColor(List<string> lines)
    {
        var localName = gameManager.DealerIdentity;
        var hasLocalBet = !string.IsNullOrWhiteSpace(localName)
            && lines.Any(line => line.StartsWith($"{localName} ", StringComparison.OrdinalIgnoreCase));

        return ImGui.GetColorU32(hasLocalBet
            ? new Vector4(1f, 0.9f, 0.25f, 0.9f)
            : new Vector4(0.25f, 0.9f, 0.4f, 0.85f));
    }

    private static bool IsActionEnabled(string cmd, GameType game, ICasinoViewModel vm, bool localTurn)
    {
        return game switch
        {
            GameType.Roulette when cmd == "SPIN" => vm.Seats.Any(s => s.BetAmount > 0 && !s.IsDealer),
            GameType.Craps when cmd == "ROLL" => vm.Seats.Any(s => s.BetAmount > 0 && !s.IsDealer),
            GameType.Baccarat when cmd == "DEAL" => vm.Seats.Any(s => s.BetAmount > 0 && !s.IsDealer),
            GameType.ChocoboRacing when cmd == "START" => vm.Seats.Any(s => s.BetAmount > 0 && !s.IsDealer),
            GameType.Ultima when cmd == "PLAY" => localTurn,
            _ => true
        };
    }

    private void DrawPartyStrip(ICasinoViewModel vm)
    {
        ImGui.TextColored(new Vector4(0.45f, 1f, 1f, 1f), "PARTY");
        ImGui.SameLine();
        ImGui.TextDisabled("(★ = active turn)");

        foreach (var seat in vm.Seats.Where(s => !s.IsDealer))
        {
            var iconColor = seat.IsActiveTurn ? new Vector4(1f, 0.85f, 0.2f, 1f) : new Vector4(0.35f, 0.35f, 0.35f, 1f);
            ImGui.TextColored(iconColor, seat.IsActiveTurn ? "★" : "●");
            ImGui.SameLine();
            ImGui.TextUnformatted(seat.PlayerName);
            ImGui.SameLine(250f);
            ImGui.TextDisabled($"{seat.Bank}\uE049");
        }

        ImGui.Separator();
    }

    private void DrawManagementControls(string? localPlayerName)
    {
        selectedGame = tableService.ActiveGameType == GameType.None ? selectedGame : tableService.ActiveGameType;

        if (!string.IsNullOrWhiteSpace(localPlayerName) && string.IsNullOrWhiteSpace(addPlayerName))
            addPlayerName = localPlayerName;
        if (string.IsNullOrWhiteSpace(addPlayerWorld))
            addPlayerWorld = "Unknown";

        if (!dealerConfirmed)
        {
            ImGui.TextWrapped("You are about to operate as the table dealer. This controls the game for all players.");
            if (ImGui.Button("I am the Dealer"))
                dealerConfirmed = true;
            ImGui.Separator();
            return;
        }

        if (!ImGui.CollapsingHeader("Table Controls", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (ImGui.BeginCombo("Game", selectedGame.ToString()))
        {
            foreach (var game in Enum.GetValues<GameType>())
            {
                bool isSelected = game == selectedGame;
                if (ImGui.Selectable(game.ToString(), isSelected))
                {
                    selectedGame = game;
                    if (selectedGame == GameType.None)
                        FactoryResetRequested?.Invoke();
                    else
                        gameManager.Activate(selectedGame);
                }

                if (isSelected) ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(180f);
        ImGui.InputText("Player Name", ref addPlayerName, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        ImGui.InputText("World", ref addPlayerWorld, 32);

        if (ImGui.Button("Add Player") && !string.IsNullOrWhiteSpace(addPlayerName))
            _ = gameManager.RouteCommand(addPlayerName.Trim(), "JOIN", [string.IsNullOrWhiteSpace(addPlayerWorld) ? "Unknown" : addPlayerWorld.Trim()]);

        ImGui.SameLine();
        if (ImGui.Button("Add Me") && !string.IsNullOrWhiteSpace(localPlayerName))
            _ = gameManager.RouteCommand(localPlayerName, "JOIN", [string.IsNullOrWhiteSpace(addPlayerWorld) ? "Unknown" : addPlayerWorld.Trim()]);

        ImGui.SameLine();
        if (ImGui.Button("Add Party"))
            AddPartyRequested?.Invoke();

        ImGui.SameLine();
        if (ImGui.Button("Add Target") && !string.IsNullOrWhiteSpace(addPlayerName))
            targetInput = addPlayerName.Trim();

        ImGui.Separator();
    }

    private void DrawActionInputs(GameType game)
    {
        if (game is GameType.Ultima or GameType.None)
            return;

        ImGui.SetNextItemWidth(120f);
        ImGui.InputInt("Amount", ref wagerInput);
        if (wagerInput < 1) wagerInput = 1;

        if (game == GameType.Blackjack)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80f);
            ImGui.InputText("Rule", ref ruleInput, 8);
        }

        var options = GetTargetPresets(game);
        if (options.Length > 0)
        {
            if (targetPresetIndex < 0 || targetPresetIndex >= options.Length)
                targetPresetIndex = 0;

            if (string.IsNullOrWhiteSpace(targetInput) || !options.Contains(targetInput, StringComparer.OrdinalIgnoreCase))
                targetInput = options[targetPresetIndex];

            ImGui.SameLine();
            ImGui.SetNextItemWidth(160f);
            if (ImGui.BeginCombo("Target", targetInput))
            {
                for (var i = 0; i < options.Length; i++)
                {
                    var selected = string.Equals(options[i], targetInput, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(options[i], selected))
                    {
                        targetPresetIndex = i;
                        targetInput = options[i];
                    }
                    if (selected) ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }
        }
    }

    private string[] BuildArgsForCommand(string cmd)
    {
        var active = tableService.ActiveGameType;
        var target = string.IsNullOrWhiteSpace(targetInput) ? string.Empty : targetInput.Trim();

        if (cmd == "BET")
        {
            return active switch
            {
                GameType.Blackjack => [wagerInput.ToString()],
                GameType.Roulette => [wagerInput.ToString(), string.IsNullOrWhiteSpace(target) ? "RED" : target.ToUpperInvariant()],
                GameType.Craps => [wagerInput.ToString(), string.IsNullOrWhiteSpace(target) ? "PASS" : target.ToUpperInvariant()],
                GameType.Baccarat => [wagerInput.ToString(), string.IsNullOrWhiteSpace(target) ? "PLAYER" : target.ToUpperInvariant()],
                GameType.ChocoboRacing => string.IsNullOrWhiteSpace(target) ? [wagerInput.ToString()] : [wagerInput.ToString(), target],
                GameType.Ultima => [wagerInput.ToString(), string.IsNullOrWhiteSpace(target) ? "WATER" : target.ToUpperInvariant()],
                GameType.TexasHoldEm => [wagerInput.ToString()],
                _ => [wagerInput.ToString()]
            };
        }

        return cmd switch
        {
            "RULE" => [string.IsNullOrWhiteSpace(ruleInput) ? "H17" : ruleInput.Trim().ToUpperInvariant()],
            "RULETOGGLE" => Array.Empty<string>(),
            "RAISE" => [wagerInput.ToString()],
            "ALL" => ["IN"],
            "PLAY" => string.IsNullOrWhiteSpace(target) ? Array.Empty<string>() : [target.Trim().ToUpperInvariant()],
            _ => Array.Empty<string>()
        };
    }

    private string[] GetTargetPresets(GameType game)
    {
        return game switch
        {
            GameType.Roulette => ["RED", "BLACK", "EVEN", "ODD", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20", "21", "22", "23", "24", "25", "26", "27", "28", "29", "30", "31", "32", "33", "34", "35", "36"],
            GameType.Craps => ["PASS", "DONTPASS", "FIELD", "SEVEN", "ANYCRAPS", "PLACE4", "PLACE5", "PLACE6", "PLACE8", "PLACE9", "PLACE10"],
            GameType.Baccarat => ["PLAYER", "BANKER", "TIE"],
            GameType.ChocoboRacing => LastRendered?.Seats
                .Where(s => s.IsDealer && !string.IsNullOrWhiteSpace(s.PlayerName))
                .Select(s => s.PlayerName)
                .Where(n => !n.Equals("Table", StringComparison.OrdinalIgnoreCase) && !n.Equals("Wheel", StringComparison.OrdinalIgnoreCase) && !n.Equals("Dealer", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>(),
            GameType.Ultima => ["WATER", "FIRE", "GRASS", "LIGHT"],
            _ => Array.Empty<string>()
        };
    }

    private static string ToActionLabel(string action)
    {
        var cmd = action.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
        return cmd switch
        {
            "RULETOGGLE" => "H17/S17",
            _ => cmd
        };
    }

    private static List<string> GetOrderedActions(IReadOnlyList<string> actions, GameType game)
    {
        string[] preferred = game switch
        {
            GameType.Blackjack => ["BET", "DEAL", "HIT", "STAND", "DOUBLE", "SPLIT", "INSURANCE", "RULETOGGLE"],
            GameType.Roulette => ["BET", "SPIN"],
            GameType.Craps => ["BET", "ROLL"],
            GameType.Baccarat => ["BET", "DEAL"],
            GameType.TexasHoldEm => ["BET", "DEAL", "CHECK", "CALL", "RAISE", "FOLD", "ALL"],
            GameType.ChocoboRacing => ["OPENBETS", "BET", "START"],
            GameType.Ultima => ["DEAL", "PLAY"],
            _ => []
        };

        var existing = actions.Select(a => a.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant()).Distinct().ToHashSet();
        var ordered = preferred.Where(existing.Contains).ToList();
        ordered.AddRange(existing.Where(e => !ordered.Contains(e)));
        return ordered;
    }

    private ICasinoViewModel ResolveViewModel()
    {
        if (tableService.ActiveEngine is BaseEngine engine)
            return engine.GetViewModel();

        return new EmptyViewModel(tableService.ActiveGameType);
    }

    private sealed class EmptyViewModel : BaseViewModel
    {
        public EmptyViewModel(GameType game)
        {
            GameTitle = game == GameType.None ? "Casino" : game.ToString();
            GameStatus = "No active table";
        }
    }

    private void DrawOvalTableVisual(ICasinoViewModel vm, string label)
    {
        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.2f, 1f), label);
        var draw = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();

        float w = 520f;
        float h = 190f;
        var center = new Vector2(pos.X + w / 2f, pos.Y + h / 2f);
        var tableColor = ImGui.GetColorU32(new Vector4(0.1f, 0.35f, 0.2f, 1f));
        var borderColor = ImGui.GetColorU32(new Vector4(0.75f, 0.65f, 0.3f, 1f));

        var tableMin = new Vector2(center.X - 180f, center.Y - 70f);
        var tableMax = new Vector2(center.X + 180f, center.Y + 70f);
        draw.AddRectFilled(tableMin, tableMax, tableColor, 70f);
        draw.AddRect(tableMin, tableMax, borderColor, 70f, ImDrawFlags.None, 2.2f);

        var boardSeat = vm.Seats.FirstOrDefault(s => s.IsDealer && s.PlayerName.Equals("Board", StringComparison.OrdinalIgnoreCase));
        if (boardSeat != null && tableService.ActiveGameType == GameType.TexasHoldEm)
        {
            if (!string.IsNullOrWhiteSpace(boardSeat.ResultText))
                draw.AddText(new Vector2(center.X - 54f, center.Y - 40f), ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.8f, 1f)), boardSeat.ResultText);

            var cursor = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(new Vector2(center.X - 120f, center.Y - 16f));
            CasinoUI.DrawCardTokens(boardSeat.Cards);
            ImGui.SetCursorScreenPos(cursor);
        }

        var tableSeat = vm.Seats.FirstOrDefault(s => s.IsDealer && s.PlayerName.Equals("Table", StringComparison.OrdinalIgnoreCase));
        if (tableSeat != null && tableService.ActiveGameType == GameType.Ultima)
        {
            if (!string.IsNullOrWhiteSpace(tableSeat.ResultText))
                draw.AddText(new Vector2(center.X - 80f, center.Y - 38f), ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.8f, 1f)), tableSeat.ResultText);

            if (tableSeat.Cards.Count > 0)
            {
                var cursor = ImGui.GetCursorScreenPos();
                ImGui.SetCursorScreenPos(new Vector2(center.X - 20f, center.Y - 14f));
                CasinoUI.DrawCardTokens(tableSeat.Cards);
                ImGui.SetCursorScreenPos(cursor);
            }
        }

        var seats = vm.Seats.Where(s => !s.IsDealer).ToList();
        if (seats.Count > 0)
        {
            for (var i = 0; i < seats.Count; i++)
            {
                var a = (float)(Math.PI * 2 * i / seats.Count) - (float)Math.PI / 2;
                var p = new Vector2(center.X + MathF.Cos(a) * 220f, center.Y + MathF.Sin(a) * 90f);

                var role = seats[i].HandResultTexts.FirstOrDefault(t => t.StartsWith("ROLE:", StringComparison.OrdinalIgnoreCase))?.Split(':')[1] ?? string.Empty;
                var color = role switch
                {
                    "DEALER" => new Vector4(1f, 0.88f, 0.25f, 1f),
                    "SB" => new Vector4(0.35f, 0.85f, 1f, 1f),
                    "BB" => new Vector4(0.95f, 0.45f, 0.45f, 1f),
                    _ => (seats[i].IsActiveTurn ? new Vector4(1f, 0.88f, 0.25f, 1f) : new Vector4(0.82f, 0.82f, 0.82f, 1f))
                };

                draw.AddCircleFilled(p, 10f, ImGui.GetColorU32(color), 16);
                var isUltima = tableService.ActiveGameType == GameType.Ultima;
                var seatDisplayName = GetDisplayName(seats[i].PlayerName);
                var seatLabel = isUltima
                    ? $"{seatDisplayName} ({seats[i].Cards.Count})"
                    : $"{seatDisplayName} {seats[i].Bank}\uE049";
                draw.AddText(new Vector2(p.X + 14f, p.Y - 9f), ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.95f, 1f)), seatLabel);

                if (ImGui.IsMouseHoveringRect(new Vector2(p.X - 12, p.Y - 12), new Vector2(p.X + 12, p.Y + 12)))
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(seats[i].PlayerName);
                    ImGui.TextDisabled(string.IsNullOrWhiteSpace(role) ? "Role: Player" : $"Role: {role}");
                    ImGui.TextDisabled($"Bank: {seats[i].Bank}\uE049");
                    ImGui.TextDisabled($"Committed: {seats[i].BetAmount}\uE049");
                    ImGui.EndTooltip();
                }
            }
        }

        ImGui.Dummy(new Vector2(w, h));
        ImGui.Separator();
    }

    private void DrawChocoboRaceVisual(ICasinoViewModel vm)
    {
        var racers = vm.Seats.Where(s => s.IsDealer).ToList();
        if (racers.Count == 0)
        {
            ImGui.TextDisabled("Waiting for dealer to open bets...");
            ImGui.Separator();
            return;
        }

        ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), "CHOCOBO ROSTER");

        if (ImGui.BeginTable("##chocoboRoster", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Racer", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Stats", ImGuiTableColumnFlags.WidthFixed, 210f);
            ImGui.TableSetupColumn("Odds", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableHeadersRow();

            foreach (var r in racers)
            {
                var stats = r.HandResultTexts.Count > 0 ? r.HandResultTexts[0] : string.Empty;
                var odds = r.HandResultTexts.Count > 1 ? r.HandResultTexts[1] : (string.IsNullOrWhiteSpace(r.ResultText) ? "-" : r.ResultText.Replace("Odds ", string.Empty));

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(r.PlayerName);

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(stats) ? "-" : stats);

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(odds);
            }

            ImGui.EndTable();
        }

        var hash = ExtractHash(vm.GameStatus);
        var racing = vm.GameStatus.Contains("Race in progress", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(hash))
            DrawChocoboLanes(hash, racers.Select(r => r.PlayerName).ToList(), racing);

        ImGui.Separator();
    }

    private static string ExtractHash(string status)
    {
        var marker = "Hash:";
        var idx = status.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return string.Empty;
        return status[(idx + marker.Length)..].Trim();
    }

    private DateTime chocoboAnimStartUtc = DateTime.MinValue;
    private string lastChocoboHash = string.Empty;
    private readonly Dictionary<string, float[]> chocoboTrackCache = new(StringComparer.OrdinalIgnoreCase);

    private void DrawChocoboLanes(string hash, List<string> racers, bool racing)
    {
        if (!string.Equals(hash, lastChocoboHash, StringComparison.Ordinal))
        {
            lastChocoboHash = hash;
            chocoboAnimStartUtc = DateTime.UtcNow;
            chocoboTrackCache.Clear();
        }

        var podium = DecodePodium(hash, racers);
        var elapsed = (DateTime.UtcNow - chocoboAnimStartUtc).TotalMilliseconds;
        var frac = racing ? Math.Clamp((float)(elapsed / 30000.0), 0f, 1f) : 1f;

        var checkpoints = BuildChocoboCheckpointMap(hash, racers, podium);
        var segFloat = frac * 6f;
        var segLow = Math.Clamp((int)MathF.Floor(segFloat), 0, 5);
        var segT = segFloat - segLow;
        // var easedT = segT * segT * (3f - 2f * segT);

        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        float w = Math.Max(ImGui.GetContentRegionAvail().X, 300f);
        float laneH = 22f;
        int n = racers.Count;
        float h = n * laneH + 4f;

        var trackBg = ImGui.GetColorU32(new Vector4(0.12f, 0.10f, 0.06f, 0.9f));
        var laneLine = ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.2f, 0.4f));
        var finishLine = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.3f));
        draw.AddRectFilled(origin, origin + new Vector2(w, h), trackBg, 4f);
        draw.AddLine(origin + new Vector2(w - 2, 0), origin + new Vector2(w - 2, h), finishLine, 2f);

        float labelW = 140f;
        float trackW = w - labelW - 10f;

        for (int i = 0; i < n; i++)
        {
            float y = origin.Y + i * laneH + 2f;
            if (i > 0)
                draw.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + w, y), laneLine);

            var name = racers[i];
            if (!checkpoints.TryGetValue(name, out var cp) || cp.Length < 7)
                continue;

            var pos = cp[segLow] + (cp[segLow + 1] - cp[segLow]) * segT;
            float dotX = origin.X + labelW + pos * trackW;
            float dotY = y + laneH * 0.5f;

            var label = name.Length > 18 ? name[..18] : name;
            draw.AddText(new Vector2(origin.X + 4, y + 2), ImGui.GetColorU32(new Vector4(0.75f, 0.75f, 0.6f, 1f)), label);
            draw.AddCircleFilled(new Vector2(dotX, dotY), 6f, ImGui.GetColorU32(new Vector4(1f, 0.84f, 0f, 1f)));
            draw.AddCircle(new Vector2(dotX, dotY), 7.5f, ImGui.GetColorU32(new Vector4(1f, 0.94f, 0.4f, 0.4f)), 12, 1.2f);
        }

        ImGui.Dummy(new Vector2(w, h));
    }

    private Dictionary<string, float[]> BuildChocoboCheckpointMap(string hash, List<string> racers, List<string> podium)
    {
        var profiles = ChocoboRacingModule.DecodeRaceProfiles(hash);

        foreach (var racer in racers)
        {
            if (chocoboTrackCache.ContainsKey(racer))
                continue;

            var idx = racers.FindIndex(r => r.Equals(racer, StringComparison.OrdinalIgnoreCase));
            var rank = podium.FindIndex(p => p.Equals(racer, StringComparison.OrdinalIgnoreCase));
            var profile = idx >= 0 && idx < profiles.Count
                ? profiles[idx]
                : new ChocoboRacingModule.RaceProfile(75, 75, 0, 0);
            var pace = ChocoboRacingModule.BuildSegmentPace(hash, profile, Math.Max(0, idx));

            chocoboTrackCache[racer] = BuildRacerCheckpoints(hash, racer, rank, pace);
        }

        return chocoboTrackCache;
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

    private static List<string> DecodePodium(string hash, List<string> racers)
    {
        return ChocoboRacingModule.BuildPodiumFromHash(hash, racers);
    }

    private void DrawWorldEditorForSeat(string playerName)
    {
        if (!worldEditByPlayer.TryGetValue(playerName, out var world) || string.IsNullOrWhiteSpace(world))
            world = gameManager.GetPlayerWorld(playerName) ?? "Unknown";

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        if (ImGui.InputText($"##world_{playerName}", ref world, 32))
            worldEditByPlayer[playerName] = world;

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var w = string.IsNullOrWhiteSpace(world) ? "Unknown" : world.Trim();
            worldEditByPlayer[playerName] = w;
            _ = gameManager.RouteCommand(playerName, "JOIN", [w]);
        }
    }

    private void DrawBankEditorForSeat(string playerName)
    {
        var actualBank = gameManager.GetPlayerBank(playerName) ?? 0;
        if (!bankEditByPlayer.TryGetValue(playerName, out var bank))
        {
            bank = actualBank;
            bankEditByPlayer[playerName] = bank;
        }

        if (!bankEditActivePlayers.Contains(playerName))
        {
            bank = actualBank;
            bankEditByPlayer[playerName] = bank;
        }

        ImGui.SetNextItemWidth(90f);
        if (ImGui.InputInt($"##bank_{playerName}", ref bank, 0, 0))
            bankEditByPlayer[playerName] = Math.Max(0, bank);

        var isActive = ImGui.IsItemActive();
        if (isActive)
            bankEditActivePlayers.Add(playerName);
        else
            bankEditActivePlayers.Remove(playerName);

        var commitFromEdit = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SameLine();
        if (ImGui.SmallButton($"Set##bankset_{playerName}") || commitFromEdit)
        {
            bank = Math.Max(0, bank);
            bankEditByPlayer[playerName] = bank;
            gameManager.TrySetPlayerBank(playerName, bank);
        }
    }

    private void DrawDealerConfigTab()
    {
        ImGui.TextColored(new Vector4(0.8f, 0.95f, 1f, 1f), "CONFIG");
        ImGui.Separator();

        ImGui.TextUnformatted("Global");
        ImGui.SetNextItemWidth(260f);
        ImGui.SliderFloat("Turn Time Limit (sec)", ref CasinoUI.GlobalTurnTimeLimitSeconds, 10f, 180f, "%.0f");

        ImGui.SetNextItemWidth(260f);
        ImGui.InputInt("Global Min Bet", ref CasinoUI.GlobalMinBet, 1, 10);
        ImGui.SetNextItemWidth(260f);
        ImGui.InputInt("Global Max Bet", ref CasinoUI.GlobalMaxBet, 10, 100);
        CasinoUI.GlobalMinBet = Math.Max(1, CasinoUI.GlobalMinBet);
        CasinoUI.GlobalMaxBet = Math.Max(CasinoUI.GlobalMinBet, CasinoUI.GlobalMaxBet);

        ImGui.Checkbox("Random Dealer Chat Delay", ref CasinoUI.RandomizeDealerChatDelay);
        ImGui.SetNextItemWidth(260f);
        ImGui.InputInt("Dealer Chat Delay Min (ms)", ref CasinoUI.DealerChatDelayMinMs, 100, 250);
        ImGui.SetNextItemWidth(260f);
        ImGui.InputInt("Dealer Chat Delay Max (ms)", ref CasinoUI.DealerChatDelayMaxMs, 100, 250);
        CasinoUI.DealerChatDelayMinMs = Math.Max(0, CasinoUI.DealerChatDelayMinMs);
        CasinoUI.DealerChatDelayMaxMs = Math.Max(CasinoUI.DealerChatDelayMinMs, CasinoUI.DealerChatDelayMaxMs);
        ImGui.TextDisabled("Default random delay: 750-1500ms between dealer messages.");
        ImGui.Checkbox("Natural Chat Output (strip [GAME] tags)", ref CasinoUI.NaturalChatOutput);
        ImGui.TextDisabled("Player-view parsing supports tagged and natural output.");

        ImGui.Separator();

        // Game-specific options — only show for the currently selected game
        var activeGame = tableService.ActiveGameType;

        if (activeGame == GameType.Craps)
        {
            ImGui.TextUnformatted("Craps");
            ImGui.SetNextItemWidth(260f);
            ImGui.SliderFloat("Betting Duration (sec)", ref CasinoUI.CrapsBettingDurationSeconds, 1f, 30f, "%.0f");
            ImGui.SetNextItemWidth(260f);
            ImGui.SliderFloat("Roll Delay (sec)", ref CasinoUI.CrapsRollDelaySeconds, 0.2f, 4f, "%.1f");
            ImGui.Separator();
        }

        if (activeGame == GameType.Blackjack)
        {
            ImGui.TextUnformatted("Blackjack");
            ImGui.SetNextItemWidth(260f);
            ImGui.SliderInt("Blackjack Payout Numerator", ref CasinoUI.BlackjackNaturalPayoutNumerator, 1, 5);
            ImGui.SetNextItemWidth(260f);
            ImGui.SliderInt("Blackjack Payout Denominator", ref CasinoUI.BlackjackNaturalPayoutDenominator, 1, 5);
            if (CasinoUI.BlackjackNaturalPayoutDenominator <= 0)
                CasinoUI.BlackjackNaturalPayoutDenominator = 1;
            ImGui.TextDisabled($"Natural payout: {CasinoUI.BlackjackNaturalPayoutNumerator}:{CasinoUI.BlackjackNaturalPayoutDenominator} (default 3:2)");
            ImGui.Separator();
        }

        if (activeGame == GameType.Baccarat)
        {
            ImGui.TextUnformatted("Baccarat");
            ImGui.Checkbox("Banker 5% Commission", ref CasinoUI.BaccaratCommissionEnabled);
            ImGui.Separator();
        }

        if (activeGame == GameType.TexasHoldEm)
        {
            ImGui.TextUnformatted("Poker");
            ImGui.Checkbox("Auto-play next hand", ref CasinoUI.PokerAutoPlayEnabled);
            ImGui.SetNextItemWidth(260f);
            ImGui.InputInt("Small Blind", ref CasinoUI.PokerSmallBlind, 10, 50);
            ImGui.SetNextItemWidth(260f);
            ImGui.InputInt("Big Blind", ref CasinoUI.PokerBigBlind, 10, 50);
            CasinoUI.PokerSmallBlind = Math.Max(1, CasinoUI.PokerSmallBlind);
            CasinoUI.PokerBigBlind = Math.Max(1, CasinoUI.PokerBigBlind);
            if (CasinoUI.PokerBigBlind < CasinoUI.PokerSmallBlind)
                CasinoUI.PokerBigBlind = CasinoUI.PokerSmallBlind;
            ImGui.TextDisabled("Defaults: Buy-in 10,000, SB 50, BB 100 (BB = 1/100 of buy-in)");
            ImGui.Separator();
        }

        ImGui.TextUnformatted("Cards");

        var sizeIndex = (int)CasinoUI.CardSize;
        if (ImGui.Combo("Card Size", ref sizeIndex, ["Big Cards", "Little Cards", "Mini Cards"], 3))
            CasinoUI.CardSize = (CardDisplaySize)Math.Clamp(sizeIndex, 0, 2);

        var styleIndex = (int)CasinoUI.CardStyle;
        if (ImGui.Combo("Card Style", ref styleIndex, ["Graphic Cards", "Text Cards"], 2))
            CasinoUI.CardStyle = (CardDisplayStyle)Math.Clamp(styleIndex,  0, 1);

        var themeIndex = (int)CasinoUI.CardTheme;
        if (ImGui.Combo("Card Theme", ref themeIndex, ["Light Mode Cards", "Dark Mode Cards"], 2))
            CasinoUI.CardTheme = (CardDisplayTheme)Math.Clamp(themeIndex, 0, 1);

        ImGui.Separator();

        ImGui.TextUnformatted("Test Mode");
        var testEnabled = testModeEnabled;
        if (ImGui.Checkbox("Enable AI Bots", ref testEnabled))
        {
            testModeEnabled = testEnabled;
            TestModeRequested?.Invoke(testModeEnabled, testBotCount);
        }

        if (testModeEnabled)
        {
            ImGui.SameLine();
            var bots = testBotCount;
            ImGui.SetNextItemWidth(170f);
            if (ImGui.SliderInt("Bot Count", ref bots, 1, 7))
            {
                testBotCount = Math.Clamp(bots, 1, 7);
                TestModeRequested?.Invoke(testModeEnabled, testBotCount);
            }
        }

        ImGui.Separator();

        ImGui.TextUnformatted("Streamer Mode");
        var wasStreamerMode = streamerModeEnabled;
        if (ImGui.Checkbox("Obscure Player Names", ref streamerModeEnabled))
        {
            if (streamerModeEnabled && !wasStreamerMode)
                dealerChatFeed.Clear();
        }
        ImGui.TextDisabled("Replaces player names with random aliases in the dealer window.");

        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("Factory Reset"))
            FactoryResetRequested?.Invoke();
    }

    private string GetDisplayName(string playerName)
    {
        if (!streamerModeEnabled)
            return playerName;

        if (streamerNameMap.TryGetValue(playerName, out var alias))
            return alias;

        string[] firstNames = ["Ruby", "Jade", "Echo", "Nova", "Zephyr", "Cinder", "Pearl", "Raven", "Storm", "Blaze", "Luna", "Ash", "Coral", "Fox", "Ember"];
        string[] lastNames = ["Stone", "Vale", "Creek", "Frost", "Spark", "Thorn", "Bloom", "Shade", "Drift", "Wing", "Fern", "Dust", "Ray", "Brook", "Gale"];
        alias = $"{firstNames[streamerRng.Next(firstNames.Length)]} {lastNames[streamerRng.Next(lastNames.Length)]}";
        streamerNameMap[playerName] = alias;
        return alias;
    }

    private static bool IsGameAnnouncement(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.StartsWith("[", StringComparison.Ordinal))
            return true;

        var plain = StripGameTag(text);
        return plain.Contains("Bets are now OPEN", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("Race started", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("Forecast hash", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("No more bets", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("Round complete", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("Point established", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("Result:", StringComparison.OrdinalIgnoreCase)
            || plain.Contains("Winner:", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripGameTag(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            var end = text.IndexOf(']');
            if (end > 0 && end + 1 < text.Length)
            {
                var maybeTag = text[1..end];
                if (maybeTag.All(ch => char.IsLetter(ch) || ch == '-' || ch == ' '))
                    return text[(end + 1)..].TrimStart();
            }
        }

        return text;
    }

    private void DrawDealerChatFeed()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Streamer Chat");
        if (ImGui.BeginChild("##dealerStreamerChat", new Vector2(0, 150), true))
        {
            var stickToBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 8f;
            foreach (var line in dealerChatFeed)
                ImGui.TextUnformatted(line);

            if (stickToBottom)
                ImGui.SetScrollHereY(1f);
        }
        ImGui.EndChild();
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
            ImGui.TextDisabled($"Commands: {ChatCasino.Commands.CommandRegistry.BuildHelp(game)}");
            ImGui.Separator();
        }
    }
}
