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

    // ── Roulette layout ───────────────────────────────────────────────────────
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

        // ── Add Party button — always visible ─────────────────────────────────
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.35f, 0.55f, 1f));
            if (ImGui.Button("\u2795 Add Party", new Vector2(90, 28)))
                _plugin.AddPartyToTable();
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add all party members to the table");

            // Reset button — top right
            ImGui.SameLine(ImGui.GetWindowWidth() - 78f);
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.08f, 0.08f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.15f, 0.15f, 1f));
            if (ImGui.Button("RESET", new Vector2(60, 28)))
            {
                _plugin.FullReset();
                state.DetectedGame    = string.Empty;
                state.BJPlayers       = new();
                state.BJDealerCards   = new();
                state.PokerCommunity  = new();
                state.PokerPlayers    = new();
                state.PokerShowdown   = new();
                state.UltimaHand      = new();
                state.UltimaPlayerOrder = new();
                state.UltimaCardCounts  = new();
                state.UltimaTopCard     = null;
                state.UltimaWinner      = string.Empty;
                state.PlayerBanks       = new(StringComparer.OrdinalIgnoreCase);
                state.Feed.Clear();
            }
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reset everything to factory state");

            ImGui.Separator();
        }

        // For Ultima the oval table already shows all players and the dealer is
        // auto-detected.  Other games keep the party selector with radio buttons.
        if (state.DetectedGame != "Ultima" && !string.IsNullOrEmpty(state.DetectedGame))
        {
            DrawPartySelector(state, myName);
            ImGui.Separator();
        }

        // ── Game visual FIRST ─────────────────────────────────────────────────
        switch (state.DetectedGame)
        {
            case "Blackjack": DrawBJView(state, myName);    break;
            case "Craps":     DrawCrapsView(state);         break;
            case "Roulette":  DrawRouletteView(state);      break;
            case "Poker":     DrawPokerView(state, myName); break;
            case "Baccarat":  DrawBaccaratView(state);      break;
            case "Chocobo":   DrawChocoboView(state);       break;
            case "Ultima":    DrawUltimaView(state, myName);break;
            default:
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
                    "Waiting for game events… Mark the dealer above so messages are tracked.");
                break;
        }

        // ── Command buttons BELOW game view ───────────────────────────────────
        if (!string.IsNullOrEmpty(state.DetectedGame))
        {
            ImGui.Separator();
            DrawRulesDropdown(state);
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
            ImGui.TextColored(col, (isDealer ? "\u2605 " : "  ") + (isMe ? "\u25CE " : "") + player.Name);
            ImGui.SameLine(260);
            // Prefer bank from chat messages; fall back to engine table
            int dispBank = state.PlayerBanks.TryGetValue(player.Name, out int cb) ? cb : player.Bank;
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), $"{dispBank}\uE049");
        }
    }

    // ── Rules dropdown ──────────────────────────────────────────────────────

    private void DrawRulesDropdown(PlayerViewState state)
    {
        string header = state.DetectedGame switch
        {
            "Blackjack" => "Blackjack Rules",
            "Roulette"  => "Roulette Rules",
            "Craps"     => "Craps Rules",
            "Baccarat"  => "Baccarat Rules",
            "Chocobo"   => "Chocobo Racing Rules",
            "Poker"     => "Texas Hold'Em Rules",
            "Ultima"    => "Ultima! Rules",
            _           => "Rules"
        };

        if (ImGui.CollapsingHeader(header))
        {
            switch (state.DetectedGame)
            {
                case "Blackjack":
                    ImGui.TextWrapped("Beat the dealer without exceeding 21. Ace=1 or 11, face cards=10.");
                    ImGui.TextWrapped("Blackjack (Ace+10) pays 1.5x. Dealer hits on soft 16 or less.");
                    ImGui.TextWrapped("Split matching pairs. Double Down: double bet, one more card.");
                    ImGui.TextWrapped("Insurance: when dealer shows Ace, pays 2:1 if dealer has BJ.");
                    ImGui.TextColored(new Vector4(1,1,0,1), ">HIT  >STAND  >DOUBLE  >SPLIT  >INSURANCE  >BET [amt]");
                    break;
                case "Roulette":
                    ImGui.TextWrapped("Wheel has 37 slots (0-36). 0=green, others alternate red/black.");
                    ImGui.TextWrapped("Straight numbers pay 35:1. RED/BLACK, EVEN/ODD, 1-18/19-36 pay 1:1.");
                    ImGui.TextWrapped("1ST/2ND/3RD dozen pay 2:1. COL1/COL2/COL3 pay 2:1.");
                    ImGui.TextColored(new Vector4(1,1,0,1), ">BET [amt] ON [target]  (e.g. >BET 100 ON RED)");
                    break;
                case "Craps":
                    ImGui.TextWrapped("COME-OUT: 7/11=Natural (Pass wins, DP loses). 2/3=Craps (DP wins, Pass loses). 12=Pass loses, DP push. 4-10 sets POINT.");
                    ImGui.TextWrapped("POINT ROUND: Roll Point=Pass wins. 7-out=DP wins, Pass/Place/Big6/Big8 lose.");
                    ImGui.TextWrapped("FIELD: 2/12=2:1; 3/4/9/10/11=1:1; 5/6/7/8=lose. BIG 6/8: 1:1 before 7. PLACE: 4/10=9:5 5/9=7:5 6/8=7:6.");
                    ImGui.TextColored(new Vector4(1,1,0,1), ">BET PASS/DONTPASS/FIELD/BIG6/BIG8 [amt]  >BET PLACE [4-10] [amt]  >ROLL");
                    break;
                case "Baccarat":
                    ImGui.TextWrapped("Closest to 9 wins. Ace=1, 2-9=face, 10/J/Q/K=0. Score = sum mod 10.");
                    ImGui.TextWrapped("Natural: 8 or 9 on first two cards ends round. Player draws on 0-5, stands 6-7.");
                    ImGui.TextWrapped("Payouts: Player 1:1, Banker 1:1, Tie 8:1.");
                    ImGui.TextColored(new Vector4(1,1,0,1), ">BET PLAYER/BANKER/TIE [amt]");
                    break;
                case "Chocobo":
                    ImGui.TextWrapped("8 racers run a 30-second race. Each has Speed, Endurance, and X-Factor.");
                    ImGui.TextWrapped("Winning bet pays stake \u00D7 the racer's odds.");
                    ImGui.TextColored(new Vector4(1,1,0,1), ">BET [#1-8 or name] [amt]");
                    break;
                case "Poker":
                    ImGui.TextWrapped("Each player gets 2 hole cards + 5 community cards over 4 rounds (Pre-Flop, Flop, Turn, River). Best 5-card hand wins.");
                    ImGui.TextWrapped("Hands: Royal Flush > Straight Flush > 4-Kind > Full House > Flush > Straight > 3-Kind > Two Pair > Pair > High Card.");
                    ImGui.TextColored(new Vector4(1,1,0,1), ">CALL  >CHECK  >RAISE [amt]  >FOLD  >ALL IN");
                    break;
                case "Ultima":
                    ImGui.TextWrapped("Match the top card by color or number. First to empty hand wins!");
                    ImGui.TextWrapped("Counterspell = skip next. Rewind = reverse direction. Summon+2 = next draws 2.");
                    ImGui.TextWrapped("Polymorph = wild (choose color). Polymorph+4 = wild + next draws 4.");
                    ImGui.TextColored(new Vector4(1,1,0,1), ">PLAY [code] [color]  >DRAW  >HAND  >SORT COLOR/RANK");
                    break;
            }
        }
    }

    // ── Command buttons ───────────────────────────────────────────────────────

    private void DrawCommandButtons(PlayerViewState state)
    {
        ImGui.TextColored(new Vector4(1f, 1f, 0f, 1f), "COMMANDS");
        ImGui.Spacing();

        switch (state.DetectedGame)
        {
            case "Blackjack": DrawBJCommands(state);       break;
            case "Craps":     DrawCrapsCommands(state);    break;
            case "Roulette":  DrawRouletteCommands(state); break;
            case "Poker":     DrawPokerCommands(state);    break;
            case "Baccarat":  DrawBaccaratCommands(state); break;
            case "Chocobo":   DrawChocoboCommands(state);  break;
        }
    }

    /// <summary>Send a game command using the same chat channel the dealer engine uses.</summary>
    private void SendCmd(string cmd)
    {
        string prefix = _plugin.Engine.ChatMode == Models.ChatMode.Party ? "/party " : "/say ";
        _plugin.SendGameMessage($"{prefix}>{cmd}");
    }

    private void DrawBJCommands(PlayerViewState state)
    {
        string myName = Plugin.PlayerState?.CharacterName ?? string.Empty;
        bool myTurn = !string.IsNullOrEmpty(myName) && state.BJActive &&
            state.BJCurrentPlayer.StartsWith(myName.Split(' ')[0], StringComparison.OrdinalIgnoreCase);
        var cmds = state.BJAvailableCmds;
        bool hasCmds = cmds.Count > 0;

        ImGui.BeginDisabled(!myTurn || (hasCmds && !cmds.Contains("HIT")));
        if (ImGui.Button("HIT",       new Vector2(65, 24))) SendCmd("HIT");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!myTurn || (hasCmds && !cmds.Contains("STAND")));
        if (ImGui.Button("STAND",     new Vector2(70, 24))) SendCmd("STAND");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!myTurn || (hasCmds && !cmds.Contains("DOUBLE")));
        if (ImGui.Button("DOUBLE",    new Vector2(75, 24))) SendCmd("DOUBLE");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!myTurn || (hasCmds && !cmds.Contains("SPLIT")));
        if (ImGui.Button("SPLIT",     new Vector2(65, 24))) SendCmd("SPLIT");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!myTurn || (hasCmds && !cmds.Contains("INSURANCE")));
        if (ImGui.Button("INSURANCE", new Vector2(95, 24))) SendCmd("INSURANCE");
        ImGui.EndDisabled();

        ImGui.SameLine(420);
        string betAmt = state.PVBetAmount;
        ImGui.SetNextItemWidth(72);
        if (ImGui.InputText("##pvbjbet", ref betAmt, 10)) state.PVBetAmount = betAmt;
        ImGui.SameLine();
        if (ImGui.Button("BET##pvbj", new Vector2(55, 24))) SendCmd($"BET {state.PVBetAmount}");
    }

    private void DrawCrapsCommands(PlayerViewState state)
    {
        // Bet amount
        ImGui.Text("Bet");
        ImGui.SameLine();
        string betAmt = state.PVBetAmount;
        ImGui.SetNextItemWidth(72);
        if (ImGui.InputText("##pvcrapsbetamt", ref betAmt, 10)) state.PVBetAmount = betAmt;

        // Bet type dropdown
        ImGui.SameLine();
        ImGui.Text("on");
        ImGui.SameLine();
        string[] betTypes = { "PASS", "DONTPASS", "FIELD", "BIG6", "BIG8", "PLACE 4", "PLACE 5", "PLACE 6", "PLACE 8", "PLACE 9", "PLACE 10" };
        int btIdx = state.PVCrapsBetType;
        ImGui.SetNextItemWidth(120);
        if (ImGui.Combo("##pvcrapsbettype", ref btIdx, betTypes, betTypes.Length)) state.PVCrapsBetType = btIdx;

        ImGui.SameLine();
        if (ImGui.Button("BET##pvcraps", new Vector2(55, 24)))
            SendCmd($"BET {betTypes[state.PVCrapsBetType]} {state.PVBetAmount}");

        ImGui.SameLine(0, 16);
        string myName = Plugin.PlayerState?.CharacterName ?? string.Empty;
        bool isShooter = !string.IsNullOrEmpty(myName) &&
            state.CrapsShooter.StartsWith(myName.Split(' ')[0], StringComparison.OrdinalIgnoreCase);
        ImGui.BeginDisabled(!isShooter);
        if (ImGui.Button("ROLL", new Vector2(56, 24))) SendCmd("ROLL");
        ImGui.EndDisabled();
    }

    private void DrawRouletteCommands(PlayerViewState state)
    {
        ImGui.BeginDisabled(state.RouletteSpinning);
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
            SendCmd($"BET {state.PVBetAmount} ON {state.PVRouletteTarget}");
        ImGui.EndDisabled();
    }

    private void DrawPokerCommands(PlayerViewState state)
    {
        string myName = Plugin.PlayerState?.CharacterName ?? string.Empty;
        bool myTurn = !string.IsNullOrEmpty(myName) &&
            state.PokerActionTo.StartsWith(myName.Split(' ')[0], StringComparison.OrdinalIgnoreCase);
        var cmds = state.PokerAvailCmds;
        bool hasCmds = cmds.Count > 0;

        ImGui.BeginDisabled(!myTurn || (hasCmds && !cmds.Contains("FOLD")));
        if (ImGui.Button("FOLD",    new Vector2(62, 24))) SendCmd("FOLD");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!myTurn || (hasCmds && !cmds.Contains("CHECK")));
        if (ImGui.Button("CHECK",   new Vector2(68, 24))) SendCmd("CHECK");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!myTurn || (hasCmds && !cmds.Contains("CALL")));
        if (ImGui.Button("CALL",    new Vector2(62, 24))) SendCmd("CALL");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!myTurn);
        if (ImGui.Button("ALL IN",  new Vector2(72, 24))) SendCmd("ALL IN");

        ImGui.SameLine(300);
        int raiseAmt = state.PVPokerRaiseAmt;
        ImGui.SetNextItemWidth(80);
        if (ImGui.DragInt("##pvpokerraise", ref raiseAmt, 5, 10, 100000)) state.PVPokerRaiseAmt = raiseAmt;
        ImGui.SameLine();
        if (ImGui.Button($"RAISE##pvpoker", new Vector2(65, 24)))
            SendCmd($"RAISE {state.PVPokerRaiseAmt}");
        ImGui.EndDisabled();
    }

    private void DrawBaccaratCommands(PlayerViewState state)
    {
        ImGui.BeginDisabled(state.BacActive);
        string betAmt = state.PVBetAmount;
        ImGui.Text("Bet");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(72);
        if (ImGui.InputText("##pvbacamt", ref betAmt, 10)) state.PVBetAmount = betAmt;
        ImGui.SameLine();
        ImGui.Text("on");
        ImGui.SameLine();
        string[] betTypes = { "PLAYER", "BANKER", "TIE" };
        int btIdx = state.PVBaccaratBetType;
        ImGui.SetNextItemWidth(90);
        if (ImGui.Combo("##pvbactype", ref btIdx, betTypes, betTypes.Length)) state.PVBaccaratBetType = btIdx;
        ImGui.SameLine();
        if (ImGui.Button("BET##pvbac", new Vector2(55, 24)))
            SendCmd($"BET {betTypes[state.PVBaccaratBetType]} {state.PVBetAmount}");
        ImGui.EndDisabled();
    }

    private void DrawChocoboCommands(PlayerViewState state)
    {
        if (state.ChocoboRacers.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "Waiting for racers to be announced…");
            return;
        }

        ImGui.BeginDisabled(!state.ChocoboBettingOpen || state.ChocoboRacing);
        string betAmt = state.PVBetAmount;
        ImGui.Text("Bet");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(72);
        if (ImGui.InputText("##pvchocamt", ref betAmt, 10)) state.PVBetAmount = betAmt;
        ImGui.SameLine();
        ImGui.Text("on");
        ImGui.SameLine();
        string[] racerNames = state.ChocoboRacers
            .OrderBy(r => r.Number)
            .Select(r => $"#{r.Number} {r.Name} ({r.Odds}x)")
            .ToArray();
        if (racerNames.Length > 0)
        {
            int pick = state.PVChocoboRacerPick;
            ImGui.SetNextItemWidth(200);
            if (ImGui.Combo("##pvchocracer", ref pick, racerNames, racerNames.Length))
                state.PVChocoboRacerPick = pick;
            ImGui.SameLine();
            if (ImGui.Button("BET##pvchoc", new Vector2(55, 24)))
            {
                var racer = state.ChocoboRacers.OrderBy(r => r.Number).ElementAtOrDefault(state.PVChocoboRacerPick);
                if (racer != null)
                    SendCmd($"BET {racer.Number} {state.PVBetAmount}");
            }
        }
        ImGui.EndDisabled();
    }

    // ── Baccarat view ─────────────────────────────────────────────────────────

    private void DrawBaccaratView(PlayerViewState state)
    {
        ImGui.TextColored(new Vector4(0.84f, 0.76f, 0.08f, 1f), "\u2666 Mini Baccarat");
        ImGui.Separator();

        if (!string.IsNullOrEmpty(state.BacPlayerCards))
        {
            var dl  = ImGui.GetWindowDrawList();
            var cSz = new Vector2(52f, 74f);
            float gap = 8f;

            // ── Player hand ──────────────────────────────────────────────
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.4f, 0.7f, 1f, 1f), "PLAYER");
            ImGui.SameLine(80);
            var pCards = state.BacPlayerCards.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            {
                var pos = ImGui.GetCursorScreenPos();
                for (int i = 0; i < pCards.Length; i++)
                    DrawCard(dl, pos + new Vector2(i * (cSz.X + gap), 0), cSz, pCards[i]);
                ImGui.SameLine();
                ImGui.Dummy(new Vector2(pCards.Length * (cSz.X + gap), cSz.Y));
                ImGui.SameLine();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + cSz.Y * 0.5f - ImGui.GetTextLineHeight() * 0.5f);
                ImGui.TextColored(new Vector4(0.4f, 0.7f, 1f, 1f), $"  ({state.BacPlayerScore})");
            }

            ImGui.Spacing();

            // ── Banker hand ──────────────────────────────────────────────
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "BANKER");
            ImGui.SameLine(80);
            var bCards = state.BacBankerCards.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            {
                var pos = ImGui.GetCursorScreenPos();
                for (int i = 0; i < bCards.Length; i++)
                    DrawCard(dl, pos + new Vector2(i * (cSz.X + gap), 0), cSz, bCards[i]);
                ImGui.SameLine();
                ImGui.Dummy(new Vector2(bCards.Length * (cSz.X + gap), cSz.Y));
                ImGui.SameLine();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + cSz.Y * 0.5f - ImGui.GetTextLineHeight() * 0.5f);
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"  ({state.BacBankerScore})");
            }

            if (!string.IsNullOrEmpty(state.BacResult))
            {
                ImGui.Spacing();
                Vector4 winColor = state.BacResult == "PLAYER" ? new Vector4(0.4f, 0.7f, 1f, 1f)
                                 : state.BacResult == "BANKER" ? new Vector4(1f, 0.4f, 0.4f, 1f)
                                 : new Vector4(0.3f, 1f, 0.3f, 1f);
                ImGui.TextColored(winColor, $"\u2756 {state.BacResult} WINS! \u2756");
            }
        }
        else
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
                "Place your bets below. Cards will appear once the dealer deals.");
        }
        ImGui.Spacing();
    }

    // ── Chocobo view ──────────────────────────────────────────────────────────

    private void DrawChocoboView(PlayerViewState state)
    {
        ImGui.TextColored(new Vector4(0.84f, 0.76f, 0.08f, 1f), "\uD83D\uDC26 Chocobo Racing");
        ImGui.Separator();

        if (state.ChocoboRacing && !string.IsNullOrEmpty(state.ChocoboRaceHash) && state.ChocoboRacers.Count > 0)
        {
            DrawChocoboRaceAnimation(state);
            ImGui.Spacing();
        }
        else if (state.ChocoboRacers.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.5f, 1f, 1f, 1f), "TODAY'S RACERS");
            ImGui.Spacing();
            if (ImGui.BeginTable("##pvchocracers", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 30);
                ImGui.TableSetupColumn("Name");
                ImGui.TableSetupColumn("Odds", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableHeadersRow();
                foreach (var r in state.ChocoboRacers.OrderBy(x => x.Number))
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.Text($"#{r.Number}");
                    ImGui.TableNextColumn(); ImGui.Text(r.Name);
                    ImGui.TableNextColumn(); ImGui.Text($"{r.Odds}x");
                }
                ImGui.EndTable();
            }
        }
        else
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
                "Waiting for the dealer to open betting\u2026");
        }
        ImGui.Spacing();
    }

    private void DrawChocoboRaceAnimation(PlayerViewState state)
    {
        var progress = Engine.ChocoboEngine.DecodeRaceHash(state.ChocoboRaceHash);
        if (progress == null) return;

        double elapsed = (DateTime.Now - state.ChocoboRaceStart).TotalMilliseconds;
        float  frac    = (float)Math.Clamp(elapsed / 30000.0, 0.0, 1.0);

        int segCount  = progress.GetLength(1);
        float segFrac = frac * (segCount - 1);
        int   segLow  = Math.Clamp((int)segFrac, 0, segCount - 2);
        float segT    = segFrac - segLow;

        var dl     = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        float W    = Math.Max(ImGui.GetContentRegionAvail().X - 8f, 300f);
        float laneH = 22f;
        int nRacers = Math.Min(state.ChocoboRacers.Count, progress.GetLength(0));
        float H     = nRacers * laneH + 4f;

        uint trackBg    = ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.10f, 0.06f, 0.9f));
        uint laneLine   = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.2f, 0.4f));
        uint finishLine = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.3f));
        dl.AddRectFilled(origin, origin + new Vector2(W, H), trackBg, 4f);
        dl.AddLine(origin + new Vector2(W - 2, 0), origin + new Vector2(W - 2, H), finishLine, 2f);

        float labelW = 90f;
        float trackW = W - labelW - 10f;

        var sorted = state.ChocoboRacers.OrderBy(x => x.Number).ToList();
        for (int r = 0; r < nRacers; r++)
        {
            float y = origin.Y + r * laneH + 2f;
            if (r > 0)
                dl.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + W, y), laneLine);

            float p0  = progress[r, segLow];
            float p1  = progress[r, Math.Min(segLow + 1, segCount - 1)];
            float pos = p0 + (p1 - p0) * segT;
            float dotX = origin.X + labelW + pos * trackW;
            float dotY = y + laneH * 0.5f;

            string label = $"#{sorted[r].Number} {sorted[r].Name}";
            if (label.Length > 12) label = label[..12];
            dl.AddText(new Vector2(origin.X + 4, y + 2),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.75f, 0.75f, 0.6f, 1f)), label);
            dl.AddCircleFilled(new Vector2(dotX, dotY), 6f,
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.84f, 0f, 1f)));
        }

        ImGui.Dummy(new Vector2(W, H + 4f));
        int secsLeft = Math.Max(0, 30 - (int)(elapsed / 1000.0));
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.4f, 1f),
            frac >= 1.0f ? "Race complete!" : $"Race in progress\u2026 {secsLeft}s");
    }


    // ── Card helpers ──────────────────────────────────────────────────────────

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

        // ── Dealer — label + cards on the same line (aligned with player rows) ──
        ImGui.TextColored(new Vector4(1f, 0.55f, 0.15f, 1f), "Dealer");
        ImGui.SameLine(nameColW);
        var dealerCardStart = ImGui.GetCursorScreenPos();
        int dealerCardCount = state.BJDealerCards.Count +
            (!state.BJHoleRevealed && state.BJDealerCards.Count > 0 ? 1 : 0);

        if (state.BJDealerCards.Count == 0)
            ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1f), "No cards yet");
        else
        {
            for (int i = 0; i < state.BJDealerCards.Count; i++)
                DrawCard(dl, dealerCardStart + new Vector2(i * (cSzD.X + gapD), 0), cSzD, state.BJDealerCards[i]);
            if (!state.BJHoleRevealed)
                DrawHidden(dl, dealerCardStart + new Vector2(state.BJDealerCards.Count * (cSzD.X + gapD), 0), cSzD);
            ImGui.Dummy(new Vector2(dealerCardCount * (cSzD.X + gapD), cSzD.Y + 4f));
        }

        ImGui.Spacing();
        ImGui.Separator();

        // ── Players ───────────────────────────────────────────────────────────
        ImGui.TextColored(new Vector4(0.5f, 1f, 1f, 1f), "Players");
        ImGui.Spacing();

        if (state.BJPlayers.Count == 0)
            ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1f), "No player hands yet.");
        else
        {
            foreach (var hand in state.BJPlayers)
            {
                bool isMe = !string.IsNullOrEmpty(myName) &&
                             hand.Name.Equals(myName, StringComparison.OrdinalIgnoreCase);
                bool isTurn = !string.IsNullOrEmpty(state.BJCurrentPlayer) &&
                    hand.Name.StartsWith(state.BJCurrentPlayer.Split(' ')[0], StringComparison.OrdinalIgnoreCase);
                string marker = isTurn ? "\u25ba " : "  ";
                Vector4 nameCol = isTurn ? new Vector4(1f, 1f, 0f, 1f)
                                 : isMe  ? new Vector4(0.3f, 1f, 0.5f, 1f)
                                 :         new Vector4(0.85f, 0.85f, 0.85f, 1f);
                ImGui.TextColored(nameCol, $"{marker}{hand.Name}");
                ImGui.SameLine(nameColW);

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

        // ── Full table visual ─────────────────────────────────────────────────
        DrawPVCrapsTable(state);
        ImGui.Separator();

        // ── Dice ─────────────────────────────────────────────────────────────
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
    }

    private void DrawPVCrapsTable(PlayerViewState state)
    {
        var dl    = ImGui.GetWindowDrawList();
        var pos   = ImGui.GetCursorScreenPos();
        var mouse = ImGui.GetIO().MousePos;

        const float W = 490f, H = 130f, pad = 4f;

        string? ttTitle = null, ttBody = null;

        // Returns true if mouse is inside the rect and the section has bets; captures tooltip data.
        bool Hover(float rx, float ry, float rw, float rh, string section)
        {
            if (mouse.X < rx || mouse.X >= rx + rw || mouse.Y < ry || mouse.Y >= ry + rh) return false;
            if (state.CrapsBets.TryGetValue(section, out var names) && names.Count > 0)
            { ttTitle = section; ttBody = string.Join("\n", names); }
            return true;
        }

        dl.AddRectFilled(pos, pos + new Vector2(W, H), 0xFF1A5C2A, 6f);
        dl.AddRect(pos, pos + new Vector2(W, H), 0xFFFFD700, 6f, ImDrawFlags.None, 2f);

        float y  = pos.Y + pad;
        float x0 = pos.X + pad;
        float tw = W - pad * 2;

        // ── Don't Pass bar ────────────────────────────────────────────────────
        const float dpH = 16f;
        bool hasDP = state.CrapsBets.ContainsKey("DONTPASS");
        dl.AddRectFilled(new Vector2(x0, y), new Vector2(x0 + tw, y + dpH), 0xFF8B1A1A, 3f);
        dl.AddRect(new Vector2(x0, y), new Vector2(x0 + tw, y + dpH), 0xFFCC4444, 3f);
        var dpSz = ImGui.CalcTextSize("DON'T PASS");
        dl.AddText(new Vector2(x0 + tw * 0.5f - dpSz.X * 0.5f, y + dpH * 0.5f - dpSz.Y * 0.5f), 0xFFFFFFFF, "DON'T PASS");
        if (hasDP)
            dl.AddCircleFilled(new Vector2(x0 + tw - 8f, y + dpH * 0.5f), 5f, 0xCCFFCC22u, 10);
        Hover(x0, y, tw, dpH, "DONTPASS");
        y += dpH + 2f;

        // ── Place number boxes + Big 6/8 ──────────────────────────────────────
        int[] placeNums = { 4, 5, 6, 8, 9, 10 };
        const float bigSideW = 56f;
        float boxW = (tw - bigSideW - 2f) / 6f;
        const float boxH = 40f;

        for (int ni = 0; ni < placeNums.Length; ni++)
        {
            int   n       = placeNums[ni];
            float bx      = x0 + ni * (boxW + 1f);
            bool  isPoint = state.CrapsPointSet && state.CrapsPoint == n;
            uint  boxBg   = isPoint ? 0xFFFFFFCC : 0xFF0F4020;

            dl.AddRectFilled(new Vector2(bx, y), new Vector2(bx + boxW, y + boxH), boxBg, 3f);
            dl.AddRect(new Vector2(bx, y), new Vector2(bx + boxW, y + boxH),
                isPoint ? 0xFFFFD700 : 0xFF448844, 3f);
            string numLbl = n == 6 ? "SIX" : n == 9 ? "NINE" : n.ToString();
            var nSz = ImGui.CalcTextSize(numLbl);
            dl.AddText(new Vector2(bx + boxW * 0.5f - nSz.X * 0.5f, y + boxH * 0.5f - nSz.Y * 0.5f),
                isPoint ? 0xFF000000 : 0xFFCCFFCC, numLbl);

            if (isPoint)
            {
                dl.AddCircleFilled(new Vector2(bx + boxW - 8f, y + 8f), 6f, 0xFFFFFFFF, 12);
                dl.AddCircle(new Vector2(bx + boxW - 8f, y + 8f), 6f, 0xFF888888, 12, 1f);
                var onSz = ImGui.CalcTextSize("ON");
                dl.AddText(new Vector2(bx + boxW - 8f - onSz.X * 0.5f, y + 8f - onSz.Y * 0.5f), 0xFF000000, "ON");
            }

            string placeKey = $"PLACE{n}";
            if (state.CrapsBets.ContainsKey(placeKey))
                dl.AddCircleFilled(new Vector2(bx + 8f, y + boxH - 8f), 5f, 0xCCFFCC22u, 10);
            Hover(bx, y, boxW, boxH, placeKey);
        }

        // Big 6/8 box
        float bigX = x0 + 6f * (boxW + 1f) + 1f;
        dl.AddRectFilled(new Vector2(bigX, y), new Vector2(bigX + bigSideW, y + boxH), 0xFF1A3A5C, 3f);
        dl.AddRect(new Vector2(bigX, y), new Vector2(bigX + bigSideW, y + boxH), 0xFF4488AA, 3f);
        var bigSz   = ImGui.CalcTextSize("BIG");
        var big68Sz = ImGui.CalcTextSize("6 | 8");
        dl.AddText(new Vector2(bigX + bigSideW * 0.5f - bigSz.X * 0.5f, y + 4f), 0xFFCCEEFF, "BIG");
        dl.AddText(new Vector2(bigX + bigSideW * 0.5f - big68Sz.X * 0.5f, y + 4f + bigSz.Y + 1f), 0xFFFFFFFF, "6 | 8");
        if (state.CrapsBets.ContainsKey("BIG6"))
            dl.AddCircleFilled(new Vector2(bigX + 8f, y + boxH - 8f), 5f, 0xCCFFCC22u, 10);
        if (state.CrapsBets.ContainsKey("BIG8"))
            dl.AddCircleFilled(new Vector2(bigX + bigSideW - 8f, y + boxH - 8f), 5f, 0xCCFFCC22u, 10);
        // If hovering Big 6/8, show whichever is present
        if (mouse.X >= bigX && mouse.X < bigX + bigSideW && mouse.Y >= y && mouse.Y < y + boxH)
        {
            var b6 = state.CrapsBets.GetValueOrDefault("BIG6");
            var b8 = state.CrapsBets.GetValueOrDefault("BIG8");
            var combined = new List<string>();
            if (b6 != null && b6.Count > 0) combined.Add($"Big 6: {string.Join(", ", b6)}");
            if (b8 != null && b8.Count > 0) combined.Add($"Big 8: {string.Join(", ", b8)}");
            if (combined.Count > 0) { ttTitle = "BIG 6 / 8"; ttBody = string.Join("\n", combined); }
        }
        y += boxH + 2f;

        // ── Pass Line + Field strip ───────────────────────────────────────────
        const float stripH = 22f;
        float passW  = tw * 0.68f;
        bool  hasPass = state.CrapsBets.ContainsKey("PASS");
        dl.AddRectFilled(new Vector2(x0, y), new Vector2(x0 + passW, y + stripH), 0xFF0D5C1A, 3f);
        dl.AddRect(new Vector2(x0, y), new Vector2(x0 + passW, y + stripH), 0xFF44AA44, 3f);
        var plSz = ImGui.CalcTextSize("PASS LINE");
        dl.AddText(new Vector2(x0 + passW * 0.5f - plSz.X * 0.5f, y + stripH * 0.5f - plSz.Y * 0.5f), 0xFFFFFFFF, "PASS LINE");
        if (hasPass)
            dl.AddCircleFilled(new Vector2(x0 + passW - 8f, y + stripH * 0.5f), 5f, 0xCCFFCC22u, 10);
        Hover(x0, y, passW, stripH, "PASS");

        float fieldX    = x0 + passW + 2f;
        float fieldW    = tw - passW - 2f;
        bool  hasField  = state.CrapsBets.ContainsKey("FIELD");
        dl.AddRectFilled(new Vector2(fieldX, y), new Vector2(fieldX + fieldW, y + stripH), 0xFF5C5C0D, 3f);
        dl.AddRect(new Vector2(fieldX, y), new Vector2(fieldX + fieldW, y + stripH), 0xFFAAAA44, 3f);
        var fSz = ImGui.CalcTextSize("FIELD");
        dl.AddText(new Vector2(fieldX + fieldW * 0.5f - fSz.X * 0.5f, y + stripH * 0.5f - fSz.Y * 0.5f), 0xFFFFFFEE, "FIELD");
        if (hasField)
            dl.AddCircleFilled(new Vector2(fieldX + fieldW - 8f, y + stripH * 0.5f), 5f, 0xCCFFCC22u, 10);
        Hover(fieldX, y, fieldW, stripH, "FIELD");
        y += stripH + 2f;

        // ── Shooter + puck ────────────────────────────────────────────────────
        float infoY = y, infoH = (pos.Y + H) - infoY - pad;
        string slbl = string.IsNullOrEmpty(state.CrapsShooter)
            ? "No shooter yet" : $"Shooter: {state.CrapsShooter}";
        var slSz = ImGui.CalcTextSize(slbl);
        dl.AddText(new Vector2(x0 + 4f, infoY + infoH * 0.5f - slSz.Y * 0.5f), 0xFFAAFFAA, slbl);

        if (!state.CrapsPointSet)
        {
            float puckX = pos.X + W - pad - 20f;
            float puckY = infoY + infoH * 0.5f;
            dl.AddCircleFilled(new Vector2(puckX, puckY), 14f, 0xFF333333, 16);
            dl.AddCircle(new Vector2(puckX, puckY), 14f, 0xFF888888, 16, 1.5f);
            var offSz = ImGui.CalcTextSize("OFF");
            dl.AddText(new Vector2(puckX - offSz.X * 0.5f, puckY - offSz.Y * 0.5f), 0xFFAAAAAA, "OFF");
        }

        ImGui.Dummy(new Vector2(W, H));

        if (ttTitle != null)
        {
            ImGui.BeginTooltip();
            ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), ttTitle);
            if (ttBody != null) ImGui.TextUnformatted(ttBody);
            ImGui.EndTooltip();
        }
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

        var betMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var bet in state.RouletteBets)
        {
            if (!betMap.TryGetValue(bet.Target, out var lst))
                betMap[bet.Target] = lst = new List<string>();
            if (!lst.Contains(bet.PlayerName)) lst.Add(bet.PlayerName);
        }

        string myNameRou = Plugin.PlayerState?.CharacterName ?? string.Empty;
        DrawRouletteGrid(state, betMap, myNameRou);
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

    private void DrawRouletteGrid(PlayerViewState state, Dictionary<string, List<string>> betMap, string myName)
    {
        var  dl       = ImGui.GetWindowDrawList();
        var  startPos = ImGui.GetCursorScreenPos();
        float cellW = 28f, cellH = 22f, pad = 2f;
        var   mouse   = ImGui.GetIO().MousePos;

        int?  winNum  = state.RouletteResult;
        bool  winIdle = !state.RouletteSpinning;
        string? ttTitle = null, ttBody = null;

        // ── Zero ─────────────────────────────────────────────────────────────
        float zeroH = cellH * 3 + pad * 2;
        var   zTL   = startPos;
        var   zBR   = new Vector2(startPos.X + cellW, startPos.Y + zeroH);
        bool  zWin  = winIdle && winNum == 0;
        dl.AddRectFilled(zTL, zBR, zWin ? 0xFFFFFFFFu : 0xFF00AA00u, 3f);
        dl.AddRect(zTL, zBR, 0xFF888888u, 3f);
        var z0sz = ImGui.CalcTextSize("0");
        dl.AddText(new Vector2(zTL.X + cellW * 0.5f - z0sz.X * 0.5f,
            zTL.Y + zeroH * 0.5f - z0sz.Y * 0.5f), zWin ? 0xFF000000u : 0xFFFFFFFFu, "0");
        if (betMap.TryGetValue("0", out var z0bets) && !zWin)
        {
            uint z0c = z0bets.Any(n => n.Equals(myName, StringComparison.OrdinalIgnoreCase)) ? 0xCC22FF44u : 0xCCFF8822u;
            dl.AddCircleFilled(new Vector2(zTL.X + cellW * 0.5f, zTL.Y + zeroH * 0.5f), 6f, z0c, 12);
        }
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

                if (betMap.TryGetValue(ns, out var nBets) && !isW)
                {
                    uint nc = nBets.Any(n => n.Equals(myName, StringComparison.OrdinalIgnoreCase)) ? 0xCC22FF44u : 0xCCFF8822u;
                    dl.AddCircleFilled(new Vector2(x + cellW*0.5f, y + cellH*0.5f), 6f, nc, 12);
                }

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
            if (betMap.TryGetValue(outside[i], out var oBets))
            {
                uint oc = oBets.Any(n => n.Equals(myName, StringComparison.OrdinalIgnoreCase)) ? 0xCC22FF44u : 0xCCFF8822u;
                dl.AddCircleFilled(new Vector2(ox + obW*0.5f, outsideY + cellH*0.5f), 6f, oc, 12);
            }
            if (mouse.X >= ox && mouse.X < ox+obW && mouse.Y >= outsideY && mouse.Y < outsideY+cellH
                && betMap.TryGetValue(outside[i], out var obp))
            { ttTitle = outside[i]; ttBody = string.Join(", ", obp); }
        }

        ImGui.Dummy(new Vector2(totalW, zeroH + cellH + pad + 6f));

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
                ImGui.TextColored(isMe ? new Vector4(0.3f,1f,0.5f,1f)
                                       : new Vector4(0.85f,0.85f,0.85f,1f),
                    $"{(isMe ? "► " : "  ")}{entry.Name}");
                ImGui.SameLine(140);
                var rpos = ImGui.GetCursorScreenPos();
                DrawCard(dl, rpos,                              cSz, entry.Card1);
                DrawCard(dl, rpos + new Vector2(cSz.X + 5f, 0), cSz, entry.Card2);
                ImGui.SetCursorScreenPos(rpos + new Vector2(cSz.X * 2 + 14f, 0));
                ImGui.TextColored(new Vector4(1f,1f,0.5f,1f), $"  {entry.HandDesc}");
                ImGui.Dummy(new Vector2(1f, cSz.Y - ImGui.GetTextLineHeight() + 2f));
            }
        }
    }

    private void DrawPokerTable(PlayerViewState state)
    {
        var dl     = ImGui.GetWindowDrawList();
        ImGui.Dummy(new Vector2(0, 6f));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
        var origin = ImGui.GetCursorScreenPos();
        float W = 600f, H = 240f;
        var center = origin + new Vector2(W * 0.5f, H * 0.5f);

        uint felt   = ImGui.ColorConvertFloat4ToU32(new Vector4(0.07f, 0.33f, 0.07f, 1f));
        uint border = ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.38f, 0.08f, 1f));
        uint potCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.84f, 0f, 1f));
        uint txtW   = ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.9f, 0.9f, 1f));
        uint txtG   = ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f, 0.6f, 0.6f, 1f));

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
            dl.AddText(center - pSz * 0.5f + new Vector2(0, -H * 0.22f - 5f), potCol, potStr);
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

        // ── Player seats around the oval ──────────────────────────────────
        int nPlayers = state.PokerPlayers.Count;
        if (nPlayers > 0)
        {
            float seatRx = 250f, seatRy = 100f;
            float seatW = 92f, seatH = 44f;
            for (int si = 0; si < nPlayers && si < 8; si++)
            {
                double angle = -Math.PI / 2.0 + si * 2.0 * Math.PI / Math.Max(nPlayers, 2);
                float sx = center.X + (float)(Math.Cos(angle) * seatRx) - seatW * 0.5f;
                float sy = center.Y + (float)(Math.Sin(angle) * seatRy) - seatH * 0.5f;
                var sPos = new Vector2(sx, sy);

                var p = state.PokerPlayers[si];
                bool isActing = !string.IsNullOrEmpty(state.PokerActionTo) &&
                    p.Name.StartsWith(state.PokerActionTo.Split(' ')[0], StringComparison.OrdinalIgnoreCase);
                uint bgC = p.Status switch
                {
                    "Folded" => ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.1f, 0.1f, 0.85f)),
                    "AllIn"  => ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.15f, 0f, 0.9f)),
                    _        => ImGui.ColorConvertFloat4ToU32(isActing
                        ? new Vector4(0.1f, 0.2f, 0.12f, 0.9f)
                        : new Vector4(0.12f, 0.12f, 0.22f, 0.85f))
                };
                uint rimC = p.Status switch
                {
                    "Folded" => ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.2f, 0.2f, 1f)),
                    "AllIn"  => ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.5f, 0f, 1f)),
                    _        => ImGui.ColorConvertFloat4ToU32(isActing
                        ? new Vector4(0.3f, 1f, 0.4f, 1f)
                        : new Vector4(0.4f, 0.4f, 0.6f, 1f))
                };

                dl.AddRectFilled(sPos, sPos + new Vector2(seatW, seatH), bgC, 4f);
                dl.AddRect(sPos, sPos + new Vector2(seatW, seatH), rimC, 4f);

                string disp = p.Name.Contains(' ') ? p.Name.Split(' ')[0] : p.Name;
                if (disp.Length > 10) disp = disp[..10];
                dl.AddText(new Vector2(sPos.X + 4, sPos.Y + 2), txtW, disp);

                string bankStr = p.Bank > 0 ? $"{p.Bank}\uE049" : "";
                if (!string.IsNullOrEmpty(bankStr))
                    dl.AddText(new Vector2(sPos.X + 4, sPos.Y + 18), txtG, bankStr);

                string statusLine = p.Status == "Folded" ? "FOLD"
                                  : p.Status == "AllIn"  ? "ALL IN"
                                  : p.Bet > 0            ? $"Bet: {p.Bet}\uE049"
                                  : "";
                if (!string.IsNullOrEmpty(statusLine))
                    dl.AddText(new Vector2(sPos.X + 4, sPos.Y + seatH - 14), txtG, statusLine);
            }
        }

        ImGui.Dummy(new Vector2(W, H + 4f));
        ImGui.Dummy(new Vector2(0, 6f));
    }

    // ── ULTIMA! VIEW ──────────────────────────────────────────────────────────

    private void DrawUltimaView(PlayerViewState state, string myName)
    {
        // ── Header ────────────────────────────────────────────────────────────
        // isMyTurn handles first-name-only display names (e.g. 'Jess' vs 'Jess Dee')
        bool isMyTurn   = !string.IsNullOrEmpty(myName) &&
                          !string.IsNullOrEmpty(state.UltimaCurrentPlayer) &&
                          (myName.Equals(state.UltimaCurrentPlayer, StringComparison.OrdinalIgnoreCase) ||
                           myName.StartsWith(state.UltimaCurrentPlayer + " ", StringComparison.OrdinalIgnoreCase));
        bool gameActive = state.UltimaPlayerOrder.Count > 0;

        ImGui.TextColored(new Vector4(0.84f, 0.76f, 0.08f, 1f), "\u2756 ULTIMA!");
        if (gameActive)
        {
            ImGui.SameLine();
            Vector4 tc = isMyTurn ? new Vector4(0.3f, 1f, 0.5f, 1f) : new Vector4(0.55f, 0.55f, 0.55f, 1f);
            string  tl = isMyTurn ? "  \u25ba YOUR TURN!" : $"  {state.UltimaCurrentPlayer}'s turn";
            ImGui.TextColored(tc, tl);
        }
        ImGui.Separator();

        // ── Poker-style table oval ────────────────────────────────────────────
        DrawUltimaTable(state, myName);
        ImGui.Separator();

        // ── Player's hand ─────────────────────────────────────────────────────
        if (state.UltimaHand.Count > 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 1f, 1f, 1f), "YOUR HAND");
            ImGui.SameLine(130);
            if (ImGui.SmallButton(state.UltimaSortByColor ? "[Color+Rank]" : " Color+Rank "))
                state.UltimaSortByColor = true;
            ImGui.SameLine();
            if (ImGui.SmallButton(!state.UltimaSortByColor ? "[Rank]" : " Rank "))
                state.UltimaSortByColor = false;
            ImGui.Spacing();

            DrawUltimaHandInteractive(state, myName);
            ImGui.Spacing();
        }
        else if (gameActive)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1f),
                "Your hand will appear here after the dealer sends it via /tell.");
            ImGui.Spacing();
            if (ImGui.Button(">HAND (request resend)", new Vector2(200, 24)))
            {
                string pfx = _plugin.Engine.ChatMode == Models.ChatMode.Party ? "/party " : "/say ";
                _plugin.SendGameMessage(pfx + ">HAND");
            }
        }
    }

    private void DrawUltimaTable(PlayerViewState state, string myName)
    {
        var  dl     = ImGui.GetWindowDrawList();
        ImGui.Dummy(new Vector2(0, 6f));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
        var  origin = ImGui.GetCursorScreenPos();
        float W = 560f, H = 220f;
        var  center = origin + new Vector2(W * 0.5f, H * 0.5f);
        float rx = 190f, ry = 68f;

        // Felt oval
        uint felt   = ImGui.ColorConvertFloat4ToU32(new Vector4(0.05f, 0.28f, 0.35f, 1f));
        uint border = ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.38f, 0.08f, 1f));
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

        // Direction arrow — arrowhead at the leading edge shows direction of play
        {
            bool   cw     = state.UltimaClockwise;
            float  ar     = 56f;
            uint   arcCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.9f, 0.3f, 0.8f));
            float  start  = cw ? -(float)(Math.PI * 0.7) : -(float)(Math.PI * 0.3);
            float  span   = cw ?  (float)(Math.PI * 1.4) : -(float)(Math.PI * 1.4);
            int    segs   = 22;
            for (int i = 0; i < segs; i++)
            {
                float a1 = start + span * i / segs;
                float a2 = start + span * (i + 1) / segs;
                dl.AddLine(center + new Vector2(MathF.Cos(a1)*ar, MathF.Sin(a1)*ar),
                           center + new Vector2(MathF.Cos(a2)*ar, MathF.Sin(a2)*ar),
                           arcCol, 3.2f);
            }
            // Arrowhead at the END of the arc, triangle points along the tangent
            float ae   = start + span;
            float tang = ae + (cw ? (float)(Math.PI*0.5f) : -(float)(Math.PI*0.5f));
            var   tip  = center + new Vector2(MathF.Cos(ae)*ar, MathF.Sin(ae)*ar);
            dl.AddTriangleFilled(
                tip + new Vector2(MathF.Cos(tang)*13, MathF.Sin(tang)*13),
                tip + new Vector2(MathF.Cos(tang+MathF.PI-0.45f)*10, MathF.Sin(tang+MathF.PI-0.45f)*10),
                tip + new Vector2(MathF.Cos(tang+MathF.PI+0.45f)*10, MathF.Sin(tang+MathF.PI+0.45f)*10),
                arcCol);
            // Direction label
            string dirLabel = cw ? "\u21BB" : "\u21BA";
            var dlsz = ImGui.CalcTextSize(dirLabel);
            dl.AddText(center + new Vector2(-dlsz.X * 0.5f, ar + 4), arcCol, dirLabel);
        }

        // Top card
        if (state.UltimaTopCard != null)
        {
            float cw2 = 54f, ch2 = 76f;
            var   cp  = center - new Vector2(cw2*0.5f, ch2*0.5f);
            DrawUltimaCardAt(dl, cp, cw2, ch2, state.UltimaTopCard);

            if (state.UltimaTopCard.IsWild)
            {
                var  sp = cp + new Vector2(cw2+5, ch2*0.5f-10);
                uint sc = ImGui.ColorConvertFloat4ToU32(UltimaCard.ColorVec(state.UltimaActiveColor));
                dl.AddRectFilled(sp, sp+new Vector2(20,20), sc, 4f);
                dl.AddRect(sp, sp+new Vector2(20,20),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1,1,1,0.4f)), 4f);
                string cl  = UltimaCard.ColorDisplayName(state.UltimaActiveColor);
                var    csz = ImGui.CalcTextSize(cl);
                dl.AddText(sp + new Vector2(22, 10-csz.Y*0.5f),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f,0.9f,0.9f,0.85f)), cl);
            }
        }
        else
        {
            uint gray = ImGui.ColorConvertFloat4ToU32(new Vector4(0.4f, 0.6f, 0.6f, 0.5f));
            string lbl = state.UltimaPlayerOrder.Count > 0 ? "Waiting for cards\u2026" : "No game in progress";
            var lsz = ImGui.CalcTextSize(lbl);
            dl.AddText(center - lsz*0.5f, gray, lbl);
        }

        // Player tokens — identical to dealer view
        var players  = state.UltimaPlayerOrder;
        int nPlayers = players.Count;
        float[] allAngles = {
            (float)( Math.PI*0.5),
            (float)(-Math.PI*0.5),
            (float)(-Math.PI*0.83),
            (float)(-Math.PI*0.17),
            (float)( Math.PI),
            0f,
            (float)( Math.PI*0.72),
            (float)( Math.PI*0.28),
        };

        for (int i = 0; i < nPlayers; i++)
        {
            float angle = allAngles[Math.Min(i, allAngles.Length-1)];
            float px = center.X + MathF.Cos(angle) * (rx + 44);
            float py = center.Y + MathF.Sin(angle) * (ry  + 34);
            var   pos = new Vector2(px, py);
            string pname = players[i];
            bool   isCur = pname.Equals(state.UltimaCurrentPlayer, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrEmpty(state.UltimaCurrentPlayer) &&
                            pname.StartsWith(state.UltimaCurrentPlayer + " ", StringComparison.OrdinalIgnoreCase));
            int    cnt   = state.UltimaCardCounts.GetValueOrDefault(pname, 0);
            string disp  = pname.Contains(' ') ? pname.Split(' ')[0] : pname;

            uint bgC  = ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f,0.12f,0.18f,0.75f));
            uint rimC = isCur
                ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f,1f,0.5f,1f))
                : ImGui.ColorConvertFloat4ToU32(new Vector4(0.45f,0.45f,0.45f,0.7f));
            var  nsz  = ImGui.CalcTextSize(disp);
            float bw  = Math.Max(nsz.X+12, 54), bh = 20;
            var   bl  = pos - new Vector2(bw*0.5f, bh*0.5f);
            dl.AddRectFilled(bl, bl+new Vector2(bw,bh), bgC, 4f);
            dl.AddRect(bl, bl+new Vector2(bw,bh), rimC, 4f, ImDrawFlags.None, isCur?1.8f:1f);
            dl.AddText(pos-new Vector2(nsz.X*0.5f,nsz.Y*0.5f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f,0.9f,0.9f,1f)), disp);

            if (cnt == 1)
            {
                uint uc = ImGui.ColorConvertFloat4ToU32(new Vector4(1f,0.84f,0f,1f));
                string ut = "ULTIMA!";
                var   usz = ImGui.CalcTextSize(ut);
                dl.AddText(pos+new Vector2(-usz.X*0.5f, bh*0.5f+2), uc, ut);
            }
            else if (cnt > 0)
            {
                float pip = 6f, gap2 = 3f;
                int   cols = Math.Min(cnt, 10);
                float rowW = cols*(pip+gap2)-gap2;
                for (int c = 0; c < cols; c++)
                {
                    var pp = pos+new Vector2(-rowW*0.5f+c*(pip+gap2), bh*0.5f+3);
                    dl.AddRectFilled(pp, pp+new Vector2(pip,pip),
                        ImGui.ColorConvertFloat4ToU32(new Vector4(0.65f,0.65f,0.65f,0.8f)), 2f);
                }
                if (cnt > 10)
                {
                    string more = $"+{cnt-10}";
                    dl.AddText(pos+new Vector2(10*(pip+gap2)-rowW*0.5f+2, bh*0.5f+2),
                        ImGui.ColorConvertFloat4ToU32(new Vector4(0.7f,0.7f,0.7f,0.9f)), more);
                }
            }
        }

        ImGui.Dummy(new Vector2(W, H + 4f));

        // Winner banner below the oval
        if (!string.IsNullOrEmpty(state.UltimaWinner))
        {
            string winMsg = $"\u2756 {state.UltimaWinner} wins Ultima! \u2756";
            var    wsz   = ImGui.CalcTextSize(winMsg);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (W - wsz.X) * 0.5f);
            ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), winMsg);
        }
        ImGui.Dummy(new Vector2(0, 4f));
    }

    private void DrawUltimaHandInteractive(PlayerViewState state, string myName)
    {
        var  dl     = ImGui.GetWindowDrawList();
        bool myTurn = !string.IsNullOrEmpty(myName) &&
                      !string.IsNullOrEmpty(state.UltimaCurrentPlayer) &&
                      (myName.Equals(state.UltimaCurrentPlayer, StringComparison.OrdinalIgnoreCase) ||
                       myName.StartsWith(state.UltimaCurrentPlayer + " ", StringComparison.OrdinalIgnoreCase));

        // Build sorted list
        var hand = state.UltimaHand.ToList();
        if (state.UltimaSortByColor)
            hand.Sort((a,b) => a.Color != b.Color
                ? ((int)a.Color).CompareTo((int)b.Color)
                : ((int)a.Type).CompareTo((int)b.Type));
        else
            hand.Sort((a,b) => a.Type != b.Type
                ? ((int)a.Type).CompareTo((int)b.Type)
                : ((int)a.Color).CompareTo((int)b.Color));

        // Cards are larger; lift space reserved above the baseline
        float cw = 58f, ch = 82f, gap = 6f, lift = 14f;

        // Record the base cursor position then push it down by `lift` so lifted
        // cards don't clip outside the reserved vertical space.
        var basePos = ImGui.GetCursorScreenPos();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + lift);
        var startPos = ImGui.GetCursorScreenPos();

        // Draw all cards using InvisibleButton for reliable ImGui click/hover detection
        for (int i = 0; i < hand.Count; i++)
        {
            var  card = hand[i];
            bool sel  = state.UltimaSelectedIdx == i;
            bool playable = card.IsWild || state.UltimaTopCard == null
                || card.Color == state.UltimaActiveColor
                || card.Type  == state.UltimaTopCard.Type;

            // Position InvisibleButton — height includes the lift zone above the card
            ImGui.SetCursorScreenPos(new Vector2(startPos.X + i*(cw+gap), startPos.Y - lift));
            bool clicked = ImGui.InvisibleButton($"##ucard_{i}", new Vector2(cw, ch + lift));
            bool hover   = ImGui.IsItemHovered();

            // Visual: lifted when selected or hovered (only if playable or wild)
            float cardY = ((sel || hover) && playable) ? startPos.Y - lift : startPos.Y;
            var   cpos  = new Vector2(startPos.X + i*(cw+gap), cardY);

            if (sel)
                dl.AddRectFilled(cpos - new Vector2(3,3), cpos + new Vector2(cw+3,ch+3),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1f,1f,0.3f,0.55f)), 9f);

            DrawUltimaCardAt(dl, cpos, cw, ch, card);

            // Dim overlay for non-playable cards
            if (!playable && myTurn)
                dl.AddRectFilled(cpos, cpos + new Vector2(cw, ch),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.45f)), 6f);

            if (hover)
                ImGui.SetTooltip(playable ? card.DisplayName : $"{card.DisplayName} (not playable)");

            if (clicked && playable)
            {
                if (card.IsWild)
                {
                    state.UltimaSelectedIdx = (state.UltimaSelectedIdx == i) ? null : (int?)i;
                }
                else if (myTurn)
                {
                    string pfx = _plugin.Engine.ChatMode == Models.ChatMode.Party ? "/party " : "/say ";
                    _plugin.SendGameMessage($"{pfx}>PLAY {card.Code}");
                    // Optimistic removal — don't wait for the tell round-trip
                    int ri = state.UltimaHand.FindIndex(c => c.Code.Equals(card.Code, StringComparison.OrdinalIgnoreCase));
                    if (ri >= 0) state.UltimaHand.RemoveAt(ri);
                    state.UltimaSelectedIdx = null;
                }
                else
                {
                    state.UltimaSelectedIdx = (state.UltimaSelectedIdx == i) ? null : (int?)i;
                }
            }
        }

        // Advance cursor past the card row
        ImGui.SetCursorScreenPos(new Vector2(basePos.X, startPos.Y + ch + 8f));
        ImGui.Dummy(new Vector2(hand.Count > 0 ? hand.Count*(cw+gap) : 1f, 1f));

        // ── Wild card color picker ────────────────────────────────────────────
        UltimaCard? selCard = state.UltimaSelectedIdx.HasValue &&
                              state.UltimaSelectedIdx.Value < hand.Count
            ? hand[state.UltimaSelectedIdx.Value] : null;

        if (selCard != null && selCard.IsWild)
        {
            ImGui.TextColored(new Vector4(0.7f,0.7f,0.7f,1f), $"[{selCard.Code}] Choose color to play:");
            ImGui.SameLine();
            string[]  cnames = { "Water", "Fire", "Grass", "Light" };
            Vector4[] cvecs  = {
                new(0.14f,0.38f,0.82f,1f),
                new(0.82f,0.14f,0.14f,1f),
                new(0.12f,0.64f,0.22f,1f),
                new(0.84f,0.76f,0.08f,1f),
            };
            for (int ci = 0; ci < 4; ci++)
            {
                bool pick = state.UltimaColorPickIdx == ci;
                ImGui.PushStyleColor(ImGuiCol.Button, cvecs[ci] with { W = pick ? 1f : 0.45f });
                if (ImGui.Button(cnames[ci] + "##ucol"+ci, new Vector2(66, 30)))
                {
                    state.UltimaColorPickIdx = ci;
                    if (myTurn)
                    {
                        string pfx = _plugin.Engine.ChatMode == Models.ChatMode.Party ? "/party " : "/say ";
                        _plugin.SendGameMessage($"{pfx}>PLAY {selCard.Code} {cnames[ci].ToUpperInvariant()}");
                        // Optimistic removal of the wild card
                        int ri = state.UltimaHand.FindIndex(c => c.Code.Equals(selCard.Code, StringComparison.OrdinalIgnoreCase));
                        if (ri >= 0) state.UltimaHand.RemoveAt(ri);
                        // Optimistic color update
                        UltimaColor[] colorMap = { UltimaColor.Water, UltimaColor.Fire, UltimaColor.Grass, UltimaColor.Light };
                        state.UltimaActiveColor = colorMap[ci];
                        state.UltimaSelectedIdx = null;
                    }
                }
                ImGui.PopStyleColor();
                if (ci < 3) ImGui.SameLine();
            }
            ImGui.Spacing();
        }

        // ── Utility buttons ───────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.BeginDisabled(!myTurn);
        if (ImGui.Button("DRAW", new Vector2(70, 28)))
        {
            string pfx = _plugin.Engine.ChatMode == Models.ChatMode.Party ? "/party " : "/say ";
            _plugin.SendGameMessage(pfx + ">DRAW");
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button(">HAND", new Vector2(56, 28)))
        {
            string pfx = _plugin.Engine.ChatMode == Models.ChatMode.Party ? "/party " : "/say ";
            _plugin.SendGameMessage(pfx + ">HAND");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Request a resend of your hand via tell");

        ImGui.SameLine();
        if (ImGui.Button(">RULES", new Vector2(62, 28)))
        {
            string pfx = _plugin.Engine.ChatMode == Models.ChatMode.Party ? "/party " : "/say ";
            _plugin.SendGameMessage(pfx + ">RULES");
        }
        ImGui.SameLine();
        if (ImGui.Button(">HELP", new Vector2(56, 28)))
        {
            string pfx = _plugin.Engine.ChatMode == Models.ChatMode.Party ? "/party " : "/say ";
            _plugin.SendGameMessage(pfx + ">HELP");
        }

        if (myTurn)
        {
            ImGui.SameLine(0, 16);
            ImGui.TextColored(new Vector4(0.3f,1f,0.5f,1f), "\u25ba YOUR TURN - click a card to play it!");
        }
    }

    private static void DrawUltimaCardAt(ImDrawListPtr dl, Vector2 pos, float w, float h, UltimaCard card)
    {
        uint bg  = ImGui.ColorConvertFloat4ToU32(UltimaCard.ColorVec(card.Color));
        uint fg  = ImGui.ColorConvertFloat4ToU32(UltimaCard.TextColor(card.Color));
        uint rim = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.18f));
        dl.AddRectFilled(pos, pos+new Vector2(w,h), bg, 6f);
        dl.AddRect(pos, pos+new Vector2(w,h), rim, 6f, ImDrawFlags.None, 1.5f);

        // Symbol centred
        string sym = card.Symbol;
        var    ssz = ImGui.CalcTextSize(sym);
        dl.AddText(pos + new Vector2(w*0.5f-ssz.X*0.5f, h*0.5f-ssz.Y*0.5f), fg, sym);

        // Code in top-left corner
        string tiny = card.Code;
        var    tsz  = ImGui.CalcTextSize(tiny);
        if (tsz.X < w-2)
            dl.AddText(pos+new Vector2(2,1), fg, tiny);
    }
}

