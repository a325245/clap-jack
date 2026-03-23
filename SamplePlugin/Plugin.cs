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
        public CommandParser CommandParser { get; init; }
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
            CommandParser = new CommandParser(Engine);

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
            var senderName = sender.TextValue;

            if (string.IsNullOrEmpty(senderName) || !text.StartsWith(">")) return;

            // Use the engine's mode and admin name from UI
            CommandParser.Parse(senderName, text, UI.AdminName, Engine.Mode);
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

            // Process dealer message queue with delays
            Engine.ProcessDealerMessageQueue();

            ProcessMessageQueue();
            UI.Draw();
        }

        private void DrawConfigUI()
        {
            UI.IsVisible = true;
        }
    }
}
