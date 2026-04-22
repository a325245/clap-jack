using System;
using System.Linq;
using System.Text.Json;
using ChatCasino.Engine;
using ChatCasino.Models;

namespace ChatCasino.Services;

public sealed class TableService : ITableService
{
    private readonly IMessageService messageService;
    private readonly ITimerService timerService;
    private readonly IPlayerService playerService;
    private DateTime lastSnapshotAtUtc = DateTime.MinValue;

    public TableService(IMessageService messageService, ITimerService timerService, IPlayerService playerService)
    {
        this.messageService = messageService;
        this.timerService = timerService;
        this.playerService = playerService;
    }

    public GameType ActiveGameType { get; private set; } = GameType.None;
    public GameState State { get; private set; } = GameState.Idle;
    public IGameProcessor? ActiveEngine { get; private set; }

    public bool TransitionTo(GameType nextGameType, IGameProcessor? nextEngine)
    {
        if (ActiveEngine is BaseEngine prev)
            prev.OnForceStop();

        if (nextEngine is BaseEngine next)
            next.OnForceStop();

        ActiveEngine = nextEngine;
        ActiveGameType = nextGameType;
        State = nextEngine is null ? GameState.Idle : GameState.Lobby;

        messageService.QueuePartyMessage($"[CASINO] Game changed to {nextGameType}");
        return true;
    }

    public void ProcessTick()
    {
        if (ActiveEngine is BaseEngine active)
            active.Tick();

        messageService.ProcessTick();
        timerService.ProcessTick();

        if ((DateTime.UtcNow - lastSnapshotAtUtc).TotalSeconds >= 10)
        {
            _ = SaveSnapshot();
            lastSnapshotAtUtc = DateTime.UtcNow;
        }
    }

    public string SaveSnapshot()
    {
        var snapshot = new TableSnapshot
        {
            ActiveGameType = ActiveGameType,
            State = State,
            Players = playerService.GetAllPlayers().ToList()
        };

        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    public bool LoadSnapshot(string snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return false;

        try
        {
            var snapshot = JsonSerializer.Deserialize<TableSnapshot>(snapshotJson);
            if (snapshot is null) return false;

            playerService.ClearPlayers();
            foreach (var p in snapshot.Players)
                playerService.UpsertPlayer(p);

            ActiveGameType = snapshot.ActiveGameType;
            State = snapshot.State;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
