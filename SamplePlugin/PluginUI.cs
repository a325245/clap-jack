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
        private string selectedPlayerName = string.Empty;

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

                    ImGui.EndTabBar();
                }

                ImGui.End();
            }
        }

        private void DrawTableTab()
        {
            ImGui.TextColored(new Vector4(1, 0.84f, 0, 1), "🎮 BLACKJACK TABLE");

            // Mode selection
            ImGui.Separator();
            ImGui.Text("Interface Mode:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            int uiMode = (int)engine.UIMode;
            string[] uiModes = { "Dealer", "Player" };
            if (ImGui.Combo("##uimode", ref uiMode, uiModes, uiModes.Length))
            {
                engine.UIMode = (Models.UIMode)uiMode;
            }

            // Chat Mode selection
            ImGui.SameLine();
            ImGui.Text("Chat Mode:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            int chatMode = (int)engine.ChatMode;
            string[] chatModes = { "Say", "Party" };
            if (ImGui.Combo("##chatmode", ref chatMode, chatModes, chatModes.Length))
            {
                engine.ChatMode = (Models.ChatMode)chatMode;
            }

            ImGui.Separator();

            if (engine.UIMode == Models.UIMode.Dealer)
            {
                DrawDealerInterface();
            }
            else
            {
                DrawPlayerInterface();
            }
        }

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

        private void DrawPlayerInterface()
        {
            ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), "PLAYER INTERFACE");

            // Player selection
            ImGui.SetNextItemWidth(200);
            ImGui.InputTextWithHint("##playerSelect", "Enter your player name", ref selectedPlayerName, 100);

            var selectedPlayer = engine.CurrentTable.Players.Values.FirstOrDefault(p => 
                p.Name.Equals(selectedPlayerName, StringComparison.OrdinalIgnoreCase));

            if (selectedPlayer != null)
            {
                ImGui.Separator();

                // Player info display
                ImGui.Text($"Player: {selectedPlayer.Name} ({selectedPlayer.Server})");
                ImGui.Text($"Bank: {selectedPlayer.Bank}");
                ImGui.Text($"Current Bet: {selectedPlayer.PersistentBet}");

                // Bet adjustment
                ImGui.Separator();
                ImGui.Text("Adjust Bet:");
                ImGui.SetNextItemWidth(100);
                int newBet = selectedPlayer.PersistentBet;
                if (ImGui.InputInt("##newbet", ref newBet))
                {
                    if (newBet >= engine.CurrentTable.MinBet && newBet <= engine.CurrentTable.MaxBet && newBet <= selectedPlayer.Bank)
                    {
                        selectedPlayer.PersistentBet = newBet;
                    }
                }
                ImGui.SameLine();
                if (ImGui.Button("Set Bet"))
                {
                    engine.SetPlayerBet(selectedPlayer.Name, newBet);
                }

                // Player cards display
                if (selectedPlayer.Hands.Count > 0)
                {
                    ImGui.Separator();
                    ImGui.Text("Your Cards:");

                    for (int handIndex = 0; handIndex < selectedPlayer.Hands.Count; handIndex++)
                    {
                        var handInfo = selectedPlayer.GetHandInfo(handIndex);
                        if (handInfo.Cards.Count > 0)
                        {
                            string handLabel = selectedPlayer.Hands.Count > 1 ? $"Hand {handIndex + 1}: " : "Cards: ";
                            ImGui.Text(handLabel);

                            // Display cards horizontally
                            ImGui.SameLine();
                            for (int cardIndex = 0; cardIndex < handInfo.Cards.Count; cardIndex++)
                            {
                                var card = handInfo.Cards[cardIndex];
                                if (cardIndex > 0) ImGui.SameLine();

                                // Draw styled card
                                var drawList = ImGui.GetWindowDrawList();
                                var pos = ImGui.GetCursorScreenPos();
                                var cardSize = new Vector2(48, 68);

                                drawList.AddRectFilled(pos, pos + cardSize, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)));
                                Vector4 borderColor = card.IsRed ? new Vector4(1, 0.2f, 0.2f, 1f) : new Vector4(0.2f, 0.2f, 0.2f, 1f);
                                drawList.AddRect(pos, pos + cardSize, ImGui.ColorConvertFloat4ToU32(borderColor), 4.0f, ImDrawFlags.RoundCornersAll, 2.0f);

                                var textPos = pos + cardSize * 0.5f;
                                var cardText = card.GetCardDisplay();
                                var textSize = ImGui.CalcTextSize(cardText);
                                textPos -= textSize * 0.5f;

                                drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(borderColor), cardText);

                                ImGui.SetCursorScreenPos(pos);
                                ImGui.InvisibleButton($"playercard_{handIndex}_{cardIndex}", cardSize);
                                ImGui.SetCursorScreenPos(pos + new Vector2(cardSize.X + 4, 0));
                            }

                            ImGui.Text($"Value: {handInfo.GetHandDescription()}");
                            if (handIndex < selectedPlayer.CurrentBets.Count)
                            {
                                ImGui.Text($"Bet: {selectedPlayer.CurrentBets[handIndex]}");
                            }
                        }
                    }
                }

                // Action buttons
                ImGui.Separator();
                ImGui.Text("Player Actions:");

                if (ImGui.Button("HIT"))
                {
                    engine.SendChatMessage($"/say >HIT");
                }
                ImGui.SameLine();
                if (ImGui.Button("STAND"))
                {
                    engine.SendChatMessage($"/say >STAND");
                }
                ImGui.SameLine();
                if (ImGui.Button("DOUBLE"))
                {
                    engine.SendChatMessage($"/say >DOUBLE");
                }
                ImGui.SameLine();
                if (ImGui.Button("SPLIT"))
                {
                    engine.SendChatMessage($"/say >SPLIT");
                }

                // Other commands
                if (ImGui.Button("Check Bank"))
                {
                    engine.SendChatMessage($"/say >BANK");
                }
                ImGui.SameLine();
                if (ImGui.Button("Go AFK"))
                {
                    engine.SendChatMessage($"/say >AFK");
                }
            }
            else if (!string.IsNullOrEmpty(selectedPlayerName))
            {
                ImGui.Text("Player not found in current table");
            }
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
