using System;
using System.Collections.Concurrent;

namespace ChatCasino.Services;

public sealed class TimerService : ITimerService
{
    private readonly ConcurrentDictionary<Guid, ScheduledAction> scheduled = new();

    public Guid Schedule(TimeSpan delay, Action callback)
    {
        var id = Guid.NewGuid();
        scheduled[id] = new ScheduledAction(DateTime.UtcNow.Add(delay), callback);
        return id;
    }

    public bool Cancel(Guid id) => scheduled.TryRemove(id, out _);

    public void ProcessTick()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in scheduled)
        {
            if (kvp.Value.RunAtUtc > now) continue;
            if (!scheduled.TryRemove(kvp.Key, out var action)) continue;
            action.Callback();
        }
    }

    private readonly record struct ScheduledAction(DateTime RunAtUtc, Action Callback);
}
