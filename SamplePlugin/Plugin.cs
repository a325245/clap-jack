using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System.IO;
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
using Dalamud.Interface.Textures.TextureWraps;

namespace SamplePlugin
{
    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "Blackjack Dealer";
        private const string CommandName = "/chatjack2";

        [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
        [PluginService] internal static IPluginLog Log { get; private set; } = null!;

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
        public CommandParser CommandParser { get; init; }
        public ChatHandler ChatHandler { get; init; }
        public PluginUI UI { get; init; }

        private string AdminName { get; set; } = string.Empty;
        private Models.DealerMode Mode { get; set; } = Models.DealerMode.Auto;

        // Message queue for delayed output
        private Queue<string> MessageQueue { get; } = new();
        private Stopwatch MessageTimer { get; } = new();
        private const int MessageDelayMs = 400;

        // Card texture cache - disabled, using styled cards
        private Dictionary<string, nint> CardTextures { get; } = new();

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
            CommandParser = new CommandParser(Engine, RouletteEngine, CrapsEngine, BaccaratEngine, ChocoboEngine, PokerEngine);
            ChatHandler = new ChatHandler();

            // Wire up roulette events (reuse same send helpers)
            RouletteEngine.OnChatMessage += SendGameMessage;
            RouletteEngine.OnPlayerTell += SendPlayerTell;
            RouletteEngine.OnUIUpdate += () => { };

            // Wire up craps events
            CrapsEngine.OnChatMessage += SendGameMessage;
            CrapsEngine.OnPlayerTell += SendPlayerTell;
            CrapsEngine.OnUIUpdate += () => { };

            // Wire up baccarat events
            BaccaratEngine.OnChatMessage += SendGameMessage;
            BaccaratEngine.OnPlayerTell += SendPlayerTell;
            BaccaratEngine.OnUIUpdate += () => { };

            // Wire up chocobo events
            ChocoboEngine.OnChatMessage += SendGameMessage;
            ChocoboEngine.OnPlayerTell += SendPlayerTell;
            ChocoboEngine.OnUIUpdate += () => { };

            // Wire up poker events
            PokerEngine.OnChatMessage += SendGameMessage;
            PokerEngine.OnPlayerTell  += SendPlayerTell;
            PokerEngine.OnUIUpdate    += () => { };

            // Wire up callbacks
            Engine.OnChatMessage += SendGameMessage;
            Engine.OnAdminEcho += SendAdminEcho;
            Engine.OnPlayerTell += SendPlayerTell;
            Engine.OnUIUpdate += () => { };

            CommandParser.OnChatMessage += SendGameMessage;
            CommandParser.OnAdminEcho += SendAdminEcho;
            CommandParser.OnPlayerTell += SendPlayerTell;

            UI = new PluginUI(this, Engine);

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

            if (string.IsNullOrEmpty(senderName) || !text.StartsWith(">")) return;

            // Detect what channel this came from
            ChatChannel sourceChannel = ChatHandler.DetectChatChannel(type);

            Log.Information($"[CHAT] Command detected! sourceChannel={sourceChannel}, cleanSender='{senderName}'");

            // Process command using the clean sender name and correct source channel
            CommandParser.Parse(senderName, text, UI.AdminName, Engine.Mode, sourceChannel);
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

            // Card textures disabled
            CardTextures.Clear();

            ChatGui.ChatMessage -= ChatGui_ChatMessage;
            CommandManager.RemoveHandler(CommandName);
            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi -= DrawConfigUI;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUI;
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

        public nint GetCardTexture(string cardFileName)
        {
            // Always return zero to use styled fallback cards
            return nint.Zero;
        }

        private nint LoadTextureFromBytes(byte[] imageBytes)
        {
            // This method is no longer needed with TextureProvider
            return nint.Zero;
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

        private void ProcessMessageQueue()
        {
            lock (MessageQueue)
            {
                if (MessageQueue.Count == 0) return;

                if (MessageTimer.ElapsedMilliseconds >= MessageDelayMs)
                {
                    var message = MessageQueue.Dequeue();
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

            ProcessMessageQueue();
            UI.Draw();
        }

        private void DrawConfigUI()
        {
            UI.IsVisible = true;
        }
    }
}
