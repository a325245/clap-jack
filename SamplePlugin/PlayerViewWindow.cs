using Dalamud.Bindings.ImGui;
using SamplePlugin.Chat;
using SamplePlugin.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static Dalamud.Bindings.ImGui.ImGui;

namespace SamplePlugin;

public class PlayerViewWindow
{
    private readonly Plugin           _plugin;
    private readonly PlayerChatParser _parser;

    // ── Roulette layout (mirrors PluginUI) ────────────────────────────────────
    private static readonly int[] RedNumbers =
        { 1,3,5,7,9,12,14,16,18,19,21,23,25,27,30,32,34,36 };
    private static readonly int[] WheelOrder =
        { 0,32,15,19,4,21,2,25,17,34,6,27,13,36,11,30,8,23,10,5,24,16,33,1,20,14,31,9,22,18,29,7,28,12,35,3,26 };
    private static readonly int[,] RouletteGrid =
    {
        { 3,  6,  9, 12, 15, 18, 21, 24, 27, 30, 33, 36 },
        { 2,  5,  8, 11, 14, 17, 20, 23, 26, 29, 32, 35 },
        { 1,  4,  7, 10, 13, 16, 19, 22, 25, 28, 31, 34 }
    };

    // ── Dice pip offsets ──────────────────────────────────────────────────────
    private static readonly Vector2[][] PipOffsets =
    {
        Array.Empty<Vector2>(),
        new[] { new Vector2(0f, 0f) },
        new[] { new Vector2(-0.28f,-0.28f), new Vector2(0.28f, 0.28f) },
        new[] { new Vector2(-0.28f,-0.28f), new Vector2(0f,0f), new Vector2(0.28f,0.28f) },
        new[] { new Vector2(-0.28f,-0.28f), new Vector2(0.28f,-0.28f),
                new Vector2(-0.28f, 0.28f), new Vector2(0.28f, 0.28f) },
        new[] { new Vector2(-0.28f,-0.28f), new Vector2(0.28f,-0.28f), new Vector2(0f,0f),
                new Vector2(-0.28f, 0.28f), new Vector2(0.28f, 0.28f) },
        new[] { new Vector2(-0.28f,-0.28f), new Vector2(0.28f,-0.28f),
                new Vector2(-0.28f, 0f),    new Vector2(0.28f, 0f),
                new Vector2(-0.28f, 0.28f), new Vector2(0.28f, 0.28f) },
    };

    public PlayerViewWindow(Plugin plugin, PlayerChatParser parser)
    {
        _plugin = plugin;
        _parser = parser;
    }

    /// <summary>Called every frame from Plugin.DrawUI — ticks craps dice animation.</summary>
    public void Draw() => _parser.Tick();

    /// <summary>Renders player view content inline inside an existing ImGui window.</summary>
    public void DrawContent(string myName)
    {
        var state = _parser.State;

        DrawPartySelector(state, myName);
        ImGui.Separator();

        // ── Game visual FIRST ─────────────────────────────────────────────────
        switch (state.DetectedGame)
        {
            case "Blackjack": DrawBJView(state, myName);    break;
            case "Craps":     DrawCrapsView(state);         break;
            case "Roulette":  DrawRouletteView(state);      break;
            case "Poker":     DrawPokerView(state, myName); break;
            default:
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
                    "Waiting for game events… Mark the dealer above so messages are tracked.");
                DrawFeed(state);
                break;
        }

