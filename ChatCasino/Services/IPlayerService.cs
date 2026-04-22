using System.Collections.Generic;
using ChatCasino.Models;

namespace ChatCasino.Services;

public interface IPlayerService
{
    Player AddPlayer(string name, string world);
    Player? GetPlayer(string name);
    bool RemovePlayer(string name);
    bool PurgePlayer(string name);
    IReadOnlyCollection<Player> GetAllActivePlayers();
    IReadOnlyCollection<Player> GetAllPlayers();
    void ClearPlayers();
    void UpsertPlayer(Player player);
}
