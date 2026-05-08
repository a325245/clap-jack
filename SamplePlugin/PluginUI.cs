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
        /// <summary>The dealer is always the local player — derived from the game client, never entered manually.</summary>
        public string AdminName => Plugin.PlayerState?.CharacterName ?? string.Empty;

        // UI State for editing
        private Dictionary<string, string> editingName = new();
        private Dictionary<string, string> editingServer = new();
        private Dictionary<string, string> editingBank = new();
        private Dictionary<string, string> editingBet = new();

        // Quick add fields
        private string quickAddPlayerName = string.Empty;

        // Session snapshot (resume feature)
        private Models.SessionSnapshot? _sessionSnapshot;
        private bool _snapshotLoaded = false;
        private string SnapshotPath =>
            System.IO.Path.Combine(plugin.PluginInterface.ConfigDirectory.FullName, "session_snapshot.json");

        private void SaveSessionSnapshot()
        {
            try
            {
                System.IO.Directory.CreateDirectory(plugin.PluginInterface.ConfigDirectory.FullName);
                var snap = engine.CreateSnapshot();
                var json = System.Text.Json.JsonSerializer.Serialize(snap,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(SnapshotPath, json);
                _sessionSnapshot  = snap;
                _snapshotLoaded   = true;
            }
            catch { }
        }

        private Models.SessionSnapshot? GetCachedSnapshot()
        {
            if (!_snapshotLoaded)
            {
                try
                {
                    if (System.IO.File.Exists(SnapshotPath))
                    {
                        var json = System.IO.File.ReadAllText(SnapshotPath);
                        _sessionSnapshot = System.Text.Json.JsonSerializer
                            .Deserialize<Models.SessionSnapshot>(json);
                    }
                }
                catch { _sessionSnapshot = null; }
                _snapshotLoaded = true;
            }
            return _sessionSnapshot;
        }

        // Debug chat log - stores recent raw chat messages with their types
        private Queue<string> DebugChatLog { get; } = new();
        private const int MaxDebugLines = 20;
        public void AddDebugChat(string line)
        {
            DebugChatLog.Enqueue(line);
            if (DebugChatLog.Count > MaxDebugLines)
                DebugChatLog.Dequeue();
        }

        private bool _showPokerHoleCards = true;
        private bool _showResetConfirm  = false;
        private bool _resetModalOpen    = true;
        private int  _viewMode = 0; // 0=Dealer  1=Player View

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

        /// <summary>Called from Plugin.OnAnyEngineUpdate after every dealer action.</summary>
        public void AutoSaveSnapshot() => SaveSessionSnapshot();

        public void Draw()
        {
            if (!IsVisible) return;

            bool isVisible = IsVisible;
            if (ImGui.Begin("Blackjack Dealer - Professional Control Panel", ref isVisible, ImGuiWindowFlags.None))
            {
                IsVisible = isVisible;

                // ── Reset button — top right corner (dealer view only) ───────
                if (_viewMode != 1)
                {
                    float btnW = 60f;
                    ImGui.SameLine(ImGui.GetWindowWidth() - btnW - 12f);
                    ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.55f, 0.08f, 0.08f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.15f, 0.15f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.90f, 0.20f, 0.20f, 1f));
                    if (ImGui.Button("RESET", new Vector2(btnW, 0)))
                        _showResetConfirm = true;
                    ImGui.PopStyleColor(3);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Reset the entire plugin to factory state");
                }

                // Reset confirmation popup
                if (_showResetConfirm)
                {
                    ImGui.OpenPopup("##resetConfirm");
                    _showResetConfirm = false;
                }
                if (ImGui.BeginPopupModal("##resetConfirm", ref _resetModalOpen, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
                {
                    ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Are you sure?");
                    ImGui.Text("This will clear ALL players, banks, bets, cards,\nand game state. This cannot be undone.");
                    ImGui.Spacing();
                    if (ImGui.Button("Yes, Reset Everything", new Vector2(180, 26)))
                    {
                        plugin.FullReset();
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel", new Vector2(80, 26)))
                        ImGui.CloseCurrentPopup();
                    ImGui.EndPopup();
                }

                if (ImGui.BeginTabBar("##maintabs"))
                {
                    if (ImGui.BeginTabItem("Table##tab1"))
                    {
                        DrawTableTab();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("Statistics##tab2"))
                    {
                        DrawStatisticsTab();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("Game Log##tab3"))
                    {
                        DrawLogTab();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("Admin##tab4"))
                    {
                        DrawAdminTab();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("Chat Debug##tab5"))
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
            // View mode selector — always visible
            ImGui.Text("View:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110);
            string[] viewModes = { "Dealer", "Player View" };
            ImGui.Combo("##viewmode", ref _viewMode, viewModes, viewModes.Length);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Switch between the dealer control panel and the player-side view");

            // ── Player View mode — render player view and return ──────────────
            {
                string myName = Plugin.PlayerState?.CharacterName ?? string.Empty;
                var pvState = plugin.ChatParser.State;
                if (pvState.ShouldAutoSwitch)
                {
                    pvState.ShouldAutoSwitch = false;
                    bool dealerIsOther = !string.IsNullOrEmpty(pvState.DealerName) &&
                        !pvState.DealerName.Equals(myName, StringComparison.OrdinalIgnoreCase);
                    if (_viewMode == 0 && dealerIsOther)
                        _viewMode = 1;
                }
            }

            if (_viewMode == 1)
            {
                ImGui.Separator();
                string myName = Plugin.PlayerState?.CharacterName ?? string.Empty;
                plugin.PlayerView.DrawContent(myName);
                return;
            }

            // ── Dealer controls — only in dealer view ─────────────────────────
            ImGui.SameLine();

            // Game type selector
            ImGui.Text("Game:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140);
            int gameType = (int)engine.CurrentTable.GameType;
            string[] gameTypes = { "None", "Blackjack", "Roulette", "Craps", "Baccarat", "Chocobo Racing", "Texas Hold'Em", "Ultima!" };
            if (ImGui.Combo("##gametype", ref gameType, gameTypes, gameTypes.Length))
            {
                var newType = (Models.GameType)gameType;
                if (newType != engine.CurrentTable.GameType)
                {
                    // ── Cease all active games, refund bets, stop timers ─────────
                    var tbl = engine.CurrentTable;

                    // Roulette: refund and reset spin
                    if (tbl.RouletteSpinState != Models.RouletteSpinState.Idle)
                        plugin.RouletteEngine.ForceStop();
                    foreach (var p in tbl.Players.Values)
                    {
                        int rouRefund = p.RouletteBets.Sum(b => b.Amount);
                        if (rouRefund > 0) p.Bank += rouRefund;
                        p.RouletteBets.Clear();
                    }

                    // Craps: refund
                    plugin.CrapsEngine.ForceStop();

                    // Baccarat: refund
                    foreach (var kvp in tbl.BaccaratBets.ToList())
                    {
                        var bet = kvp.Value;
                        var bp = tbl.Players.Values.FirstOrDefault(x =>
                            x.Name.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase));
                        if (bp != null) bp.Bank += bet.PlayerBet + bet.BankerBet + bet.TieBet;
                    }
                    tbl.BaccaratBets.Clear();
                    tbl.BaccaratPhase = Models.BaccaratPhase.WaitingForBets;

                    // Chocobo: refund
                    foreach (var kvp in tbl.ChocoboBets.ToList())
                    {
                        var bp = tbl.Players.Values.FirstOrDefault(x =>
                            x.Name.ToUpperInvariant() == kvp.Key);
                        if (bp != null) bp.Bank += kvp.Value.Amount;
                    }
                    tbl.ChocoboBets.Clear();
                    tbl.ChocoboRacePhase = Models.ChocoboRacePhase.Idle;

                    // Poker: cancel hand
                    plugin.PokerEngine.ForceStop();

                    // Ultima: cancel silently (ForceEnd sends a chat message)
                    tbl.UltimaPhase = Models.UltimaPhase.WaitingForPlayers;

                    // Blackjack: reset turn state
                    tbl.TurnTimeRemaining = tbl.TurnTimeLimit;

                    // Silence all queued messages from the force stops
                    plugin.RouletteEngine.ClearQueue();
                    plugin.CrapsEngine.ClearQueue();
                    plugin.BaccaratEngine.ClearQueue();
                    plugin.ChocoboEngine.ClearQueue();
                    plugin.PokerEngine.ClearQueue();
                    engine.ClearQueue();

                    // Reset common state
                    tbl.GameState = Models.GameState.Lobby;
                    tbl.TurnOrder.Clear();
                    tbl.CurrentTurnIndex = 0;

                    engine.CurrentTable.GameType = newType;
                    if (newType == Models.GameType.None)
                    {
                        engine.Announce("Plugin set to idle — commands are now disabled.");
                    }
                    else
                    {
                        string gameName = newType switch
                        {
                            Models.GameType.Roulette      => "Roulette",
                            Models.GameType.Craps         => "Craps",
                            Models.GameType.Baccarat      => "Mini Baccarat",
                            Models.GameType.ChocoboRacing => "Chocobo Racing",
                            Models.GameType.TexasHoldEm   => "Texas Hold'Em",
                            Models.GameType.Ultima        => "Ultima!",
                            _                             => "Blackjack"
                        };
                        engine.Announce($"Now playing: {gameName}!");
                    }
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
                plugin.PokerEngine.ChatMode    = (Models.ChatMode)chatMode;
                plugin.UltimaEngine.ChatMode   = (Models.ChatMode)chatMode;
            }

            // Echo player actions toggle
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "  Echo:");
            ImGui.SameLine();
            bool _echo = engine.EchoPlayerActions;
            if (ImGui.Checkbox("##echoactions", ref _echo))
                engine.EchoPlayerActions = _echo;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Show player action confirmations in chat (hits, doubles, splits)");

            ImGui.Separator();

            if (engine.CurrentTable.GameType == Models.GameType.None)
                DrawNoneInterface();
            else if (engine.CurrentTable.GameType == Models.GameType.Roulette)
                DrawRouletteInterface();
            else if (engine.CurrentTable.GameType == Models.GameType.Craps)
                DrawCrapsInterface();
            else if (engine.CurrentTable.GameType == Models.GameType.Baccarat)
                DrawBaccaratInterface();
            else if (engine.CurrentTable.GameType == Models.GameType.ChocoboRacing)
                DrawChocoboInterface();
            else if (engine.CurrentTable.GameType == Models.GameType.TexasHoldEm)
                DrawPokerInterface();
            else if (engine.CurrentTable.GameType == Models.GameType.Ultima)
                DrawUltimaInterface();
            else
                DrawDealerInterface();
        }


        // ── ROULETTE UI ─────────────────────────────────────────────────────────

        private string rouletteTargetInput = string.Empty;
        private int rouletteBetAmount = 50;
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
                if (ImGui.Button("SPIN  ", new Vector2(120, 36)))
                    roulette.StartSpin("Dealer", out _);
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "Place bets, then hit SPIN.");
            }
            else if (table.RouletteSpinState == Models.RouletteSpinState.Spinning)
            {
                ImGui.TextColored(new Vector4(1, 0.8f, 0, 1), "No more bets! Wheel spinning...");
            }
            else if (table.RouletteSpinState == Models.RouletteSpinState.Resolving)
            {
                ImGui.TextColored(new Vector4(0, 1, 0.5f, 1), "Resolving payouts...");
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
                    ImGui.Text($"{player.Bank}\uE049");

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
                            return $"[{b.Target}:{b.Amount}\uE049]";
                        }));
                        ImGui.TextUnformatted(betStr);
                    }

                    // Total risk
                    ImGui.TableSetColumnIndex(3);
                    int risk = player.RouletteBets.Sum(b => b.Amount);
                    if (risk > 0)
                        ImGui.TextColored(new Vector4(1,0.7f,0,1), $"{risk}\uE049");
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
            var table    = engine.CurrentTable;
            var drawList = ImGui.GetWindowDrawList();
            var startPos = ImGui.GetCursorScreenPos();
            float cellW = 28, cellH = 22, pad = 2;
            var   mouse  = ImGui.GetIO().MousePos;

            // Build bet map: target → list of "Name (amt)" strings
            var betMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var player in table.Players.Values)
                foreach (var bet in player.RouletteBets)
                {
                    if (!betMap.TryGetValue(bet.Target, out var lst))
                        betMap[bet.Target] = lst = new();
                    lst.Add($"{player.Name} ({bet.Amount}\uE049)");
                }

            string? ttTitle = null;
            string? ttBody  = null;

            // 0 cell on the left spanning all 3 rows
            float zeroH = cellH * 3 + pad * 2;
            var zeroTL = startPos;
            var zeroBR = new Vector2(startPos.X + cellW, startPos.Y + zeroH);
            bool zeroWin = table.RouletteResult == 0 && table.RouletteSpinState == Models.RouletteSpinState.Idle && table.RouletteResult.HasValue;
            drawList.AddRectFilled(zeroTL, zeroBR, zeroWin ? 0xFFFFFFFF : 0xFF00AA00, 3);
            drawList.AddRect(zeroTL, zeroBR, 0xFF888888, 3);
            var zeroSz = ImGui.CalcTextSize("0");
            drawList.AddText(new Vector2(zeroTL.X + cellW * 0.5f - zeroSz.X * 0.5f, zeroTL.Y + zeroH * 0.5f - zeroSz.Y * 0.5f), zeroWin ? 0xFF000000 : 0xFFFFFFFF, "0");
            if (betMap.ContainsKey("0") && !zeroWin)
                drawList.AddCircleFilled(new Vector2(zeroTL.X + cellW * 0.5f, zeroTL.Y + zeroH * 0.5f), 6f, 0xCCFFCC22, 12);
            if (mouse.X >= zeroTL.X && mouse.X < zeroBR.X && mouse.Y >= zeroTL.Y && mouse.Y < zeroBR.Y
                && betMap.TryGetValue("0", out var z0p))
            { ttTitle = "0"; ttBody = string.Join("\n", z0p); }

            // 3 rows x 12 columns of numbers
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 12; col++)
                {
                    int n = RouletteGrid[row, col];
                    float x = startPos.X + cellW + pad + col * (cellW + pad);
                    float y = startPos.Y + row * (cellH + pad);
                    string ns = n.ToString();

                    bool isWin = table.RouletteResult == n &&
                                 table.RouletteSpinState == Models.RouletteSpinState.Idle &&
                                 table.RouletteResult.HasValue;

                    uint bg = isWin ? 0xFFFFFFFF :
                              Array.IndexOf(RedNumbers, n) >= 0 ? 0xFF2233CC : 0xFF111111;

                    drawList.AddRectFilled(new Vector2(x, y), new Vector2(x + cellW, y + cellH), bg, 2);
                    drawList.AddRect(new Vector2(x, y), new Vector2(x + cellW, y + cellH), 0xFF555555, 2);

                    var nSz = ImGui.CalcTextSize(ns);
                    drawList.AddText(
                        new Vector2(x + cellW * 0.5f - nSz.X * 0.5f, y + cellH * 0.5f - nSz.Y * 0.5f),
                        isWin ? 0xFF000000 : 0xFFFFFFFF, ns);

                    if (betMap.ContainsKey(ns) && !isWin)
                        drawList.AddCircleFilled(new Vector2(x + cellW * 0.5f, y + cellH * 0.5f), 6f, 0xCCFFCC22, 12);

                    if (mouse.X >= x && mouse.X < x + cellW && mouse.Y >= y && mouse.Y < y + cellH
                        && betMap.TryGetValue(ns, out var nbp))
                    { ttTitle = ns; ttBody = string.Join("\n", nbp); }
                }
            }

            // ── Outside bets row: RED / BLACK / EVEN / ODD ────────────────────
            float outsideY = startPos.Y + 3 * (cellH + pad);
            float totalW   = cellW + pad + 12 * (cellW + pad);
            float obW      = totalW * 0.25f - pad;
            string[] outsideNames = { "RED", "BLACK", "EVEN", "ODD" };
            uint[]   outsideBg    = { 0xFF2233CCu, 0xFF111111u, 0xFF222222u, 0xFF222222u };

            for (int oi = 0; oi < 4; oi++)
            {
                float ox = startPos.X + cellW + pad + oi * (obW + pad);
                bool  ob = betMap.ContainsKey(outsideNames[oi]);
                drawList.AddRectFilled(new Vector2(ox, outsideY), new Vector2(ox+obW, outsideY+cellH), outsideBg[oi], 2f);
                drawList.AddRect(new Vector2(ox, outsideY), new Vector2(ox+obW, outsideY+cellH), 0xFF555555u, 2f);
                var oSz = ImGui.CalcTextSize(outsideNames[oi]);
                drawList.AddText(new Vector2(ox + obW*0.5f - oSz.X*0.5f, outsideY + cellH*0.5f - oSz.Y*0.5f),
                    0xFFFFFFFFu, outsideNames[oi]);
                if (ob)
                    drawList.AddCircleFilled(new Vector2(ox + obW*0.5f, outsideY + cellH*0.5f), 6f, 0xCCFFCC22u, 12);
                if (mouse.X >= ox && mouse.X < ox+obW && mouse.Y >= outsideY && mouse.Y < outsideY+cellH
                    && betMap.TryGetValue(outsideNames[oi], out var obp))
                { ttTitle = outsideNames[oi]; ttBody = string.Join("\n", obp); }
            }

            // Advance cursor past the grid + outside row
            ImGui.SetCursorScreenPos(new Vector2(startPos.X, outsideY + cellH + 8));

            if (ttTitle != null)
            {
                ImGui.BeginTooltip();
                ImGui.TextColored(new Vector4(1f,0.84f,0f,1f), ttTitle);
                if (ttBody != null) ImGui.TextUnformatted(ttBody);
                ImGui.EndTooltip();
            }
        }

        // ── CRAPS UI ─────────────────────────────────────────────────────────────

        private int crapsProxyBetAmt = 50;
        private int crapsProxyBetType = 0;
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
                ImGui.TextColored(new Vector4(1, 0.84f, 0, 1), $"POINT: {table.CrapsPoint} — Roll {table.CrapsPoint} to win or 7-out");
            else
                ImGui.TextColored(new Vector4(0.5f, 1, 0.5f, 1), "COME-OUT — Pass/Don't Pass bets open");

            ImGui.SameLine(300);
            ImGui.TextColored(new Vector4(0.5f, 1, 1, 1), $"Shooter: {shooter}");

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
                    if (ImGui.Button("OPEN BETS  ", new Vector2(140, 32)))
                        craps.StartBettingPhase();
                    ImGui.SameLine();
                }
                if (ImGui.Button("ROLL  ", new Vector2(120, 32)))
                    craps.StartRoll(out _);
                ImGui.SameLine();
                string hint = table.CrapsPhase == Models.CrapsPhase.PointEstablished
                    ? $"Point: {table.CrapsPoint}"
                    : (table.CrapsBettingPhase ? "Bets open — shooter may roll!" : "Open bets or roll.");
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), hint);
            }
            else
            {
                ImGui.TextColored(new Vector4(1, 0.8f, 0, 1), "Rolling...");
            }

            ImGui.Separator();

            // ── Rules ────────────────────────────────────────────────────────────
            if (ImGui.CollapsingHeader("Craps Rules (summary)"))
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
                    ImGui.Text($"{player.Bank}\uE049");

                    string pk = player.Name.ToUpperInvariant();
                    table.CrapsBets.TryGetValue(pk, out var pb);

                    ImGui.TableSetColumnIndex(2);
                    if (pb != null && pb.PassLineBet > 0) ImGui.TextColored(new Vector4(0, 1, 0.5f, 1), $"{pb.PassLineBet}\uE049");
                    else ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1), "-");

                    ImGui.TableSetColumnIndex(3);
                    if (pb != null && pb.DontPassBet > 0) ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), $"{pb.DontPassBet}\uE049");
                    else ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1), "-");

                    ImGui.TableSetColumnIndex(4);
                    if (pb != null && pb.FieldBet > 0) ImGui.TextColored(new Vector4(0.8f, 0.8f, 0, 1), $"{pb.FieldBet}\uE049");
                    else ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1), "-");

                    ImGui.TableSetColumnIndex(5);
                    string bigStr = "";
                    if (pb != null && pb.Big6Bet > 0) bigStr += $"6:{pb.Big6Bet}\uE049 ";
                    if (pb != null && pb.Big8Bet > 0) bigStr += $"8:{pb.Big8Bet}\uE049";
                    if (!string.IsNullOrEmpty(bigStr)) ImGui.TextColored(new Vector4(0.4f, 0.8f, 1, 1), bigStr.Trim());
                    else ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1), "-");

                    ImGui.TableSetColumnIndex(6);
                    if (pb != null && pb.PlaceBets.Count > 0)
                    {
                        string placeStr = string.Join(" ", pb.PlaceBets.Select(kv => $"{kv.Key}:{kv.Value}\uE049"));
                        ImGui.TextColored(new Vector4(0.9f, 0.6f, 1, 1), placeStr);
                    }
                    else ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1), "-");

                    ImGui.TableSetColumnIndex(7);
                    int net = player.CrapsNetGains;
                    Vector4 netCol = net > 0 ? new Vector4(0, 1, 0, 1) : net < 0 ? new Vector4(1, 0.4f, 0.4f, 1) : new Vector4(0.6f, 0.6f, 0.6f, 1);
                    ImGui.TextColored(netCol, net >= 0 ? $"+{net}\uE049" : $"{net}\uE049");
                }
                ImGui.EndTable();
            }

            ImGui.Separator();
            DrawPlayersManagementTab();
        }

        private void DrawCrapsTable()
        {
            var table    = engine.CurrentTable;
            var drawList = ImGui.GetWindowDrawList();
            var pos      = ImGui.GetCursorScreenPos();
            var mouse    = ImGui.GetIO().MousePos;

            const float W = 490f, H = 134f, pad = 4f;

            // ── Aggregate bets for dot display + tooltip data ─────────────────────────────
            // betTooltip: section label → "Name1 (Xgil)\nName2 (Ygil)\n…"
            var betTooltip = new Dictionary<string, string>();

            void AddTip(string section, IEnumerable<(string name, int amt)> entries)
            {
                var lines = entries.Where(e => e.amt > 0).Select(e => $"{e.name}  {e.amt}\uE049").ToList();
                if (lines.Count > 0) betTooltip[section] = string.Join("\n", lines);
            }

            AddTip("PASS",     table.CrapsBets.Select(kv => (kv.Key, kv.Value.PassLineBet)));
            AddTip("DONTPASS", table.CrapsBets.Select(kv => (kv.Key, kv.Value.DontPassBet)));
            AddTip("FIELD",    table.CrapsBets.Select(kv => (kv.Key, kv.Value.FieldBet)));
            AddTip("BIG6",     table.CrapsBets.Select(kv => (kv.Key, kv.Value.Big6Bet)));
            AddTip("BIG8",     table.CrapsBets.Select(kv => (kv.Key, kv.Value.Big8Bet)));

            int[] pNums = { 4, 5, 6, 8, 9, 10 };
            foreach (int pn in pNums)
                AddTip($"PLACE{pn}", table.CrapsBets.Select(kv =>
                    (kv.Key, kv.Value.PlaceBets.GetValueOrDefault(pn))));

            string? ttTitle = null, ttBody = null;

            bool HoverRect(float rx, float ry, float rw, float rh, string section)
            {
                if (mouse.X < rx || mouse.X >= rx + rw || mouse.Y < ry || mouse.Y >= ry + rh) return false;
                if (betTooltip.TryGetValue(section, out var body)) { ttTitle = section; ttBody = body; }
                return true;
            }

            // Felt background
            drawList.AddRectFilled(pos, pos + new Vector2(W, H), 0xFF1A5C2A, 6f);
            drawList.AddRect(pos, pos + new Vector2(W, H), 0xFFFFD700, 6f, ImDrawFlags.None, 2f);

            float y = pos.Y + pad;
            float x0 = pos.X + pad;
            float totalW = W - pad * 2;

            // ── Don't Pass bar ────────────────────────────────────────────────
            const float dpH = 16f;
            drawList.AddRectFilled(new Vector2(x0, y), new Vector2(x0 + totalW, y + dpH), 0xFF8B1A1A, 3f);
            drawList.AddRect(new Vector2(x0, y), new Vector2(x0 + totalW, y + dpH), 0xFFCC4444, 3f);
            var dpSz = ImGui.CalcTextSize("DON'T PASS");
            drawList.AddText(new Vector2(x0 + totalW * 0.5f - dpSz.X * 0.5f, y + dpH * 0.5f - dpSz.Y * 0.5f), 0xFFFFFFFF, "DON'T PASS");
            if (betTooltip.ContainsKey("DONTPASS"))
                drawList.AddCircleFilled(new Vector2(x0 + totalW - 8f, y + dpH * 0.5f), 5f, 0xCCFFCC22u, 10);
            HoverRect(x0, y, totalW, dpH, "DONTPASS");
            y += dpH + 2f;

            // ── Place number boxes + Big 6/8 ─────────────────────────────────
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
                drawList.AddRect(new Vector2(bx, y), new Vector2(bx + boxW, y + boxH),
                    isPoint ? 0xFFFFD700 : 0xFF448844, 3f);
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
                if (betTooltip.ContainsKey($"PLACE{n}"))
                    drawList.AddCircleFilled(new Vector2(bx + 8f, y + boxH - 8f), 5f, 0xCCFFCC22u, 10);
                HoverRect(bx, y, boxW, boxH, $"PLACE{n}");
            }
            float bigX = x0 + 6f * (boxW + 1f) + 1f;
            drawList.AddRectFilled(new Vector2(bigX, y), new Vector2(bigX + bigSideW, y + boxH), 0xFF1A3A5C, 3f);
            drawList.AddRect(new Vector2(bigX, y), new Vector2(bigX + bigSideW, y + boxH), 0xFF4488AA, 3f);
            var bigSz   = ImGui.CalcTextSize("BIG");
            var big68Sz = ImGui.CalcTextSize("6 | 8");
            drawList.AddText(new Vector2(bigX + bigSideW * 0.5f - bigSz.X * 0.5f, y + 4f), 0xFFCCEEFF, "BIG");
            drawList.AddText(new Vector2(bigX + bigSideW * 0.5f - big68Sz.X * 0.5f, y + 4f + bigSz.Y + 1f), 0xFFFFFFFF, "6 | 8");
            if (betTooltip.ContainsKey("BIG6"))
                drawList.AddCircleFilled(new Vector2(bigX + 8f, y + boxH - 8f), 5f, 0xCCFFCC22u, 10);
            if (betTooltip.ContainsKey("BIG8"))
                drawList.AddCircleFilled(new Vector2(bigX + bigSideW - 8f, y + boxH - 8f), 5f, 0xCCFFCC22u, 10);
            HoverRect(bigX, y, bigSideW, boxH, "BIG6");
            y += boxH + 2f;

            // ── Pass Line + Field strip ───────────────────────────────────────
            const float stripH = 22f;
            float passW = totalW * 0.68f;
            drawList.AddRectFilled(new Vector2(x0, y), new Vector2(x0 + passW, y + stripH), 0xFF0D5C1A, 3f);
            drawList.AddRect(new Vector2(x0, y), new Vector2(x0 + passW, y + stripH), 0xFF44AA44, 3f);
            var plSz = ImGui.CalcTextSize("PASS LINE");
            drawList.AddText(new Vector2(x0 + passW * 0.5f - plSz.X * 0.5f, y + stripH * 0.5f - plSz.Y * 0.5f), 0xFFFFFFFF, "PASS LINE");
            if (betTooltip.ContainsKey("PASS"))
                drawList.AddCircleFilled(new Vector2(x0 + passW - 8f, y + stripH * 0.5f), 5f, 0xCCFFCC22u, 10);
            HoverRect(x0, y, passW, stripH, "PASS");

            float fieldX = x0 + passW + 2f;
            float fieldW = totalW - passW - 2f;
            drawList.AddRectFilled(new Vector2(fieldX, y), new Vector2(fieldX + fieldW, y + stripH), 0xFF5C5C0D, 3f);
            drawList.AddRect(new Vector2(fieldX, y), new Vector2(fieldX + fieldW, y + stripH), 0xFFAAAA44, 3f);
            var fSz = ImGui.CalcTextSize("FIELD");
            drawList.AddText(new Vector2(fieldX + fieldW * 0.5f - fSz.X * 0.5f, y + stripH * 0.5f - fSz.Y * 0.5f), 0xFFFFFFEE, "FIELD");
            if (betTooltip.ContainsKey("FIELD"))
                drawList.AddCircleFilled(new Vector2(fieldX + fieldW - 8f, y + stripH * 0.5f), 5f, 0xCCFFCC22u, 10);
            HoverRect(fieldX, y, fieldW, stripH, "FIELD");
            y += stripH + 2f;

            // ── Shooter label + OFF puck ──────────────────────────────────────
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

            if (ttTitle != null)
            {
                ImGui.BeginTooltip();
                ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), ttTitle);
                if (ttBody != null) ImGui.TextUnformatted(ttBody);
                ImGui.EndTooltip();
            }
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
        private int bacSelectedPlayerIdx = 0;
        private int bacProxyBetType = 0; // 0=PLAYER 1=BANKER 2=TIE

        private void DrawBaccaratInterface()
        {
            var table = engine.CurrentTable;
            var bac = plugin.BaccaratEngine;

            // Phase banner
            bool inRound = table.BaccaratPhase != Models.BaccaratPhase.WaitingForBets;
            ImGui.TextColored(new Vector4(1, 0.84f, 0, 1),
                inRound ? "Round in progress..." : "MINI BACCARAT — Place bets then Deal!");

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
                if (ImGui.Button("DEAL  ", new Vector2(120, 36)))
                    bac.Deal(out _);
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "Place bets first.");
            }
            else
            {
                ImGui.TextColored(new Vector4(1, 0.8f, 0, 1), "Dealing...");
            }

            ImGui.Separator();

            // ── Summarized rules ────────────────────────────────────────────────
            if (ImGui.CollapsingHeader("Baccarat Rules (summary)"))
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
                    ImGui.Text($"{player.Bank}\uE049");

                    string pk = player.Name.ToUpperInvariant();
                    table.BaccaratBets.TryGetValue(pk, out var bb);

                    ImGui.TableSetColumnIndex(2);
                    if (bb != null && bb.PlayerBet > 0)
                        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1, 1), $"{bb.PlayerBet}\uE049");
                    else
                        ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1), "-");

                    ImGui.TableSetColumnIndex(3);
                    if (bb != null && bb.BankerBet > 0)
                        ImGui.TextColored(new Vector4(1, 0.5f, 0.2f, 1), $"{bb.BankerBet}\uE049");
                    else
                        ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1), "-");

                    ImGui.TableSetColumnIndex(4);
                    if (bb != null && bb.TieBet > 0)
                        ImGui.TextColored(new Vector4(0.4f, 1, 0.4f, 1), $"{bb.TieBet}\uE049");
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
                if (ImGui.Button("DEAL CARDS", new Vector2(200, 50)))
                {
                    engine.StartGame();
                }
                ImGui.SameLine();
                if (ImGui.Button("UNDO", new Vector2(100, 50)))
                {
                    engine.Undo();
                }
            }
            else
            {
                ImGui.TextColored(new Vector4(1, 1, 0, 1f), "Game in Progress...");
                if (ImGui.Button("Reset to Lobby", new Vector2(150, 30)))
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
            if (engine.CurrentTable.Players.Count > 0 && engine.CurrentTable.GameState == Models.GameState.Lobby
                && engine.CurrentTable.GameType != Models.GameType.Ultima)
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
            ImGui.Text($"State: {engine.CurrentTable.GameState}");
            ImGui.SameLine(200);
            ImGui.Text($"Mode: {engine.Mode}");
            ImGui.SameLine(350);
            if (engine.CurrentTable.GameState == Models.GameState.Playing)
            {
                ImGui.TextColored(new Vector4(1, 1, 0, 1), $"⏱️ Timer: {engine.CurrentTable.TurnTimeRemaining}s");
            }
            else
            {
                ImGui.Text($"⏱Timer: {engine.CurrentTable.TurnTimeLimit}s");
            }

            // Current turn info
            if (engine.CurrentTable.GameState == Models.GameState.Playing && 
                engine.CurrentTable.CurrentTurnIndex < engine.CurrentTable.TurnOrder.Count)
            {
                var currentPlayer = engine.CurrentTable.TurnOrder[engine.CurrentTable.CurrentTurnIndex];
                ImGui.TextColored(new Vector4(0, 1, 0, 1), $"Current Turn: {currentPlayer}");
            }
        }

        private void DrawDealerSection()
        {
            ImGui.TextColored(new Vector4(1, 0.5f, 0, 1f), "DEALER");

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

        private void DrawPlayersManagementTab()
        {
            ImGui.TextColored(new Vector4(0.2f, 1f, 0.8f, 1f), "PLAYER MANAGEMENT");

            // Quick Add Section
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1, 1, 0.5f, 1f), "ADD PLAYERS");

            ImGui.SetNextItemWidth(200);
            ImGui.InputTextWithHint("##quickAdd", "Player Name or Name@Server", ref quickAddPlayerName, 100);
            ImGui.SameLine();
            if (ImGui.Button("Add Player", new Vector2(100, 0)))
            {
                if (!string.IsNullOrWhiteSpace(quickAddPlayerName))
                {
                    engine.AddPlayer(quickAddPlayerName.Trim());
                    quickAddPlayerName = string.Empty;
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Add Party", new Vector2(100, 0)))
                plugin.AddPartyToTable();

            var _snap = GetCachedSnapshot();
            if (_snap != null)
            {
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.10f, 0.30f, 0.60f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.20f, 0.45f, 0.80f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.30f, 0.55f, 1.00f, 1f));
                if (ImGui.Button($"\u21a9 Restore ({_snap.SavedAt:HH:mm}, {_snap.Players.Count}p)"))
                    engine.RestoreSnapshot(_snap);
                ImGui.PopStyleColor(3);
            }

            ImGui.Separator();

            // Players Table
            if (engine.CurrentTable.Players.Count == 0)
            {
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "No players added yet. Use the + Add Player button above.");
                return;
            }

            ImGui.TextColored(new Vector4(1, 1, 0.5f, 1f), $"PLAYERS TABLE ({engine.CurrentTable.Players.Count} players)");

            var gameType    = engine.CurrentTable.GameType;
            bool isRoulette  = gameType == Models.GameType.Roulette;
            bool showBankCol = gameType != Models.GameType.Ultima;
            bool showBetCol  = gameType != Models.GameType.None &&
                               gameType != Models.GameType.Craps &&
                               gameType != Models.GameType.ChocoboRacing &&
                               gameType != Models.GameType.TexasHoldEm &&
                               gameType != Models.GameType.Ultima;
            bool showPokerCards = gameType == Models.GameType.TexasHoldEm && _showPokerHoleCards;
            bool showCardsCol = gameType == Models.GameType.Blackjack || showPokerCards;
            int nextIdx   = 2; // after Name (0), Server (1)
            int c_bank    = showBankCol  ? nextIdx++ : -1;
            int c_bet     = showBetCol   ? nextIdx++ : -1;
            int c_afk     = nextIdx++;
            int c_stats   = nextIdx++;
            int c_cards   = showCardsCol ? nextIdx++ : -1;
            int c_actions = nextIdx;
            int colCount  = nextIdx + 1;

            if (ImGui.BeginTable("PlayersTable", colCount, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
            {
                // Table Headers
                ImGui.TableSetupColumn("Name",   ImGuiTableColumnFlags.WidthStretch, 2.0f);
                ImGui.TableSetupColumn("Server", ImGuiTableColumnFlags.WidthStretch, 1.3f);
                if (showBankCol)
                    ImGui.TableSetupColumn("Bank",   ImGuiTableColumnFlags.WidthStretch, 1.2f);
                if (showBetCol)
                    ImGui.TableSetupColumn("Bet", ImGuiTableColumnFlags.WidthStretch, 1.0f);
                ImGui.TableSetupColumn("AFK", ImGuiTableColumnFlags.WidthFixed, 40f);
                string statsLabel = gameType switch
                {
                    Models.GameType.Roulette      => "❖ R.Net",
                    Models.GameType.Craps         => "❖ C.Net",
                    Models.GameType.Baccarat      => "❖ B.Net",
                    Models.GameType.ChocoboRacing => "❖ CH.Net",
                    Models.GameType.TexasHoldEm   => "❖ P.Net",
                    _                             => "❖ Stats"
                };
                ImGui.TableSetupColumn(statsLabel, ImGuiTableColumnFlags.WidthStretch, 1.3f);
                if (showCardsCol)
                    ImGui.TableSetupColumn(
                        showPokerCards ? "Hole Cards" : "Cards",
                        ImGuiTableColumnFlags.WidthFixed,
                        showPokerCards ? 90 : 300);
                ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthStretch, 2.0f);
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

                    // Snapshot kicked state NOW — before any Kick button can change it this frame.
                    // An unbalanced Push/Pop corrupts the entire ImGui style stack for ALL windows.
                    bool wasKickedAtRowStart = player.IsKicked;
                    if (wasKickedAtRowStart)
                    {
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                            ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.12f, 0.12f, 1f)));
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.35f, 0.35f, 0.35f, 1f));
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
                    if (showBankCol)
                    {
                        ImGui.TableSetColumnIndex(c_bank);
                        ImGui.SetNextItemWidth(-1);
                        string tempBank = editingBank[playerKey];

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
                                editingBank[playerKey] = player.Bank.ToString();
                        }
                        ImGui.PopStyleColor();
                    }

                    // Bet Column - LIVE UPDATING (hidden for Craps/Ultima/etc.)
                    if (showBetCol)
                    {
                        ImGui.TableSetColumnIndex(c_bet);
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
                            ImGui.TextColored(nc, net >= 0 ? $"+{net}\uE049" : $"{net}\uE049");
                            break;
                        }
                        case Models.GameType.Craps:
                        {
                            int net = player.CrapsNetGains;
                            Vector4 nc = net > 0 ? new Vector4(0, 1, 0, 1f) : net < 0 ? new Vector4(1, 0.4f, 0.4f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                            ImGui.TextColored(nc, net >= 0 ? $"+{net}\uE049" : $"{net}\uE049");
                            break;
                        }
                        case Models.GameType.Baccarat:
                        {
                            int net = player.BaccaratNetGains;
                            Vector4 nc = net > 0 ? new Vector4(0, 1, 0, 1f) : net < 0 ? new Vector4(1, 0.4f, 0.4f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                            ImGui.TextColored(nc, net >= 0 ? $"+{net}\uE049" : $"{net}\uE049");
                            break;
                        }
                        case Models.GameType.ChocoboRacing:
                        {
                            int net = player.ChocoboNetGains;
                            Vector4 nc = net > 0 ? new Vector4(0, 1, 0, 1f) : net < 0 ? new Vector4(1, 0.4f, 0.4f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                            ImGui.TextColored(nc, net >= 0 ? $"+{net}\uE049" : $"{net}\uE049");
                            break;
                        }
                        case Models.GameType.TexasHoldEm:
                        {
                            int net = player.PokerNetGains;
                            Vector4 nc = net > 0 ? new Vector4(0, 1, 0, 1f) : net < 0 ? new Vector4(1, 0.4f, 0.4f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                            ImGui.TextColored(nc, net >= 0 ? $"+{net}\uE049" : $"{net}\uE049");
                            break;
                        }
                        case Models.GameType.Ultima:
                        {
                            if (player.UltimaWins > 0 || player.UltimaLosses > 0)
                            {
                                ImGui.TextColored(new Vector4(0.3f, 1f, 0.5f, 1f), $"W:{player.UltimaWins}");
                                ImGui.SameLine(0, 4);
                                ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), $"L:{player.UltimaLosses}");
                            }
                            else
                                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "-");
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

                    // Cards Column — Blackjack or Poker hole cards
                    if (showCardsCol)
                    {
                        ImGui.TableSetColumnIndex(c_cards);
                        if (showPokerCards)
                        {
                            DrawPokerHoleCardsCell(player);
                        }
                        else
                        {
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

                                // Display cards
                                for (int cardIndex = 0; cardIndex < handInfo.Cards.Count; cardIndex++)
                                {
                                    var card = handInfo.Cards[cardIndex];
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
                        } // end blackjack cards block
                    } // end showCardsCol block

                    // Actions Column
                    ImGui.TableSetColumnIndex(c_actions);
                    if (player.IsKicked)
                    {
                        ImGui.TextColored(new Vector4(0.6f, 0.3f, 0.3f, 1f), "[Kicked]");
                        ImGui.SameLine();
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.55f, 1f));
                        if (ImGui.Button($"Remove##rm{i}", new Vector2(65, 0)))
                        {
                            engine.HardRemovePlayer(player.Name);
                            editingName.Remove(playerKey);
                            editingServer.Remove(playerKey);
                            editingBank.Remove(playerKey);
                            editingBet.Remove(playerKey);
                        }
                        ImGui.PopStyleColor();
                    }
                    else
                    {
                        if (ImGui.Button($"Kick##kick{i}", new Vector2(50, 0)))
                            engine.RemovePlayer(player.Name);
                        ImGui.SameLine();
                        if (ImGui.Button($"DM##dm{i}", new Vector2(40, 0)))
                        {
                            string betInfo = player.PersistentBet > 0 ? $"Bet: {player.PersistentBet}" : "No bet placed";
                            int bjNet = player.TotalWinnings;
                            int rNet = player.RouletteNetGains;
                            string netInfo = $"net: {(bjNet >= 0 ? "+" : "")}{bjNet}\uE049 | R net: {(rNet >= 0 ? "+" : "")}{rNet}\uE049";
                            engine.OnPlayerTell?.Invoke($"{player.Name}@{player.Server}", $"Bank: {player.Bank} | {betInfo} | {netInfo}");
                        }
                        ImGui.SameLine();
                        if (ImGui.Button($"{(player.IsAfk ? "[AFK]" : "AFK")}##afkbtn{i}", new Vector2(38, 0)))
                            engine.ToggleAFK(player.Name);
                    }

                    if (wasKickedAtRowStart) ImGui.PopStyleColor();
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
                    case Models.GameType.None:              break;
                    case Models.GameType.Roulette:      plugin.RouletteEngine.ForceStop(); break;
                    case Models.GameType.Craps:         plugin.CrapsEngine.ForceStop();    break;
                    case Models.GameType.Baccarat:      plugin.BaccaratEngine.ForceStop(); break;
                    case Models.GameType.ChocoboRacing: plugin.ChocoboEngine.ForceStop();  break;
                    case Models.GameType.TexasHoldEm:   plugin.PokerEngine.ForceStop();    break;
                    case Models.GameType.Ultima:        plugin.UltimaEngine.ForceEnd();    break;
                    default:                            engine.ForceStop();                break;
                }
            }
            ImGui.PopStyleColor(3);
        }

        // ── NONE (IDLE) UI ────────────────────────────────────────────────────────

        private void DrawNoneInterface()
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "NO GAME ACTIVE");
            ImGui.Spacing();
            ImGui.TextWrapped("The plugin is currently idle. Select a game from the dropdown above to begin. No chat commands will be processed.");
            ImGui.Separator();
            DrawPlayersManagementTab();
        }

        // ── ULTIMA! UI ───────────────────────────────────────────────────────────

        private int ultimaResendIdx = 0;

        private void DrawUltimaInterface()
        {
            var table  = engine.CurrentTable;
            var ultima = plugin.UltimaEngine;

            // ── Header ───────────────────────────────────────────────────────────
            ImGui.TextColored(new Vector4(0.84f, 0.76f, 0.08f, 1f), "\u2756 ULTIMA!");
            if (table.UltimaPhase == Models.UltimaPhase.Playing)
            {
                ImGui.SameLine();
                string dir = table.UltimaClockwise ? "\u25ba Clockwise" : "\u25c4 Counter-clockwise";
                ImGui.TextColored(new Vector4(0.5f, 1f, 1f, 1f), $"  {dir}");
                if (table.UltimaPlayerOrder.Count > 0)
                {
                    ImGui.SameLine(320);
                    string cur = table.UltimaPlayerOrder[table.UltimaCurrentIndex];
                    ImGui.TextColored(new Vector4(0.3f, 1f, 0.5f, 1f), $"Turn: {table.GetDisplayName(cur)}");
                }
            }
            else if (table.UltimaPhase == Models.UltimaPhase.Complete)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), $"  \u2756 {table.GetDisplayName(table.UltimaWinner)} wins!");
            }
            ImGui.Separator();

            // ── Shared oval table (same visual as player view, fed from engine state) ──
            DrawUltimaTableFromEngine();
            ImGui.Separator();

            // ── Privacy note when game is active ─────────────────────────────────
            if (table.UltimaPhase == Models.UltimaPhase.Playing)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.78f, 0.2f, 1f));
                ImGui.TextWrapped("\u26a0 Other players' hands are private. Your own hand is below.");
                ImGui.PopStyleColor();
                ImGui.Spacing();

                // ── Dealer's own hand (the admin is a player too) ─────────────────
                if (!string.IsNullOrEmpty(AdminName) &&
                    table.UltimaHands.TryGetValue(AdminName, out var dealerHand) && dealerHand.Count > 0)
                {
                    bool myTurn = table.UltimaCurrentIndex < table.UltimaPlayerOrder.Count &&
                                  table.UltimaPlayerOrder[table.UltimaCurrentIndex]
                                      .Equals(AdminName, StringComparison.OrdinalIgnoreCase);

                    ImGui.TextColored(new Vector4(0.5f, 1f, 1f, 1f), "YOUR HAND");
                    ImGui.SameLine(120);
                    if (myTurn)
                        ImGui.TextColored(new Vector4(0.3f, 1f, 0.5f, 1f), "\u25ba YOUR TURN - click to play");
                    ImGui.Spacing();

                    DrawUltimaHandDealer(dealerHand, ultima, AdminName, myTurn);
                    ImGui.Spacing();
                    ImGui.Separator();
                }
            }

            // ── Start / admin controls ────────────────────────────────────────────
            if (table.UltimaPhase != Models.UltimaPhase.Playing)
            {
                ImGui.Spacing();
                if (ImGui.Button("\u2756 Start Ultima!", new Vector2(160, 32)))
                    ultima.StartGame(AdminName, out _);
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "Or any player: >DEAL");
            }
            else
            {
                // Resend hand — only meaningful while a game is running
                var pNames = table.UltimaPlayerOrder.ToArray();
                if (pNames.Length > 0)
                {
                    if (ultimaResendIdx >= pNames.Length) ultimaResendIdx = 0;
                    ImGui.SetNextItemWidth(150);
                    ImGui.Combo("##uresend", ref ultimaResendIdx, pNames, pNames.Length);
                    ImGui.SameLine();
                    if (ImGui.Button("Resend Hand", new Vector2(100, 24)))
                        ultima.ResendHand(pNames[ultimaResendIdx]);
                }
            }

            // ── Force End — always visible at bottom ──────────────────────────────
            ImGui.Spacing();
            ImGui.Separator();
            bool notPlaying = table.UltimaPhase != Models.UltimaPhase.Playing;
            ImGui.BeginDisabled(notPlaying);
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.55f, 0.10f, 0.10f, notPlaying ? 0.4f : 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.20f, 0.20f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(1.00f, 0.30f, 0.30f, 1f));
            if (ImGui.Button("\u26d4 Force End Ultima", new Vector2(160, 28)))
                ultima.ForceEnd();
            ImGui.PopStyleColor(3);
            ImGui.EndDisabled();

            // ── Rules / Help quick-reference ──────────────────────────────────────
            ImGui.SameLine(0, 12);
            if (ImGui.Button("Rules", new Vector2(56, 28)))
            {
                string pfx = engine.ChatMode == Models.ChatMode.Party ? "/party " : "/say ";
                plugin.SendGameMessage(pfx + ">RULES");
            }
            ImGui.SameLine();
            if (ImGui.Button("Help", new Vector2(48, 28)))
            {
                string pfx = engine.ChatMode == Models.ChatMode.Party ? "/party " : "/say ";
                plugin.SendGameMessage(pfx + ">HELP");
            }
            ImGui.Spacing();

            DrawPlayersManagementTab();
        }

        private void DrawUltimaCardInline(Models.UltimaCard card, float w, float h)
        {
            var dl  = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            var bg  = ImGui.ColorConvertFloat4ToU32(Models.UltimaCard.ColorVec(card.Color));
            var txt = ImGui.ColorConvertFloat4ToU32(Models.UltimaCard.TextColor(card.Color));
            dl.AddRectFilled(pos, pos + new Vector2(w, h), bg, 6f);
            dl.AddRect(pos, pos + new Vector2(w, h),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.25f)), 6f, ImDrawFlags.None, 1.5f);
            var sym = card.Symbol;
            var ssz = ImGui.CalcTextSize(sym);
            dl.AddText(pos + new Vector2(w * 0.5f - ssz.X * 0.5f, h * 0.5f - ssz.Y * 0.5f), txt, sym);
            ImGui.Dummy(new Vector2(w + 4, h + 4));
        }

        private void DrawUltimaHandRow(System.Collections.Generic.List<Models.UltimaCard> hand)
        {
            var dl  = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            float cw = 30f, ch = 44f, gap = 2f;
            for (int i = 0; i < hand.Count; i++)
            {
                var card = hand[i];
                float x = pos.X + i * (cw + gap);
                var   bg = ImGui.ColorConvertFloat4ToU32(Models.UltimaCard.ColorVec(card.Color));
                var   fg = ImGui.ColorConvertFloat4ToU32(Models.UltimaCard.TextColor(card.Color));
                dl.AddRectFilled(new Vector2(x, pos.Y), new Vector2(x + cw, pos.Y + ch), bg, 3f);
                var sym = card.Symbol;
                var ssz = ImGui.CalcTextSize(sym);
                if (ssz.X < cw)
                    dl.AddText(new Vector2(x + cw * 0.5f - ssz.X * 0.5f, pos.Y + ch * 0.5f - ssz.Y * 0.5f), fg, sym);
            }
            ImGui.Dummy(new Vector2(hand.Count * (cw + gap) + 4, ch + 4));
        }

        // ── POKER HOLE CARDS CELL HELPER ──────────────────────────────────────────

        private int _dealerUltimaSelIdx  = -1;
        private int _dealerUltimaColPick = 0;

        /// <summary>
        /// Dealer's interactive Ultima hand — same InvisibleButton-per-card approach as the
        /// player view but calls <see cref="UltimaEngine.PlayCard"/> directly (no chat round-trip).
        /// </summary>
        private void DrawUltimaHandDealer(
            System.Collections.Generic.List<Models.UltimaCard> hand,
            Engine.UltimaEngine ultima,
            string adminName,
            bool myTurn)
        {
            var dl = ImGui.GetWindowDrawList();

            float cw = 58f, ch = 82f, gap = 6f, lift = 14f;
            var basePos  = ImGui.GetCursorScreenPos();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + lift);
            var startPos = ImGui.GetCursorScreenPos();

            for (int i = 0; i < hand.Count; i++)
            {
                var  card = hand[i];
                bool sel  = _dealerUltimaSelIdx == i;

                ImGui.SetCursorScreenPos(new Vector2(startPos.X + i*(cw+gap), startPos.Y - lift));
                bool clicked = ImGui.InvisibleButton($"##dcard_{i}", new Vector2(cw, ch + lift));
                bool hover   = ImGui.IsItemHovered();

                float cardY = (sel || hover) ? startPos.Y - lift : startPos.Y;
                var   cpos  = new Vector2(startPos.X + i*(cw+gap), cardY);

                if (sel)
                    dl.AddRectFilled(cpos - new Vector2(3,3), cpos + new Vector2(cw+3,ch+3),
                        ImGui.ColorConvertFloat4ToU32(new Vector4(1f,1f,0.3f,0.55f)), 9f);

                DrawUltimaCardInline_AtPos(dl, cpos, cw, ch, card);

                if (hover) ImGui.SetTooltip(card.DisplayName);

                if (clicked)
                {
                    if (card.IsWild)
                        _dealerUltimaSelIdx = (_dealerUltimaSelIdx == i) ? -1 : i;
                    else if (myTurn)
                    {
                        ultima.PlayCard(adminName, card.Code, null, out _);
                        _dealerUltimaSelIdx = -1;
                        SyncDealerHandToPlayerView(adminName);
                    }
                    else
                        _dealerUltimaSelIdx = (_dealerUltimaSelIdx == i) ? -1 : i;
                }
            }

            ImGui.SetCursorScreenPos(new Vector2(basePos.X, startPos.Y + ch + 8f));
            ImGui.Dummy(new Vector2(hand.Count > 0 ? hand.Count*(cw+gap) : 1f, 1f));

            // Wild color picker
            Models.UltimaCard? selCard = _dealerUltimaSelIdx >= 0 && _dealerUltimaSelIdx < hand.Count
                ? hand[_dealerUltimaSelIdx] : null;
            if (selCard != null && selCard.IsWild)
            {
                ImGui.TextColored(new Vector4(0.7f,0.7f,0.7f,1f), $"[{selCard.Code}] Choose color:");
                ImGui.SameLine();
                string[]  cnames = { "Water", "Fire", "Grass", "Light" };
                Vector4[] cvecs  = {
                    new(0.14f,0.38f,0.82f,1f), new(0.82f,0.14f,0.14f,1f),
                    new(0.12f,0.64f,0.22f,1f), new(0.84f,0.76f,0.08f,1f),
                };
                for (int ci = 0; ci < 4; ci++)
                {
                    bool pick = _dealerUltimaColPick == ci;
                    ImGui.PushStyleColor(ImGuiCol.Button, cvecs[ci] with { W = pick ? 1f : 0.45f });
                    if (ImGui.Button(cnames[ci] + "##dcolbtn" + ci, new Vector2(66, 30)))
                    {
                        _dealerUltimaColPick = ci;
                        if (myTurn)
                        {
                            ultima.PlayCard(adminName, selCard.Code, cnames[ci].ToUpperInvariant(), out _);
                            _dealerUltimaSelIdx = -1;
                            SyncDealerHandToPlayerView(adminName);
                        }
                    }
                    ImGui.PopStyleColor();
                    if (ci < 3) ImGui.SameLine();
                }
                ImGui.Spacing();
            }

            // Utility row
            ImGui.Spacing();
            ImGui.BeginDisabled(!myTurn);
            if (ImGui.Button("DRAW##dealerDraw", new Vector2(70, 28)))
            {
                ultima.DrawCard(adminName, out _);
                SyncDealerHandToPlayerView(adminName);
            }
            ImGui.EndDisabled();

            ImGui.SameLine();
            bool ultima1 = hand.Count == 1;
            if (ultima1) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f,0.15f,0.15f,1f));
            if (ImGui.Button("ULTIMA!##dealerUlt", new Vector2(75, 28)))
                ultima.CallUltima(adminName);
            if (ultima1) ImGui.PopStyleColor();
        }

        /// <summary>
        /// After the dealer plays or draws a card from the dealer panel, the engine state
        /// updates immediately but the tell-to-self never arrives as TellIncoming.
        /// This pushes ALL relevant Ultima state into the player-view parser so the Player
        /// tab stays perfectly in sync without any chat round-trip.
        /// </summary>
        private void SyncDealerHandToPlayerView(string adminName)
        {
            var t = engine.CurrentTable;
            var s = plugin.ChatParser.State;
            // Atomic list replacements to avoid the render thread seeing an empty
            // list between Clear() and AddRange() (race condition → "No game in progress").
            if (t.UltimaHands.TryGetValue(adminName, out var syncHand))
                s.UltimaHand = new List<Models.UltimaCard>(syncHand);
            s.UltimaTopCard     = t.UltimaTopCard;
            s.UltimaActiveColor = t.UltimaActiveColor;
            s.UltimaClockwise   = t.UltimaClockwise;
            s.UltimaWinner      = t.UltimaWinner;
            if (t.UltimaPhase == Models.UltimaPhase.Playing &&
                t.UltimaCurrentIndex < t.UltimaPlayerOrder.Count)
                s.UltimaCurrentPlayer = t.GetDisplayName(t.UltimaPlayerOrder[t.UltimaCurrentIndex]);
            s.UltimaPlayerOrder = t.UltimaPlayerOrder.Select(n => t.GetDisplayName(n)).ToList();
            var newCounts = new Dictionary<string, int>();
            foreach (var kvp in t.UltimaHands)
                newCounts[t.GetDisplayName(kvp.Key)] = kvp.Value.Count;
            s.UltimaCardCounts = newCounts;
        }

        private static void DrawUltimaCardInline_AtPos(
            ImDrawListPtr dl, Vector2 pos, float w, float h, Models.UltimaCard card)
        {
            uint bg  = ImGui.ColorConvertFloat4ToU32(Models.UltimaCard.ColorVec(card.Color));
            uint fg  = ImGui.ColorConvertFloat4ToU32(Models.UltimaCard.TextColor(card.Color));
            uint rim = ImGui.ColorConvertFloat4ToU32(new Vector4(1f,1f,1f,0.18f));
            dl.AddRectFilled(pos, pos+new Vector2(w,h), bg, 6f);
            dl.AddRect(pos, pos+new Vector2(w,h), rim, 6f, ImDrawFlags.None, 1.5f);
            var sym = card.Symbol;
            var ssz = ImGui.CalcTextSize(sym);
            dl.AddText(pos + new Vector2(w*0.5f-ssz.X*0.5f, h*0.5f-ssz.Y*0.5f), fg, sym);
            string code = card.Code;
            var   csz   = ImGui.CalcTextSize(code);
            if (csz.X < w-2) dl.AddText(pos + new Vector2(3, 2), fg, code);
        }

        /// <summary>
        /// Draws the Ultima! oval table using live engine state — mirrors the
        /// layout in PlayerViewWindow.DrawUltimaTable but reads from the Table directly.
        /// </summary>
        private void DrawUltimaTableFromEngine()
        {
            var  table  = engine.CurrentTable;
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
                bool  cw     = table.UltimaClockwise;
                float ar     = 56f;
                uint  arcCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.8f, 0.3f, 0.65f));
                float start  = cw ? -(float)(Math.PI * 0.7) : -(float)(Math.PI * 0.3);
                float span   = cw ?  (float)(Math.PI * 1.4) : -(float)(Math.PI * 1.4);
                int   segs   = 22;
                for (int i = 0; i < segs; i++)
                {
                    float a1 = start + span * i / segs;
                    float a2 = start + span * (i + 1) / segs;
                    dl.AddLine(center + new Vector2(MathF.Cos(a1)*ar, MathF.Sin(a1)*ar),
                               center + new Vector2(MathF.Cos(a2)*ar, MathF.Sin(a2)*ar),
                               arcCol, 2.8f);
                }
                float ae   = start + span;
                float tang = ae + (cw ? (float)(Math.PI*0.5f) : -(float)(Math.PI*0.5f));
                var   tip  = center + new Vector2(MathF.Cos(ae)*ar, MathF.Sin(ae)*ar);
                dl.AddTriangleFilled(
                    tip + new Vector2(MathF.Cos(tang)*13, MathF.Sin(tang)*13),
                    tip + new Vector2(MathF.Cos(tang+MathF.PI-0.45f)*10, MathF.Sin(tang+MathF.PI-0.45f)*10),
                    tip + new Vector2(MathF.Cos(tang+MathF.PI+0.45f)*10, MathF.Sin(tang+MathF.PI+0.45f)*10),
                    arcCol);
            }

            // Top card
            if (table.UltimaTopCard != null)
            {
                float cw2 = 54f, ch2 = 76f;
                var   cp  = center - new Vector2(cw2*0.5f, ch2*0.5f);
                var   bg  = ImGui.ColorConvertFloat4ToU32(Models.UltimaCard.ColorVec(table.UltimaTopCard.Color));
                var   fg  = ImGui.ColorConvertFloat4ToU32(Models.UltimaCard.TextColor(table.UltimaTopCard.Color));
                uint  rim = ImGui.ColorConvertFloat4ToU32(new Vector4(1f,1f,1f,0.18f));
                dl.AddRectFilled(cp, cp+new Vector2(cw2,ch2), bg, 6f);
                dl.AddRect(cp, cp+new Vector2(cw2,ch2), rim, 6f, ImDrawFlags.None, 1.5f);
                string sym = table.UltimaTopCard.Symbol;
                var    ssz = ImGui.CalcTextSize(sym);
                dl.AddText(cp+new Vector2(cw2*0.5f-ssz.X*0.5f, ch2*0.5f-ssz.Y*0.5f), fg, sym);
                string code = table.UltimaTopCard.Code;
                var    csz  = ImGui.CalcTextSize(code);
                if (csz.X < cw2-2) dl.AddText(cp+new Vector2(2,1), fg, code);

                // Active-colour swatch for wilds
                if (table.UltimaTopCard.IsWild)
                {
                    var sp = cp + new Vector2(cw2+5, ch2*0.5f-10);
                    uint sc = ImGui.ColorConvertFloat4ToU32(Models.UltimaCard.ColorVec(table.UltimaActiveColor));
                    dl.AddRectFilled(sp, sp+new Vector2(20,20), sc, 4f);
                    dl.AddRect(sp, sp+new Vector2(20,20),
                        ImGui.ColorConvertFloat4ToU32(new Vector4(1,1,1,0.4f)), 4f);
                    string cl = Models.UltimaCard.ColorDisplayName(table.UltimaActiveColor);
                    var    clsz = ImGui.CalcTextSize(cl);
                    dl.AddText(sp+new Vector2(22, 10-clsz.Y*0.5f),
                        ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f,0.9f,0.9f,0.85f)), cl);
                }
            }
            else
            {
                uint gray = ImGui.ColorConvertFloat4ToU32(new Vector4(0.4f,0.6f,0.6f,0.5f));
                string lbl = table.UltimaPhase == Models.UltimaPhase.Playing
                    ? "Waiting for cards…" : "No game in progress";
                var lsz = ImGui.CalcTextSize(lbl);
                dl.AddText(center - lsz*0.5f, gray, lbl);
            }

            // Player tokens at fixed seat positions (same angles as PlayerViewWindow)
            var players  = table.UltimaPlayerOrder;
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
                bool   isCur = pname.Equals(
                    nPlayers > 0 ? players[table.UltimaCurrentIndex] : string.Empty,
                    StringComparison.OrdinalIgnoreCase);
                int    cnt   = table.UltimaHands.TryGetValue(pname, out var ph) ? ph.Count : 0;
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
                        var    msz  = ImGui.CalcTextSize(more);
                        dl.AddText(pos+new Vector2(10*(pip+gap2)-rowW*0.5f+2, bh*0.5f+2),
                            ImGui.ColorConvertFloat4ToU32(new Vector4(0.7f,0.7f,0.7f,0.9f)), more);
                    }
                }
            }

            ImGui.Dummy(new Vector2(W, H + 4f));

            // Winner banner below the oval
            if (!string.IsNullOrEmpty(table.UltimaWinner))
            {
                string winMsg = $"\u2756 {table.GetDisplayName(table.UltimaWinner)} wins Ultima! \u2756";
                var    wsz   = ImGui.CalcTextSize(winMsg);
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (W - wsz.X) * 0.5f);
                ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), winMsg);
            }
        }

        private void DrawPokerHoleCardsCell(Models.Player player)
        {
            var table = engine.CurrentTable;
            bool handInProgress = table.PokerPhase != Models.PokerPhase.WaitingForPlayers &&
                                   table.PokerPhase != Models.PokerPhase.Complete;
            var seat = plugin.PokerEngine.Seats.FirstOrDefault(s =>
                s.IsOccupied && s.PlayerName.Equals(player.Name, StringComparison.OrdinalIgnoreCase));
            if (seat == null || !handInProgress || string.IsNullOrEmpty(seat.HoleCard1.Value))
            {
                ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1f), "—");
                return;
            }
            float alpha = seat.IsFolded ? 0.45f : 1f;
            var c1 = seat.HoleCard1;
            var c2 = seat.HoleCard2;
            ImGui.TextColored(new Vector4(c1.IsRed ? 1f : 0.9f, c1.IsRed ? 0.3f : 0.9f, c1.IsRed ? 0.3f : 0.9f, alpha), c1.GetCardDisplay());
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(c2.IsRed ? 1f : 0.9f, c2.IsRed ? 0.3f : 0.9f, c2.IsRed ? 0.3f : 0.9f, alpha), c2.GetCardDisplay());
            if (seat.IsFolded)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 0.8f), "X");
            }
        }

        // ── CHOCOBO RACING UI ─────────────────────────────────────────────────────

        private int chocoSelectedPlayerIdx = 0;
        private int chocoSelectedRacerIdx  = 0;
        private int chocoBetAmt = 50;

        private void DrawChocoboInterface()
        {
            var table  = engine.CurrentTable;
            var chocobo = plugin.ChocoboEngine;
            bool racing   = table.ChocoboRacePhase == Models.ChocoboRacePhase.Racing;
            bool complete = table.ChocoboRacePhase == Models.ChocoboRacePhase.Complete;
            bool idle     = table.ChocoboRacePhase == Models.ChocoboRacePhase.Idle;

            // Phase banner
            ImGui.TextColored(new Vector4(1, 0.84f, 0, 1),
                racing   ? "Race in progress — 30 second race!" :
                complete ? "Race complete! Payouts processed." :
                idle     ? "CHOCOBO RACING — Press New Race to open betting." :
                "CHOCOBO RACING — Place bets then Start Race!");

            ImGui.Separator();

            // ── Roster ───────────────────────────────────────────────────────────
            ImGui.TextColored(new Vector4(0.5f, 1, 1, 1), "RACE ROSTER");
            if (ImGui.BeginTable("##chocoRoster", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("#",    ImGuiTableColumnFlags.WidthFixed,   22);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("SPD",  ImGuiTableColumnFlags.WidthFixed,   38);
                ImGui.TableSetupColumn("END",  ImGuiTableColumnFlags.WidthFixed,   38);
                ImGui.TableSetupColumn("XF",   ImGuiTableColumnFlags.WidthFixed,   42);
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
                    ImGui.TextColored(new Vector4(0.6f, 1f, 0.8f, 1f), $"{racer.XFactor:0.00}x");

                    ImGui.TableSetColumnIndex(5);
                    ImGui.TextColored(new Vector4(1, 0.84f, 0, 1), $"{racer.Odds:0.0}x");

                    ImGui.TableSetColumnIndex(6);
                    int totalOnRacer = table.ChocoboBets.Values.Where(b => b.RacerIndex == i).Sum(b => b.Amount);
                    if (totalOnRacer > 0)
                        ImGui.TextColored(new Vector4(0.4f, 1, 0.4f, 1), $"{totalOnRacer}\uE049");
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
            if (idle || complete)
            {
                if (ImGui.Button("NEW RACE", new Vector2(110, 32)))
                {
                    chocobo.OpenBetting(out _);
                }
            }
            else if (!racing)
            {
                if (ImGui.Button("START RACE", new Vector2(130, 32)))
                {
                    if (!chocobo.StartRace(out string err))
                        engine.Announce(err);
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "Place bets first. Admin: >START");
            }
            else
            {
                double elapsed = (DateTime.Now - table.ChocoboRaceStart).TotalSeconds;
                double remaining = Math.Max(0, 30.0 - elapsed);
                ImGui.TextColored(new Vector4(1, 1, 0, 1), $"Time remaining: {remaining:F0}s");
            }

            ImGui.Separator();

            // ── Bet controls (only during betting phase) ──────────────────────────
            if (!idle && !racing && !complete)
            {
                ImGui.TextColored(new Vector4(1, 1, 0, 1), "PLACE BET");
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), " Range:");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                int cMin = table.ChocoboMinBet;
                if (ImGui.InputInt("##chocomin", ref cMin, 5, 100))
                    table.ChocoboMinBet = Math.Max(1, Math.Min(cMin, table.ChocoboMaxBet));
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "-");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(130);
                int cMax = table.ChocoboMaxBet;
                if (ImGui.InputInt("##chocomax", ref cMax, 100, 1000))
                    table.ChocoboMaxBet = Math.Max(table.ChocoboMinBet, cMax);

                var pNames = table.Players.Values.Select(p => p.Name).ToArray();
                if (pNames.Length > 0)
                {
                    if (chocoSelectedPlayerIdx >= pNames.Length) chocoSelectedPlayerIdx = 0;
                    ImGui.SetNextItemWidth(140);
                    ImGui.Combo("##chocoplayer", ref chocoSelectedPlayerIdx, pNames, pNames.Length);
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(120);
                    ImGui.InputInt("##chocoamt", ref chocoBetAmt);
                    if (chocoBetAmt < table.ChocoboMinBet) chocoBetAmt = table.ChocoboMinBet;
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
                        ImGui.TableSetColumnIndex(2); ImGui.TextColored(new Vector4(1, 1, 0.5f, 1), $"{bet.Amount}\uE049");
                        ImGui.TableSetColumnIndex(3); ImGui.TextColored(new Vector4(1, 0.84f, 0, 1), $"{racer.Odds:0.0}x");
                    }
                    ImGui.EndTable();
                }
                ImGui.Separator();
            }

                    DrawPlayersManagementTab();
            }

            // ── TEXAS HOLD'EM UI ───────────────────────────────────────────────────────

            private int pokerProxyRaiseAmt = 100;

            private void DrawPokerInterface()
            {
                var table  = engine.CurrentTable;
                var poker  = plugin.PokerEngine;
                bool inHand = table.PokerPhase != Models.PokerPhase.WaitingForPlayers &&
                              table.PokerPhase != Models.PokerPhase.Complete;

                // Phase banner
                Vector4 bannerCol = table.PokerPhase switch
                {
                    Models.PokerPhase.PreFlop  => new Vector4(0.4f, 0.8f, 1f,  1f),
                    Models.PokerPhase.Flop     => new Vector4(0.4f, 1f,   0.6f, 1f),
                    Models.PokerPhase.Turn     => new Vector4(1f,   0.8f, 0.2f, 1f),
                    Models.PokerPhase.River    => new Vector4(1f,   0.5f, 0.2f, 1f),
                    Models.PokerPhase.Showdown => new Vector4(1f,   0.3f, 0.3f, 1f),
                    Models.PokerPhase.Complete => new Vector4(0.5f, 1f,   0.5f, 1f),
                    _                          => new Vector4(0.8f, 0.8f, 0.8f, 1f)
                };
                string phaseLabel = table.PokerPhase switch
                {
                    Models.PokerPhase.WaitingForPlayers => "♠ Texas Hold'Em — Press DEAL to start a hand.",
                    Models.PokerPhase.PreFlop  => "♠ Pre-Flop",
                    Models.PokerPhase.Flop     => "♠ Flop",
                    Models.PokerPhase.Turn     => "♠ Turn",
                    Models.PokerPhase.River    => "♠ River",
                    Models.PokerPhase.Showdown => "♠ SHOWDOWN",
                    Models.PokerPhase.Complete => "♠ Hand complete! Press DEAL for next hand.",
                    _                          => "♠ Texas Hold'Em"
                };
                ImGui.TextColored(bannerCol, phaseLabel);

                // Pot + blind info
                if (inHand || table.PokerPhase == Models.PokerPhase.Complete)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), $"  POT: {table.PokerPot}\uE049");
                }

                ImGui.Separator();

                // ── Oval table canvas ────────────────────────────────────────────────────
                ImGui.Dummy(new Vector2(0, 12f));
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 16f);
                var drawList = ImGui.GetWindowDrawList();
                Vector2 canvasPos  = ImGui.GetCursorScreenPos();
                float   canvasW    = 620f;
                float   canvasH    = 270f;
                Vector2 center     = canvasPos + new Vector2(canvasW * 0.5f, canvasH * 0.5f);

                // Felt oval background
                uint feltColor   = ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.35f, 0.08f, 1f));
                uint feltBorder  = ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f,  0.4f,  0.1f,  1f));
                uint potColor    = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.84f, 0f, 1f));
                uint textWhite   = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));
                uint textGray    = ImGui.ColorConvertFloat4ToU32(new Vector4(0.7f, 0.7f, 0.7f, 1f));

                float rx = 205f, ry = 80f;
                const int segs = 48;
                var ovalPath = new List<Vector2>(segs);
                for (int si = 0; si < segs; si++)
                {
                    float ang = (float)(si * 2 * Math.PI / segs);
                    ovalPath.Add(center + new Vector2(MathF.Cos(ang) * rx, MathF.Sin(ang) * ry));
                }
                foreach (var pt in ovalPath) drawList.PathLineTo(pt);
                drawList.PathFillConvex(feltColor);
                foreach (var pt in ovalPath) drawList.PathLineTo(pt);
                drawList.PathStroke(feltBorder, ImDrawFlags.Closed, 3f);

                // Pot label in center of felt
                string potStr  = $"POT: {table.PokerPot}\uE049";
                var    potSize = ImGui.CalcTextSize(potStr);
                drawList.AddText(center - potSize * 0.5f + new Vector2(0, -10f), potColor, potStr);

                // Community cards (up to 5), centered below pot label
                if (table.PokerCommunity.Count > 0)
                {
                    float cardW = 30f, cardH = 22f, gap = 4f;
                    int   numCards = table.PokerCommunity.Count;
                    float totalW = numCards * cardW + (numCards - 1) * gap;
                    float startX = center.X - totalW * 0.5f;
                    float cardY  = center.Y;

                    for (int ci = 0; ci < numCards; ci++)
                    {
                        var    card      = table.PokerCommunity[ci];
                        bool   isRed     = card.Suit == "H" || card.Suit == "D";
                        uint   cardBg    = ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.95f, 0.90f, 1f));
                        uint   cardText  = ImGui.ColorConvertFloat4ToU32(isRed
                                           ? new Vector4(0.85f, 0.1f, 0.1f, 1f)
                                           : new Vector4(0.05f, 0.05f, 0.05f, 1f));
                        Vector2 cardPos = new Vector2(startX + ci * (cardW + gap), cardY);
                        drawList.AddRectFilled(cardPos, cardPos + new Vector2(cardW, cardH), cardBg, 3f);
                        drawList.AddRect(cardPos, cardPos + new Vector2(cardW, cardH), cardText, 3f);
                        string  label    = card.GetCardDisplay();
                        var     lblSz    = ImGui.CalcTextSize(label);
                        drawList.AddText(cardPos + new Vector2(cardW, cardH) * 0.5f - lblSz * 0.5f, cardText, label);
                    }
                }

                // Seats around the oval (rx=265, ry=115, 8 evenly spaced clockwise from top)
                float seatRx = 260f, seatRy = 110f;
                float seatW = 92f, seatH = 38f;

                for (int si = 0; si < PokerEngine.MaxSeats; si++)
                {
                    double angle   = -Math.PI / 2.0 + si * 2.0 * Math.PI / PokerEngine.MaxSeats;
                    float  sx      = center.X + (float)(Math.Cos(angle) * seatRx) - seatW * 0.5f;
                    float  sy      = center.Y + (float)(Math.Sin(angle) * seatRy) - seatH * 0.5f;
                    Vector2 sPos   = new Vector2(sx, sy);
                    var seat       = poker.Seats[si];

                    uint bgColor, borderColor;
                    bool isCurrentTurn = inHand && table.PokerCurrentSeat == si && seat.IsActive;
                    bool isDealer      = table.PokerDealerSeat == si && seat.IsOccupied;
                    var  seatPlayer    = seat.IsOccupied ? engine.GetPlayer(seat.PlayerName) : null;
                    bool seatIsAfk     = seatPlayer?.IsAfk ?? false;

                    if (!seat.IsOccupied)
                    {
                        bgColor     = ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.12f, 0.12f, 0.85f));
                        borderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f,  0.3f,  0.3f,  1f));
                    }
                    else if (seat.IsFolded)
                    {
                        bgColor     = ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f,  0.1f,  0.1f,  0.9f));
                        borderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f,  0.2f,  0.2f,  1f));
                    }
                    else if (seat.IsAllIn)
                    {
                        bgColor     = ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.15f, 0.0f,  0.95f));
                        borderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f,    0.5f,  0.0f,  1f));
                    }
                    else if (seatIsAfk)
                    {
                        bgColor     = ImGui.ColorConvertFloat4ToU32(new Vector4(0.22f, 0.18f, 0.08f, 0.9f));
                        borderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f,  0.5f,  0.2f,  1f));
                    }
                    else if (isCurrentTurn)
                    {
                        bgColor     = ImGui.ColorConvertFloat4ToU32(new Vector4(0.1f,  0.35f, 0.1f,  0.95f));
                        borderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f,  1f,    0.2f,  1f));
                    }
                    else
                    {
                        bgColor     = ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.15f, 0.25f, 0.9f));
                        borderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.4f,  0.4f,  0.7f,  1f));
                    }

                    drawList.AddRectFilled(sPos, sPos + new Vector2(seatW, seatH), bgColor, 4f);
                    drawList.AddRect(sPos, sPos + new Vector2(seatW, seatH), borderColor, 4f, ImDrawFlags.None, isCurrentTurn ? 2f : 1f);

                    if (!seat.IsOccupied)
                    {
                        string emptyLabel = $"Seat {si + 1}";
                        var    eLblSz     = ImGui.CalcTextSize(emptyLabel);
                        drawList.AddText(sPos + new Vector2(seatW, seatH) * 0.5f - eLblSz * 0.5f, textGray, emptyLabel);
                    }
                    else
                    {
                        // Role badge (D/SB/BB)
                        string badge = "";
                        if (table.PokerDealerSeat == si) badge = "D";
                        else
                        {
                            int sbSeat = poker.NextOccupiedSeat(table.PokerDealerSeat);
                            int bbSeat = sbSeat >= 0 ? poker.NextOccupiedSeat(sbSeat) : -1;
                            if (si == sbSeat) badge = "SB";
                            else if (si == bbSeat) badge = "BB";
                        }

                        // Name (trimmed)
                        string displayName = seat.PlayerName.Length > 10
                            ? seat.PlayerName[..10] + "…"
                            : seat.PlayerName;
                        var nameSz = ImGui.CalcTextSize(displayName);
                        drawList.AddText(new Vector2(sPos.X + 4f, sPos.Y + 3f), textWhite, displayName);

                        if (!string.IsNullOrEmpty(badge))
                        {
                            uint   badgeCol  = badge == "D"
                                ? ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.84f, 0f, 1f))
                                : ImGui.ColorConvertFloat4ToU32(new Vector4(0.4f, 0.8f, 1f, 1f));
                            uint   badgeText = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 1f));
                            Vector2 outDir      = new Vector2(MathF.Cos((float)angle), MathF.Sin((float)angle));
                            Vector2 seatCenter  = sPos + new Vector2(seatW * 0.5f, seatH * 0.5f);
                            float   edgeDist    = MathF.Abs(outDir.X) * seatW * 0.5f + MathF.Abs(outDir.Y) * seatH * 0.5f;
                            Vector2 tokenCenter = seatCenter + outDir * (edgeDist + 15f);
                                drawList.AddCircleFilled(tokenCenter, 13f, badgeCol);
                                drawList.AddCircle(tokenCenter, 13f, badgeText, 0, 1.5f);
                            var badgeSz = ImGui.CalcTextSize(badge);
                            drawList.AddText(tokenCenter - badgeSz * 0.5f, badgeText, badge);
                        }

                        // Bet / status line
                        string statusLine;
                        if (seat.IsFolded) statusLine = "FOLDED";
                        else if (seat.IsAllIn) statusLine = $"ALL IN {seat.TotalBet}\uE049";
                        else if (seat.Bet > 0) statusLine = $"Bet: {seat.Bet}\uE049";
                        else if (seatIsAfk) statusLine = "AFK";
                        else
                        {
                            var p = engine.GetPlayer(seat.PlayerName);
                            statusLine = p != null ? $"{p.Bank}\uE049" : "";
                        }
                        uint stCol = seat.IsFolded ? textGray
                                   : seat.IsAllIn  ? ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.5f, 0f, 1f))
                                   : potColor;
                        var stSz = ImGui.CalcTextSize(statusLine);
                        drawList.AddText(new Vector2(sPos.X + 4f, sPos.Y + seatH - stSz.Y - 3f), stCol, statusLine);
                    }
                }

                // Dummy invisible button to reserve canvas space
                ImGui.Dummy(new Vector2(canvasW, canvasH));
                ImGui.Dummy(new Vector2(0, 12f));

                ImGui.Separator();

                // ── Dealer's own hole cards (so they can play as a player too) ──────────
                {
                    string adminName = Plugin.PlayerState?.CharacterName ?? string.Empty;
                    int adminSeat = -1;
                    for (int asi = 0; asi < PokerEngine.MaxSeats; asi++)
                    {
                        if (poker.Seats[asi].IsOccupied &&
                            poker.Seats[asi].PlayerName.Equals(adminName, StringComparison.OrdinalIgnoreCase))
                        { adminSeat = asi; break; }
                    }
                    if (adminSeat >= 0 && inHand && poker.Seats[adminSeat].IsActive &&
                        !string.IsNullOrEmpty(poker.Seats[adminSeat].HoleCard1.Suit))
                    {
                        var seat = poker.Seats[adminSeat];
                        ImGui.TextColored(new Vector4(0.5f, 1f, 1f, 1f), "YOUR HAND:");
                        ImGui.SameLine();
                        var cards = new[] { seat.HoleCard1, seat.HoleCard2 };
                        for (int hci = 0; hci < cards.Length; hci++)
                        {
                            var c = cards[hci];
                            if (string.IsNullOrEmpty(c.Suit)) continue;
                            bool red = c.Suit == "H" || c.Suit == "D";
                            ImGui.TextColored(red ? new Vector4(0.9f, 0.15f, 0.15f, 1f)
                                                  : new Vector4(0.95f, 0.95f, 0.95f, 1f),
                                c.GetCardDisplay());
                            if (hci < cards.Length - 1) ImGui.SameLine();
                        }
                        ImGui.Separator();
                    }
                }

                // ── Turn timer bar ───────────────────────────────────────────────────────
                if (inHand && table.PokerCurrentSeat >= 0 && poker.Seats[table.PokerCurrentSeat].IsActive)
                {
                    int    limit   = table.TurnTimeLimit;
                    double elapsed = (DateTime.Now - table.PokerTurnStart).TotalSeconds;
                    float  frac    = (float)Math.Clamp(1.0 - elapsed / limit, 0.0, 1.0);
                    string curName = poker.Seats[table.PokerCurrentSeat].PlayerName;

                    Vector4 timerCol = frac > 0.5f ? new Vector4(0.2f, 0.8f, 0.2f, 1f)
                                     : frac > 0.25f ? new Vector4(0.9f, 0.7f, 0.0f, 1f)
                                     :                new Vector4(1f,   0.2f, 0.2f, 1f);
                    ImGui.PushStyleColor(ImGuiCol.PlotHistogram, timerCol);
                    ImGui.ProgressBar(frac, new Vector2(-1, 18), $"{curName}'s turn — {Math.Max(0, (int)(limit - elapsed))}s");
                    ImGui.PopStyleColor();
                }

                ImGui.Separator();

                // ── Admin proxy action buttons ────────────────────────────────────────────
                ImGui.TextColored(new Vector4(1f, 1f, 0f, 1f), "ADMIN ACTIONS");

                if (!inHand)
                {
                    if (ImGui.Button("♠ DEAL HAND", new Vector2(120, 28)))
                        poker.DealHand(out _);
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "SB:");
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(120);
                    int pokerSb = table.PokerSmallBlind;
                    if (ImGui.InputInt("##pokerSB", ref pokerSb, 5, 100))
                        table.PokerSmallBlind = Math.Max(1, pokerSb);
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"/ BB: {table.PokerSmallBlind * 2}\uE049");
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), " Ante:");
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(120);
                    int pokerAnte = table.PokerAnte;
                    if (ImGui.InputInt("##pokerAnte", ref pokerAnte, 5, 100))
                        table.PokerAnte = Math.Max(0, pokerAnte);
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"|  Players: {engine.CurrentTable.Players.Count}");
                }
                else
                {
                    int curSeat = table.PokerCurrentSeat;
                    string curName = curSeat >= 0 && poker.Seats[curSeat].IsOccupied
                        ? poker.Seats[curSeat].PlayerName : "—";
                    ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"Acting: {curName}");

                    ImGui.BeginDisabled(curSeat < 0 || !poker.Seats[curSeat].IsActive);

                    if (ImGui.Button("FOLD",  new Vector2(60, 24))) poker.PlayerFold(curName, out _);
                    ImGui.SameLine();
                    if (ImGui.Button("CHECK", new Vector2(60, 24))) poker.PlayerCheck(curName, out _);
                    ImGui.SameLine();
                    if (ImGui.Button("CALL",  new Vector2(60, 24))) poker.PlayerCall(curName, out _);
                    ImGui.SameLine();

                    ImGui.SetNextItemWidth(80);
                    ImGui.DragInt("##proxyRaise", ref pokerProxyRaiseAmt, 5, table.PokerSmallBlind * 2, table.MaxBet);
                    ImGui.SameLine();
                    if (ImGui.Button($"RAISE +{pokerProxyRaiseAmt}", new Vector2(100, 24)))
                        poker.PlayerRaise(curName, pokerProxyRaiseAmt, out _);
                    ImGui.SameLine();
                    if (ImGui.Button("ALL IN", new Vector2(65, 24))) poker.PlayerAllIn(curName, out _);

                    ImGui.EndDisabled();

                    // AFK toggle — available regardless of whose turn it is
                    var curActingPlayer = engine.GetPlayer(curName);
                    if (curActingPlayer != null)
                    {
                        ImGui.SameLine();
                        bool actingIsAfk = curActingPlayer.IsAfk;
                        if (ImGui.Button(actingIsAfk ? "[AFK]##pokerafk" : "AFK##pokerafk", new Vector2(45, 24)))
                            engine.ToggleAFK(curName);
                    }
                }

                ImGui.Separator();
                DrawPlayersManagementTab();
            }

            private void DrawAdminTab()
        {
            ImGui.TextColored(new Vector4(1, 0.84f, 0, 1), "ADMIN CONTROLS");

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

            ImGui.Separator();

            // Poker settings
            ImGui.TextColored(new Vector4(0.5f, 1f, 1f, 1f), "POKER SETTINGS");
            ImGui.Checkbox("Show player hole cards in player table##showPokerCards", ref _showPokerHoleCards);

            bool useFirst = engine.CurrentTable.UseFirstNameOnly;
            if (ImGui.Checkbox("Use first name only when addressing players##firstname", ref useFirst))
                engine.CurrentTable.UseFirstNameOnly = useFirst;

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
                        ImGui.Text($"Bank: {player.Bank} | Winnings: {player.TotalWinnings}");
                        ImGui.Text($"Games: {player.GamesPlayed} | Won: {player.GamesWon} | Win Rate: {player.GetWinPercentage():F1}%");

                        // Roulette net gains
                        int rouNet = player.RouletteNetGains;
                        Vector4 rouColor = rouNet > 0 ? new Vector4(0, 1, 0, 1f) :
                                           rouNet < 0 ? new Vector4(1, 0.4f, 0.4f, 1f) :
                                                        new Vector4(0.6f, 0.6f, 0.6f, 1f);
                        ImGui.TextColored(rouColor, $"Roulette Net: {(rouNet >= 0 ? "+" : "")}{rouNet}\uE049");

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
            var status = "BLACKJACK TABLE STATUS:\n";
            foreach (var player in engine.CurrentTable.Players.Values)
            {
                status += $"  {player.Name} ({player.Server}) - Bank: {player.Bank}, Bet: {player.PersistentBet}, AFK: {player.IsAfk}\n";
            }
            plugin.SendGameMessage(status);
        }
    }
}

