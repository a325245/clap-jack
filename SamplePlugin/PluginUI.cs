using Dalamud.Bindings.ImGui;
using SamplePlugin.Engine;
using SamplePlugin.Models;
using System;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using static Dalamud.Bindings.ImGui.ImGui;

namespace SamplePlugin
{
    public class PluginUI : IDisposable
    {
        private Plugin plugin;
        private BlackjackEngine engine;
        public bool IsVisible { get; set; }
        public string AdminName { get; set; } = string.Empty;

        // UI State for editing
        private Dictionary<string, string> editingName = new();
        private Dictionary<string, string> editingServer = new();
        private Dictionary<string, string> editingBank = new();
        private Dictionary<string, string> editingBet = new();

        // Quick add fields
        private string quickAddPlayerName = string.Empty;

        // Debug chat log - stores recent raw chat messages with their types
        private Queue<string> DebugChatLog { get; } = new();
        private const int MaxDebugLines = 20;
        public void AddDebugChat(string line)
        {
            DebugChatLog.Enqueue(line);
            if (DebugChatLog.Count > MaxDebugLines)
                DebugChatLog.Dequeue();
        }

        // Old UI state (keeping for other tabs)
        private string playerAddName = string.Empty;
        private string playerRemoveName = string.Empty;
        private string playerBankName = string.Empty;
        private string playerBankAmount = string.Empty;
        private string playerSetBankAmount = string.Empty;
        private string playerBetAmount = string.Empty;
        private string playerRenameOld = string.Empty;
        private string playerRenameNew = string.Empty;
        private string playerServerName = string.Empty;
        private string playerServerValue = string.Empty;
        private string kickPlayerName = string.Empty;
        private string afkPlayerName = string.Empty;
        private string simulatePlayerName = string.Empty;

        public PluginUI(Plugin plugin, BlackjackEngine engine)
        {
            this.plugin = plugin;
            this.engine = engine;
        }

        public void Dispose() { }

        private string GetDisplayServerName(string serverName)
        {
            return string.IsNullOrEmpty(serverName) || serverName.Equals("Local", StringComparison.OrdinalIgnoreCase) 
                ? "Ultros" 
                : serverName;
        }

        public void Draw()
        {
            if (!IsVisible) return;

            bool isVisible = IsVisible;
            if (ImGui.Begin("Blackjack Dealer - Professional Control Panel", ref isVisible, ImGuiWindowFlags.None))
            {
                IsVisible = isVisible;

                if (ImGui.BeginTabBar("##maintabs"))
                {
                    if (ImGui.BeginTabItem("🎮 Table##tab1"))
                    {
                        DrawTableTab();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("📊 Statistics##tab2"))
                    {
                        DrawStatisticsTab();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("📋 Game Log##tab3"))
                    {
                        DrawLogTab();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("⚙️ Admin##tab4"))
                    {
                        DrawAdminTab();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("🔍 Chat Debug##tab5"))
                    {
                        DrawChatDebugTab();
                        ImGui.EndTabItem();
                    }

                    ImGui.EndTabBar();
                }

                ImGui.End();
            }
        }

        private void DrawTableTab()
        {
            ImGui.TextColored(new Vector4(1, 0.84f, 0, 1), "🎮 BLACKJACK TABLE");

            ImGui.Separator();

            // Game type selector
            ImGui.Text("Game:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            int gameType = (int)engine.CurrentTable.GameType;
            string[] gameTypes = { "Blackjack", "Roulette", "Craps", "Baccarat", "Chocobo Racing" };
            if (ImGui.Combo("##gametype", ref gameType, gameTypes, gameTypes.Length))
            {
                var newType = (Models.GameType)gameType;
                if (newType != engine.CurrentTable.GameType)
                {
                    engine.CurrentTable.GameType = newType;
                    engine.CurrentTable.GameState = Models.GameState.Lobby;
                    string gameName = newType switch
                    {
                        Models.GameType.Roulette      => "Roulette",
                        Models.GameType.Craps         => "Craps",
                        Models.GameType.Baccarat      => "Mini Baccarat",
                        Models.GameType.ChocoboRacing => "Chocobo Racing",
                        _                             => "Blackjack"
                    };
                    engine.Announce($"Now playing: {gameName}!");
                }
            }

            // Chat Mode selection
            ImGui.SameLine();
            ImGui.Text("Chat:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            int chatMode = (int)engine.ChatMode;
            string[] chatModes = { "Say", "Party" };
            if (ImGui.Combo("##chatmode", ref chatMode, chatModes, chatModes.Length))
            {
                engine.ChatMode = (Models.ChatMode)chatMode;
                plugin.RouletteEngine.ChatMode = (Models.ChatMode)chatMode;
                plugin.CrapsEngine.ChatMode    = (Models.ChatMode)chatMode;
                plugin.BaccaratEngine.ChatMode = (Models.ChatMode)chatMode;
                plugin.ChocoboEngine.ChatMode  = (Models.ChatMode)chatMode;
            }

            ImGui.Separator();

            if (engine.CurrentTable.GameType == Models.GameType.Roulette)
                DrawRouletteInterface();
            else if (engine.CurrentTable.GameType == Models.GameType.Craps)
                DrawCrapsInterface();
            else if (engine.CurrentTable.GameType == Models.GameType.Baccarat)
                DrawBaccaratInterface();
            else if (engine.CurrentTable.GameType == Models.GameType.ChocoboRacing)
                DrawChocoboInterface();
            else
                DrawDealerInterface();
        }


        // ── ROULETTE UI ─────────────────────────────────────────────────────────

        private string rouletteTargetInput = string.Empty;
        private int rouletteBetAmount = 50;
        private string rouletteProxyPlayer = string.Empty;
        private int rouletteSelectedPlayerIdx = 0;

        private static readonly int[] RedNumbers = { 1,3,5,7,9,12,14,16,18,19,21,23,25,27,30,32,34,36 };

        // European physical wheel order — naturally alternates red/black around 0
        private static readonly int[] WheelOrder =
        {
            0,32,15,19,4,21,2,25,17,34,6,27,13,36,11,30,8,23,10,5,24,16,33,1,20,14,31,9,22,18,29,7,28,12,35,3,26
        };

        // Standard roulette table layout: 3 rows, 12 columns
        // Row 0 (top): 3,6,9,12,15,18,21,24,27,30,33,36
        // Row 1 (mid): 2,5,8,11,14,17,20,23,26,29,32,35
        // Row 2 (bot): 1,4,7,10,13,16,19,22,25,28,31,34
        private static readonly int[,] RouletteGrid = {
            { 3,  6,  9, 12, 15, 18, 21, 24, 27, 30, 33, 36 },
            { 2,  5,  8, 11, 14, 17, 20, 23, 26, 29, 32, 35 },
            { 1,  4,  7, 10, 13, 16, 19, 22, 25, 28, 31, 34 }
        };

        private void DrawRouletteInterface()
        {
            var table = engine.CurrentTable;
            var roulette = plugin.RouletteEngine;

            // ── Wheel + last result ─────────────────────────────────────────────
            DrawRouletteWheel();

            ImGui.Separator();

            // ── Spin button - dealer controlled, no timer ───────────────────────
            if (table.GameState == Models.GameState.Lobby && table.RouletteSpinState == Models.RouletteSpinState.Idle)
            {
                if (ImGui.Button("🎡  SPIN  ", new Vector2(120, 36)))
                    roulette.StartSpin("Dealer", out _);
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "Place bets, then hit SPIN.");
            }
            else if (table.RouletteSpinState == Models.RouletteSpinState.Spinning)
            {
                ImGui.TextColored(new Vector4(1, 0.8f, 0, 1), "🎡  No more bets! Wheel spinning...");
            }
            else if (table.RouletteSpinState == Models.RouletteSpinState.Resolving)
            {
                ImGui.TextColored(new Vector4(0, 1, 0.5f, 1), "✅  Resolving payouts...");
            }

            ImGui.Separator();

            // ── Horizontal number grid ──────────────────────────────────────────
            DrawRouletteNumberGrid();

            ImGui.Separator();

            // ── Proxy bet controls ──────────────────────────────────────────────
            ImGui.TextColored(new Vector4(1, 1, 0, 1), "PLACE BET");

            var rPlayerNames = table.Players.Values.Select(p => p.Name).ToArray();
            if (rPlayerNames.Length > 0)
            {
                if (rouletteSelectedPlayerIdx >= rPlayerNames.Length) rouletteSelectedPlayerIdx = 0;
                ImGui.SetNextItemWidth(150);
                ImGui.Combo("##rproxyplayerdrop", ref rouletteSelectedPlayerIdx, rPlayerNames, rPlayerNames.Length);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(70);
                ImGui.InputInt("##rbetamt", ref rouletteBetAmount);
                if (rouletteBetAmount < table.MinBet) rouletteBetAmount = table.MinBet;
                ImGui.SameLine();
                ImGui.Text("ON");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(180);
                ImGui.InputTextWithHint("##rtargets", "RED, EVEN, 14, 7 ...", ref rouletteTargetInput, 128);
                ImGui.SameLine();
                if (ImGui.Button("Bet##rbetbtn") && !string.IsNullOrWhiteSpace(rouletteTargetInput))
                    roulette.PlaceBet(rPlayerNames[rouletteSelectedPlayerIdx], rouletteBetAmount, rouletteTargetInput, out _);
                ImGui.SameLine();
                if (ImGui.Button("Clear##rclearbtn"))
                    roulette.ClearPlayerBets(rPlayerNames[rouletteSelectedPlayerIdx]);
            }
            else
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "No players at table.");
            }

            ImGui.Separator();

            // ── Player list ─────────────────────────────────────────────────────
            ImGui.TextColored(new Vector4(0.5f, 1, 1, 1), "PLAYERS");

            if (ImGui.BeginTable("##rplayers", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Player",  ImGuiTableColumnFlags.None, 120);
                ImGui.TableSetupColumn("Bank",    ImGuiTableColumnFlags.None, 70);
                ImGui.TableSetupColumn("Bets",    ImGuiTableColumnFlags.None, 200);
                ImGui.TableSetupColumn("Risk",    ImGuiTableColumnFlags.None, 60);
                ImGui.TableHeadersRow();

                foreach (var player in table.Players.Values)
                {
                    ImGui.TableNextRow();

                    // Player name (grey if AFK)
                    ImGui.TableSetColumnIndex(0);
                    if (player.IsAfk)
                        ImGui.TextColored(new Vector4(0.5f,0.5f,0.5f,1), $"{player.Name} (AFK)");
                    else
                        ImGui.Text(player.Name);

                    // Bank
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text($"{player.Bank}G");

                    // Bets list
                    ImGui.TableSetColumnIndex(2);
                    if (player.RouletteBets.Count == 0)
                    {
                        ImGui.TextColored(new Vector4(0.4f,0.4f,0.4f,1), "No bets");
                    }
                    else
                    {
                        var betStr = string.Join("  ", player.RouletteBets.Select(b =>
                        {
                            uint col = b.Target == "RED"   ? 0xFF3333FF :
                                       b.Target == "BLACK" ? 0xFFAAAAAA :
                                                             0xFF55FF55;
                            return $"[{b.Target}:{b.Amount}G]";
                        }));
                        ImGui.TextUnformatted(betStr);
                    }

                    // Total risk
                    ImGui.TableSetColumnIndex(3);
                    int risk = player.RouletteBets.Sum(b => b.Amount);
                    if (risk > 0)
                        ImGui.TextColored(new Vector4(1,0.7f,0,1), $"{risk}G");
                    else
                        ImGui.TextColored(new Vector4(0.4f,0.4f,0.4f,1), "-");
                }

                ImGui.EndTable();
            }

            ImGui.Separator();
            DrawPlayersManagementTab();
        }

