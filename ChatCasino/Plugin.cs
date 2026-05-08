using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Chat;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ChatCasino.Engine;
using ChatCasino.Models;
using ChatCasino.Services;
using ChatCasino.UI;
using ChatCasino.Windows;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ChatCasino;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/casino";
    private const string SnapshotFileName = "session.snapshot.json";

    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;

    public string Name => "Chat Casino";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;

    private readonly WindowSystem windowSystem = new("ChatCasino");
    private readonly MainWindow mainWindow;
    private readonly PlayerViewWindow playerViewWindow;

    private readonly MessagingService messageService;
    private readonly ITimerService timerService;
    private readonly IDeckService deckService;
    private readonly IBankService bankService;
    private readonly IPlayerService playerService;
    private readonly ITableService tableService;
    private readonly GameManager gameManager;
    private readonly DynamicWindowRenderer renderer;
    private readonly ViewSyncService viewSync;
    private readonly TestBotService testBotService;
    private readonly FlavorTextService flavorTextService;

    private bool isDealerView = true;
    private bool dealerConfirmed;
    private string? lastOutboundChatMessage;
    private string? pendingRetryMessage;
    private bool testModeEnabled;
    private int testModeBotCount = 3;

    private readonly Dictionary<string, int> afkReminderMinutesByPlayer = new(StringComparer.OrdinalIgnoreCase);

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IChatGui chatGui)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.chatGui = chatGui;

        flavorTextService = new FlavorTextService(pluginInterface.ConfigDirectory.FullName);
        messageService = new MessagingService();
        timerService = new TimerService();
        deckService = new DeckService();
        bankService = new BankService();
        playerService = new PlayerService();
        tableService = new TableService(messageService, timerService, playerService);
        gameManager = new GameManager(tableService, playerService);
        renderer = new DynamicWindowRenderer(gameManager, tableService, flavorTextService);
        viewSync = new ViewSyncService(playerService);
        testBotService = new TestBotService(gameManager, timerService);

        mainWindow = new MainWindow(renderer);
        playerViewWindow = new PlayerViewWindow(gameManager, tableService);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(playerViewWindow);

        renderer.AddPartyRequested = AddPartyToTable;
        renderer.TestModeRequested = OnTestModeRequested;
        renderer.DealerBroadcastRequested = msg =>
        {
            if (!string.IsNullOrWhiteSpace(msg))
                SendMessageToChat($"/party {msg}");
        };
        renderer.FactoryResetRequested = FactoryResetState;
        renderer.SetTestModeState(testModeEnabled, testModeBotCount);
        playerViewWindow.SendChatCommand = SendPlayerChatCommand;
        playerViewWindow.OpenDealerView = () =>
        {
            isDealerView = true;
            mainWindow.IsOpen = true;
        };

        RegisterModules();

        gameManager.OnCommandFeedback = msg =>
        {
            if (!string.IsNullOrWhiteSpace(msg))
                chatGui.Print($"[Casino] {msg}");
        };
        //set to echo for testing
        messageService.OnPartyMessage = m => SendMessageToChat($"/party {m}");
        //messageService.OnPartyMessage = m => SendMessageToChat($"/echo {m}");
        messageService.OnTellMessage = (p, s, m) =>
        {
            Log.Information($"[CasinoTell] -> {p}@{s}: {m}");

            if (p.StartsWith("TestBot", StringComparison.OrdinalIgnoreCase))
            {
                SendMessageToChat($"/echo [BOT-TELL:{p}] {m}");
                return;
            }

            var local = ObjectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(local) && p.Equals(local, StringComparison.OrdinalIgnoreCase))
            {
                SendMessageToChat($"/echo [SELF-TELL:{p}] {m}");
                viewSync.MirrorTellTarget(p);
                return;
            }

            var world = string.IsNullOrWhiteSpace(s) ? "Unknown" : s.Trim();
            if (world.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                SendMessageToChat($"/tell {p} {m}");
            else
                SendMessageToChat($"/tell {p}@{world} {m}");

            viewSync.MirrorTellTarget(p);
        };
        messageService.OnAdminEcho = m => SendMessageToChat($"/echo {m}");

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open UI: /casino to play and /casino dealer to deal."
        });

        chatGui.ChatMessage += OnChatMessage;
        pluginInterface.UiBuilder.Draw += DrawUI;
        pluginInterface.UiBuilder.OpenMainUi += OpenPlayerView;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
    }

    private void RegisterModules()
    {
        gameManager.RegisterEngine(GameType.Blackjack,
            new BlackjackModule(messageService, deckService, playerService, bankService, new HitsSoft17Strategy()));
        gameManager.RegisterEngine(GameType.Roulette,
            new RouletteModule(messageService, deckService, playerService, bankService, timerService));
        gameManager.RegisterEngine(GameType.Craps,
            new CrapsModule(messageService, deckService, playerService, bankService));
        gameManager.RegisterEngine(GameType.Baccarat,
            new BaccaratModule(messageService, deckService, playerService, bankService));
        gameManager.RegisterEngine(GameType.ChocoboRacing,
            new ChocoboRacingModule(messageService, deckService, playerService, bankService, timerService));
        gameManager.RegisterEngine(GameType.TexasHoldEmPvP,
            new TexasHoldEmModule(messageService, deckService, playerService, bankService, timerService, new PokerEvaluator(), new PotManager()));
        gameManager.RegisterEngine(GameType.TexasHoldEmPvD,
            new TexasHoldEmPvDModule(messageService, deckService, playerService, bankService, new PokerEvaluator()));
        gameManager.RegisterEngine(GameType.Ultima,
            new UltimaModule(messageService, deckService, playerService, timerService));
        gameManager.RegisterEngine(GameType.Bingo,
            new BingoModule(messageService, deckService, playerService, bankService));
    }

    private void OnCommand(string command, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            playerViewWindow.IsOpen = true;
            chatGui.Print("[Casino] Player view opened.");
            return;
        }

        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1 && parts[0].Equals("dealer", StringComparison.OrdinalIgnoreCase))
        {
            isDealerView = true;
            mainWindow.IsOpen = true;
            playerViewWindow.IsOpen = true;
            chatGui.Print("[Casino] Dealer view opened.");
            return;
        }

        if (parts.Length >= 2 && parts[0].Equals("game", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseGame(parts[1], out var game))
            {
                gameManager.Activate(game);
                chatGui.Print($"[Casino] Active game set to {game}.");
            }
            return;
        }

        if (parts.Length >= 2 && parts[0].Equals("view", StringComparison.OrdinalIgnoreCase))
        {
            if (parts[1].Equals("dealer", StringComparison.OrdinalIgnoreCase))
            {
                isDealerView = true;
                mainWindow.IsOpen = true;
                playerViewWindow.IsOpen = false;
            }
            else if (parts[1].Equals("player", StringComparison.OrdinalIgnoreCase))
            {
                isDealerView = false;
                mainWindow.IsOpen = true;
                playerViewWindow.IsOpen = true;
            }

            chatGui.Print($"[Casino] View mode: {(isDealerView ? "Dealer" : "Player")}");
            return;
        }

        if (parts[0].Equals("save", StringComparison.OrdinalIgnoreCase))
        {
            SaveSnapshot();
            return;
        }

        if (parts[0].Equals("load", StringComparison.OrdinalIgnoreCase))
        {
            LoadSnapshot();
            return;
        }

        if (parts[0].Equals("addparty", StringComparison.OrdinalIgnoreCase))
        {
            AddPartyToTable();
            return;
        }

        if (parts.Length >= 2 && parts[0].Equals("bank", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[1], out var bankAmount) && bankAmount >= 0)
        {
            var local = ObjectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
            var p = playerService.GetPlayer(local);
            if (p != null)
            {
                p.CurrentBank = bankAmount;
                chatGui.Print($"[Casino] Bank set to {bankAmount}\uE049");
            }
            return;
        }

        if (parts.Length >= 1 && parts[0].Equals("join", StringComparison.OrdinalIgnoreCase))
        {
            // join is dealer-only via the UI; ignore self-join attempts from text parser
            return;
        }
    }

    private void ToggleMainUi()
    {
        mainWindow.IsOpen = !mainWindow.IsOpen;
    }

    private void OpenPlayerView()
    {
        playerViewWindow.IsOpen = true;
    }

    private void SaveSnapshot()
    {
        try
        {
            var snapshot = tableService.SaveSnapshot();
            var path = Path.Combine(pluginInterface.ConfigDirectory.FullName, SnapshotFileName);
            File.WriteAllText(path, snapshot);
            chatGui.Print($"[Casino] Snapshot saved: {path}");
        }
        catch (Exception ex)
        {
            chatGui.Print($"[Casino] Snapshot save failed: {ex.Message}");
        }
    }

    private void LoadSnapshot()
    {
        try
        {
            var path = Path.Combine(pluginInterface.ConfigDirectory.FullName, SnapshotFileName);
            if (!File.Exists(path))
            {
                chatGui.Print("[Casino] No snapshot file found.");
                return;
            }

            var snapshot = File.ReadAllText(path);
            if (!tableService.LoadSnapshot(snapshot))
            {
                chatGui.Print("[Casino] Snapshot load failed.");
                return;
            }

            if (tableService.ActiveGameType != GameType.None)
                gameManager.Activate(tableService.ActiveGameType);

            chatGui.Print("[Casino] Snapshot loaded.");
        }
        catch (Exception ex)
        {
            chatGui.Print($"[Casino] Snapshot load failed: {ex.Message}");
        }
    }

    private void OnChatMessage(IHandleableChatMessage chatMessage)
    {
        var text = chatMessage.Message.TextValue.Trim();
        var type = chatMessage.LogKind;

        if (text.Contains("Your message was not heard. You must wait before", StringComparison.OrdinalIgnoreCase))
        {
            var retry = lastOutboundChatMessage;
            if (!string.IsNullOrWhiteSpace(retry) && !string.Equals(pendingRetryMessage, retry, StringComparison.Ordinal))
            {
                pendingRetryMessage = retry;
                _ = timerService.Schedule(TimeSpan.FromSeconds(2.5), () =>
                {
                    var msg = pendingRetryMessage;
                    pendingRetryMessage = null;
                    if (!string.IsNullOrWhiteSpace(msg))
                        SendMessageToChat(msg);
                });
            }
        }

        var senderName = StripSenderPrefix(chatMessage.Sender.TextValue);
        var local = ObjectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
        if (string.IsNullOrWhiteSpace(senderName) || senderName.Equals("You", StringComparison.OrdinalIgnoreCase))
            senderName = local;

        // Resolve cross-world concat names like "Mike KaneyoLeviathan" → "Mike Kaneyo"
        senderName = NormalizeSenderAgainstPlayers(senderName);

        var isFeedChannel = type is XivChatType.Echo or XivChatType.Say or XivChatType.Party;
        var isTellChannel = type == XivChatType.TellIncoming;

        if (isFeedChannel)
            testBotService.OnChat(text);

        if (isFeedChannel && !string.IsNullOrWhiteSpace(senderName))
            renderer.RecordDealerChat(senderName, text, type == XivChatType.Party);

        // Always feed chat to player view so mirrored state stays accurate (e.g., kicked players)
        if ((isFeedChannel || isTellChannel) && !string.IsNullOrWhiteSpace(senderName))
            playerViewWindow.RecordChat(senderName, text);

        if (!isFeedChannel)
            return;

        if (!isDealerView)
            return;

        if (CasinoUI.DealerManualMode)
            return;

        // Table-level commands (join/leave/afk/help) work regardless of active game.
        // Game-specific commands require an active game module.
        var routed = TryRouteCommandFromChat(senderName, text, requirePrefix: true)
            || TryRouteCommandFromChat(senderName, text, requirePrefix: false);

        if (!routed)
            return;
    }

    private bool TryRouteCommandFromChat(string senderName, string text, bool requirePrefix)
    {
        if (string.IsNullOrWhiteSpace(senderName) || string.IsNullOrWhiteSpace(text))
            return false;

        var payload = text;
        if (requirePrefix)
        {
            if (!payload.StartsWith('>'))
                return false;
            payload = payload[1..].TrimStart();
        }
        else
        {
            if (payload.StartsWith('>'))
                return false;

            if (payload.StartsWith("[", StringComparison.OrdinalIgnoreCase)
                || payload.Contains(':')
                || payload.StartsWith("/", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var parts = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        var cmd = parts[0].ToUpperInvariant();

        // Bingo shorthand: >3 or >3 cards → BUY 3
        if (tableService.ActiveGameType == GameType.Bingo
            && int.TryParse(cmd, out var buyNum))
        {
            cmd = "BUY";
            parts = ["BUY", buyNum.ToString()];
        }

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BET","DEAL","HIT","STAND","DOUBLE","SPLIT","INSURANCE","SPIN","ROLL",
            "CHECK","CALL","RAISE","FOLD","ALL","HAND","PLAY","DRAW","AFK","JOIN","LEAVE","REMOVE","HELP","RULES",
            "1","2","3","ONE","TWO","THREE",
            "BUY","CATCHUP","BINGO"
        };

        if (!known.Contains(cmd))
            return false;

        // Table-level commands always route; game commands require an active game.
        var isTableCmd = cmd is "JOIN" or "LEAVE" or "REMOVE" or "AFK";
        if (!isTableCmd && tableService.ActiveGameType == GameType.None)
            return false;

        // AFK command should only be processed by the dealer, not other players
        if (cmd.Equals("AFK", StringComparison.OrdinalIgnoreCase) && !isDealerView)
            return false;

        if (cmd.Equals("RULES", StringComparison.OrdinalIgnoreCase))
        {
            var game = tableService.ActiveGameType;
            if (game == GameType.None)
                return false;

            var rules = GameRulebook.GetRules(game);
            messageService.QueuePartyMessage($"[CASINO] Rules for {game}:");
            foreach (var rule in rules)
                messageService.QueuePartyMessage($"[CASINO] - {rule}");
            return true;
        }

        var cmdArgs = parts.Skip(1).ToArray();
        var result = gameManager.RouteCommand(senderName, cmd, cmdArgs);

        if (cmd.Equals("HELP", StringComparison.OrdinalIgnoreCase) && result.Success && !string.IsNullOrWhiteSpace(result.Message))
        {
            messageService.QueuePartyMessage($"[CASINO] Commands: {result.Message}");
            return true;
        }

        if (cmd.Equals("AFK", StringComparison.OrdinalIgnoreCase) && result.Success && isDealerView)
        {
            var afkNow = gameManager.IsPlayerAfk(senderName);
            messageService.QueuePartyMessage($"[CASINO] {senderName} is now {(afkNow ? "AFK" : "back")}. ");
        }

        if (!result.Success && !string.IsNullOrWhiteSpace(result.Message))
        {
            messageService.QueueAdminEcho($"[CASINO] {result.Message}");
        }

        Log.Information($"[CasinoCmd] {senderName}: {cmd} {string.Join(' ', cmdArgs)} => {(result.Success ? "OK" : "FAIL")}: {result.Message}");
        return true;
    }

    private void SendMessageToChat(string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (message.Length > 500) return;

            lastOutboundChatMessage = message;

            unsafe
            {
                var str = Utf8String.FromString(message);
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
        catch (Exception ex)
        {
            Log.Error(ex, "SendMessageToChat failed");
        }
    }

    private void DrawUI()
    {
        try
        {
            var local = ObjectTable.LocalPlayer?.Name.TextValue;
            if (!string.IsNullOrWhiteSpace(local))
            {
                gameManager.SetDealerIdentity(local);
                viewSync.MirrorLocalPlayer(local);
            }

            mainWindow.DealerView = isDealerView;
            mainWindow.LocalPlayerName = local;
            playerViewWindow.LocalPlayerName = local;

            tableService.ProcessTick();
            EchoAfkRemindersToDealer();
            windowSystem.Draw();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DrawUI failed");
            mainWindow.IsOpen = false;
        }
    }

    private void EchoAfkRemindersToDealer()
    {
        if (!isDealerView)
            return;

        foreach (var p in playerService.GetAllPlayers())
        {
            if (!p.IsAfk || p.IsKicked)
            {
                afkReminderMinutesByPlayer.Remove(p.Name);
                continue;
            }

            if (!p.Metadata.TryGetValue("AFK.SinceUtcTicks", out var ticksObj) || ticksObj is not long sinceTicks)
                continue;

            var elapsed = DateTime.UtcNow - new DateTime(sinceTicks, DateTimeKind.Utc);
            var minutes = (int)Math.Floor(elapsed.TotalMinutes);
            if (minutes < 1) continue;

            if (afkReminderMinutesByPlayer.TryGetValue(p.Name, out var last) && last == minutes)
                continue;

            afkReminderMinutesByPlayer[p.Name] = minutes;
            SendMessageToChat($"/echo [Casino] {p.Name} has been AFK for {minutes} minute{(minutes == 1 ? string.Empty : "s")}. ");
        }
    }

    private static bool TryParseGame(string token, out GameType game)
    {
        token = token.ToLowerInvariant();
        game = token switch
        {
            "blackjack" => GameType.Blackjack,
            "roulette" => GameType.Roulette,
            "craps" => GameType.Craps,
            "baccarat" => GameType.Baccarat,
            "chocobo" or "chocoboracing" => GameType.ChocoboRacing,
            "poker" or "texasholdem" or "pvp" or "texasholdem pvp" => GameType.TexasHoldEmPvP,
            "pvd" or "texasholdem pvd" or "ultimaholdem" => GameType.TexasHoldEmPvD,
            "ultima" => GameType.Ultima,
            "bingo" => GameType.Bingo,
            _ => GameType.None
        };

        return game != GameType.None;
    }

    private static string StripSenderPrefix(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var start = 0;
        while (start < name.Length && !char.IsLetter(name[start]))
            start++;
        var trimmed = name[start..].Trim();

        // Strip FFXIV job abbreviation prefixes (e.g., "BRD Irie Rusme" → "Irie Rusme")
        string[] jobPrefixes = ["GLA","PGL","MRD","LNC","ARC","CNJ","THM","CRP","BSM","ARM","GSM","LTW","WVR","ALC","CUL","MIN","BTN","FSH","PLD","MNK","WAR","DRG","BRD","WHM","BLM","ACN","SMN","SCH","ROG","NIN","MCH","DRK","AST","SAM","RDM","BLU","GNB","DNC","RPR","SGE","VPR","PCT"];
        foreach (var prefix in jobPrefixes)
        {
            if (trimmed.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase) && trimmed.Length > prefix.Length + 1)
            {
                trimmed = trimmed[(prefix.Length + 1)..].TrimStart();
                break;
            }
        }

        // Strip cross-world server suffix: "Firstname Lastname@World" → "Firstname Lastname"
        var atIdx = trimmed.IndexOf('@');
        if (atIdx > 0)
            trimmed = trimmed[..atIdx].TrimEnd();

        return trimmed;
    }

    /// <summary>
    /// Cross-world party chat delivers sender names like "Mike KaneyoLeviathan" (world appended
    /// directly to the last name, no separator). Match the incoming name against all registered
    /// players: if a player's registered name is a leading prefix of the raw sender string (followed
    /// by one or more capital letters = the world name), return the registered name instead.
    /// </summary>
    private string NormalizeSenderAgainstPlayers(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return rawName;

        foreach (var player in playerService.GetAllPlayers())
        {
            var pName = player.Name;
            if (pName.Length >= rawName.Length) continue; // rawName must be longer
            if (!rawName.StartsWith(pName, StringComparison.OrdinalIgnoreCase)) continue;

            // The remainder after the player name should be a world name: starts with uppercase, all letters
            var suffix = rawName[pName.Length..];
            if (suffix.Length > 0 && char.IsUpper(suffix[0]) && suffix.All(char.IsLetter))
                return pName;
        }

        return rawName;
    }

    private string? ResolvePlayerWorld(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return null;

        foreach (var member in PartyList)
        {
            if (!member.Name.TextValue.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var world = member.World.Value.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(world))
                    return world;
            }
            catch
            {
                // ignored, fallback below
            }
        }

        var existing = playerService.GetPlayer(playerName)?.HomeWorld;
        if (!string.IsNullOrWhiteSpace(existing) && !existing.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return existing;

        return null;
    }

    private static string NormalizeWorld(string? world)
        => string.IsNullOrWhiteSpace(world) ? "Ultros" : world.Trim();

    private string ResolveBestWorldForJoin(string playerName, string? extractedWorld)
    {
        var fromExtract = NormalizeWorld(extractedWorld);
        if (!fromExtract.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return fromExtract;

        var resolved = ResolvePlayerWorld(playerName);
        if (!string.IsNullOrWhiteSpace(resolved))
            return NormalizeWorld(resolved);

        return "Ultros";
    }

    private void AddPartyToTable()
    {
        var local = ObjectTable.LocalPlayer?.Name.TextValue ?? string.Empty;

        foreach (var member in PartyList)
        {
            var name = member.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            // Skip self — dealer should not be added as a player
            if (!string.IsNullOrWhiteSpace(local) && name.Equals(local, StringComparison.OrdinalIgnoreCase))
                continue;

            string? extractedWorld;
            try
            {
                extractedWorld = member.World.Value.Name.ExtractText();
            }
            catch
            {
                extractedWorld = null;
            }

            var world = ResolveBestWorldForJoin(name, extractedWorld);
            _ = gameManager.RouteCommand(name, "JOIN", [world]);
        }
    }

    private void SendPlayerChatCommand(string commandText, bool usePartyChannel)
    {
        if (string.IsNullOrWhiteSpace(commandText))
            return;

        var prefix = usePartyChannel ? "/party" : "/say";
        // Player-side UI actions should flow through chat so dealer-side parsing can consume them.
        SendMessageToChat($"{prefix} {commandText}");
    }

    private void OnTestModeRequested(bool enabled, int botCount)
    {
        testModeEnabled = enabled;
        testModeBotCount = Math.Clamp(botCount, 1, 7);
        testBotService.Configure(testModeEnabled, testModeBotCount);
        renderer.SetTestModeState(testModeEnabled, testModeBotCount);
    }

    private void FactoryResetState()
    {
        testModeEnabled = false;
        testModeBotCount = 3;
        testBotService.Configure(false, testModeBotCount);
        renderer.SetTestModeState(testModeEnabled, testModeBotCount);

        // Reset all player bets to default before clearing
        foreach (var p in playerService.GetAllPlayers())
            p.Metadata["Blackjack.Bet"] = 100;

        playerService.ClearPlayers();
        gameManager.Activate(GameType.None);

        chatGui.Print("[Casino] Reset complete.");
    }

    public void Dispose()
    {
        chatGui.ChatMessage -= OnChatMessage;
        commandManager.RemoveHandler(CommandName);
        pluginInterface.UiBuilder.Draw -= DrawUI;
        pluginInterface.UiBuilder.OpenMainUi -= OpenPlayerView;
        pluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        windowSystem.RemoveAllWindows();
    }
}
