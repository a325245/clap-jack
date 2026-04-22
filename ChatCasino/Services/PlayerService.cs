using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ChatCasino.Models;

namespace ChatCasino.Services;

public sealed class PlayerService : IPlayerService
{
    private readonly ConcurrentDictionary<string, Player> players = new(StringComparer.OrdinalIgnoreCase);

    public Player AddPlayer(string name, string world)
    {
        var key = name.Trim();
        var resolvedWorld = string.IsNullOrWhiteSpace(world) ? "Unknown" : world.Trim();

        return players.AddOrUpdate(
            key,
            _ => new Player
            {
                Name = key,
                HomeWorld = resolvedWorld,
                CurrentBank = 10_000,
                IsAfk = false,
                IsKicked = false
            },
            (_, existing) =>
            {
                if (!resolvedWorld.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(existing.HomeWorld) || existing.HomeWorld.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    existing.HomeWorld = resolvedWorld;
                existing.IsAfk = false;
                existing.IsKicked = false;
                if (existing.CurrentBank < 0)
                    existing.CurrentBank = 0;
                return existing;
            });
    }

    public Player? GetPlayer(string name)
    {
        players.TryGetValue(name.Trim(), out var player);
        return player;
    }

    public bool RemovePlayer(string name)
    {
        if (!players.TryGetValue(name.Trim(), out var player))
            return false;

        player.IsKicked = true;
        player.IsAfk = false;
        player.Metadata.Remove("AFK.SinceUtcTicks");
        return true;
    }

    public bool PurgePlayer(string name)
        => players.TryRemove(name.Trim(), out _);

    public IReadOnlyCollection<Player> GetAllActivePlayers()
        => players.Values.Where(p => !p.IsAfk && !p.IsKicked).ToArray();

    public IReadOnlyCollection<Player> GetAllPlayers()
        => players.Values.ToArray();

    public void ClearPlayers()
    {
        players.Clear();
    }

    public void UpsertPlayer(Player player)
    {
        var clone = Clone(player);
        players.AddOrUpdate(clone.Name, clone, (_, _) => clone);
    }

    private static Player Clone(Player p)
    {
        var clone = new Player
        {
            Name = p.Name,
            HomeWorld = p.HomeWorld,
            CurrentBank = p.CurrentBank,
            IsAfk = p.IsAfk,
            IsKicked = p.IsKicked
        };

        foreach (var kvp in p.SessionStats)
            clone.SessionStats[kvp.Key] = new PlayerStats
            {
                Wins = kvp.Value.Wins,
                Losses = kvp.Value.Losses,
                NetGains = kvp.Value.NetGains
            };

        foreach (var kvp in p.Metadata)
            clone.Metadata[kvp.Key] = kvp.Value;

        return clone;
    }
}
