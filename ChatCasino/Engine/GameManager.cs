using System;
using System.Collections.Generic;
using ChatCasino.Commands;
using ChatCasino.Models;
using ChatCasino.Services;

namespace ChatCasino.Engine;

public sealed class GameManager
{
    private readonly ITableService tableService;
    private readonly IPlayerService playerService;
    private readonly Dictionary<GameType, IGameProcessor> modules = new();

    public GameManager(ITableService tableService, IPlayerService playerService)
    {
        this.tableService = tableService;
        this.playerService = playerService;
    }

    public string DealerIdentity { get; private set; } = string.Empty;
    public Action<string>? OnCommandFeedback { get; set; }
    public Action<string, string, string[]>? OnCommandExecuting { get; set; }

    public void SetDealerIdentity(string localPlayerName)
    {
        DealerIdentity = localPlayerName?.Trim() ?? string.Empty;
    }

    public void RegisterEngine(GameType gameType, IGameProcessor module)
    {
        modules[gameType] = module;
    }

    public void Register(GameType gameType, IGameProcessor module)
    {
        RegisterEngine(gameType, module);
    }

    public bool Activate(GameType gameType)
    {
        modules.TryGetValue(gameType, out var module);
        return tableService.TransitionTo(gameType, module);
    }

    public string GetHelpForActiveGame() => CommandRegistry.BuildHelp(tableService.ActiveGameType);

    public string? GetPlayerWorld(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return null;
        return playerService.GetPlayer(playerName)?.HomeWorld;
    }

    public int? GetPlayerBank(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return null;
        return playerService.GetPlayer(playerName)?.CurrentBank;
    }

    public bool TrySetPlayerBank(string playerName, int bank)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return false;
        var player = playerService.GetPlayer(playerName);
        if (player is null) return false;
        player.CurrentBank = Math.Max(0, bank);
        return true;
    }

    public bool IsPlayerAfk(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return false;
        var player = playerService.GetPlayer(playerName);
        return player is not null && player.IsAfk && !player.IsKicked;
    }

    public bool TrySetPlayerAfk(string playerName, bool afk)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return false;
        var player = playerService.GetPlayer(playerName);
        if (player is null || player.IsKicked) return false;

        player.IsAfk = afk;
        if (afk)
            player.Metadata["AFK.SinceUtcTicks"] = DateTime.UtcNow.Ticks;
        else
            player.Metadata.Remove("AFK.SinceUtcTicks");

        return true;
    }

    public IReadOnlyCollection<Player> GetAllPlayers() => playerService.GetAllPlayers();

    public CmdResult RouteCommand(string player, string cmd, string[] args)
    {
        CmdResult result;

        if (cmd.Equals("AFK", StringComparison.OrdinalIgnoreCase))
        {
            var target = playerService.GetPlayer(player);
            if (target is null)
            {
                result = CmdResult.Fail("Player not found.");
                OnCommandFeedback?.Invoke(result.Message);
                return result;
            }

            var next = !target.IsAfk;
            _ = TrySetPlayerAfk(player, next);
            result = CmdResult.Ok(next ? "AFK enabled." : "AFK disabled.");
            OnCommandFeedback?.Invoke(result.Message);
            return result;
        }

        if (cmd.Equals("JOIN", StringComparison.OrdinalIgnoreCase))
        {
            var world = args.Length >= 1 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : "Unknown";
            playerService.AddPlayer(player, world);
            result = CmdResult.Ok($"Joined table as {player}@{world}.");
            OnCommandFeedback?.Invoke(result.Message);
            return result;
        }

        if (cmd.Equals("LEAVE", StringComparison.OrdinalIgnoreCase))
        {
            result = playerService.RemovePlayer(player)
                ? CmdResult.Ok("Player kicked from active play.")
                : CmdResult.Fail("Player not found.");
            OnCommandFeedback?.Invoke(result.Message);
            return result;
        }

        if (cmd.Equals("REMOVE", StringComparison.OrdinalIgnoreCase))
        {
            result = playerService.PurgePlayer(player)
                ? CmdResult.Ok("Player removed from table.")
                : CmdResult.Fail("Player not found.");
            OnCommandFeedback?.Invoke(result.Message);
            return result;
        }

        if (cmd.Equals("HELP", StringComparison.OrdinalIgnoreCase))
        {
            if (tableService.ActiveGameType == GameType.None)
                return CmdResult.Fail(string.Empty);

            result = CmdResult.Ok(GetHelpForActiveGame());
            OnCommandFeedback?.Invoke(result.Message);
            return result;
        }

        if (tableService.ActiveEngine is null)
        {
            result = CmdResult.Fail(string.Empty);
            return result;
        }

        OnCommandExecuting?.Invoke(player, cmd, args);
        result = tableService.ActiveEngine.Execute(player, cmd, args);
        if (!result.Success || !string.IsNullOrWhiteSpace(result.Message))
            OnCommandFeedback?.Invoke(result.Message);

        return result;
    }
}
