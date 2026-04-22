using ChatCasino.Engine;
using ChatCasino.Models;

namespace ChatCasino.Services;

public interface ITableService
{
    GameType ActiveGameType { get; }
    GameState State { get; }
    IGameProcessor? ActiveEngine { get; }

    bool TransitionTo(GameType nextGameType, IGameProcessor? nextEngine);
    void ProcessTick();
    string SaveSnapshot();
    bool LoadSnapshot(string snapshotJson);
}
