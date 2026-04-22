using System;
using System.Collections.Generic;
using ChatCasino.Models;

namespace ChatCasino.Services;

public sealed class LocalPlayerViewState
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public sealed class ViewSyncService
{
    private readonly IPlayerService playerService;

    public ViewSyncService(IPlayerService playerService)
    {
        this.playerService = playerService;
    }

    public LocalPlayerViewState Current { get; } = new();

    public void MirrorLocalPlayer(string localPlayerName)
    {
        var p = playerService.GetPlayer(localPlayerName);
        if (p is null) return;

        Current.Name = p.Name;
        Current.Metadata = new Dictionary<string, object>(p.Metadata, StringComparer.OrdinalIgnoreCase);
    }

    public void MirrorTellTarget(string playerName)
    {
        var p = playerService.GetPlayer(playerName);
        if (p is null) return;

        Current.Name = p.Name;
        Current.Metadata = new Dictionary<string, object>(p.Metadata, StringComparer.OrdinalIgnoreCase);
    }
}