        private void DrawRouletteWheel()
        {
            var table = engine.CurrentTable;
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            float cx = pos.X + 74, cy = pos.Y + 74;
            float radius = 66f;
            float segAngle = (float)(2 * Math.PI / 37);
            const float TwoPI = (float)(2 * Math.PI);

            float wheelRotation;
            float ballAngle;

            if (table.RouletteSpinState == Models.RouletteSpinState.Spinning)
            {
                double ms = Math.Min((DateTime.Now - table.RouletteSpinStart).TotalMilliseconds, 4000.0);
                double totalAngle = 0.9 * ms - 0.43 * ms * ms / 4000.0;

                wheelRotation = (float)(totalAngle * 0.018) % TwoPI;
                // Ball travels counter-clockwise faster than the wheel
                ballAngle = -(float)(totalAngle * 0.04) % TwoPI;
            }
            else if (table.RouletteResult.HasValue)
            {
                wheelRotation = 0f;
                // Ball sits on the winning slot in wheel-order space
                int slotIndex = Array.IndexOf(WheelOrder, table.RouletteResult.Value);
                ballAngle = slotIndex * segAngle - (float)(Math.PI / 2);
            }
            else
            {
                wheelRotation = 0f;
                ballAngle = -(float)(Math.PI / 2);
            }

            // Wheel background
            drawList.AddCircleFilled(new Vector2(cx, cy), radius, 0xFF1a1a1a, 64);
            drawList.AddCircle(new Vector2(cx, cy), radius + 2, 0xFFFFD700, 64, 3f);

            // Draw segments in European wheel order so colours naturally alternate
            for (int slot = 0; slot < 37; slot++)
            {
                int number = WheelOrder[slot];
                float a1 = slot * segAngle - (float)(Math.PI / 2) + wheelRotation;
                float a2 = a1 + segAngle;

                uint color = number == 0 ? 0xFF00AA00
                           : Array.IndexOf(RedNumbers, number) >= 0 ? 0xFF2233CC
                           : 0xFF222222;

                var c  = new Vector2(cx, cy);
                var p1 = new Vector2(cx + (float)Math.Cos(a1) * (radius - 3), cy + (float)Math.Sin(a1) * (radius - 3));
                var p2 = new Vector2(cx + (float)Math.Cos(a2) * (radius - 3), cy + (float)Math.Sin(a2) * (radius - 3));
                drawList.AddTriangleFilled(c, p1, p2, color);

                // Thin separator line between segments
                drawList.AddLine(c, p1, 0xFF111111, 1f);
            }

            // Gold outer ring on top of segments
            drawList.AddCircle(new Vector2(cx, cy), radius, 0xFFFFD700, 64, 2f);

            // Ball track (slightly inside the outer ring)
            float trackR = radius - 7f;
            drawList.AddCircle(new Vector2(cx, cy), trackR, 0x55FFFFFF, 64, 1f);

            // Ball — white filled circle riding the track
            var ballPos = new Vector2(
                cx + (float)Math.Cos(ballAngle) * trackR,
                cy + (float)Math.Sin(ballAngle) * trackR);
            drawList.AddCircleFilled(ballPos, 5f, 0xFFFFFFFF);
            drawList.AddCircle(ballPos, 5f, 0xFFAAAAAA, 16, 1f);

            // Center hub
            drawList.AddCircleFilled(new Vector2(cx, cy), 8, 0xFF333333);
            drawList.AddCircle(new Vector2(cx, cy), 8, 0xFFFFD700, 16, 1.5f);

            // Center number display when idle
        /*    if (table.RouletteResult.HasValue && table.RouletteSpinState == Models.RouletteSpinState.Idle)
            {
                string col   = Engine.RouletteEngine.GetColor(table.RouletteResult.Value);
                uint textCol = col == "RED" ? 0xFF3333FF : col == "GREEN" ? 0xFF00CC00 : 0xFFCCCCCC;
                string numStr = table.RouletteResult.Value.ToString();
                var sz = ImGui.CalcTextSize(numStr);
                drawList.AddText(new Vector2(cx - sz.X * 0.5f, cy - sz.Y * 0.5f), textCol, numStr);
            }
        */

            // Right of wheel: last result summary
            ImGui.SetCursorScreenPos(new Vector2(pos.X + 162, pos.Y + 10));
            ImGui.BeginGroup();

            if (table.RouletteResult.HasValue && table.RouletteSpinState == Models.RouletteSpinState.Idle)
            {
                int r    = table.RouletteResult.Value;
                string c = Engine.RouletteEngine.GetColor(r);
                Vector4 cv = c == "RED"   ? new Vector4(1,0.2f,0.2f,1) :
                             c == "GREEN" ? new Vector4(0,1,0,1) :
                                            new Vector4(0.8f,0.8f,0.8f,1);
                ImGui.TextColored(new Vector4(0.6f,0.6f,0.6f,1), "Last result:");
                ImGui.TextColored(cv, $"  {r}  {c}");
            }
            else if (table.RouletteSpinState == Models.RouletteSpinState.Spinning)
            {
                ImGui.TextColored(new Vector4(1,1,0,1), "Spinning...");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.5f,0.5f,0.5f,1), "Awaiting first spin");
            }