        // ── Command buttons BELOW game view ───────────────────────────────────
        if (!string.IsNullOrEmpty(state.DetectedGame))
        {
            ImGui.Separator();
            DrawCommandButtons(state);
        }
    }

    // ── Party / dealer selector ───────────────────────────────────────────────

    private void DrawPartySelector(PlayerViewState state, string myName)
    {
        var players = _plugin.Engine.CurrentTable.Players.Values.ToList();
        if (players.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
                "No players in table — add them in the Dealer view, then mark who is dealer.");
            return;
        }

        ImGui.TextColored(new Vector4(0.5f, 1f, 1f, 1f), "PARTY  (★ = dealer)");
        ImGui.Spacing();

        for (int i = 0; i < players.Count; i++)
        {
            var  player   = players[i];
            bool isDealer = player.Name.Equals(state.DealerName, StringComparison.OrdinalIgnoreCase);
            bool isMe     = !string.IsNullOrEmpty(myName) &&
                             player.Name.Equals(myName, StringComparison.OrdinalIgnoreCase);

            ImGui.PushStyleColor(ImGuiCol.CheckMark, new Vector4(1f, 0.84f, 0f, 1f));
            bool radio = isDealer;
            if (ImGui.RadioButton($"##dealer{i}", ref radio, true))
                state.DealerName = isDealer ? string.Empty : player.Name;
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mark as dealer (filter chat parsing)");

            ImGui.SameLine();
            Vector4 col = isDealer ? new Vector4(1f, 0.84f, 0f, 1f)
                        : isMe     ? new Vector4(0.4f, 1f, 0.6f, 1f)
                        :            new Vector4(0.85f, 0.85f, 0.85f, 1f);
            ImGui.TextColored(col, (isDealer ? "★ " : "  ") + (isMe ? "◎ " : "") + player.Name);
            ImGui.SameLine(260);
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), $"{player.Bank}\uE049");
        }
    }

    // ── Command buttons ───────────────────────────────────────────────────────

    private void DrawCommandButtons(PlayerViewState state)
    {
        ImGui.TextColored(new Vector4(1f, 1f, 0f, 1f), "COMMANDS");
        ImGui.SameLine(120);
        string[] chatLabels = { "Say", "Party" };
        int chatMode = state.PVChatMode;
        ImGui.SetNextItemWidth(80);
        if (ImGui.Combo("##pvchat", ref chatMode, chatLabels, chatLabels.Length))
            state.PVChatMode = chatMode;
        ImGui.Spacing();

        switch (state.DetectedGame)
        {
            case "Blackjack": DrawBJCommands(state);       break;
            case "Craps":     DrawCrapsCommands(state);    break;
            case "Roulette":  DrawRouletteCommands(state); break;
            case "Poker":     DrawPokerCommands(state);    break;
        }
    }

    private void SendCmd(PlayerViewState state, string cmd)
    {
        string prefix = state.PVChatMode == 1 ? "/party " : "/say ";
        _plugin.SendGameMessage($"{prefix}>{cmd}");
    }

    private void DrawBJCommands(PlayerViewState state)
    {
        if (ImGui.Button("HIT",       new Vector2(65, 24))) SendCmd(state, "HIT");
        ImGui.SameLine();
        if (ImGui.Button("STAND",     new Vector2(70, 24))) SendCmd(state, "STAND");
        ImGui.SameLine();
        if (ImGui.Button("DOUBLE",    new Vector2(75, 24))) SendCmd(state, "DOUBLE");
        ImGui.SameLine();
        if (ImGui.Button("SPLIT",     new Vector2(65, 24))) SendCmd(state, "SPLIT");
        ImGui.SameLine();
        if (ImGui.Button("INSURANCE", new Vector2(95, 24))) SendCmd(state, "INSURANCE");

        ImGui.SameLine(420);
        string betAmt = state.PVBetAmount;
        ImGui.SetNextItemWidth(72);
        if (ImGui.InputText("##pvbjbet", ref betAmt, 10)) state.PVBetAmount = betAmt;
        ImGui.SameLine();
        if (ImGui.Button("BET##pvbj", new Vector2(55, 24))) SendCmd(state, $"BET {state.PVBetAmount}");
    }

    private void DrawCrapsCommands(PlayerViewState state)
    {
        string betAmt = state.PVBetAmount;
        ImGui.SetNextItemWidth(72);
        if (ImGui.InputText("##pvcrapsbetamt", ref betAmt, 10)) state.PVBetAmount = betAmt;
        ImGui.SameLine();
        if (ImGui.Button("PASS",   new Vector2(62, 24))) SendCmd(state, $"BET PASS {state.PVBetAmount}");
        ImGui.SameLine();
        if (ImGui.Button("DP",     new Vector2(50, 24))) SendCmd(state, $"BET DONTPASS {state.PVBetAmount}");
        ImGui.SameLine();
        if (ImGui.Button("FIELD",  new Vector2(62, 24))) SendCmd(state, $"BET FIELD {state.PVBetAmount}");
        ImGui.SameLine();
        if (ImGui.Button("BIG6",   new Vector2(56, 24))) SendCmd(state, $"BET BIG6 {state.PVBetAmount}");
        ImGui.SameLine();
        if (ImGui.Button("BIG8",   new Vector2(56, 24))) SendCmd(state, $"BET BIG8 {state.PVBetAmount}");
        ImGui.SameLine();
        if (ImGui.Button("ROLL",   new Vector2(56, 24))) SendCmd(state, "ROLL");

        string[] placeNums = { "4", "5", "6", "8", "9", "10" };
        int pIdx = state.PVCrapsPlaceNum;
        ImGui.SetNextItemWidth(52);
        if (ImGui.Combo("##pvplacenum", ref pIdx, placeNums, placeNums.Length)) state.PVCrapsPlaceNum = pIdx;
        ImGui.SameLine();
        if (ImGui.Button("PLACE##pvcraps", new Vector2(68, 24)))
            SendCmd(state, $"BET PLACE {placeNums[state.PVCrapsPlaceNum]} {state.PVBetAmount}");
    }

    private void DrawRouletteCommands(PlayerViewState state)
    {
        string betAmt = state.PVBetAmount;
        ImGui.SetNextItemWidth(72);
        if (ImGui.InputText("##pvroubetamt", ref betAmt, 10)) state.PVBetAmount = betAmt;
        ImGui.SameLine();
        ImGui.Text("ON");
        ImGui.SameLine();
        string target = state.PVRouletteTarget;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputTextWithHint("##pvtarget", "RED, EVEN, 14 …", ref target, 64))
            state.PVRouletteTarget = target;
        ImGui.SameLine();
        if (ImGui.Button("BET##pvrou", new Vector2(55, 24)))
            SendCmd(state, $"BET {state.PVBetAmount} {state.PVRouletteTarget}");
    }

    private void DrawPokerCommands(PlayerViewState state)
    {
        if (ImGui.Button("FOLD",    new Vector2(62, 24))) SendCmd(state, "FOLD");
        ImGui.SameLine();
        if (ImGui.Button("CHECK",   new Vector2(68, 24))) SendCmd(state, "CHECK");
        ImGui.SameLine();
        if (ImGui.Button("CALL",    new Vector2(62, 24))) SendCmd(state, "CALL");
        ImGui.SameLine();
        if (ImGui.Button("ALL IN",  new Vector2(72, 24))) SendCmd(state, "ALL IN");

        ImGui.SameLine(300);
        int raiseAmt = state.PVPokerRaiseAmt;
        ImGui.SetNextItemWidth(80);
        if (ImGui.DragInt("##pvpokerraise", ref raiseAmt, 5, 10, 100000)) state.PVPokerRaiseAmt = raiseAmt;
        ImGui.SameLine();
        if (ImGui.Button($"RAISE##pvpoker", new Vector2(65, 24)))
            SendCmd(state, $"RAISE {state.PVPokerRaiseAmt}");
    }

    // ── Card drawing helpers ──────────────────────────────────────────────────

    private static bool CardIsRed(string card) => card.EndsWith("♥") || card.EndsWith("♦");

    private static void DrawCard(ImDrawListPtr dl, Vector2 pos, Vector2 size, string cardStr)
    {
        bool red = CardIsRed(cardStr);
        uint bg  = ImGui.ColorConvertFloat4ToU32(new Vector4(0.98f, 0.98f, 0.96f, 1f));
        uint fg  = red
            ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.82f, 0.08f, 0.08f, 1f))
            : ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.08f, 0.08f, 1f));
        dl.AddRectFilled(pos, pos + size, bg, 5f);
        dl.AddRect(pos, pos + size, fg, 5f, ImDrawFlags.None, 1.5f);
        var tsz = ImGui.CalcTextSize(cardStr);
        dl.AddText(pos + size * 0.5f - tsz * 0.5f, fg, cardStr);
    }

    private static void DrawHidden(ImDrawListPtr dl, Vector2 pos, Vector2 size)
    {
        uint bg  = ImGui.ColorConvertFloat4ToU32(new Vector4(0.22f, 0.22f, 0.32f, 1f));
        uint bdr = ImGui.ColorConvertFloat4ToU32(new Vector4(0.48f, 0.48f, 0.60f, 1f));
        uint fg  = ImGui.ColorConvertFloat4ToU32(new Vector4(0.78f, 0.78f, 0.78f, 1f));
        dl.AddRectFilled(pos, pos + size, bg, 5f);
        dl.AddRect(pos, pos + size, bdr, 5f, ImDrawFlags.None, 1.5f);
        var tsz = ImGui.CalcTextSize("?");
        dl.AddText(pos + size * 0.5f - tsz * 0.5f, fg, "?");
    }

    // ── BLACKJACK VIEW ────────────────────────────────────────────────────────

    private void DrawBJView(PlayerViewState state, string myName)
    {
        ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), "BLACKJACK");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
            state.BJActive ? "  Round in progress" : "  Waiting for next round");
        ImGui.Separator();

        var dl   = ImGui.GetWindowDrawList();
        var cSzD = new Vector2(56f, 80f);
        var cSzP = new Vector2(46f, 66f);
        float gapD = 7f, gapP = 5f;
        const float nameColW = 130f;

        // ── Dealer ────────────────────────────────────────────────────────────
        // Name label and cards on the same line, aligned to nameColW like players
        ImGui.TextColored(new Vector4(1f, 0.55f, 0.15f, 1f), "Dealer");
        ImGui.SameLine(nameColW);
        var dealerCardStart = ImGui.GetCursorScreenPos();
        int dealerCardCount = state.BJDealerCards.Count + (!state.BJHoleRevealed && state.BJDealerCards.Count > 0 ? 1 : 0);

        if (state.BJDealerCards.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1f), "No cards yet");
        }
        else
        {
            for (int i = 0; i < state.BJDealerCards.Count; i++)
                DrawCard(dl, dealerCardStart + new Vector2(i * (cSzD.X + gapD), 0), cSzD, state.BJDealerCards[i]);
            if (!state.BJHoleRevealed)
            {
                int n = state.BJDealerCards.Count;
                DrawHidden(dl, dealerCardStart + new Vector2(n * (cSzD.X + gapD), 0), cSzD);
            }
            ImGui.Dummy(new Vector2(dealerCardCount * (cSzD.X + gapD), cSzD.Y + 4f));
        }

        ImGui.Spacing();
        ImGui.Separator();

        // ── Players ───────────────────────────────────────────────────────────
        ImGui.TextColored(new Vector4(0.5f, 1f, 1f, 1f), "Players");
        ImGui.Spacing();

        if (state.BJPlayers.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1f), "No player hands yet.");
        }
        else
        {
            foreach (var hand in state.BJPlayers)
            {
                bool isMe = !string.IsNullOrEmpty(myName) &&
                             hand.Name.Equals(myName, StringComparison.OrdinalIgnoreCase);

                Vector4 nameCol = isMe ? new Vector4(0.3f, 1f, 0.5f, 1f)
                                       : new Vector4(0.85f, 0.85f, 0.85f, 1f);
                ImGui.TextColored(nameCol, $"{(isMe ? "► " : "  ")}{hand.Name}");
                ImGui.SameLine(nameColW);  // align cards to the same column as dealer

                var rpos = ImGui.GetCursorScreenPos();
                for (int c = 0; c < hand.Cards.Count; c++)
                    DrawCard(dl, rpos + new Vector2(c * (cSzP.X + gapP), 0), cSzP, hand.Cards[c]);

                float usedW = hand.Cards.Count * (cSzP.X + gapP) + 8f;
                ImGui.SetCursorScreenPos(rpos + new Vector2(usedW, 0));

                Vector4 dc = hand.IsBust ? new Vector4(1f, 0.35f, 0.35f, 1f)
                           : hand.IsBJ   ? new Vector4(1f, 1f,    0f,    1f)
                           :               new Vector4(0.75f, 1f,  0.75f, 1f);
                ImGui.TextColored(dc, $"  {hand.Desc}");
                ImGui.Dummy(new Vector2(1f, cSzP.Y - ImGui.GetTextLineHeight() + 2f));
            }
        }

        ImGui.Separator();
        DrawFeed(state, 8);
    }

    // ── CRAPS VIEW ────────────────────────────────────────────────────────────

    private void DrawCrapsView(PlayerViewState state)
    {
        ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), "CRAPS");
        ImGui.SameLine();
        if (state.CrapsPointSet)
            ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), $"  POINT: {state.CrapsPoint}");
        else
            ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), "  COME-OUT roll");

        if (!string.IsNullOrEmpty(state.CrapsShooter))
        {
            ImGui.SameLine(320);
            ImGui.TextColored(new Vector4(0.5f, 1f, 1f, 1f), $"Shooter: {state.CrapsShooter}");
        }
        ImGui.Separator();

        DrawDicePair(state.CrapsDie1, state.CrapsDie2, state.CrapsDiceRolling);

        if (state.CrapsDiceRolling)
            ImGui.TextColored(new Vector4(1f, 1f, 0f, 1f), "Rolling...");
        else if (state.CrapsHasResult)
        {
            int total = state.CrapsDie1 + state.CrapsDie2;
            Vector4 tc = total == 7  ? new Vector4(1f, 0.3f, 0.3f, 1f)
                       : total == 11 ? new Vector4(0.3f, 1f, 0.4f, 1f)
                       :               new Vector4(1f, 1f, 0f, 1f);
            string ann = total switch
            {
                7             => "Seven-out!",
                11            => "Natural!",
                2 or 3 or 12  => "Craps!",
                _ when state.CrapsPointSet && total == state.CrapsPoint => "Point made!",
                _ when state.CrapsPointSet => string.Empty,
                _             => $"Point set: {state.CrapsPoint}"
            };
            ImGui.TextColored(tc, $"= {total}  {ann}");
        }

        // ── Parsed bet indicators ─────────────────────────────────────────────
        if (state.CrapsBetTotals.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), "Active bets:");
            ImGui.SameLine();

            string[] betOrder = { "PASS", "DONTPASS", "FIELD", "BIG6", "BIG8",
                                   "PLACE4","PLACE5","PLACE6","PLACE8","PLACE9","PLACE10" };
            foreach (var key in betOrder)
            {
                if (!state.CrapsBetTotals.ContainsKey(key)) continue;
                ImGui.SameLine();
                string lbl = key switch
                {
                    "DONTPASS" => "DP",
                    _ when key.StartsWith("PLACE") => $"P{key[5..]}",
                    _ => key
                };
                Vector4 bc = key == "PASS"     ? new Vector4(0.2f, 0.8f, 0.2f, 1f)
                           : key == "DONTPASS" ? new Vector4(0.9f, 0.3f, 0.3f, 1f)
                           : key == "FIELD"    ? new Vector4(0.9f, 0.8f, 0.1f, 1f)
                           :                    new Vector4(0.4f, 0.8f, 1.0f, 1f);
                ImGui.TextColored(bc, $"[{lbl}]");
            }
            ImGui.Spacing();
        }

        ImGui.Separator();
        DrawFeed(state, 10);
    }

    private void DrawDicePair(int d1, int d2, bool rolling)
    {
        var   dl  = ImGui.GetWindowDrawList();
        var   pos = ImGui.GetCursorScreenPos();
        float sz  = 64f, gap = 16f;
        DrawDieFace(dl, pos, sz, d1, rolling);
        DrawDieFace(dl, new Vector2(pos.X + sz + gap, pos.Y), sz, d2, rolling);
        ImGui.Dummy(new Vector2(sz * 2 + gap + 8f, sz + 6f));
    }

    private static void DrawDieFace(ImDrawListPtr dl, Vector2 tl, float sz, int face, bool rolling)
    {
        var  br  = tl + new Vector2(sz, sz);
        uint bg  = rolling ? 0xFF444455u : 0xFFEEEEEEu;
        uint pip = rolling ? 0xFFCCCCFFu : 0xFF111111u;
        uint bdr = rolling ? 0xFF8888CCu : 0xFF555555u;
        dl.AddRectFilled(tl, br, bg, 8f);
        dl.AddRect(tl, br, bdr, 8f, ImDrawFlags.None, 1.5f);
        if (face < 1 || face > 6) return;
        var  cx = new Vector2(tl.X + sz * 0.5f, tl.Y + sz * 0.5f);
        float r = sz * 0.085f;
        foreach (var off in PipOffsets[face])
            dl.AddCircleFilled(cx + off * sz, r, pip, 12);
    }

    // ── ROULETTE VIEW ─────────────────────────────────────────────────────────

    private void DrawRouletteView(PlayerViewState state)
    {
        ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), "ROULETTE");
        ImGui.SameLine();
        if (state.RouletteSpinning)
            ImGui.TextColored(new Vector4(1f, 1f, 0f, 1f), "  No more bets! Spinning...");
        else if (state.RouletteResult.HasValue)
        {
            int r = state.RouletteResult.Value;
            bool isRed = Array.IndexOf(RedNumbers, r) >= 0;
            bool isGrn = r == 0;
            Vector4 rc = isGrn ? new Vector4(0f,1f,0f,1f)
                       : isRed ? new Vector4(1f,0.3f,0.3f,1f)
                       :         new Vector4(0.85f,0.85f,0.85f,1f);
            ImGui.TextColored(rc, $"  Result: {r}  {(isGrn?"GREEN":isRed?"RED":"BLACK")}");
        }
        else
            ImGui.TextColored(new Vector4(0.5f,1f,0.5f,1f), "  Place bets!");

        ImGui.Separator();

        DrawPVRouletteWheel(state);
        ImGui.Separator();

        // Build bet map: target → players
        var betMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var bet in state.RouletteBets)
        {
            if (!betMap.TryGetValue(bet.Target, out var lst))
                betMap[bet.Target] = lst = new List<string>();
            if (!lst.Contains(bet.PlayerName)) lst.Add(bet.PlayerName);
        }

        DrawRouletteGrid(state, betMap);

        ImGui.Separator();
        if (betMap.Count > 0)
        {
            ImGui.TextColored(new Vector4(0.5f,1f,1f,1f), "Bets:");
            foreach (var kv in betMap)
            {
                ImGui.Text($"  {kv.Key}:");
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f,1f,0.5f,1f), string.Join(", ", kv.Value));
            }
            ImGui.Separator();
        }

        DrawFeed(state, 6);
    }

    private void DrawPVRouletteWheel(PlayerViewState state)
    {
        var dl   = ImGui.GetWindowDrawList();
        var pos  = ImGui.GetCursorScreenPos();
        float cx = pos.X + 74f, cy = pos.Y + 74f;
        float rad = 66f;
        float seg = (float)(2 * Math.PI / 37);
        const float TwoPI = (float)(2 * Math.PI);

        float wheelRot, ballAngle;
        if (state.RouletteSpinning)
        {
            double ms = Math.Min((DateTime.Now - state.RouletteSpinStart).TotalMilliseconds, 4000.0);
            double a  = 0.9 * ms - 0.43 * ms * ms / 4000.0;
            wheelRot  = (float)(a * 0.018) % TwoPI;
            ballAngle = -(float)(a * 0.04) % TwoPI;
        }
        else if (state.RouletteResult.HasValue)
        {
            wheelRot  = 0f;
            int slot  = Array.IndexOf(WheelOrder, state.RouletteResult.Value);
            ballAngle = slot * seg - (float)(Math.PI / 2);
        }
        else
        {
            wheelRot  = 0f;
            ballAngle = -(float)(Math.PI / 2);
        }

        dl.AddCircleFilled(new Vector2(cx, cy), rad, 0xFF1a1a1a, 64);
        dl.AddCircle(new Vector2(cx, cy), rad + 2, 0xFFFFD700, 64, 3f);

        for (int slot = 0; slot < 37; slot++)
        {
            int   num = WheelOrder[slot];
            float a1  = slot * seg - (float)(Math.PI / 2) + wheelRot;
            float a2  = a1 + seg;
            uint  col = num == 0 ? 0xFF00AA00u
                      : Array.IndexOf(RedNumbers, num) >= 0 ? 0xFF2233CCu : 0xFF222222u;
            var c  = new Vector2(cx, cy);
            var p1 = new Vector2(cx + MathF.Cos(a1) * (rad - 3), cy + MathF.Sin(a1) * (rad - 3));
            var p2 = new Vector2(cx + MathF.Cos(a2) * (rad - 3), cy + MathF.Sin(a2) * (rad - 3));
            dl.AddTriangleFilled(c, p1, p2, col);
            dl.AddLine(c, p1, 0xFF111111, 1f);
        }

        dl.AddCircle(new Vector2(cx, cy), rad, 0xFFFFD700, 64, 2f);
        float trackR = rad - 7f;
        dl.AddCircle(new Vector2(cx, cy), trackR, 0x55FFFFFF, 64, 1f);

        var ballPos = new Vector2(cx + MathF.Cos(ballAngle) * trackR,
                                  cy + MathF.Sin(ballAngle) * trackR);
        dl.AddCircleFilled(ballPos, 5f, 0xFFFFFFFF);
        dl.AddCircle(ballPos, 5f, 0xFFAAAAAA, 16, 1f);
        dl.AddCircleFilled(new Vector2(cx, cy), 8, 0xFF333333);
        dl.AddCircle(new Vector2(cx, cy), 8, 0xFFFFD700, 16, 1.5f);

        ImGui.SetCursorScreenPos(new Vector2(pos.X + 162, pos.Y + 10));
        ImGui.BeginGroup();
        if (state.RouletteResult.HasValue && !state.RouletteSpinning)
        {
            int r = state.RouletteResult.Value;
            bool isRed = Array.IndexOf(RedNumbers, r) >= 0;
            bool isGrn = r == 0;
            Vector4 cv = isGrn ? new Vector4(0f,1f,0f,1f)
                       : isRed ? new Vector4(1f,0.25f,0.25f,1f)
                       :         new Vector4(0.85f,0.85f,0.85f,1f);
            ImGui.TextColored(new Vector4(0.55f,0.55f,0.55f,1f), "Last result:");
            ImGui.TextColored(cv, $"  {r}  {(isGrn?"GREEN":isRed?"RED":"BLACK")}");
        }
        else if (state.RouletteSpinning)
            ImGui.TextColored(new Vector4(1f,1f,0f,1f), "Spinning...");
        else
            ImGui.TextColored(new Vector4(0.5f,0.5f,0.5f,1f), "Awaiting spin");
        ImGui.EndGroup();
        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + 160));
    }

    private void DrawRouletteGrid(PlayerViewState state, Dictionary<string, List<string>> betMap)
    {
        var  dl       = ImGui.GetWindowDrawList();
        var  startPos = ImGui.GetCursorScreenPos();
        float cellW = 28f, cellH = 22f, pad = 2f;
        var   mouse   = ImGui.GetIO().MousePos;

        int?  winNum  = state.RouletteResult;
        bool  winIdle = !state.RouletteSpinning;

        // Collect hovered tooltip (drawn after grid to avoid layout issues)
        string? ttTitle = null;
        string? ttBody  = null;

        // ── Zero cell ─────────────────────────────────────────────────────────
        float zeroH = cellH * 3 + pad * 2;
        var   zTL   = startPos;
        var   zBR   = new Vector2(startPos.X + cellW, startPos.Y + zeroH);
        bool  zWin  = winIdle && winNum == 0;
        dl.AddRectFilled(zTL, zBR, zWin ? 0xFFFFFFFFu : 0xFF00AA00u, 3f);
        dl.AddRect(zTL, zBR, 0xFF888888u, 3f);
        var z0sz = ImGui.CalcTextSize("0");
        dl.AddText(new Vector2(zTL.X + cellW * 0.5f - z0sz.X * 0.5f,
            zTL.Y + zeroH * 0.5f - z0sz.Y * 0.5f), zWin ? 0xFF000000u : 0xFFFFFFFFu, "0");
        if (betMap.ContainsKey("0") && !zWin)
            dl.AddCircleFilled(new Vector2(zTL.X + cellW * 0.5f, zTL.Y + zeroH * 0.5f),
                6f, 0xCCFFCC22u, 12);
        if (mouse.X >= zTL.X && mouse.X < zBR.X && mouse.Y >= zTL.Y && mouse.Y < zBR.Y
            && betMap.TryGetValue("0", out var z0p))
        { ttTitle = "0"; ttBody = string.Join(", ", z0p); }

        // ── Number cells ──────────────────────────────────────────────────────
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 12; col++)
            {
                int   n   = RouletteGrid[row, col];
                float x   = startPos.X + cellW + pad + col * (cellW + pad);
                float y   = startPos.Y + row * (cellH + pad);
                bool  isW = winIdle && winNum == n;
                string ns = n.ToString();

                uint bg = isW ? 0xFFFFFFFFu
                        : Array.IndexOf(RedNumbers, n) >= 0 ? 0xFF2233CCu : 0xFF111111u;
                dl.AddRectFilled(new Vector2(x, y), new Vector2(x+cellW, y+cellH), bg, 2f);
                dl.AddRect(new Vector2(x, y), new Vector2(x+cellW, y+cellH), 0xFF555555u, 2f);
                var nSz = ImGui.CalcTextSize(ns);
                dl.AddText(new Vector2(x + cellW*0.5f - nSz.X*0.5f, y + cellH*0.5f - nSz.Y*0.5f),
                    isW ? 0xFF000000u : 0xFFFFFFFFu, ns);

                if (betMap.ContainsKey(ns) && !isW)
                    dl.AddCircleFilled(new Vector2(x + cellW*0.5f, y + cellH*0.5f), 6f, 0xCCFFCC22u, 12);

                // Hover check
                if (mouse.X >= x && mouse.X < x+cellW && mouse.Y >= y && mouse.Y < y+cellH
                    && betMap.TryGetValue(ns, out var nbp))
                { ttTitle = ns; ttBody = string.Join(", ", nbp); }
            }
        }

        // ── Outside bet strip ─────────────────────────────────────────────────
        float outsideY = startPos.Y + 3 * (cellH + pad);
        float totalW   = cellW + pad + 12 * (cellW + pad);
        float obW      = totalW * 0.25f - pad;
        string[] outside = { "RED", "BLACK", "EVEN", "ODD" };
        uint[]   obBg    = { 0xFF2233CCu, 0xFF111111u, 0xFF222222u, 0xFF222222u };

        for (int i = 0; i < 4; i++)
        {
            float ox = startPos.X + cellW + pad + i * (obW + pad);
            bool  ob = betMap.ContainsKey(outside[i]);
            dl.AddRectFilled(new Vector2(ox, outsideY), new Vector2(ox+obW, outsideY+cellH), obBg[i], 2f);
            dl.AddRect(new Vector2(ox, outsideY), new Vector2(ox+obW, outsideY+cellH), 0xFF555555u, 2f);
            var oSz = ImGui.CalcTextSize(outside[i]);
            dl.AddText(new Vector2(ox + obW*0.5f - oSz.X*0.5f, outsideY + cellH*0.5f - oSz.Y*0.5f),
                0xFFFFFFFFu, outside[i]);
            if (ob)
                dl.AddCircleFilled(new Vector2(ox + obW*0.5f, outsideY + cellH*0.5f), 6f, 0xCCFFCC22u, 12);

            if (mouse.X >= ox && mouse.X < ox+obW && mouse.Y >= outsideY && mouse.Y < outsideY+cellH
                && betMap.TryGetValue(outside[i], out var obp))
            { ttTitle = outside[i]; ttBody = string.Join(", ", obp); }
        }

        ImGui.Dummy(new Vector2(totalW, zeroH + cellH + pad + 6f));

        // ── Tooltip ───────────────────────────────────────────────────────────
        if (ttTitle != null)
        {
            ImGui.BeginTooltip();
            ImGui.TextColored(new Vector4(1f,0.84f,0f,1f), ttTitle);
            if (ttBody != null) ImGui.Text(ttBody);
            ImGui.EndTooltip();
        }
    }

    // ── POKER VIEW ────────────────────────────────────────────────────────────

    private void DrawPokerView(PlayerViewState state, string myName)
    {
        ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), "TEXAS HOLD'EM");
        ImGui.SameLine();
        if (!string.IsNullOrEmpty(state.PokerPhaseLabel))
            ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f),
                $"  {state.PokerPhaseLabel.ToUpperInvariant()}");
        if (state.PokerPot > 0)
        {
            ImGui.SameLine(340);
            ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), $"Pot: {state.PokerPot}\uE049");
        }
        ImGui.Separator();

        DrawPokerTable(state);
        ImGui.Separator();

        if (state.MyHoleReceived)
        {
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.5f, 1f), "YOUR CARDS");
            ImGui.Spacing();
            var dl    = ImGui.GetWindowDrawList();
            var pos   = ImGui.GetCursorScreenPos();
            var bigSz = new Vector2(64f, 92f);
            DrawCard(dl, pos,                               bigSz, state.MyHoleCard1);
            DrawCard(dl, pos + new Vector2(bigSz.X + 10f, 0), bigSz, state.MyHoleCard2);
            ImGui.Dummy(new Vector2(bigSz.X * 2 + 14f, bigSz.Y + 6f));
            ImGui.Separator();
        }
        else
        {
            ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1f),
                "Your hole cards will appear here when dealt via /tell.");
            ImGui.Separator();
        }

        if (state.PokerShowdown.Count > 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "SHOWDOWN");
            ImGui.Spacing();
            var dl  = ImGui.GetWindowDrawList();
            var cSz = new Vector2(44f, 62f);
            foreach (var entry in state.PokerShowdown)
            {
                bool isMe = !string.IsNullOrEmpty(myName) &&
                             entry.Name.Equals(myName, StringComparison.OrdinalIgnoreCase);
                ImGui.TextColored(isMe ? new Vector4(0.3f,1f,0.5f,1f) : new Vector4(0.85f,0.85f,0.85f,1f),
                    $"{(isMe ? "► " : "  ")}{entry.Name}");
                ImGui.SameLine(140);
                var rpos = ImGui.GetCursorScreenPos();
                DrawCard(dl, rpos,                              cSz, entry.Card1);
                DrawCard(dl, rpos + new Vector2(cSz.X + 5f, 0), cSz, entry.Card2);
                ImGui.SetCursorScreenPos(rpos + new Vector2(cSz.X * 2 + 14f, 0));
                ImGui.TextColored(new Vector4(1f,1f,0.5f,1f), $"  {entry.HandDesc}");
                ImGui.Dummy(new Vector2(1f, cSz.Y - ImGui.GetTextLineHeight() + 2f));
            }
            ImGui.Separator();
        }

        DrawFeed(state, 6);
    }

    private void DrawPokerTable(PlayerViewState state)
    {
        var dl     = ImGui.GetWindowDrawList();
        ImGui.Dummy(new Vector2(0, 6f));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
        var origin = ImGui.GetCursorScreenPos();
        float W = 600f, H = 200f;
        var center = origin + new Vector2(W * 0.5f, H * 0.5f);

        uint felt   = ImGui.ColorConvertFloat4ToU32(new Vector4(0.07f, 0.33f, 0.07f, 1f));
        uint border = ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.38f, 0.08f, 1f));
        uint potCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.84f, 0f, 1f));

        float rx = 210f, ry = 72f;
        for (int i = 0; i < 48; i++)
        {
            float a = (float)(i * 2 * Math.PI / 48);
            dl.PathLineTo(center + new Vector2(MathF.Cos(a) * rx, MathF.Sin(a) * ry));
        }
        dl.PathFillConvex(felt);
        for (int i = 0; i < 48; i++)
        {
            float a = (float)(i * 2 * Math.PI / 48);
            dl.PathLineTo(center + new Vector2(MathF.Cos(a) * rx, MathF.Sin(a) * ry));
        }
        dl.PathStroke(border, ImDrawFlags.Closed, 3f);

        if (state.PokerPot > 0)
        {
            string potStr = $"POT: {state.PokerPot}\uE049";
            var pSz = ImGui.CalcTextSize(potStr);
            dl.AddText(center - pSz * 0.5f + new Vector2(0, -H * 0.22f), potCol, potStr);
        }

        if (state.PokerCommunity.Count > 0)
        {
            var cSz = new Vector2(52f, 74f); float g = 8f;
            int n = state.PokerCommunity.Count;
            float tw = n * cSz.X + (n - 1) * g;
            var sx = new Vector2(center.X - tw * 0.5f, center.Y - cSz.Y * 0.5f);
            for (int i = 0; i < n; i++)
                DrawCard(dl, sx + new Vector2(i * (cSz.X + g), 0), cSz, state.PokerCommunity[i]);
        }
        else
        {
            uint gray = ImGui.ColorConvertFloat4ToU32(new Vector4(0.4f, 0.7f, 0.4f, 0.55f));
            string lbl = "Waiting for community cards…";
            var lsz = ImGui.CalcTextSize(lbl);
            dl.AddText(center - lsz * 0.5f, gray, lbl);
        }

        if (!string.IsNullOrEmpty(state.PokerPhaseLabel))
        {
            uint phCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.8f, 0.6f, 0.65f));
            string ph = state.PokerPhaseLabel.ToUpperInvariant();
            var phSz = ImGui.CalcTextSize(ph);
            dl.AddText(center - phSz * 0.5f + new Vector2(0, H * 0.28f), phCol, ph);
        }

        ImGui.Dummy(new Vector2(W, H + 4f));
        ImGui.Dummy(new Vector2(0, 6f));
    }

    // ── Feed ─────────────────────────────────────────────────────────────────

    private static void DrawFeed(PlayerViewState state, int maxLines = 10)
    {
        if (state.Feed.Count == 0) return;
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "Recent messages:");
        if (!ImGui.BeginChild("##pvfeed", new Vector2(-1, 130), true)) return;
        int shown = 0;
        foreach (var line in state.Feed)
        {
            if (shown++ >= maxLines) break;
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), line);
        }
        ImGui.EndChild();
    }
}
