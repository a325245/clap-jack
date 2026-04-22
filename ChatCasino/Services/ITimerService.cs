using System;

namespace ChatCasino.Services;

public interface ITimerService
{
    Guid Schedule(TimeSpan delay, Action callback);
    bool Cancel(Guid id);
    void ProcessTick();
}