            ImGui.EndGroup();
            ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + 160));
        }

        private void DrawRouletteNumberGrid()
        {
            var drawList = ImGui.GetWindowDrawList();
            var startPos = ImGui.GetCursorScreenPos();
            float cellW = 28, cellH = 22, pad = 2;

            // 0 cell on the left spanning all 3 rows
            float zeroH = cellH * 3 + pad * 2;
            var zeroTL = startPos;
            var zeroBR = new Vector2(startPos.X + cellW, startPos.Y + zeroH);
            bool zeroWin = engine.CurrentTable.RouletteResult == 0 && engine.CurrentTable.RouletteSpinState == Models.RouletteSpinState.Idle && engine.CurrentTable.RouletteResult.HasValue;
            drawList.AddRectFilled(zeroTL, zeroBR, zeroWin ? 0xFFFFFFFF : 0xFF00AA00, 3);
            drawList.AddRect(zeroTL, zeroBR, 0xFF888888, 3);
            var zeroSz = ImGui.CalcTextSize("0");
            drawList.AddText(new Vector2(zeroTL.X + cellW * 0.5f - zeroSz.X * 0.5f, zeroTL.Y + zeroH * 0.5f - zeroSz.Y * 0.5f), zeroWin ? 0xFF000000 : 0xFFFFFFFF, "0");

            // 3 rows x 12 columns of numbers
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 12; col++)
                {
                    int n = RouletteGrid[row, col];
                    float x = startPos.X + cellW + pad + col * (cellW + pad);
                    float y = startPos.Y + row * (cellH + pad);

                    bool isWin = engine.CurrentTable.RouletteResult == n &&
                                 engine.CurrentTable.RouletteSpinState == Models.RouletteSpinState.Idle &&
                                 engine.CurrentTable.RouletteResult.HasValue;

                    uint bg = isWin ? 0xFFFFFFFF :
                              Array.IndexOf(RedNumbers, n) >= 0 ? 0xFF2233CC : 0xFF111111;

                    drawList.AddRectFilled(new Vector2(x, y), new Vector2(x + cellW, y + cellH), bg, 2);
                    drawList.AddRect(new Vector2(x, y), new Vector2(x + cellW, y + cellH), 0xFF555555, 2);

                    var ns  = n.ToString();
                    var nSz = ImGui.CalcTextSize(ns);
                    drawList.AddText(
                        new Vector2(x + cellW * 0.5f - nSz.X * 0.5f, y + cellH * 0.5f - nSz.Y * 0.5f),
                        isWin ? 0xFF000000 : 0xFFFFFFFF, ns);
                }
            }

            // Advance cursor past the grid
            float gridW = cellW + pad + 12 * (cellW + pad);
            ImGui.SetCursorScreenPos(new Vector2(startPos.X, startPos.Y + zeroH + 8));
        }

        // ── CRAPS UI ─────────────────────────────────────────────────────────────

        private int crapsProxyBetAmt = 50;
        private string crapsProxyPlayer = string.Empty;
        private int crapsProxyBetType = 0;      // 0=PASS 1=DONTPASS 2=FIELD 3=BIG6 4=BIG8 5=PLACE
        private int crapsSelectedPlayerIdx = 0;
        private int crapsPlaceNumberIdx = 0;

        private void DrawCrapsInterface()
        {
            var table = engine.CurrentTable;
            var craps = plugin.CrapsEngine;

            // ── Craps table visual ───────────────────────────────────────────────
            DrawCrapsTable();

            ImGui.Separator();

            // ── Phase / shooter / timer banner ───────────────────────────────────
            string shooter = string.IsNullOrEmpty(table.CrapsShooterName) ? "?" : table.CrapsShooterName;
            if (table.CrapsPhase == Models.CrapsPhase.PointEstablished)
                ImGui.TextColored(new Vector4(1, 0.84f, 0, 1), $"⬛ POINT: {table.CrapsPoint} — Roll {table.CrapsPoint} to win or 7-out");
            else
                ImGui.TextColored(new Vector4(0.5f, 1, 0.5f, 1), "🎯 COME-OUT — Pass/Don't Pass bets open");

            ImGui.SameLine(300);
            ImGui.TextColored(new Vector4(0.5f, 1, 1, 1), $"🎲 Shooter: {shooter}");

            if (table.CrapsBettingPhase && !table.CrapsRolling)
            {
                int secs = craps.GetBettingSecondsRemaining();
                ImGui.SameLine();
                ImGui.TextColored(secs <= 5 ? new Vector4(1, 0.3f, 0.3f, 1) : new Vector4(1, 1, 0, 1), $"  ⏱️ {secs}s");
            }

            ImGui.Separator();

            // ── Dice display ─────────────────────────────────────────────────────
            DrawDice(table.CrapsDie1, table.CrapsDie2, table.CrapsRolling);

            ImGui.Separator();

            // ── Buttons ──────────────────────────────────────────────────────────
            if (!table.CrapsRolling)
            {
                if (!table.CrapsBettingPhase)
                {
                    if (ImGui.Button("📋  OPEN BETS  ", new Vector2(140, 32)))
                        craps.StartBettingPhase();
                    ImGui.SameLine();
                }
                if (ImGui.Button("🎲  ROLL  ", new Vector2(120, 32)))
                    craps.StartRoll(out _);
                ImGui.SameLine();
                string hint = table.CrapsPhase == Models.CrapsPhase.PointEstablished
                    ? $"Point: {table.CrapsPoint}"
                    : (table.CrapsBettingPhase ? "Bets open — shooter may roll!" : "Open bets or roll.");
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), hint);
            }
            else
            {
                ImGui.TextColored(new Vector4(1, 0.8f, 0, 1), "🎲  Rolling...");
            }

            ImGui.Separator();

            // ── Rules ────────────────────────────────────────────────────────────
            if (ImGui.CollapsingHeader("📖 Craps Rules (summary)"))
            {
                ImGui.TextWrapped("COME-OUT: 7/11=Natural (Pass wins, DP loses). 2/3=Craps (DP wins, Pass loses). 12=Pass loses, DP push. 4-10 sets the POINT.");
                ImGui.TextWrapped("POINT ROUND: Roll Point=Pass wins & carry-over bets paid. 7-out=DP wins, Pass/Place/Big6/Big8 lose.");
                ImGui.TextWrapped("FIELD (one-roll): 2/12=2:1; 3/4/9/10/11=1:1; 5/6/7/8=lose. Resets after every roll.");
                ImGui.TextWrapped("BIG 6/8: Pays 1:1 if 6 or 8 rolls before 7. Stays up until won or seven-out.");
                ImGui.TextWrapped("PLACE: 4/10=9:5  5/9=7:5  6/8=7:6. Stays up until number hits or seven-out. Only available after point is set.");
                ImGui.TextColored(new Vector4(1, 1, 0, 1), ">BET PASS/DONTPASS/FIELD/BIG6/BIG8 [amt]   >BET PLACE [4/5/6/8/9/10] [amt]   >ROLL (shooter only)");
            }

            ImGui.Separator();

            // ── Place bet controls (dealer proxy) ────────────────────────────────
            ImGui.TextColored(new Vector4(1, 1, 0, 1), "PLACE BET (dealer proxy)");

            var playerNames = table.Players.Values.Select(p => p.Name).ToArray();
            if (playerNames.Length == 0)
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "No players at table.");
            }
            else
            {
                if (crapsSelectedPlayerIdx >= playerNames.Length) crapsSelectedPlayerIdx = 0;
                ImGui.SetNextItemWidth(150);
                ImGui.Combo("##crapsplayerdrop", ref crapsSelectedPlayerIdx, playerNames, playerNames.Length);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(70);
                ImGui.InputInt("##crapsbetamt", ref crapsProxyBetAmt);
                if (crapsProxyBetAmt < table.MinBet) crapsProxyBetAmt = table.MinBet;
                ImGui.SameLine();

                string[] betTypeLabels = { "PASS", "DONTPASS", "FIELD", "BIG6", "BIG8", "PLACE..." };
                string[] betTypeCodes  = { "PASS", "DONTPASS", "FIELD", "BIG6", "BIG8", "PLACE"  };
                ImGui.SetNextItemWidth(100);
                ImGui.Combo("##crapstype", ref crapsProxyBetType, betTypeLabels, betTypeLabels.Length);

                bool isPlaceBet = crapsProxyBetType == 5;
                if (isPlaceBet)
                {
                    ImGui.SameLine();
                    string[] placeNumbers = { "4", "5", "6", "8", "9", "10" };
                    int[]    placeValues  = { 4, 5, 6, 8, 9, 10 };
                    ImGui.SetNextItemWidth(55);
                    ImGui.Combo("##crapsplacenum", ref crapsPlaceNumberIdx, placeNumbers, placeNumbers.Length);
                    ImGui.SameLine();
                    if (ImGui.Button("Bet##crapsbetbtn"))
                        craps.PlaceBet(playerNames[crapsSelectedPlayerIdx], "PLACE", crapsProxyBetAmt, out _, placeValues[crapsPlaceNumberIdx]);
                }
                else
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Bet##crapsbetbtn"))
                        craps.PlaceBet(playerNames[crapsSelectedPlayerIdx], betTypeCodes[crapsProxyBetType], crapsProxyBetAmt, out _);
                }
            }

            ImGui.Separator();

            // ── Player bets table ─────────────────────────────────────────────────
            ImGui.TextColored(new Vector4(0.5f, 1, 1, 1), "PLAYERS & BETS");
            if (ImGui.BeginTable("##crapsplayers", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Player",  ImGuiTableColumnFlags.None, 110);
                ImGui.TableSetupColumn("Bank",    ImGuiTableColumnFlags.None, 60);
                ImGui.TableSetupColumn("Pass",    ImGuiTableColumnFlags.None, 50);
                ImGui.TableSetupColumn("DP",      ImGuiTableColumnFlags.None, 50);
                ImGui.TableSetupColumn("Field",   ImGuiTableColumnFlags.None, 50);
                ImGui.TableSetupColumn("Big6/8",  ImGuiTableColumnFlags.None, 65);
                ImGui.TableSetupColumn("Place",   ImGuiTableColumnFlags.None, 110);
                ImGui.TableSetupColumn("Net",     ImGuiTableColumnFlags.None, 55);
                ImGui.TableHeadersRow();

                foreach (var player in table.Players.Values)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(player.IsAfk ? $"{player.Name} (AFK)" : player.Name);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text($"{player.Bank}G");

                    string pk = player.Name.ToUpperInvariant();
                    table.CrapsBets.TryGetValue(pk, out var pb);

                    ImGui.TableSetColumnIndex(2);
                    if (pb != null && pb.PassLineBet > 0) ImGui.TextColored(new Vector4(0, 1, 0.5f, 1), $"{pb.PassLineBet}G");
                    else ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1), "-");

                    ImGui.TableSetColumnIndex(3);
                    if (pb != null && pb.DontPassBet > 0) ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), $"{pb.DontPassBet}G");
                    else ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1), "-");

                    ImGui.TableSetColumnIndex(4);
                    if (pb != null && pb.FieldBet > 0) ImGui.TextColored(new Vector4(0.8f, 0.8f, 0, 1), $"{pb.FieldBet}G");
                    else ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1), "-");

                    ImGui.TableSetColumnIndex(5);
                    string bigStr = "";
                    if (pb != null && pb.Big6Bet > 0) bigStr += $"6:{pb.Big6Bet}G ";
                    if (pb != null && pb.Big8Bet > 0) bigStr += $"8:{pb.Big8Bet}G";
                    if (!string.IsNullOrEmpty(bigStr)) ImGui.TextColored(new Vector4(0.4f, 0.8f, 1, 1), bigStr.Trim());
                    else ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1), "-");

                    ImGui.TableSetColumnIndex(6);
                    if (pb != null && pb.PlaceBets.Count > 0)
                    {
                        string placeStr = string.Join(" ", pb.PlaceBets.Select(kv => $"{kv.Key}:{kv.Value}G"));
                        ImGui.TextColored(new Vector4(0.9f, 0.6f, 1, 1), placeStr);
                    }
                    else ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1), "-");

                    ImGui.TableSetColumnIndex(7);
                    int net = player.CrapsNetGains;
                    Vector4 netCol = net > 0 ? new Vector4(0, 1, 0, 1) : net < 0 ? new Vector4(1, 0.4f, 0.4f, 1) : new Vector4(0.6f, 0.6f, 0.6f, 1);
                    ImGui.TextColored(netCol, net >= 0 ? $"+{net}G" : $"{net}G");
                }
                ImGui.EndTable();
            }

            ImGui.Separator();
            DrawPlayersManagementTab();
        }

        private void DrawCrapsTable()
        {
            var table = engine.CurrentTable;
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();

            const float W = 490f, H = 134f, pad = 4f;

            // Felt background
            drawList.AddRectFilled(pos, pos + new Vector2(W, H), 0xFF1A5C2A, 6f);
            drawList.AddRect(pos, pos + new Vector2(W, H), 0xFFFFD700, 6f, ImDrawFlags.None, 2f);

            float y = pos.Y + pad;
            float x0 = pos.X + pad;
            float totalW = W - pad * 2;

            // ── Don't Pass bar ───────────────────────────────────────────────────
            const float dpH = 16f;
            drawList.AddRectFilled(new Vector2(x0, y), new Vector2(x0 + totalW, y + dpH), 0xFF8B1A1A, 3f);
            drawList.AddRect(new Vector2(x0, y), new Vector2(x0 + totalW, y + dpH), 0xFFCC4444, 3f);
            var dpSz = ImGui.CalcTextSize("DON'T PASS");
            drawList.AddText(new Vector2(x0 + totalW * 0.5f - dpSz.X * 0.5f, y + dpH * 0.5f - dpSz.Y * 0.5f), 0xFFFFFFFF, "DON'T PASS");
            y += dpH + 2f;

            // ── Place number boxes + Big 6/8 ─────────────────────────────────────
            int[] placeNums = { 4, 5, 6, 8, 9, 10 };
            const float bigSideW = 56f;
            float boxW = (totalW - bigSideW - 2f) / 6f;
            const float boxH = 40f;
            for (int ni = 0; ni < placeNums.Length; ni++)
            {
                int n = placeNums[ni];
                float bx = x0 + ni * (boxW + 1f);
                bool isPoint = table.CrapsPhase == Models.CrapsPhase.PointEstablished && table.CrapsPoint == n;
                uint boxBg = isPoint ? 0xFFFFFFCC : 0xFF0F4020;
                drawList.AddRectFilled(new Vector2(bx, y), new Vector2(bx + boxW, y + boxH), boxBg, 3f);
                drawList.AddRect(new Vector2(bx, y), new Vector2(bx + boxW, y + boxH), isPoint ? 0xFFFFD700 : 0xFF448844, 3f);
                string numLabel = n == 6 ? "SIX" : n == 9 ? "NINE" : n.ToString();
                var nSz = ImGui.CalcTextSize(numLabel);
                drawList.AddText(new Vector2(bx + boxW * 0.5f - nSz.X * 0.5f, y + boxH * 0.5f - nSz.Y * 0.5f),
                    isPoint ? 0xFF000000 : 0xFFCCFFCC, numLabel);
                if (isPoint)
                {
                    drawList.AddCircleFilled(new Vector2(bx + boxW - 8f, y + 8f), 6f, 0xFFFFFFFF, 12);
                    drawList.AddCircle(new Vector2(bx + boxW - 8f, y + 8f), 6f, 0xFF888888, 12, 1f);
                    var onSz = ImGui.CalcTextSize("ON");
                    drawList.AddText(new Vector2(bx + boxW - 8f - onSz.X * 0.5f, y + 8f - onSz.Y * 0.5f), 0xFF000000, "ON");
                }
            }
            // Big 6/8 box
            float bigX = x0 + 6f * (boxW + 1f) + 1f;
            drawList.AddRectFilled(new Vector2(bigX, y), new Vector2(bigX + bigSideW, y + boxH), 0xFF1A3A5C, 3f);
            drawList.AddRect(new Vector2(bigX, y), new Vector2(bigX + bigSideW, y + boxH), 0xFF4488AA, 3f);
            var bigSz  = ImGui.CalcTextSize("BIG");
            var big68Sz = ImGui.CalcTextSize("6 | 8");
            drawList.AddText(new Vector2(bigX + bigSideW * 0.5f - bigSz.X * 0.5f, y + 4f), 0xFFCCEEFF, "BIG");
            drawList.AddText(new Vector2(bigX + bigSideW * 0.5f - big68Sz.X * 0.5f, y + 4f + bigSz.Y + 1f), 0xFFFFFFFF, "6 | 8");
            y += boxH + 2f;

            // ── Pass Line + Field strip ──────────────────────────────────────────
            const float stripH = 22f;
            float passW = totalW * 0.68f;
            drawList.AddRectFilled(new Vector2(x0, y), new Vector2(x0 + passW, y + stripH), 0xFF0D5C1A, 3f);
            drawList.AddRect(new Vector2(x0, y), new Vector2(x0 + passW, y + stripH), 0xFF44AA44, 3f);
            var plSz = ImGui.CalcTextSize("PASS LINE");
            drawList.AddText(new Vector2(x0 + passW * 0.5f - plSz.X * 0.5f, y + stripH * 0.5f - plSz.Y * 0.5f), 0xFFFFFFFF, "PASS LINE");

            float fieldX = x0 + passW + 2f;
            float fieldW = totalW - passW - 2f;
            drawList.AddRectFilled(new Vector2(fieldX, y), new Vector2(fieldX + fieldW, y + stripH), 0xFF5C5C0D, 3f);
            drawList.AddRect(new Vector2(fieldX, y), new Vector2(fieldX + fieldW, y + stripH), 0xFFAAAA44, 3f);
            var fSz = ImGui.CalcTextSize("FIELD");
            drawList.AddText(new Vector2(fieldX + fieldW * 0.5f - fSz.X * 0.5f, y + stripH * 0.5f - fSz.Y * 0.5f), 0xFFFFFFEE, "FIELD");
            y += stripH + 2f;

            // ── Shooter label + OFF puck ─────────────────────────────────────────
            float infoY = y;
            float infoH = (pos.Y + H) - infoY - pad;
            string shooterLabel = string.IsNullOrEmpty(table.CrapsShooterName)
                ? "No shooter yet"
                : $"Shooter: {table.CrapsShooterName}";
            var slSz = ImGui.CalcTextSize(shooterLabel);
            drawList.AddText(new Vector2(x0 + 4f, infoY + infoH * 0.5f - slSz.Y * 0.5f), 0xFFAAFFAA, shooterLabel);

            if (table.CrapsPhase == Models.CrapsPhase.WaitingForBets)
            {
                float puckX = pos.X + W - pad - 20f;
                float puckY = infoY + infoH * 0.5f;
                drawList.AddCircleFilled(new Vector2(puckX, puckY), 14f, 0xFF333333, 16);
                drawList.AddCircle(new Vector2(puckX, puckY), 14f, 0xFF888888, 16, 1.5f);
                var offSz = ImGui.CalcTextSize("OFF");
                drawList.AddText(new Vector2(puckX - offSz.X * 0.5f, puckY - offSz.Y * 0.5f), 0xFFAAAAAA, "OFF");
            }

            ImGui.Dummy(new Vector2(W, H));
        }

        private void DrawDice(int d1, int d2, bool rolling)
        {
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            float dieSize = 52f;
            float gap = 12f;

            DrawDieFace(drawList, pos, dieSize, d1, rolling);
            DrawDieFace(drawList, new Vector2(pos.X + dieSize + gap, pos.Y), dieSize, d2, rolling);

            // Total label
            if (!rolling)
            {
                int total = d1 + d2;
                string label = $"= {total}";
                var lsz = ImGui.CalcTextSize(label);
                float lx = pos.X + dieSize * 2 + gap + 8;
                float ly = pos.Y + dieSize * 0.5f - lsz.Y * 0.5f;
                drawList.AddText(new Vector2(lx, ly), 0xFFFFFFFF, label);
            }

            ImGui.Dummy(new Vector2(dieSize * 2 + gap + 50, dieSize + 4));
        }

        private static readonly Vector2[][] PipOffsets = {
            Array.Empty<Vector2>(), // placeholder for index 0
            new[] { new Vector2(0f, 0f) },                                                                                         // 1
            new[] { new Vector2(-0.28f, -0.28f), new Vector2(0.28f, 0.28f) },                                                     // 2
            new[] { new Vector2(-0.28f, -0.28f), new Vector2(0f, 0f), new Vector2(0.28f, 0.28f) },                                // 3
            new[] { new Vector2(-0.28f, -0.28f), new Vector2(0.28f, -0.28f), new Vector2(-0.28f, 0.28f), new Vector2(0.28f, 0.28f) }, // 4
            new[] { new Vector2(-0.28f, -0.28f), new Vector2(0.28f, -0.28f), new Vector2(0f, 0f), new Vector2(-0.28f, 0.28f), new Vector2(0.28f, 0.28f) }, // 5
            new[] { new Vector2(-0.28f, -0.28f), new Vector2(0.28f, -0.28f), new Vector2(-0.28f, 0f), new Vector2(0.28f, 0f), new Vector2(-0.28f, 0.28f), new Vector2(0.28f, 0.28f) }, // 6
        };

        private static void DrawDieFace(ImDrawListPtr drawList, Vector2 topLeft, float size, int face, bool rolling)
        {
            var br = topLeft + new Vector2(size, size);
            uint bgCol  = rolling ? 0xFF444455 : 0xFFEEEEEE;
            uint pipCol = rolling ? 0xFFCCCCFF : 0xFF111111;
            uint border = rolling ? 0xFF8888CC : 0xFF555555;

            drawList.AddRectFilled(topLeft, br, bgCol, 6f);
            drawList.AddRect(topLeft, br, border, 6f, ImDrawFlags.None, 1.5f);

            if (face < 1 || face > 6) return;
            var cx = new Vector2(topLeft.X + size * 0.5f, topLeft.Y + size * 0.5f);
            float pipR = size * 0.08f;

            foreach (var off in PipOffsets[face])
                drawList.AddCircleFilled(cx + off * size, pipR, pipCol, 12);
        }

        // ── BACCARAT UI ───────────────────────────────────────────────────────────

        private int bacProxyBetAmt = 50;
        private string bacProxyPlayer = string.Empty;
        private int bacSelectedPlayerIdx = 0;
        private int bacProxyBetType = 0; // 0=PLAYER 1=BANKER 2=TIE

        private void DrawBaccaratInterface()
        {
            var table = engine.CurrentTable;
            var bac = plugin.BaccaratEngine;

            // Phase banner
            bool inRound = table.BaccaratPhase != Models.BaccaratPhase.WaitingForBets;
            ImGui.TextColored(new Vector4(1, 0.84f, 0, 1),
                inRound ? "🃏 Round in progress..." : "🃏 MINI BACCARAT — Place bets then Deal!");

            ImGui.Separator();

            // ── Hand display ────────────────────────────────────────────────────
            if (table.BaccaratPlayerHand.Count > 0 || table.BaccaratBankerHand.Count > 0)
            {
                int pScore = BaccaratEngine.GetBaccaratScore(table.BaccaratPlayerHand);
                int bScore = BaccaratEngine.GetBaccaratScore(table.BaccaratBankerHand);

                DrawBaccaratHand("PLAYER", table.BaccaratPlayerHand, pScore);
                DrawBaccaratHand("BANKER", table.BaccaratBankerHand, bScore);
                ImGui.Separator();
            }

            // ── Deal button ─────────────────────────────────────────────────────
            if (!inRound)
            {
                if (ImGui.Button("🃏  DEAL  ", new Vector2(120, 36)))
                    bac.Deal(out _);
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "Place bets first.");
            }
            else
            {
                ImGui.TextColored(new Vector4(1, 0.8f, 0, 1), "🃏  Dealing...");
            }

            ImGui.Separator();

            // ── Summarized rules ────────────────────────────────────────────────
            if (ImGui.CollapsingHeader("📖 Baccarat Rules (summary)"))
            {
                ImGui.TextWrapped("OBJECTIVE: Bet on which hand — Player or Banker — will be closest to 9, or bet on a Tie.");
                ImGui.Spacing();
                ImGui.TextWrapped("CARD VALUES: Ace = 1. Cards 2–9 = face value. 10, J, Q, K = 0. Hand score = sum mod 10.");
                ImGui.Spacing();
                ImGui.TextWrapped("NATURALS: If either hand totals 8 or 9 after two cards, no more cards are drawn.");
                ImGui.Spacing();
                ImGui.TextWrapped("PLAYER RULE: Player draws a third card on 0–5; stands on 6–7.");
                ImGui.Spacing();
                ImGui.TextWrapped("BANKER RULE: If Player did not draw, Banker draws on 0–5. If Player drew, Banker follows standard third-card rules based on Banker's score and Player's third card.");
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1, 1, 0, 1), "Player pays 1:1  |  Banker pays 1:1 (no commission)  |  Tie pays 8:1");
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "Bets: >BET PLAYER [amt]  >BET BANKER [amt]  >BET TIE [amt]");
            }

            ImGui.Separator();

            // ── Place bet controls ──────────────────────────────────────────────
            ImGui.TextColored(new Vector4(1, 1, 0, 1), "PLACE BET");

            var bacPlayerNames = table.Players.Values.Select(p => p.Name).ToArray();
            if (bacPlayerNames.Length > 0)
            {
                if (bacSelectedPlayerIdx >= bacPlayerNames.Length) bacSelectedPlayerIdx = 0;
                ImGui.SetNextItemWidth(150);
                ImGui.Combo("##bacplayerdrop", ref bacSelectedPlayerIdx, bacPlayerNames, bacPlayerNames.Length);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(70);
                ImGui.InputInt("##bacbetamt", ref bacProxyBetAmt);
                if (bacProxyBetAmt < table.MinBet) bacProxyBetAmt = table.MinBet;
                ImGui.SameLine();
                ImGui.SetNextItemWidth(90);
                string[] bacBetTypes = { "PLAYER", "BANKER", "TIE" };
                ImGui.Combo("##bactype", ref bacProxyBetType, bacBetTypes, bacBetTypes.Length);
                ImGui.SameLine();
                if (ImGui.Button("Bet##bacbetbtn"))
                    bac.PlaceBet(bacPlayerNames[bacSelectedPlayerIdx], bacBetTypes[bacProxyBetType], bacProxyBetAmt, out _);
            }
            else
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "No players at table.");
            }

            ImGui.Separator();

            // ── Player bets table ───────────────────────────────────────────────
            ImGui.TextColored(new Vector4(0.5f, 1, 1, 1), "PLAYERS & BETS");
            if (ImGui.BeginTable("##bacplayers", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.None, 120);
                ImGui.TableSetupColumn("Bank",   ImGuiTableColumnFlags.None, 70);
                ImGui.TableSetupColumn("Player Bet", ImGuiTableColumnFlags.None, 80);
                ImGui.TableSetupColumn("Banker Bet", ImGuiTableColumnFlags.None, 80);
                ImGui.TableSetupColumn("Tie Bet",    ImGuiTableColumnFlags.None, 70);
                ImGui.TableHeadersRow();

                foreach (var player in table.Players.Values)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(player.IsAfk ? $"{player.Name} (AFK)" : player.Name);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text($"{player.Bank}G");

                    string pk = player.Name.ToUpperInvariant();
                    table.BaccaratBets.TryGetValue(pk, out var bb);

                    ImGui.TableSetColumnIndex(2);
                    if (bb != null && bb.PlayerBet > 0)
                        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1, 1), $"{bb.PlayerBet}G");
                    else
                        ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1), "-");

                    ImGui.TableSetColumnIndex(3);
                    if (bb != null && bb.BankerBet > 0)
                        ImGui.TextColored(new Vector4(1, 0.5f, 0.2f, 1), $"{bb.BankerBet}G");
                    else
                        ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1), "-");

                    ImGui.TableSetColumnIndex(4);
                    if (bb != null && bb.TieBet > 0)
                        ImGui.TextColored(new Vector4(0.4f, 1, 0.4f, 1), $"{bb.TieBet}G");
                    else
                        ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1), "-");
                }
                ImGui.EndTable();
            }

            ImGui.Separator();
            DrawPlayersManagementTab();
        }

        private void DrawBaccaratHand(string label, List<Models.Card> hand, int score)
        {
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            float cardW = 44f, cardH = 62f, gap = 6f;

            Vector4 labelCol = label == "PLAYER" ? new Vector4(0.4f, 0.8f, 1, 1) : new Vector4(1, 0.5f, 0.2f, 1);
            ImGui.TextColored(labelCol, $"{label}  ({score})");

            var startPos = ImGui.GetCursorScreenPos();
            for (int i = 0; i < hand.Count; i++)
            {
                Models.Card card = hand[i];
                var tl = new Vector2(startPos.X + i * (cardW + gap), startPos.Y);
                var br = tl + new Vector2(cardW, cardH);

                drawList.AddRectFilled(tl, br, 0xFFFFFFFF, 4f);
                Vector4 bc = card.IsRed ? new Vector4(1, 0.2f, 0.2f, 1) : new Vector4(0.15f, 0.15f, 0.15f, 1);
                drawList.AddRect(tl, br, ImGui.ColorConvertFloat4ToU32(bc), 4f, ImDrawFlags.None, 1.5f);

                string txt = card.GetCardDisplay();
                var tsz = ImGui.CalcTextSize(txt);
                drawList.AddText(tl + new Vector2(cardW * 0.5f - tsz.X * 0.5f, cardH * 0.5f - tsz.Y * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(bc), txt);
            }
            ImGui.Dummy(new Vector2((cardW + gap) * Math.Max(hand.Count, 3), cardH + 4));
        }

        // ── BLACKJACK UI ─────────────────────────────────────────────────────────

        private int bjProxyBetPlayerIdx = 0;
        private int bjProxyBetAmt = 100;

        private void DrawDealerInterface()
        {
            // Game Status Header
            DrawGameStatusHeader();

            ImGui.Separator();

            // Main Game Controls
            ImGui.TextColored(new Vector4(0.5f, 1f, 1f, 1f), "DEALER CONTROLS");

            // Big action buttons
            if (engine.CurrentTable.GameState == Models.GameState.Lobby)
            {
                if (ImGui.Button("🎴 DEAL CARDS", new Vector2(200, 50)))
                {
                    engine.StartGame();
                }
                ImGui.SameLine();
                if (ImGui.Button("↶ UNDO", new Vector2(100, 50)))
                {
                    engine.Undo();
                }
            }
            else
            {
                ImGui.TextColored(new Vector4(1, 1, 0, 1f), "🎮 Game in Progress...");
                if (ImGui.Button("🛑 Reset to Lobby", new Vector2(150, 30)))
                {
                    engine.CurrentTable.GameState = Models.GameState.Lobby;
                    foreach (var player in engine.CurrentTable.Players.Values)
                    {
                        player.Hands.Clear();
                        player.CurrentBets.Clear();
                        player.IsStanding = false;
                    }
                    engine.CurrentTable.DealerHand.Clear();
                }
            }

            ImGui.Separator();

            // Actions for Current Player
            if (engine.CurrentTable.GameState == Models.GameState.Playing && 
                engine.CurrentTable.CurrentTurnIndex < engine.CurrentTable.TurnOrder.Count)
            {
                var currentPlayerName = engine.CurrentTable.TurnOrder[engine.CurrentTable.CurrentTurnIndex];
                var currentPlayer = engine.GetPlayer(currentPlayerName);

                if (currentPlayer != null)
                {
                    ImGui.TextColored(new Vector4(1, 1, 0, 1f), $"ACTIONS FOR {currentPlayerName.ToUpper()}");
                    ImGui.Text($"Time Remaining: {engine.CurrentTable.TurnTimeRemaining}s");

                    // Action buttons
                    if (ImGui.Button("HIT", new Vector2(80, 0)))
                    {
                        engine.PlayerHit(currentPlayerName);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("STAND", new Vector2(80, 0)))
                    {
                        engine.PlayerStand(currentPlayerName);
                    }
                    ImGui.SameLine();
                    if (currentPlayer.CanDoubleDown() && ImGui.Button("DOUBLE", new Vector2(80, 0)))
                    {
                        engine.PlayerDouble(currentPlayerName);
                    }
                    ImGui.SameLine();
                    if (currentPlayer.CanSplit() && ImGui.Button("SPLIT", new Vector2(80, 0)))
                    {
                        engine.PlayerSplit(currentPlayerName);
                    }

                    // Insurance if available
                    if (engine.CurrentTable.DealerHand.Count > 0 && engine.CurrentTable.DealerHand[0].IsAce && 
                        engine.CurrentTable.InsuranceEnabled && ImGui.Button("INSURANCE", new Vector2(80, 0)))
                    {
                        engine.PlayerInsurance(currentPlayerName);
                    }

                    ImGui.Separator();
                }
            }

            // Dealer Display
            DrawDealerSection();

            ImGui.Separator();

            // ── Quick Bet setter (proxy) ─────────────────────────────────────────
            if (engine.CurrentTable.Players.Count > 0 && engine.CurrentTable.GameState == Models.GameState.Lobby)
            {
                ImGui.TextColored(new Vector4(1, 1, 0, 1), "SET BET (dealer proxy)");
                var bjPlayerNames = engine.CurrentTable.Players.Values.Select(p => p.Name).ToArray();
                if (bjProxyBetPlayerIdx >= bjPlayerNames.Length) bjProxyBetPlayerIdx = 0;
                ImGui.SetNextItemWidth(150);
                ImGui.Combo("##bjbetplayerdrop", ref bjProxyBetPlayerIdx, bjPlayerNames, bjPlayerNames.Length);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80);
                ImGui.InputInt("##bjbetamt", ref bjProxyBetAmt);
                if (bjProxyBetAmt < engine.CurrentTable.MinBet) bjProxyBetAmt = engine.CurrentTable.MinBet;
                if (bjProxyBetAmt > engine.CurrentTable.MaxBet) bjProxyBetAmt = engine.CurrentTable.MaxBet;
                ImGui.SameLine();
                if (ImGui.Button("Set##bjsetbet"))
                    engine.SetPlayerBet(bjPlayerNames[bjProxyBetPlayerIdx], bjProxyBetAmt);
                ImGui.SameLine();
                if (ImGui.Button("Set All##bjsetallbet"))
                {
                    foreach (var pn in bjPlayerNames)
                        engine.SetPlayerBet(pn, bjProxyBetAmt);
                }
                ImGui.Separator();
            }

            // Players Management merged here
            DrawPlayersManagementTab();
        }

        private void DrawGameStatusHeader()
        {
            ImGui.Text($"🎯 State: {engine.CurrentTable.GameState}");
            ImGui.SameLine(200);
            ImGui.Text($"🤖 Mode: {engine.Mode}");
            ImGui.SameLine(350);
            if (engine.CurrentTable.GameState == Models.GameState.Playing)
            {
                ImGui.TextColored(new Vector4(1, 1, 0, 1), $"⏱️ Timer: {engine.CurrentTable.TurnTimeRemaining}s");
            }
            else
            {
                ImGui.Text($"⏱️ Timer: {engine.CurrentTable.TurnTimeLimit}s");
            }

            // Current turn info
            if (engine.CurrentTable.GameState == Models.GameState.Playing && 
                engine.CurrentTable.CurrentTurnIndex < engine.CurrentTable.TurnOrder.Count)
            {
                var currentPlayer = engine.CurrentTable.TurnOrder[engine.CurrentTable.CurrentTurnIndex];
                ImGui.TextColored(new Vector4(0, 1, 0, 1), $"🎮 Current Turn: {currentPlayer}");
            }
        }

        private void DrawDealerSection()
        {
            ImGui.TextColored(new Vector4(1, 0.5f, 0, 1f), "🎴 DEALER");

            if (engine.CurrentTable.DealerHand.Count > 0)
            {
                // Create a larger area for dealer cards - minimum 100px tall
                ImGui.BeginChild("DealerCardArea", new Vector2(0, 120), true); // 120px tall bordered area

                ImGui.Text("Cards: ");
                ImGui.SameLine();

                // Move cards 20 pixels to the right
                var startPos = ImGui.GetCursorScreenPos();
                ImGui.SetCursorScreenPos(startPos + new Vector2(20, 0));

                for (int cardIndex = 0; cardIndex < engine.CurrentTable.DealerHand.Count; cardIndex++)
                {
                    if (cardIndex > 0) ImGui.SameLine();

                    // Don't show hole card if not revealed
                    bool isHoleCard = cardIndex == 1 && !engine.CurrentTable.HoleCardRevealed;

                    var drawList = ImGui.GetWindowDrawList();
                    var pos = ImGui.GetCursorScreenPos();
                    var cardSize = new Vector2(64, 92); // Increased size for dealer area

                    if (isHoleCard)
                    {
                        // Draw hidden card - darker background with "?" 
                        drawList.AddRectFilled(pos, pos + cardSize, ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.3f, 1)));
                        drawList.AddRect(pos, pos + cardSize, ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.8f, 0.8f, 1f)), 4.0f, ImDrawFlags.RoundCornersAll, 2.0f);

                        var textPos = pos + cardSize * 0.5f;
                        var questionText = "?";
                        var textSize = ImGui.CalcTextSize(questionText);
                        textPos -= textSize * 0.5f;

                        drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)), questionText);
                    }
                    else
                    {
                        // Draw revealed card
                        var card = engine.CurrentTable.DealerHand[cardIndex];

                        // Card background (white)
                        drawList.AddRectFilled(pos, pos + cardSize, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)));

                        // Card border
                        Vector4 borderColor = card.IsRed ? new Vector4(1, 0.2f, 0.2f, 1f) : new Vector4(0.2f, 0.2f, 0.2f, 1f);
                        drawList.AddRect(pos, pos + cardSize, ImGui.ColorConvertFloat4ToU32(borderColor), 4.0f, ImDrawFlags.RoundCornersAll, 2.0f);

                        // Card text (center aligned)
                        var textPos = pos + cardSize * 0.5f;
                        var cardText = card.GetCardDisplay();
                        var textSize = ImGui.CalcTextSize(cardText);
                        textPos -= textSize * 0.5f;

                        drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(borderColor), cardText);
                    }

                    // Invisible button for interaction
                    ImGui.SetCursorScreenPos(pos);
                    ImGui.InvisibleButton($"dealercard_{cardIndex}", cardSize);

                    // Tooltip
                    if (ImGui.IsItemHovered())
                    {
                        if (isHoleCard)
                        {
                            ImGui.SetTooltip("Hidden Card");
                        }
                        else
                        {
                            ImGui.SetTooltip(engine.CurrentTable.DealerHand[cardIndex].GetCardDisplay());
                        }
                    }

                    // Move cursor for next card with more spacing
                    ImGui.SetCursorScreenPos(pos + new Vector2(cardSize.X + 8, 0));
                }

                ImGui.EndChild(); // End the dealer card area

                // Score display below the card area
                if (engine.CurrentTable.HoleCardRevealed)
                {
                    int dealerScore = engine.CurrentTable.GetDealerScore();
                    Vector4 dealerColor = dealerScore > 21 ? new Vector4(1, 0.5f, 0.5f, 1) : new Vector4(1, 1, 1, 1);
                    ImGui.TextColored(dealerColor, $"Total: {dealerScore}");
                }
                else
                {
                    ImGui.Text("Total: [Hidden]");
                }
            }
            else
            {
                ImGui.Text("No cards dealt");
            }
        }

        private void DrawQuickSettings()
        {
            ImGui.TextColored(new Vector4(0.5f, 1f, 1f, 1f), "QUICK SETTINGS");

            ImGui.Text("Mode:");
            ImGui.SameLine();
            if (ImGui.Button(engine.Mode == DealerMode.Auto ? "🤖 Auto" : "✋ Manual", new Vector2(100, 0)))
            {
                engine.Mode = engine.Mode == DealerMode.Auto ? DealerMode.Manual : DealerMode.Auto;
            }

            ImGui.Text($"Limits: {engine.CurrentTable.MinBet} - {engine.CurrentTable.MaxBet}");
            ImGui.SameLine();
            if (ImGui.Button("⚙️ Adjust", new Vector2(80, 0)))
            {
                // Could open a popup for limit adjustment
            }
        }

        private void DrawPlayersManagementTab()
        {
            ImGui.TextColored(new Vector4(0.2f, 1f, 0.8f, 1f), "👥 PLAYER MANAGEMENT");

            // Quick Add Section
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1, 1, 0.5f, 1f), "➕ ADD PLAYERS");

            ImGui.SetNextItemWidth(200);
            ImGui.InputTextWithHint("##quickAdd", "Player Name or Name@Server", ref quickAddPlayerName, 100);
            ImGui.SameLine();
            if (ImGui.Button("➕ Add Player", new Vector2(100, 0)))
            {
                if (!string.IsNullOrWhiteSpace(quickAddPlayerName))
                {
                    engine.AddPlayer(quickAddPlayerName.Trim());
                    quickAddPlayerName = string.Empty;
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("🎯 Add Target", new Vector2(100, 0)))
            {
                // TODO: Add current target functionality
                ImGui.OpenPopup("TargetNotImplemented");
            }

            if (ImGui.BeginPopup("TargetNotImplemented"))
            {
                ImGui.Text("Target detection not yet implemented.");
                ImGui.Text("For now, manually type the name above.");
                if (ImGui.Button("OK")) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }

            ImGui.Separator();

            // Players Table
            if (engine.CurrentTable.Players.Count == 0)
            {
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "No players added yet. Use the + Add Player button above.");
                return;
            }

            ImGui.TextColored(new Vector4(1, 1, 0.5f, 1f), $"📋 PLAYERS TABLE ({engine.CurrentTable.Players.Count} players)");

            var gameType    = engine.CurrentTable.GameType;
            bool isRoulette  = gameType == Models.GameType.Roulette;
            bool showBetCol  = gameType != Models.GameType.Craps && gameType != Models.GameType.ChocoboRacing;
            bool showCardsCol = gameType == Models.GameType.Blackjack;
            int c_afk     = showBetCol ? 4 : 3;
            int c_stats   = c_afk + 1;
            int c_cards   = showCardsCol ? c_stats + 1 : -1;
            int c_actions = c_stats + (showCardsCol ? 2 : 1);
            int colCount  = 6 + (showBetCol ? 1 : 0) + (showCardsCol ? 1 : 0);

            if (ImGui.BeginTable("PlayersTable", colCount, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
            {
                // Table Headers
                ImGui.TableSetupColumn("👤 Name",   ImGuiTableColumnFlags.WidthFixed, 120);
                ImGui.TableSetupColumn("🌐 Server", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("💰 Bank",   ImGuiTableColumnFlags.WidthFixed, 80);
                if (showBetCol)
                    ImGui.TableSetupColumn("🎰 Bet", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("💤 AFK", ImGuiTableColumnFlags.WidthFixed, 50);
                string statsLabel = gameType switch
                {
                    Models.GameType.Roulette      => "📊 R.Net",
                    Models.GameType.Craps         => "📊 C.Net",
                    Models.GameType.Baccarat      => "📊 B.Net",
                    Models.GameType.ChocoboRacing => "📊 CH.Net",
                    _                             => "📊 Stats"
                };
                ImGui.TableSetupColumn(statsLabel, ImGuiTableColumnFlags.WidthFixed, 100);
                if (showCardsCol)
                    ImGui.TableSetupColumn("🎴 Cards", ImGuiTableColumnFlags.WidthFixed, 300);
                ImGui.TableSetupColumn("🔧 Actions", ImGuiTableColumnFlags.WidthFixed, 160);
                ImGui.TableHeadersRow();

                var players = engine.CurrentTable.Players.Values.ToList();
                for (int i = 0; i < players.Count; i++)
                {
                    var player = players[i];
                    string playerKey = player.Name.ToUpper();

                    // LIVE UPDATE: Always sync editing fields with current player data
                    editingName[playerKey] = player.Name;
                    editingServer[playerKey] = GetDisplayServerName(player.Server);
                    editingBank[playerKey] = player.Bank.ToString();
                    editingBet[playerKey] = player.PersistentBet.ToString();

                    // Check if this player is currently playing
                    bool isCurrentPlayerTurn = engine.CurrentTable.GameState == Models.GameState.Playing &&
                                       engine.CurrentTable.CurrentTurnIndex < engine.CurrentTable.TurnOrder.Count &&
                                       engine.CurrentTable.TurnOrder[engine.CurrentTable.CurrentTurnIndex].Equals(player.Name.ToUpper(), StringComparison.OrdinalIgnoreCase);

                    // Set background color for current player
                    if (isCurrentPlayerTurn)
                    {
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.3f, 0.5f))); 
                    }

                    ImGui.TableNextRow();

                    // Highlight current turn
                    bool isCurrentTurn = engine.CurrentTable.CurrentTurnIndex < engine.CurrentTable.TurnOrder.Count &&
                        engine.CurrentTable.TurnOrder[engine.CurrentTable.CurrentTurnIndex].Equals(player.Name.ToUpper());

                    if (isCurrentTurn)
                    {
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0.5f, 0, 0.3f)));
                    }

                    // Name Column
                    ImGui.TableSetColumnIndex(0);
                    if (isCurrentTurn)
                    {
                        ImGui.TextColored(new Vector4(0, 1, 0, 1), "►");
                        ImGui.SameLine();
                    }

                    ImGui.SetNextItemWidth(-1);
                    string tempName = editingName[playerKey];
                    if (ImGui.InputText($"##name{i}", ref tempName, 50))
                    {
                        if (tempName != player.Name && !string.IsNullOrWhiteSpace(tempName))
                        {
                            // Rename player
                            string oldKey = playerKey;
                            string newKey = tempName.ToUpper();

                            if (!engine.CurrentTable.Players.ContainsKey(newKey))
                            {
                                engine.CurrentTable.Players.Remove(oldKey);
                                player.Name = tempName;
                                engine.CurrentTable.Players[newKey] = player;

                                // Update editing keys
                                if (editingName.ContainsKey(oldKey))
                                {
                                    editingName[newKey] = tempName;
                                    editingServer[newKey] = editingServer[oldKey];
                                    editingBank[newKey] = editingBank[oldKey];
                                    editingBet[newKey] = editingBet[oldKey];

                                    editingName.Remove(oldKey);
                                    editingServer.Remove(oldKey);
                                    editingBank.Remove(oldKey);
                                    editingBet.Remove(oldKey);
                                }
                            }
                        }
                    }

                    // Server Column
                    ImGui.TableSetColumnIndex(1);
                    ImGui.SetNextItemWidth(-1);
                    string tempServer = editingServer[playerKey];
                    if (ImGui.InputText($"##server{i}", ref tempServer, 20))
                    {
                        player.Server = tempServer;
                        editingServer[playerKey] = tempServer;
                    }

                    // Bank Column - LIVE UPDATING
                    ImGui.TableSetColumnIndex(2);
                    ImGui.SetNextItemWidth(-1);
                    string tempBank = editingBank[playerKey];

                    // Color code based on bank amount
                    Vector4 bankColor = player.Bank <= 0 ? new Vector4(1, 0.3f, 0.3f, 1f) : 
                                       player.Bank < 1000 ? new Vector4(1, 1, 0.5f, 1f) : 
                                       new Vector4(0.5f, 1f, 0.5f, 1f);
                    ImGui.PushStyleColor(ImGuiCol.Text, bankColor);

                    if (ImGui.InputText($"##bank{i}", ref tempBank, 20))
                    {
                        if (int.TryParse(tempBank, out int newBank) && newBank >= 0)
                        {
                            player.Bank = newBank;
                            editingBank[playerKey] = newBank.ToString();
                        }
                        else
                        {
                            // Reset to current valid value
                            editingBank[playerKey] = player.Bank.ToString();
                        }
                    }
                    ImGui.PopStyleColor();

                    // Bet Column - LIVE UPDATING (hidden for Craps)
                    if (showBetCol)
                    {
                        ImGui.TableSetColumnIndex(3);
                        ImGui.SetNextItemWidth(-1);
                        string tempBet = editingBet[playerKey];

                        bool validBet = player.PersistentBet >= engine.CurrentTable.MinBet &&
                                       player.PersistentBet <= engine.CurrentTable.MaxBet &&
                                       player.PersistentBet <= player.Bank;
                        Vector4 betColor = !validBet ? new Vector4(1, 0.5f, 0.5f, 1f) : new Vector4(1, 1, 1, 1f);
                        ImGui.PushStyleColor(ImGuiCol.Text, betColor);

                        if (ImGui.InputText($"##bet{i}", ref tempBet, 20))
                        {
                            if (int.TryParse(tempBet, out int newBet) && newBet >= 0)
                            {
                                player.PersistentBet = newBet;
                                editingBet[playerKey] = newBet.ToString();
                            }
                            else
                                editingBet[playerKey] = player.PersistentBet.ToString();
                        }
                        ImGui.PopStyleColor();
                    }

                    // AFK Column
                    ImGui.TableSetColumnIndex(c_afk);
                    bool isAfk = player.IsAfk;
                    if (ImGui.Checkbox($"##afk{i}", ref isAfk))
                        engine.ToggleAFK(player.Name);

                    // Stats Column
                    ImGui.TableSetColumnIndex(c_stats);
                    switch (gameType)
                    {
                        case Models.GameType.Roulette:
                        {
                            int net = player.RouletteNetGains;
                            Vector4 nc = net > 0 ? new Vector4(0, 1, 0, 1f) : net < 0 ? new Vector4(1, 0.4f, 0.4f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                            ImGui.TextColored(nc, net >= 0 ? $"+{net}G" : $"{net}G");
                            break;
                        }
                        case Models.GameType.Craps:
                        {
                            int net = player.CrapsNetGains;
                            Vector4 nc = net > 0 ? new Vector4(0, 1, 0, 1f) : net < 0 ? new Vector4(1, 0.4f, 0.4f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                            ImGui.TextColored(nc, net >= 0 ? $"+{net}G" : $"{net}G");
                            break;
                        }
                        case Models.GameType.Baccarat:
                        {
                            int net = player.BaccaratNetGains;
                            Vector4 nc = net > 0 ? new Vector4(0, 1, 0, 1f) : net < 0 ? new Vector4(1, 0.4f, 0.4f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                            ImGui.TextColored(nc, net >= 0 ? $"+{net}G" : $"{net}G");
                            break;
                        }
                        case Models.GameType.ChocoboRacing:
                        {
                            int net = player.ChocoboNetGains;
                            Vector4 nc = net > 0 ? new Vector4(0, 1, 0, 1f) : net < 0 ? new Vector4(1, 0.4f, 0.4f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                            ImGui.TextColored(nc, net >= 0 ? $"+{net}G" : $"{net}G");
                            break;
                        }
                        default:
                            if (player.GamesPlayed > 0)
                            {
                                float winRate = (float)player.GetWinPercentage();
                                Vector4 sc = winRate > 60f ? new Vector4(0, 1, 0, 1f) : winRate > 40f ? new Vector4(1, 1, 0, 1f) : new Vector4(1, 0.5f, 0.5f, 1f);
                                ImGui.TextColored(sc, $"{player.GamesWon}/{player.GamesPlayed}");
                                ImGui.TextColored(sc, $"({winRate:F0}%)");
                            }
                            else
                                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "No games");
                            break;
                    }

                    // Cards Column — only shown for Blackjack
                    if (showCardsCol)
                    {
                        ImGui.TableSetColumnIndex(c_cards);
                    if (player.Hands.Count > 0)
                    {
                        for (int handIndex = 0; handIndex < player.Hands.Count; handIndex++)
                        {
                            var handInfo = player.GetHandInfo(handIndex);
                            if (handInfo.Cards.Count > 0)
                            {
                                if (handIndex > 0) 
                                {
                                    ImGui.Text("");  // New line for multiple hands
                                }

                                string handLabel = player.Hands.Count > 1 ? $"H{handIndex + 1}: " : "";

                                // Highlight active hand
                                if (handIndex == player.ActiveHandIndex && isCurrentTurn)
                                {
                                    ImGui.TextColored(new Vector4(0, 1, 0, 1f), handLabel);
                                }
                                else
                                {
                                    ImGui.Text(handLabel);
                                }
                                ImGui.SameLine();

                                // Display cards with PNG images or fallback styling
                                for (int cardIndex = 0; cardIndex < handInfo.Cards.Count; cardIndex++)
                                {
                                    var card = handInfo.Cards[cardIndex];
                                    var texture = plugin.GetCardTexture(card.GetImageFileName());

                                    if (cardIndex > 0) ImGui.SameLine();

                                    // Always use styled card representation
                                    var drawList = ImGui.GetWindowDrawList();
                                    var pos = ImGui.GetCursorScreenPos();
                                    var cardSize = new Vector2(48, 68);

                                    // Card background (white)
                                    drawList.AddRectFilled(pos, pos + cardSize, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)));

                                    // Card border
                                    Vector4 borderColor = card.IsRed ? new Vector4(1, 0.2f, 0.2f, 1f) : new Vector4(0.2f, 0.2f, 0.2f, 1f);
                                    drawList.AddRect(pos, pos + cardSize, ImGui.ColorConvertFloat4ToU32(borderColor), 4.0f, ImDrawFlags.RoundCornersAll, 2.0f);

                                    // Card text (center aligned)
                                    var textPos = pos + cardSize * 0.5f;
                                    var cardText = card.GetCardDisplay();
                                    var textSize = ImGui.CalcTextSize(cardText);
                                    textPos -= textSize * 0.5f;

                                    drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(borderColor), cardText);

                                    // Invisible button for interaction
                                    ImGui.SetCursorScreenPos(pos);
                                    ImGui.InvisibleButton($"card_{playerKey}_{handIndex}_{cardIndex}", cardSize);

                                    // Tooltip
                                    if (ImGui.IsItemHovered())
                                    {
                                        ImGui.SetTooltip($"{card.GetCardDisplay()}");
                                    }

                                    // Move cursor for next card
                                    ImGui.SetCursorScreenPos(pos + new Vector2(cardSize.X + 4, 0));
                                }

                                // Hand value - LIVE UPDATING
                                ImGui.SameLine();
                                Vector4 valueColor = handInfo.IsBust ? new Vector4(1, 0.5f, 0.5f, 1f) : 
                                                     handInfo.IsBlackjack ? new Vector4(1, 1, 0, 1f) :
                                                     new Vector4(0.8f, 1, 0.8f, 1f);
                                ImGui.TextColored(valueColor, $" -> {handInfo.GetHandDescription()}");

                                // Show bet for this hand
                                if (handIndex < player.CurrentBets.Count)
                                {
                                    ImGui.SameLine();
                                    ImGui.TextColored(new Vector4(1, 1, 0.5f, 1f), $" (${player.CurrentBets[handIndex]})");
                                }
                            }
                        }
                    }
                    else
                    {
                        // Center "Cards" text vertically when no cards
                        var availableHeight = ImGui.GetContentRegionAvail().Y;
                        if (availableHeight > 20)
                        {
                            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (availableHeight - 20) * 0.5f);
                        }
                        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Cards");
                    }
                    } // end !isRoulette cards block

                    // Actions Column
                    ImGui.TableSetColumnIndex(c_actions);
                    if (ImGui.Button($"Kick##kick{i}", new Vector2(50, 0)))
                    {
                        engine.RemovePlayer(player.Name);
                        editingName.Remove(playerKey);
                        editingServer.Remove(playerKey);
                        editingBank.Remove(playerKey);
                        editingBet.Remove(playerKey);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button($"DM##dm{i}", new Vector2(40, 0)))
                    {
                        string betInfo = player.PersistentBet > 0 ? $"Bet: {player.PersistentBet}" : "No bet placed";
                        int bjNet = player.TotalWinnings;
                        int rNet = player.RouletteNetGains;
                        string netInfo = $"BJ net: {(bjNet >= 0 ? "+" : "")}{bjNet}G | R net: {(rNet >= 0 ? "+" : "")}{rNet}G";
                        engine.OnPlayerTell?.Invoke($"{player.Name}@{player.Server}", $"Bank: {player.Bank} | {betInfo} | {netInfo}");
                    }
                    ImGui.SameLine();
                    if (ImGui.Button($"{(player.IsAfk ? "[AFK]" : "AFK")}##afkbtn{i}", new Vector2(38, 0)))
                        engine.ToggleAFK(player.Name);
                }  // end player loop

                ImGui.EndTable();
            }

            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.55f, 0.10f, 0.10f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.20f, 0.20f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(1.00f, 0.30f, 0.30f, 1f));
            if (ImGui.Button("\u26d4 FORCE STOP & REFUND BETS", new Vector2(-1, 28)))
            {
                switch (engine.CurrentTable.GameType)
                {
                    case Models.GameType.Roulette:      plugin.RouletteEngine.ForceStop(); break;
                    case Models.GameType.Craps:         plugin.CrapsEngine.ForceStop();    break;
                    case Models.GameType.Baccarat:      plugin.BaccaratEngine.ForceStop(); break;
                    case Models.GameType.ChocoboRacing: plugin.ChocoboEngine.ForceStop();  break;
                    default:                            engine.ForceStop();                break;
                }
            }
            ImGui.PopStyleColor(3);
        }

        // ── CHOCOBO RACING UI ─────────────────────────────────────────────────────

        private int chocoSelectedPlayerIdx = 0;
        private int chocoSelectedRacerIdx  = 0;
        private int chocoBetAmt = 50;

        private void DrawChocoboInterface()
        {
            var table  = engine.CurrentTable;
            var chocobo = plugin.ChocoboEngine;
            bool racing  = table.ChocoboRacePhase == Models.ChocoboRacePhase.Racing;
            bool complete = table.ChocoboRacePhase == Models.ChocoboRacePhase.Complete;

            // Phase banner
            ImGui.TextColored(new Vector4(1, 0.84f, 0, 1),
                racing  ? "🐦 Race in progress — 30 second race!" :
                complete ? "🐦 Race complete! Payouts processed." :
                "🐦 CHOCOBO RACING — Place bets then Start Race!");

            ImGui.Separator();

            // ── Roster ───────────────────────────────────────────────────────────
            ImGui.TextColored(new Vector4(0.5f, 1, 1, 1), "RACE ROSTER");
            if (ImGui.BeginTable("##chocoRoster", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("#",    ImGuiTableColumnFlags.WidthFixed,   22);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("SPD",  ImGuiTableColumnFlags.WidthFixed,   38);
                ImGui.TableSetupColumn("END",  ImGuiTableColumnFlags.WidthFixed,   38);
                ImGui.TableSetupColumn("Odds", ImGuiTableColumnFlags.WidthFixed,   48);
                ImGui.TableSetupColumn("Bets", ImGuiTableColumnFlags.WidthFixed,   60);
                ImGui.TableHeadersRow();

                for (int i = 0; i < chocobo.Roster.Length; i++)
                {
                    var racer = chocobo.Roster[i];
                    bool isWinner = complete && chocobo.WinnerIndex == i;

                    ImGui.TableNextRow();

                    if (isWinner)
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0.4f, 0, 0.35f)));

                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text($"{racer.Number}");

                    ImGui.TableSetColumnIndex(1);
                    if (isWinner)
                        ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), $"★ {racer.Name}");
                    else
                        ImGui.Text(racer.Name);

                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text($"{racer.Speed}");

                    ImGui.TableSetColumnIndex(3);
                    ImGui.Text($"{racer.Endurance}");

                    ImGui.TableSetColumnIndex(4);
                    ImGui.TextColored(new Vector4(1, 0.84f, 0, 1), $"{racer.Odds:0.0}x");

                    ImGui.TableSetColumnIndex(5);
                    int totalOnRacer = table.ChocoboBets.Values.Where(b => b.RacerIndex == i).Sum(b => b.Amount);
                    if (totalOnRacer > 0)
                        ImGui.TextColored(new Vector4(0.4f, 1, 0.4f, 1), $"{totalOnRacer}G");
                    else
                        ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1), "-");
                }
                ImGui.EndTable();
            }

            ImGui.Separator();

            // ── Live race progress bars ───────────────────────────────────────────
            if (racing || complete)
            {
                ImGui.TextColored(new Vector4(1, 0.8f, 0, 1), racing ? "RACE IN PROGRESS" : "FINAL POSITIONS");

                float maxProg = chocobo.GetMaxTotalProgress();
                var order = Enumerable.Range(0, chocobo.Roster.Length)
                    .OrderByDescending(r => chocobo.GetRacerProgress(r))
                    .ToList();

                for (int rank = 0; rank < order.Count; rank++)
                {
                    int  ri    = order[rank];
                    var  racer = chocobo.Roster[ri];
                    float prog = chocobo.GetRacerProgress(ri) / maxProg;
                    bool isW   = complete && chocobo.WinnerIndex == ri;

                    Vector4 labelCol = isW
                        ? new Vector4(0.3f, 1f, 0.3f, 1f)
                        : new Vector4(0.9f, 0.9f, 0.9f, 1f);

                    ImGui.TextColored(labelCol, $"#{racer.Number} {racer.Name}");
                    ImGui.SameLine(185);
                    ImGui.ProgressBar(prog, new Vector2(-1, 0), $"{(int)(prog * 100)}%");
                }

                ImGui.Separator();
            }

            // ── Start / New Race buttons ──────────────────────────────────────────
            if (!racing && !complete)
            {
                if (ImGui.Button("START RACE", new Vector2(130, 32)))
                {
                    if (!chocobo.StartRace(out string err))
                        engine.Announce(err);
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "Place bets first. Admin: >START");
            }
            else if (complete)
            {
                if (ImGui.Button("NEW RACE", new Vector2(110, 28)))
                {
                    chocobo.OpenBetting(out _);
                }
            }
            else
            {
                double elapsed = (DateTime.Now - table.ChocoboRaceStart).TotalSeconds;
                double remaining = Math.Max(0, 30.0 - elapsed);
                ImGui.TextColored(new Vector4(1, 1, 0, 1), $"Time remaining: {remaining:F0}s");
            }

            ImGui.Separator();

            // ── Bet controls (only during betting phase) ──────────────────────────
            if (!racing && !complete)
            {
                ImGui.TextColored(new Vector4(1, 1, 0, 1), "PLACE BET");

                var pNames = table.Players.Values.Select(p => p.Name).ToArray();
                if (pNames.Length > 0)
                {
                    if (chocoSelectedPlayerIdx >= pNames.Length) chocoSelectedPlayerIdx = 0;
                    ImGui.SetNextItemWidth(140);
                    ImGui.Combo("##chocoplayer", ref chocoSelectedPlayerIdx, pNames, pNames.Length);
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(70);
                    ImGui.InputInt("##chocoamt", ref chocoBetAmt);
                    if (chocoBetAmt < table.MinBet) chocoBetAmt = table.MinBet;
                    ImGui.SameLine();
                    var rNames = chocobo.Roster.Select(r => $"#{r.Number} {r.Name}").ToArray();
                    ImGui.SetNextItemWidth(170);
                    ImGui.Combo("##chocoracer", ref chocoSelectedRacerIdx, rNames, rNames.Length);
                    ImGui.SameLine();
                    if (ImGui.Button("Bet##chocobet"))
                        chocobo.PlaceBet(pNames[chocoSelectedPlayerIdx], chocoSelectedRacerIdx + 1, chocoBetAmt, out _);
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "No players at table.");
                }

                ImGui.Separator();
            }

            // ── Current bets table ────────────────────────────────────────────────
            if (table.ChocoboBets.Count > 0)
            {
                ImGui.TextColored(new Vector4(0.5f, 1, 1, 1), "CURRENT BETS");
                if (ImGui.BeginTable("##chocobets", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Player",  ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Chocobo", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Bet",     ImGuiTableColumnFlags.WidthFixed, 60);
                    ImGui.TableSetupColumn("Odds",    ImGuiTableColumnFlags.WidthFixed, 48);
                    ImGui.TableHeadersRow();

                    foreach (var kvp in table.ChocoboBets)
                    {
                        var bet   = kvp.Value;
                        var racer = chocobo.Roster[bet.RacerIndex];
                        var plr   = engine.GetPlayer(kvp.Key);

                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0); ImGui.Text(plr?.Name ?? kvp.Key);
                        ImGui.TableSetColumnIndex(1); ImGui.Text($"#{racer.Number} {racer.Name}");
                        ImGui.TableSetColumnIndex(2); ImGui.TextColored(new Vector4(1, 1, 0.5f, 1), $"{bet.Amount}G");
                        ImGui.TableSetColumnIndex(3); ImGui.TextColored(new Vector4(1, 0.84f, 0, 1), $"{racer.Odds:0.0}x");
                    }
                    ImGui.EndTable();
                }
                ImGui.Separator();
            }

            DrawPlayersManagementTab();
        }

        private void DrawAdminTab()
        {
            ImGui.TextColored(new Vector4(1, 0.84f, 0, 1), "ADMIN CONTROLS");

            // Admin Name Setting
            ImGui.Text("Admin Name:");
            string adminNameTemp = AdminName;
            ImGui.SetNextItemWidth(200);
            if (ImGui.InputText("##adminName", ref adminNameTemp, 100))
                AdminName = adminNameTemp;
            ImGui.SameLine();
            ImGui.Text($"Current: {(string.IsNullOrEmpty(AdminName) ? "None" : AdminName)}");

            ImGui.Separator();

            // Message delay slider
            ImGui.TextColored(new Vector4(0.5f, 1, 1, 1), "MESSAGE QUEUE DELAY");
            ImGui.SetNextItemWidth(280);
            int delayMs = engine.CurrentTable.MessageDelayMs;
            if (ImGui.SliderInt("ms##msgdelay", ref delayMs, 1000, 6000))
                engine.CurrentTable.MessageDelayMs = delayMs;
            ImGui.SameLine();
            ImGui.Text($"({delayMs / 1000.0:F1}s)");

            // Announce new players toggle
            bool announce = engine.CurrentTable.AnnounceNewPlayers;
            if (ImGui.Checkbox("Announce when players are added to table", ref announce))
                engine.CurrentTable.AnnounceNewPlayers = announce;

            ImGui.Separator();

            // Game State & Mode
            ImGui.Text($"Game State: {engine.CurrentTable.GameState}");
            ImGui.Text($"Current Mode: {engine.Mode}");

            if (ImGui.Button("Set Auto Mode", new Vector2(120, 0)))
                engine.Mode = DealerMode.Auto;
            ImGui.SameLine();
            if (ImGui.Button("Set Manual Mode", new Vector2(120, 0)))
                engine.Mode = DealerMode.Manual;

            ImGui.Separator();

            // Table Controls
            ImGui.TextColored(new Vector4(0.5f, 1f, 1f, 1f), "TABLE CONTROLS");

            if (ImGui.Button("DEAL", new Vector2(100, 0)))
            {
                engine.StartGame();
            }
            ImGui.SameLine();
            if (ImGui.Button("UNDO", new Vector2(100, 0)))
            {
                engine.Undo();
            }
            ImGui.SameLine();
            if (ImGui.Button("Reset to Lobby", new Vector2(120, 0)))
            {
                engine.CurrentTable.GameState = Models.GameState.Lobby;
                engine.CurrentTable.Players.Clear();
            }

            if (ImGui.Button("Table Status", new Vector2(120, 0)))
            {
                DisplayTableStatus();
            }

            ImGui.Separator();

            // Limits & Timer
            ImGui.TextColored(new Vector4(0.5f, 1f, 1f, 1f), "LIMITS & TIMER");

            ImGui.Text("Bet Limits:");
            int minBet = engine.CurrentTable.MinBet;
            ImGui.SetNextItemWidth(100);
            if (ImGui.DragInt("Min##minBet", ref minBet, 1, 1, 10000))
                engine.CurrentTable.MinBet = minBet;
            ImGui.SameLine();
            int maxBet = engine.CurrentTable.MaxBet;
            ImGui.SetNextItemWidth(100);
            if (ImGui.DragInt("Max##maxBet", ref maxBet, 1, 1, 10000))
                engine.CurrentTable.MaxBet = maxBet;

            // Max splits
            ImGui.Text("Max Splits:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            int maxSplits = engine.CurrentTable.MaxSplitsAllowed;
            string[] splitOptions = { "0 (none)", "1", "2", "3", "4" };
            if (ImGui.Combo("##maxsplits", ref maxSplits, splitOptions, splitOptions.Length))
                engine.CurrentTable.MaxSplitsAllowed = maxSplits;

            // Persistent deck
            bool persistDeck = engine.CurrentTable.PersistentDeck;
            if (ImGui.Checkbox("Persistent deck (don't reshuffle between rounds)", ref persistDeck))
            {
                engine.CurrentTable.PersistentDeck = persistDeck;
                if (!persistDeck) engine.CurrentTable.DealtCards.Clear();
            }

            ImGui.Text($"Timer: {engine.CurrentTable.TurnTimeLimit}s | Remaining: {engine.CurrentTable.TurnTimeRemaining}s");
            int timer = engine.CurrentTable.TurnTimeLimit;
            ImGui.SetNextItemWidth(200);
            if (ImGui.DragInt("Timer Limit##timer", ref timer, 1, 5, 300))
            {
                engine.CurrentTable.TurnTimeLimit = timer;
            }

            // Manual Dealer Actions (when in Manual mode)
            if (engine.Mode == DealerMode.Manual && engine.CurrentTable.GameState == Models.GameState.Playing)
            {
                ImGui.Separator();
                ImGui.TextColored(new Vector4(1, 0.5f, 0.5f, 1f), "MANUAL DEALER ACTIONS");

                if (engine.CurrentTable.CurrentTurnIndex < engine.CurrentTable.TurnOrder.Count)
                {
                    var currentPlayer = engine.CurrentTable.TurnOrder[engine.CurrentTable.CurrentTurnIndex];
                    ImGui.Text($"Current Player: {currentPlayer}");

                    if (ImGui.Button("Force HIT", new Vector2(80, 0)))
                    {
                        engine.PlayerHit(currentPlayer);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Force STAND", new Vector2(80, 0)))
                    {
                        engine.PlayerStand(currentPlayer);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Force DOUBLE", new Vector2(80, 0)))
                    {
                        engine.PlayerDouble(currentPlayer);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Force SPLIT", new Vector2(80, 0)))
                    {
                        engine.PlayerSplit(currentPlayer);
                    }
                }
            }
        }

        private void DrawPlayerManagementTab()
        {
            ImGui.TextColored(new Vector4(0.2f, 1f, 0.8f, 1f), "PLAYER MANAGEMENT");

            // Add Player
            ImGui.Text("Add Player:");
            ImGui.SetNextItemWidth(200);
            ImGui.InputText("##addPlayer", ref playerAddName, 100);
            ImGui.SameLine();
            if (ImGui.Button("Add##addBtn", new Vector2(60, 0)))
            {
                if (!string.IsNullOrEmpty(playerAddName))
                {
                    engine.AddPlayer(playerAddName);
                    playerAddName = string.Empty;
                }
            }

            // Remove Player
            ImGui.Text("Remove Player:");
            ImGui.SetNextItemWidth(200);
            ImGui.InputText("##removePlayer", ref playerRemoveName, 100);
            ImGui.SameLine();
            if (ImGui.Button("Remove##removeBtn", new Vector2(60, 0)))
            {
                if (!string.IsNullOrEmpty(playerRemoveName))
                {
                    engine.RemovePlayer(playerRemoveName);
                    playerRemoveName = string.Empty;
                }
            }

            ImGui.Separator();

            // Bank Management
            ImGui.TextColored(new Vector4(1, 1, 0.5f, 1f), "BANK MANAGEMENT");

            ImGui.Text("Add to Bank:");
            ImGui.SetNextItemWidth(150);
            ImGui.InputText("Player##bankPlayer", ref playerBankName, 100);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.InputText("Amount##bankAmount", ref playerBankAmount, 20);
            ImGui.SameLine();
            if (ImGui.Button("Add to Bank##addBank", new Vector2(100, 0)))
            {
                if (!string.IsNullOrEmpty(playerBankName) && int.TryParse(playerBankAmount, out int amount))
                {
                    engine.AddPlayerBank(playerBankName, amount);
                    playerBankName = string.Empty;
                    playerBankAmount = string.Empty;
                }
            }

            ImGui.Text("Set Bank:");
            ImGui.SetNextItemWidth(150);
            ImGui.InputText("Player##setBankPlayer", ref playerBankName, 100);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.InputText("Amount##setBankAmount", ref playerSetBankAmount, 20);
            ImGui.SameLine();
            if (ImGui.Button("Set Bank##setBank", new Vector2(100, 0)))
            {
                if (!string.IsNullOrEmpty(playerBankName) && int.TryParse(playerSetBankAmount, out int amount))
                {
                    engine.SetPlayerBank(playerBankName, amount);
                    playerBankName = string.Empty;
                    playerSetBankAmount = string.Empty;
                }
            }

            ImGui.Separator();

            // Other Player Actions
            ImGui.TextColored(new Vector4(1, 1, 0.5f, 1f), "PLAYER ACTIONS");

            // Set Bet
            ImGui.Text("Set Player Bet:");
            ImGui.SetNextItemWidth(150);
            ImGui.InputText("Player##betPlayer", ref playerBankName, 100);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.InputText("Bet##betAmount", ref playerBetAmount, 20);
            ImGui.SameLine();
            if (ImGui.Button("Set Bet##setBet", new Vector2(100, 0)))
            {
                if (!string.IsNullOrEmpty(playerBankName) && int.TryParse(playerBetAmount, out int amount))
                {
                    var player = engine.CurrentTable.Players.Values.FirstOrDefault(p => 
                        p.Name.Equals(playerBankName, StringComparison.OrdinalIgnoreCase));
                    if (player != null)
                    {
                        player.PersistentBet = amount;
                        playerBankName = string.Empty;
                        playerBetAmount = string.Empty;
                    }
                }
            }

            // Rename Player
            ImGui.Text("Rename Player:");
            ImGui.SetNextItemWidth(120);
            ImGui.InputText("Old##renameOld", ref playerRenameOld, 100);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            ImGui.InputText("New##renameNew", ref playerRenameNew, 100);
            ImGui.SameLine();
            if (ImGui.Button("Rename##renameBtn", new Vector2(80, 0)))
            {
                if (!string.IsNullOrEmpty(playerRenameOld) && !string.IsNullOrEmpty(playerRenameNew))
                {
                    RenamePlayer(playerRenameOld, playerRenameNew);
                    playerRenameOld = string.Empty;
                    playerRenameNew = string.Empty;
                }
            }

            // Set Server
            ImGui.Text("Set Player Server:");
            ImGui.SetNextItemWidth(150);
            ImGui.InputText("Player##serverPlayer", ref playerServerName, 100);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.InputText("Server##serverValue", ref playerServerValue, 50);
            ImGui.SameLine();
            if (ImGui.Button("Set Server##setServer", new Vector2(100, 0)))
            {
                if (!string.IsNullOrEmpty(playerServerName) && !string.IsNullOrEmpty(playerServerValue))
                {
                    var player = engine.CurrentTable.Players.Values.FirstOrDefault(p => 
                        p.Name.Equals(playerServerName, StringComparison.OrdinalIgnoreCase));
                    if (player != null)
                    {
                        player.Server = playerServerValue;
                        playerServerName = string.Empty;
                        playerServerValue = string.Empty;
                    }
                }
            }

            // Kick Player
            ImGui.Text("Kick Player:");
            ImGui.SetNextItemWidth(200);
            ImGui.InputText("##kickPlayer", ref kickPlayerName, 100);
            ImGui.SameLine();
            if (ImGui.Button("Kick##kickBtn", new Vector2(60, 0)))
            {
                if (!string.IsNullOrEmpty(kickPlayerName))
                {
                    engine.RemovePlayer(kickPlayerName);
                    kickPlayerName = string.Empty;
                }
            }

            // AFK Toggle
            ImGui.Text("Toggle AFK:");
            ImGui.SetNextItemWidth(200);
            ImGui.InputText("##afkPlayer", ref afkPlayerName, 100);
            ImGui.SameLine();
            if (ImGui.Button("Toggle AFK##afkBtn", new Vector2(100, 0)))
            {
                if (!string.IsNullOrEmpty(afkPlayerName))
                {
                    engine.ToggleAFK(afkPlayerName);
                    afkPlayerName = string.Empty;
                }
            }
        }

        private void DrawGameActionsTab()
        {
            ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), "GAME ACTIONS");

            if (engine.CurrentTable.GameState == Models.GameState.Playing)
            {
                if (engine.CurrentTable.CurrentTurnIndex < engine.CurrentTable.TurnOrder.Count)
                {
                    var currentPlayer = engine.CurrentTable.TurnOrder[engine.CurrentTable.CurrentTurnIndex];
                    ImGui.Text($"Current Turn: {currentPlayer}");
                    ImGui.Text($"Time Remaining: {engine.CurrentTable.TurnTimeRemaining}s");

                    ImGui.Separator();

                    ImGui.TextColored(new Vector4(1, 1, 0, 1), "PLAYER ACTIONS (Force as Admin):");

                    if (ImGui.Button("HIT", new Vector2(100, 0)))
                    {
                        engine.PlayerHit(currentPlayer);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("STAND", new Vector2(100, 0)))
                    {
                        engine.PlayerStand(currentPlayer);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("DOUBLE DOWN", new Vector2(100, 0)))
                    {
                        engine.PlayerDouble(currentPlayer);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("SPLIT", new Vector2(100, 0)))
                    {
                        engine.PlayerSplit(currentPlayer);
                    }

                    // Insurance button (if dealer shows ace)
                    if (engine.CurrentTable.DealerHand.Count > 0 && engine.CurrentTable.DealerHand[0].IsAce && engine.CurrentTable.InsuranceEnabled)
                    {
                        ImGui.Separator();
                        ImGui.TextColored(new Vector4(0, 1, 1, 1f), "INSURANCE AVAILABLE");
                        if (ImGui.Button("BUY INSURANCE", new Vector2(120, 0)))
                        {
                            engine.PlayerInsurance(currentPlayer);
                        }
                    }
                }
                else
                {
                    ImGui.Text("All players have finished their turns.");
                }
            }
            else
            {
                ImGui.Text("No active game. Use DEAL to start a game.");
            }

            ImGui.Separator();

            // Quick Player Commands (simulate as any player)
            ImGui.TextColored(new Vector4(1, 0.5f, 1, 1f), "SIMULATE PLAYER COMMANDS");
            ImGui.Text("(These simulate commands as if a player typed them)");

            ImGui.SetNextItemWidth(150);
            ImGui.InputText("As Player##simPlayer", ref simulatePlayerName, 100);

            if (!string.IsNullOrEmpty(simulatePlayerName))
            {
                ImGui.SameLine();
                if (ImGui.Button("Simulate HIT##simHit", new Vector2(80, 0)))
                {
                    plugin.CommandParser.Parse(simulatePlayerName, ">HIT", AdminName, engine.Mode);
                }
                ImGui.SameLine();
                if (ImGui.Button("Simulate STAND##simStand", new Vector2(80, 0)))
                {
                    plugin.CommandParser.Parse(simulatePlayerName, ">STAND", AdminName, engine.Mode);
                }

                // Second row of simulation buttons
                if (ImGui.Button("Simulate DOUBLE##simDouble", new Vector2(80, 0)))
                {
                    plugin.CommandParser.Parse(simulatePlayerName, ">DOUBLE", AdminName, engine.Mode);
                }
                ImGui.SameLine();
                if (ImGui.Button("Simulate SPLIT##simSplit", new Vector2(80, 0)))
                {
                    plugin.CommandParser.Parse(simulatePlayerName, ">SPLIT", AdminName, engine.Mode);
                }
                ImGui.SameLine();
                if (ImGui.Button("Simulate INSURANCE##simInsurance", new Vector2(80, 0)))
                {
                    plugin.CommandParser.Parse(simulatePlayerName, ">INSURANCE", AdminName, engine.Mode);
                }
            }
        }

        private void DrawPlayersTab()
        {
            ImGui.TextColored(new Vector4(0.2f, 1f, 0.8f, 1f), $"PLAYERS ({engine.CurrentTable.Players.Count})");

            // Dealer section
            if (engine.CurrentTable.DealerHand.Count > 0)
            {
                ImGui.Separator();
                ImGui.TextColored(new Vector4(1, 0.5f, 0, 1f), "🎴 DEALER");

                // Display dealer cards with fancy rendering
                ImGui.Text("Cards: ");
                ImGui.SameLine();

                for (int cardIndex = 0; cardIndex < engine.CurrentTable.DealerHand.Count; cardIndex++)
                {
                    if (cardIndex > 0) ImGui.SameLine();

                    // Don't show hole card if not revealed
                    bool isHoleCard = cardIndex == 1 && !engine.CurrentTable.HoleCardRevealed;

                    var drawList = ImGui.GetWindowDrawList();
                    var pos = ImGui.GetCursorScreenPos();
                    var cardSize = new Vector2(48, 68);

                    if (isHoleCard)
                    {
                        // Draw hidden card - darker background with "?" 
                        drawList.AddRectFilled(pos, pos + cardSize, ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.3f, 1)));
                        drawList.AddRect(pos, pos + cardSize, ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.8f, 0.8f, 1f)), 4.0f, ImDrawFlags.RoundCornersAll, 2.0f);

                        var textPos = pos + cardSize * 0.5f;
                        var questionText = "?";
                        var textSize = ImGui.CalcTextSize(questionText);
                        textPos -= textSize * 0.5f;

                        drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)), questionText);
                    }
                    else
                    {
                        // Draw revealed card
                        var card = engine.CurrentTable.DealerHand[cardIndex];

                        // Card background (white)
                        drawList.AddRectFilled(pos, pos + cardSize, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)));

                        // Card border
                        Vector4 borderColor = card.IsRed ? new Vector4(1, 0.2f, 0.2f, 1f) : new Vector4(0.2f, 0.2f, 0.2f, 1f);
                        drawList.AddRect(pos, pos + cardSize, ImGui.ColorConvertFloat4ToU32(borderColor), 4.0f, ImDrawFlags.RoundCornersAll, 2.0f);

                        // Card text (center aligned)
                        var textPos = pos + cardSize * 0.5f;
                        var cardText = card.GetCardDisplay();
                        var textSize = ImGui.CalcTextSize(cardText);
                        textPos -= textSize * 0.5f;

                        drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(borderColor), cardText);
                    }

                    // Invisible button for interaction
                    ImGui.SetCursorScreenPos(pos);
                    ImGui.InvisibleButton($"dealercard_{cardIndex}", cardSize);

                    // Tooltip
                    if (ImGui.IsItemHovered())
                    {
                        if (isHoleCard)
                        {
                            ImGui.SetTooltip("Hidden Card");
                        }
                        else
                        {
                            ImGui.SetTooltip(engine.CurrentTable.DealerHand[cardIndex].GetCardDisplay());
                        }
                    }

                    // Move cursor for next card
                    ImGui.SetCursorScreenPos(pos + new Vector2(cardSize.X + 4, 0));
                }

                ImGui.Text(""); // New line after cards

                if (engine.CurrentTable.HoleCardRevealed)
                {
                    int dealerScore = engine.CurrentTable.GetDealerScore();
                    Vector4 dealerColor = dealerScore > 21 ? new Vector4(1, 0.5f, 0.5f, 1) : new Vector4(1, 1, 1, 1);
                    ImGui.TextColored(dealerColor, $"Total: {dealerScore}");
                }
                else
                {
                    ImGui.Text("Total: [Hidden]");
                }
            }

            if (engine.CurrentTable.Players.Count == 0)
            {
                ImGui.Separator();
                ImGui.Text("No players. Use Player Management tab to add players.");
                return;
            }

            foreach (var player in engine.CurrentTable.Players.Values)
            {
                ImGui.Separator();

                // Enhanced player header with statistics
                bool isCurrentTurn = engine.CurrentTable.CurrentTurnIndex < engine.CurrentTable.TurnOrder.Count &&
                    engine.CurrentTable.TurnOrder[engine.CurrentTable.CurrentTurnIndex].Equals(player.Name.ToUpper());

                if (isCurrentTurn)
                {
                    ImGui.TextColored(new Vector4(0, 1, 0, 1), $"► {player.Name}");
                }
                else
                {
                    ImGui.TextColored(new Vector4(1, 1, 0.5f, 1f), player.Name);
                }

                ImGui.SameLine();
                ImGui.Text($"({player.Server})");

                // Player stats in one line
                ImGui.Text($"Bank: {player.Bank} | Bet: {player.PersistentBet} | W/L: {player.GamesWon}/{player.GamesPlayed - player.GamesWon} ({player.GetWinPercentage():F1}%)");

                // Enhanced hand display with colors
                if (player.Hands.Count > 0)
                {
                    for (int handIndex = 0; handIndex < player.Hands.Count; handIndex++)
                    {
                        var handInfo = player.GetHandInfo(handIndex);
                        if (handInfo.Cards.Count > 0)
                        {
                            string handLabel = player.Hands.Count > 1 ? $"Hand {handIndex + 1}: " : "Hand: ";
                            ImGui.Text(handLabel);
                            ImGui.SameLine();

                            // Display cards with color coding
                            for (int cardIndex = 0; cardIndex < handInfo.Cards.Count; cardIndex++)
                            {
                                var card = handInfo.Cards[cardIndex];
                                Vector4 cardColor = card.IsRed ? new Vector4(1, 0.3f, 0.3f, 1f) : new Vector4(0.8f, 0.8f, 0.8f, 1f);

                                if (cardIndex > 0) ImGui.SameLine();
                                ImGui.TextColored(cardColor, card.GetCardDisplay());
                                if (cardIndex < handInfo.Cards.Count - 1) 
                                {
                                    ImGui.SameLine();
                                    ImGui.Text(" ");
                                }
                            }

                            // Hand value with status
                            ImGui.SameLine();
                            Vector4 valueColor = handInfo.IsBust ? new Vector4(1, 0.5f, 0.5f, 1f) : 
                                                 handInfo.IsBlackjack ? new Vector4(1, 1, 0, 1f) :
                                                 new Vector4(0.8f, 1, 0.8f, 1f);
                            ImGui.TextColored(valueColor, $" → {handInfo.GetHandDescription()}");

                            // Active hand indicator
                            if (handIndex == player.ActiveHandIndex && isCurrentTurn)
                            {
                                ImGui.SameLine();
                                ImGui.TextColored(new Vector4(0, 1, 0, 1f), " ◄ ACTIVE");
                            }
                        }
                    }
                }

                // Player status indicators
                if (player.IsAfk)
                {
                    ImGui.TextColored(new Vector4(1, 0.5f, 0.5f, 1f), "⚠ AFK");
                }
                else if (isCurrentTurn && engine.CurrentTable.GameState == Models.GameState.Playing)
                {
                    ImGui.TextColored(new Vector4(0, 1, 0, 1f), "🎮 PLAYING");

                    // Show available actions
                    var currentHandInfo = player.GetHandInfo();
                    ImGui.SameLine();
                    if (player.CanSplit())
                    {
                        ImGui.TextColored(new Vector4(1, 1, 0, 1f), " | Can SPLIT");
                    }
                    if (player.CanDoubleDown())
                    {
                        ImGui.TextColored(new Vector4(1, 1, 0, 1f), " | Can DOUBLE");
                    }
                }
                else if (player.IsStanding)
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 1f, 1f), "✋ STANDING");
                }

                // Insurance display
                if (player.HasInsurance)
                {
                    ImGui.TextColored(new Vector4(0, 1, 1, 1f), $"🛡️ Insurance: {player.InsuranceBet}");
                }

                // Quick actions for this player
                ImGui.Text("Quick Actions:");
                ImGui.SameLine();
                if (ImGui.Button($"Toggle AFK##{player.Name}afk", new Vector2(80, 0)))
                {
                    engine.ToggleAFK(player.Name);
                }
                ImGui.SameLine();
                if (ImGui.Button($"Remove##{player.Name}remove", new Vector2(80, 0)))
                {
                    engine.RemovePlayer(player.Name);
                }
            }
        }

        private void DrawStatisticsTab()
        {
            ImGui.TextColored(new Vector4(1, 1, 0, 1f), "GAME STATISTICS");

            // Table statistics
            ImGui.Separator();
            ImGui.Text("TABLE STATISTICS");
            ImGui.Text($"Total Games: {engine.CurrentTable.TotalGames}");
            ImGui.Text($"Cards Remaining: {engine.CurrentTable.Deck.Count}/52 ({engine.CurrentTable.GetCardsRemainingPercent():F1}%)");
            ImGui.Text($"Session Duration: {engine.CurrentTable.GetSessionDuration():h\\:mm\\:ss}");

            // Deck status bar
            float deckPercent = (float)engine.CurrentTable.GetCardsRemainingPercent() / 100;
            ImGui.ProgressBar(deckPercent, new Vector2(-1, 0), $"Deck: {engine.CurrentTable.Deck.Count} cards");

            // Persistent deck — show already-dealt cards
            if (engine.CurrentTable.PersistentDeck && engine.CurrentTable.DealtCards.Count > 0)
            {
                ImGui.Separator();
                ImGui.TextColored(new Vector4(1, 0.7f, 0, 1f), $"DEALT CARDS ({engine.CurrentTable.DealtCards.Count})");
                if (ImGui.BeginChild("##dealtcards", new Vector2(0, 90), true))
                {
                    var grouped = engine.CurrentTable.DealtCards
                        .GroupBy(c => c.Value)
                        .OrderBy(g => g.Key)
                        .Select(g => $"{g.Key}×{g.Count()}");
                    ImGui.TextWrapped(string.Join("  ", grouped));
                    ImGui.EndChild();
                }
            }

            ImGui.Separator();

            // Player statistics
            if (engine.CurrentTable.Players.Count > 0)
            {
                ImGui.Text("PLAYER STATISTICS");

                if (ImGui.BeginChild("PlayerStats", new Vector2(0, 300), true))
                {
                    foreach (var player in engine.CurrentTable.Players.Values)
                    {
                        ImGui.Separator();
                        ImGui.TextColored(new Vector4(1, 1, 0.5f, 1f), player.Name);

                        // BJ stats
                        ImGui.Text($"Bank: {player.Bank} | BJ Winnings: {player.TotalWinnings}");
                        ImGui.Text($"BJ Games: {player.GamesPlayed} | Won: {player.GamesWon} | Win Rate: {player.GetWinPercentage():F1}%");

                        // Roulette net gains
                        int rouNet = player.RouletteNetGains;
                        Vector4 rouColor = rouNet > 0 ? new Vector4(0, 1, 0, 1f) :
                                           rouNet < 0 ? new Vector4(1, 0.4f, 0.4f, 1f) :
                                                        new Vector4(0.6f, 0.6f, 0.6f, 1f);
                        ImGui.TextColored(rouColor, $"Roulette Net: {(rouNet >= 0 ? "+" : "")}{rouNet}G");

                        // Win rate progress bar
                        if (player.GamesPlayed > 0)
                        {
                            float winRate = (float)player.GetWinPercentage() / 100;
                            Vector4 winColor = winRate > 0.6f ? new Vector4(0, 1, 0, 1f) : 
                                             winRate > 0.4f ? new Vector4(1, 1, 0, 1f) : 
                                             new Vector4(1, 0.5f, 0.5f, 1f);
                            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, winColor);
                            ImGui.ProgressBar(winRate, new Vector2(-1, 0), $"{player.GetWinPercentage():F1}%");
                            ImGui.PopStyleColor();
                        }

                        // Recent bet history
                        if (player.BetHistory.Count > 0)
                        {
                            ImGui.Text("Recent Results:");
                            var recent = player.BetHistory.TakeLast(5).Reverse();
                            foreach (var bet in recent)
                            {
                                Vector4 resultColor = bet.Result.Contains("WIN") || bet.Result == "BLACKJACK" ? new Vector4(0, 1, 0, 1f) : 
                                                    bet.Result == "PUSH" ? new Vector4(1, 1, 0, 1f) : 
                                                    new Vector4(1, 0.5f, 0.5f, 1f);
                                ImGui.TextColored(resultColor, $"  {bet.Timestamp:HH:mm} - {bet.Result} ({bet.BetAmount})");
                            }
                        }
                    }
                    ImGui.EndChild();
                }
            }
            else
            {
                ImGui.Text("No players to show statistics for.");
            }
        }

        private void DrawChatDebugTab()
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0f, 1f), "CHAT DEBUG - ALL INCOMING MESSAGES");
            ImGui.TextWrapped("Every chat message received is shown here with its XivChatType number. Look for your party messages to find the correct type.");
            ImGui.Separator();

            if (ImGui.Button("Clear##cleardebug"))
                DebugChatLog.Clear();

            ImGui.Separator();

            ImGui.BeginChild("##debugchatscroll", new Vector2(0, 400), true);
            foreach (var line in DebugChatLog)
            {
                // Highlight lines containing ">" commands
                if (line.Contains(">"))
                    ImGui.TextColored(new Vector4(0, 1, 0.5f, 1f), line);
                else
                    ImGui.TextUnformatted(line);
            }
            ImGui.SetScrollHereY(1.0f);
            ImGui.EndChild();
        }

        private void DrawLogTab()
        {
            ImGui.TextColored(new Vector4(0.2f, 1f, 0.8f, 1f), "GAME LOG");
            ImGui.SameLine(300);
            if (ImGui.Button("Clear Log", new Vector2(100, 0)))
            {
                engine.CurrentTable.GameLog.Clear();
            }

            ImGui.Separator();

            if (ImGui.BeginChild("GameLogScroll", new Vector2(0, 400), true))
            {
                foreach (var logEntry in engine.CurrentTable.GameLog)
                {
                    ImGui.TextWrapped(logEntry);
                }

                // Auto-scroll to bottom
                if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 10)
                {
                    ImGui.SetScrollHereY(1.0f);
                }

                ImGui.EndChild();
            }
        }

        private void RenamePlayer(string oldName, string newName)
        {
            string oldNameUpper = oldName.ToUpper();
            if (engine.CurrentTable.Players.ContainsKey(oldNameUpper))
            {
                var player = engine.CurrentTable.Players[oldNameUpper];
                engine.CurrentTable.Players.Remove(oldNameUpper);
                player.Name = newName;
                engine.CurrentTable.Players[newName.ToUpper()] = player;
            }
        }

        private void DisplayTableStatus()
        {
            var status = "[BLACKJACK] TABLE STATUS:\n";
            foreach (var player in engine.CurrentTable.Players.Values)
            {
                status += $"  {player.Name} ({player.Server}) - Bank: {player.Bank}, Bet: {player.PersistentBet}, AFK: {player.IsAfk}\n";
            }
            plugin.SendGameMessage(status);
        }
    }
}
