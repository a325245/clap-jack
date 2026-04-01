using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using SamplePlugin.Windows;
using SamplePlugin.Engine;
using SamplePlugin.Commands;
using SamplePlugin.Chat;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace SamplePlugin
{
    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "Blackjack Dealer";
        private const string CommandName = "/chatjack2";

        [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
        [PluginService] internal static IPluginLog Log { get; private set; } = null!;
        [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
        [PluginService] internal static IClientState ClientState { get; private set; } = null!;

        public IDalamudPluginInterface PluginInterface { get; init; }
        private ICommandManager CommandManager { get; init; }
        private IChatGui ChatGui { get; init; }

        public Configuration Configuration { get; init; }

        public BlackjackEngine Engine { get; init; }
        public RouletteEngine RouletteEngine { get; init; }
        public CrapsEngine CrapsEngine { get; init; }
        public BaccaratEngine BaccaratEngine { get; init; }
        public ChocoboEngine ChocoboEngine { get; init; }
        public PokerEngine    PokerEngine   { get; init; }
        public UltimaEngine   UltimaEngine  { get; init; }
        public CommandParser CommandParser { get; init; }
        public ChatHandler ChatHandler { get; init; }
        public Chat.PlayerChatParser ChatParser { get; init; }
        public PlayerViewWindow PlayerView { get; init; }
        public PluginUI UI { get; init; }

        private Models.DealerMode Mode { get; set; } = Models.DealerMode.Auto;

        // Message queue for delayed output
        private Queue<string> MessageQueue { get; } = new();
        private Stopwatch MessageTimer { get; } = new();
        private const int MessageDelayMs = 400;
        private const int TellDelayMs = 4000;
        private bool _lastMessageWasTell = false;

        public static Plugin? PluginAccessorInstance { get; private set; }

        public Plugin(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IChatGui chatGui)
        {
            PluginInterface = pluginInterface;
            CommandManager = commandManager;
            ChatGui = chatGui;

            Configuration = (Configuration?)PluginInterface.GetPluginConfig() ?? new Configuration();
            PluginAccessorInstance = this;

            Engine = new BlackjackEngine();
            RouletteEngine = new RouletteEngine(Engine.CurrentTable);
            CrapsEngine = new CrapsEngine(Engine.CurrentTable);
            BaccaratEngine = new BaccaratEngine(Engine.CurrentTable);
            ChocoboEngine = new ChocoboEngine(Engine.CurrentTable);
            PokerEngine   = new PokerEngine(Engine.CurrentTable);
            UltimaEngine  = new UltimaEngine(Engine.CurrentTable);
            CommandParser = new CommandParser(Engine, RouletteEngine, CrapsEngine, BaccaratEngine, ChocoboEngine, PokerEngine, UltimaEngine);
            ChatHandler = new ChatHandler();
            ChatParser  = new Chat.PlayerChatParser(Engine.CurrentTable);

            // Wire up roulette events (reuse same send helpers)
            RouletteEngine.OnChatMessage += SendGameMessage;
            RouletteEngine.OnPlayerTell += SendPlayerTell;
            RouletteEngine.OnUIUpdate += OnAnyEngineUpdate;

            // Wire up craps events
            CrapsEngine.OnChatMessage += SendGameMessage;
            CrapsEngine.OnPlayerTell += SendPlayerTell;
            CrapsEngine.OnUIUpdate += OnAnyEngineUpdate;

            // Wire up baccarat events
            BaccaratEngine.OnChatMessage += SendGameMessage;
            BaccaratEngine.OnPlayerTell += SendPlayerTell;
            BaccaratEngine.OnUIUpdate += OnAnyEngineUpdate;

            // Wire up chocobo events
            ChocoboEngine.OnChatMessage += SendGameMessage;
            ChocoboEngine.OnPlayerTell += SendPlayerTell;
            ChocoboEngine.OnUIUpdate += OnAnyEngineUpdate;

            // Wire up poker events
            PokerEngine.OnChatMessage += SendGameMessage;
            PokerEngine.OnPlayerTell  += SendPlayerTell;
            PokerEngine.OnUIUpdate    += OnAnyEngineUpdate;

            // Wire up ultima events
            UltimaEngine.OnChatMessage += SendGameMessage;
            UltimaEngine.OnPlayerTell  += SendPlayerTell;
            UltimaEngine.OnUIUpdate    += OnAnyEngineUpdate;

            // Wire up callbacks
            Engine.OnChatMessage += SendGameMessage;
            Engine.OnAdminEcho += SendAdminEcho;
            Engine.OnPlayerTell += SendPlayerTell;
            Engine.OnUIUpdate += OnAnyEngineUpdate;

            CommandParser.OnChatMessage += SendGameMessage;
            CommandParser.OnAdminEcho += SendAdminEcho;
            CommandParser.OnPlayerTell += SendPlayerTell;
            CommandParser.ResolveServer = ResolvePlayerServer;

            UI         = new PluginUI(this, Engine);
            PlayerView = new PlayerViewWindow(this, ChatParser);

            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Opens the blackjack dealer interface."
            });

            ChatGui.ChatMessage += ChatGui_ChatMessage;
            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUI;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;

            MessageTimer.Start();
        }

        private void ChatGui_ChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
        {
            var text = message.TextValue.Trim();
            var rawSender = sender.TextValue;

            // Strip any leading special/icon characters from the sender name
            // Party chat prefixes names with job icons (e.g. "🎴Jess Dee" → "Jess Dee")
            var senderName = StripSenderPrefix(rawSender);

            // Log EVERY chat message to identify party chat type
            Log.Information($"[CHAT] type={type} ({(int)type}), rawSender='{rawSender}', cleanSender='{senderName}', msg='{text}'");

            // Also store recent chat in UI for debugging
            UI.AddDebugChat($"[{(int)type}:{type}] {rawSender} ({senderName}): {text}");

            if (string.IsNullOrEmpty(senderName)) return;

            // Detect what channel this came from
            ChatChannel sourceChannel = ChatHandler.DetectChatChannel(type);

            Log.Information($"[CHAT] Command detected! sourceChannel={sourceChannel}, cleanSender='{senderName}'");

            // Process command using the clean sender name and correct source channel
            CommandParser.Parse(senderName, text, UI.AdminName, Engine.Mode, sourceChannel);

            // After any command is processed, mirror the local player's entire Ultima
            // view state from the engine. This covers hand, top card, current player, card
            // counts, and winner — all of which would otherwise rely on a tell-to-self that
            // never arrives as TellIncoming.
            if (Engine.CurrentTable.GameType == Models.GameType.Ultima ||
                Engine.CurrentTable.UltimaPhase == Models.UltimaPhase.Complete)
            {
                var t = Engine.CurrentTable;
                var s = ChatParser.State;
                string myName = ClientState?.LocalPlayer?.Name.TextValue ?? string.Empty;
                if (!string.IsNullOrEmpty(myName) && t.UltimaHands.TryGetValue(myName, out var myHand))
                    s.UltimaHand = new List<Models.UltimaCard>(myHand);
                s.UltimaTopCard     = t.UltimaTopCard;
                s.UltimaActiveColor = t.UltimaActiveColor;
                s.UltimaClockwise   = t.UltimaClockwise;
                s.UltimaWinner      = t.UltimaWinner;
                if (t.UltimaPhase == Models.UltimaPhase.Playing &&
                    t.UltimaCurrentIndex < t.UltimaPlayerOrder.Count)
                    s.UltimaCurrentPlayer = t.GetDisplayName(t.UltimaPlayerOrder[t.UltimaCurrentIndex]);
                // Atomic replacement avoids race with the render thread seeing an empty list
                s.UltimaPlayerOrder = t.UltimaPlayerOrder.Select(n => t.GetDisplayName(n)).ToList();
                var newCounts = new Dictionary<string, int>();
                foreach (var kvp in t.UltimaHands)
                    newCounts[t.GetDisplayName(kvp.Key)] = kvp.Value.Count;
                s.UltimaCardCounts = newCounts;
            }

            // Feed to player-view parser. ONLY TellIncoming contains our own hand/cards.
            // TellOutgoing appears when the dealer sends other players their hands and must NOT
            // be parsed as our own hand data.
            ChatParser.ParseMessage(senderName, text, type == XivChatType.TellIncoming);
        }

        // Strips leading non-letter characters (job icons, party markers, etc.) from FFXIV sender names
        private string StripSenderPrefix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            int start = 0;
            while (start < name.Length && !char.IsLetter(name[start]))
                start++;

            return name.Substring(start).Trim();
        }

        public void Dispose()
        {
            UI.Dispose();

            ChatGui.ChatMessage -= ChatGui_ChatMessage;
            CommandManager.RemoveHandler(CommandName);
            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi -= DrawConfigUI;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUI;
        }

        /// <summary>
        /// Fired after every dealer action on every engine.
        /// Auto-saves the session snapshot so the state can be restored at any time.
        /// </summary>
        private void OnAnyEngineUpdate()
        {
            UI?.AutoSaveSnapshot();
        }

        private void OnCommand(string command, string args)
        {
            // Debug command to show all chat types
            if (args == "debug")
            {
                Log.Information("Debug mode activated - will log all chat messages for analysis");
                // You can use this to see what XivChatType values are being used
            }

            UI.IsVisible = !UI.IsVisible;
        }

        private void ToggleMainUI()
        {
            UI.IsVisible = !UI.IsVisible;
        }

        public void AddPartyToTable()
        {
            // Suppress chat announcements during bulk add
            bool prevAnnounce = Engine.CurrentTable.AnnounceNewPlayers;
            Engine.CurrentTable.AnnounceNewPlayers = false;

            // Add the local player first
            string? localName = ClientState?.LocalPlayer?.Name.TextValue;
            if (!string.IsNullOrEmpty(localName))
            {
                string? localWorld = ClientState?.LocalPlayer?.HomeWorld.Value.Name.ExtractText();
                Engine.AddPlayer(string.IsNullOrEmpty(localWorld) ? localName : $"{localName}@{localWorld}");
            }

            // Add party members with their home world for cross-world tell support
            foreach (var member in PartyList)
            {
                string name = member.Name.TextValue;
                if (string.IsNullOrWhiteSpace(name)) continue;
                string? world = member.World.Value.Name.ExtractText();
                Engine.AddPlayer(string.IsNullOrEmpty(world) ? name : $"{name}@{world}");
            }

            Engine.CurrentTable.AnnounceNewPlayers = prevAnnounce;
        }

        public void SendGameMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            lock (MessageQueue) { MessageQueue.Enqueue(message); }
        }

        private void SendAdminEcho(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            lock (MessageQueue) { MessageQueue.Enqueue($"/echo {message}"); }
        }

        private void SendPlayerTell(string nameAtServer, string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            lock (MessageQueue) { MessageQueue.Enqueue($"/tell {nameAtServer} {message}"); }
        }

        /// <summary>Resolve a player name to their home world by checking the party list.</summary>
        private string? ResolvePlayerServer(string playerName)
        {
            foreach (var member in PartyList)
            {
                if (member.Name.TextValue.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                    return member.World.Value.Name.ExtractText();
            }
            return null;
        }

        private void ProcessMessageQueue()
        {
            lock (MessageQueue)
            {
                if (MessageQueue.Count == 0) return;

                // Use a longer delay after tells to avoid FFXIV's anti-spam filter
                var nextMsg = MessageQueue.Peek();
                bool nextIsTell = nextMsg.StartsWith("/tell ", StringComparison.OrdinalIgnoreCase);
                int requiredDelay = (_lastMessageWasTell || nextIsTell) ? TellDelayMs : MessageDelayMs;

                if (MessageTimer.ElapsedMilliseconds >= requiredDelay)
                {
                    var message = MessageQueue.Dequeue();
                    _lastMessageWasTell = message.StartsWith("/tell ", StringComparison.OrdinalIgnoreCase);
                    SendMessageToChat(message);
                    MessageTimer.Restart();
                }
            }
        }

        private void SendMessageToChat(string message)
        {
            try
            {
                if (string.IsNullOrEmpty(message)) return;

                string fullMessage = message.StartsWith("/") ? message : $"/say {message}";
                if (fullMessage.Length > 500) return;

                unsafe
                {
                    var str = Utf8String.FromString(fullMessage);
                    str->SanitizeString(
                        AllowedEntities.Numbers |
                        AllowedEntities.UppercaseLetters |
                        AllowedEntities.LowercaseLetters |
                        AllowedEntities.OtherCharacters |
                        AllowedEntities.SpecialCharacters);

                    if (str->StringLength > 500) return;
                    UIModule.Instance()->ProcessChatBoxEntry(str);
                }
            }
            catch { }
        }

        public void SaveConfiguration()
        {
            PluginInterface.SavePluginConfig(Configuration);
        }

        /// <summary>Send a sync tell to every seated player with their game/bank info.</summary>
        public void SyncAllPlayers()
        {
            var table = Engine.CurrentTable;
            string gameLabel = table.GameType switch
            {
                Models.GameType.Blackjack     => "Blackjack",
                Models.GameType.Roulette      => "Roulette",
                Models.GameType.Craps         => "Craps",
                Models.GameType.Baccarat      => "Mini Baccarat",
                Models.GameType.ChocoboRacing => "Chocobo Racing",
                Models.GameType.TexasHoldEm   => "Texas Hold'Em",
                Models.GameType.Ultima        => "Ultima!",
                _                             => "None"
            };
            string players = string.Join(", ", table.Players.Values
                .Where(x => !x.IsKicked)
                .Select(x => $"{table.GetDisplayName(x.Name)}({x.Bank})"));

            foreach (var p in table.Players.Values.Where(x => !x.IsKicked))
            {
                string server = !string.IsNullOrEmpty(p.Server) ? p.Server
                    : ResolvePlayerServer(p.Name) ?? "Ultros";
                string tellTarget = $"{p.Name}@{server}";
                string msg = $"Game: {gameLabel} | Bank: {p.Bank}\uE049. | Players: {players}";
                SendPlayerTell(tellTarget, msg);
            }
        }

        /// <summary>Reset the entire plugin to factory-fresh state.</summary>
        public void FullReset()
        {
            var table = Engine.CurrentTable;

            // Stop any active games silently
            RouletteEngine.ForceStop();
            CrapsEngine.ForceStop();
            PokerEngine.ForceStop();
            UltimaEngine.ForceEnd();

            // Silence all queued messages
            RouletteEngine.ClearQueue();
            CrapsEngine.ClearQueue();
            BaccaratEngine.ClearQueue();
            ChocoboEngine.ClearQueue();
            PokerEngine.ClearQueue();
            Engine.ClearQueue();

            // Clear all players and state
            table.Players.Clear();
            table.BaccaratBets.Clear();
            table.ChocoboBets.Clear();
            table.TurnOrder.Clear();
            table.DealerHand.Clear();
            table.GameLog.Clear();

            // Reset game state
            table.GameType = Models.GameType.None;
            table.GameState = Models.GameState.Lobby;
            table.CurrentTurnIndex = 0;
            table.TurnTimeRemaining = table.TurnTimeLimit;
            table.RouletteSpinState = Models.RouletteSpinState.Idle;
            table.RouletteResult = 0;
            table.BaccaratPhase = Models.BaccaratPhase.WaitingForBets;
            table.ChocoboRacePhase = Models.ChocoboRacePhase.Idle;
            table.UltimaPhase = Models.UltimaPhase.WaitingForPlayers;
            table.PokerPhase = Models.PokerPhase.WaitingForPlayers;
            table.PokerPot = 0;
            table.PokerCommunity.Clear();

            // Reset engine state
            table.BuildDeck();

            // Single announcement
            string cmd = Engine.ChatMode == Models.ChatMode.Party ? "/party Table reset." : "/say Table reset.";
            SendGameMessage(cmd);

            Log.Information("[RESET] Plugin fully reset to factory state.");
        }

        public void ToggleConfigUi()
        {
            UI.IsVisible = !UI.IsVisible;
        }

        private void DrawUI()
        {
            // Update timer every frame
            Engine.UpdateTimer();

            // Process blackjack message queue with delays
            Engine.ProcessDealerMessageQueue();

            // Process roulette spin state machine (4-second delay then resolve)
            RouletteEngine.ProcessSpin();
            RouletteEngine.ProcessMessageQueue();

            // Process craps dice roll animation and betting timer
            CrapsEngine.ProcessRoll();
            CrapsEngine.ProcessBettingTimer();
            CrapsEngine.ProcessMessageQueue();

            // Process baccarat message queue
            BaccaratEngine.ProcessMessageQueue();

            // Process chocobo race state machine and message queue
            ChocoboEngine.ProcessRace();
            ChocoboEngine.ProcessMessageQueue();

            // Process poker turn timer and message queue
            PokerEngine.ProcessTick();

            // Process ultima turn timer
            UltimaEngine.ProcessTick();

            ProcessAfkEchoes();
            ProcessMessageQueue();
            UI.Draw();
            PlayerView.Draw();
        }

            private DateTime _lastAfkEchoCheck = DateTime.MinValue;
            private void ProcessAfkEchoes()
            {
                if ((DateTime.Now - _lastAfkEchoCheck).TotalSeconds < 15) return;
                _lastAfkEchoCheck = DateTime.Now;

                foreach (var player in Engine.CurrentTable.Players.Values)
                {
                    if (player.IsKicked) continue;
                    if (!player.IsAfk || !player.AfkSince.HasValue) continue;
                    int mins = (int)(DateTime.Now - player.AfkSince.Value).TotalMinutes;
                    if (mins > 0 && mins > player.AfkNotifiedMinutes)
                    {
                        player.AfkNotifiedMinutes = mins;
                        SendAdminEcho($"💤 {player.Name} has been AFK for {mins} minute{(mins == 1 ? "" : "s")}.");
                    }
                }
            }

        private void DrawConfigUI()
        {
            UI.IsVisible = true;
        }
    }
}
