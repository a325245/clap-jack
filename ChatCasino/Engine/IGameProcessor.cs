using System.Collections.Generic;
using ChatCasino.Models;

namespace ChatCasino.Engine;

public interface IGameProcessor
{
    CmdResult Execute(string player, string cmd, string[] args);
    IEnumerable<string> GetValidCommands();
}
