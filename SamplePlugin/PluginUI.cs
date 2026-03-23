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
            string[] gameTypes = { "Blackjack", "Roulette" };
            if (ImGui.Combo("##gametype", ref gameType, gameTypes, gameTypes.Length))
            {
                engine.CurrentTable.GameType = (Models.GameType)gameType;
                // Reset game state when switching
                engine.CurrentTable.GameState = Models.GameState.Lobby;
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
            }

            ImGui.Separator();

            if (engine.CurrentTable.GameType == Models.GameType.Roulette)
                DrawRouletteInterface();
            else
                DrawDealerInterface();
        }


        // ── ROULETTE UI ─────────────────────────────────────────────────────────

        private string rouletteTargetInput = string.Empty;
        private int rouletteBetAmount = 50;
        private string rouletteProxyPlayer = string.Empty;

        private static readonly int[] RedNumbers = { 1,3,5,7,9,12,14,16,18,19,21,23,25,27,30,32,34,36 };

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

            ImGui.SetNextItemWidth(150);
            ImGui.InputTextWithHint("##rproxyplayer", "Player name", ref rouletteProxyPlayer, 64);
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
            if (ImGui.Button("Bet##rbetbtn") && !string.IsNullOrWhiteSpace(rouletteProxyPlayer) && !string.IsNullOrWhiteSpace(rouletteTargetInput))
                roulette.PlaceBet(rouletteProxyPlayer, rouletteBetAmount, rouletteTargetInput, out _);
            ImGui.SameLine();
            if (ImGui.Button("Clear##rclearbtn") && !string.IsNullOrWhiteSpace(rouletteProxyPlayer))
                roulette.ClearPlayerBets(rouletteProxyPlayer);

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
            float cx = pos.X + 70, cy = pos.Y + 70;
            float radius = 62;
            float segAngle = (float)(2 * Math.PI / 37);
            const float TwoPI = (float)(2 * Math.PI);

            // Calculate rotation angles
            float wheelRotation = 0f; // wheel spins clockwise
            float needleAngle;        // needle spins counter-clockwise

            if (table.RouletteSpinState == Models.RouletteSpinState.Spinning)
            {
                double ms    = (DateTime.Now - table.RouletteSpinStart).TotalMilliseconds;
                // Speed eases from fast → slow over 4 seconds
                double speed = Math.Max(0.04, 0.9 - (ms / 4000.0) * 0.86);

                // Wheel rotates clockwise (+)
                wheelRotation = (float)(ms * speed * 0.018) % TwoPI;

                // Needle rotates counter-clockwise (-) at ~1.4x wheel speed
                needleAngle = -(float)(ms * speed * 0.025) % TwoPI;
            }
            else if (table.RouletteResult.HasValue)
            {
                // When idle, needle points at the winning number, wheel is stationary
                needleAngle = table.RouletteResult.Value * segAngle - (float)(Math.PI / 2);
            }
            else
            {
                needleAngle = -(float)(Math.PI / 2);
            }

            // Wheel background
            drawList.AddCircleFilled(new Vector2(cx, cy), radius, 0xFF1a1a1a, 64);
            drawList.AddCircle(new Vector2(cx, cy), radius, 0xFFFFD700, 64, 2f);

            // 37 colored segments — offset by wheelRotation so the wheel body actually turns
            for (int i = 0; i <= 36; i++)
            {
                float a1 = i * segAngle - (float)(Math.PI / 2) + wheelRotation;
                float a2 = a1 + segAngle;
                uint color = i == 0 ? 0xFF00AA00 : Array.IndexOf(RedNumbers, i) >= 0 ? 0xFF2233CC : 0xFF222222;
                var c  = new Vector2(cx, cy);
                var p1 = new Vector2(cx + (float)Math.Cos(a1) * (radius - 3), cy + (float)Math.Sin(a1) * (radius - 3));
                var p2 = new Vector2(cx + (float)Math.Cos(a2) * (radius - 3), cy + (float)Math.Sin(a2) * (radius - 3));
                drawList.AddTriangleFilled(c, p1, p2, color);
            }

            // Gold outer ring on top of segments
            drawList.AddCircle(new Vector2(cx, cy), radius, 0xFFFFD700, 64, 2f);

            // Needle — counter-clockwise, drawn on top of the wheel
            var tip = new Vector2(
                cx + (float)Math.Cos(needleAngle) * (radius - 6),
                cy + (float)Math.Sin(needleAngle) * (radius - 6));
            drawList.AddLine(new Vector2(cx, cy), tip, 0xFFFFFFFF, 2f);
            drawList.AddCircleFilled(new Vector2(cx, cy), 5, 0xFFFFD700);

            // Center number display when idle
            if (table.RouletteResult.HasValue && table.RouletteSpinState == Models.RouletteSpinState.Idle)
            {
                string col   = Engine.RouletteEngine.GetColor(table.RouletteResult.Value);
                uint textCol = col == "RED" ? 0xFF3333FF : col == "GREEN" ? 0xFF00CC00 : 0xFFCCCCCC;
                var sz = ImGui.CalcTextSize(table.RouletteResult.Value.ToString());
                drawList.AddText(new Vector2(cx - sz.X * 0.5f, cy - sz.Y * 0.5f), textCol, table.RouletteResult.Value.ToString());
            }

            // Right of wheel: last result summary
            ImGui.SetCursorScreenPos(new Vector2(pos.X + 155, pos.Y + 10));
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
                ImGui.TextColored(new Vector4(1,1,0,1), "🎡 Spinning...");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.5f,0.5f,0.5f,1), "Awaiting first spin");
            }

            ImGui.EndGroup();
            ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + 152));
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

        // ── BLACKJACK UI ─────────────────────────────────────────────────────────

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

            if (ImGui.BeginTable("PlayersTable", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
            {
                // Table Headers
                ImGui.TableSetupColumn("👤 Name", ImGuiTableColumnFlags.WidthFixed, 120);
                ImGui.TableSetupColumn("🌐 Server", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("💰 Bank", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("🎰 Bet", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("💤 AFK", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("📊 Stats", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn("🎴 Cards", ImGuiTableColumnFlags.WidthFixed, 300);
                ImGui.TableSetupColumn("🔧 Actions", ImGuiTableColumnFlags.WidthFixed, 120);
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

                    // Bet Column - LIVE UPDATING
                    ImGui.TableSetColumnIndex(3);
                    ImGui.SetNextItemWidth(-1);
                    string tempBet = editingBet[playerKey];

                    // Color code based on bet validity
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
                        {
                            // Reset to current valid value
                            editingBet[playerKey] = player.PersistentBet.ToString();
                        }
                    }
                    ImGui.PopStyleColor();

                    // AFK Column
                    ImGui.TableSetColumnIndex(4);
                    bool isAfk = player.IsAfk;
                    if (ImGui.Checkbox($"##afk{i}", ref isAfk))
                    {
                        player.IsAfk = isAfk;
                    }

                    // Stats Column - LIVE UPDATING
                    ImGui.TableSetColumnIndex(5);
                    if (player.GamesPlayed > 0)
                    {
                        float winRate = (float)player.GetWinPercentage();
                        Vector4 statsColor = winRate > 60f ? new Vector4(0, 1, 0, 1f) : 
                                            winRate > 40f ? new Vector4(1, 1, 0, 1f) : 
                                            new Vector4(1, 0.5f, 0.5f, 1f);
                        ImGui.TextColored(statsColor, $"{player.GamesWon}/{player.GamesPlayed}");
                        ImGui.TextColored(statsColor, $"({winRate:F0}%)");
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "No games");
                    }

                    // Cards Column - LIVE UPDATING with PNG Images
                    ImGui.TableSetColumnIndex(6);
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

                    // Actions Column
                    ImGui.TableSetColumnIndex(7);
                    if (ImGui.Button($"Kick##kick{i}", new Vector2(50, 0)))
                    {
                        engine.RemovePlayer(player.Name);
                        // Clean up editing dictionaries
                        editingName.Remove(playerKey);
                        editingServer.Remove(playerKey);
                        editingBank.Remove(playerKey);
                        editingBet.Remove(playerKey);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button($"DM##dm{i}", new Vector2(40, 0)))
                    {
                        // Send bank and bet info via DM
                        string betInfo = player.PersistentBet > 0 ? $"Bet: {player.PersistentBet}" : "No bet placed";
                        engine.OnPlayerTell?.Invoke($"{player.Name}@{player.Server}", 
                            $"Bank: {player.Bank} | {betInfo}");
                    }
                }

                ImGui.EndTable();
            }
        }

        private void DrawAdminTab()
        {
            ImGui.TextColored(new Vector4(1, 0.84f, 0, 1), "ADMIN CONTROLS");

            // Admin Name Setting
            ImGui.Text("Admin Name:");
            string adminNameTemp = AdminName;
            ImGui.SetNextItemWidth(200);
            if (ImGui.InputText("##adminName", ref adminNameTemp, 100))
            {
                AdminName = adminNameTemp;
            }
            ImGui.SameLine();
            ImGui.Text($"Current: {(string.IsNullOrEmpty(AdminName) ? "None" : AdminName)}");

            ImGui.Separator();

            // Game State & Mode
            ImGui.Text($"Game State: {engine.CurrentTable.GameState}");
            ImGui.Text($"Current Mode: {engine.Mode}");

            if (ImGui.Button("Set Auto Mode", new Vector2(120, 0)))
            {
                engine.Mode = DealerMode.Auto;
            }
            ImGui.SameLine();
            if (ImGui.Button("Set Manual Mode", new Vector2(120, 0)))
            {
                engine.Mode = DealerMode.Manual;
            }

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
            {
                engine.CurrentTable.MinBet = minBet;
            }
            ImGui.SameLine();
            int maxBet = engine.CurrentTable.MaxBet;
            ImGui.SetNextItemWidth(100);
            if (ImGui.DragInt("Max##maxBet", ref maxBet, 1, 1, 10000))
            {
                engine.CurrentTable.MaxBet = maxBet;
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
            Vector4 deckColor = deckPercent > 0.5f ? new Vector4(0, 1, 0, 1f) : 
                               deckPercent > 0.25f ? new Vector4(1, 1, 0, 1f) : 
                               new Vector4(1, 0, 0, 1f);
            ImGui.ProgressBar(deckPercent, new Vector2(-1, 0), $"Deck: {engine.CurrentTable.Deck.Count} cards");

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

                        ImGui.Text($"Bank: {player.Bank} | Total Winnings: {player.TotalWinnings}");
                        ImGui.Text($"Games: {player.GamesPlayed} | Won: {player.GamesWon} | Win Rate: {player.GetWinPercentage():F1}%");

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
