using System.Collections.Generic;
using ChatCasino.Models;

namespace ChatCasino.Commands;

public static class CommandRegistry
{
    private static readonly Dictionary<GameType, string[]> Commands = new()
    {
        [GameType.None] = ["JOIN", "LEAVE", "REMOVE", "BANK", "AFK", "HELP", "RULES"],
        [GameType.Blackjack] = ["BET [amt]", "DEAL", "HIT", "STAND", "DOUBLE", "SPLIT", "INSURANCE", "RULETOGGLE"],
        [GameType.Roulette] = ["BET [amt] [target]", "SPIN"],
        [GameType.Craps] = ["BET [amt] PASS|DONTPASS|FIELD|SEVEN|ANYCRAPS|PLACE4/5/6/8/9/10", "ROLL"],
        [GameType.Baccarat] = ["BET [amt] PLAYER|BANKER|TIE", "DEAL"],
        [GameType.TexasHoldEm] = ["FOLD", "CHECK", "CALL", "RAISE [amt]", "ALL IN", "DEAL", "HAND"],
        [GameType.ChocoboRacing] = ["BET [amt] [racer]", "START"],
        [GameType.Ultima] = ["DEAL", "PLAY [code] [color?]", "DRAW", "HAND"]
    };

    public static IReadOnlyList<string> GetCommands(GameType gameType)
        => Commands.TryGetValue(gameType, out var list) ? list : Commands[GameType.None];

    public static string BuildHelp(GameType gameType)
        => string.Join(" | ", GetCommands(gameType));
}
