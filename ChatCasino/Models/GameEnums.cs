namespace ChatCasino.Models;

public enum GameType
{
    None,
    Blackjack,
    Roulette,
    Craps,
    Baccarat,
    ChocoboRacing,
    TexasHoldEmPvP,
    TexasHoldEmPvD,
    Ultima,
    Bingo
}

public enum GameState
{
    Idle,
    Lobby,
    InRound,
    Resolving,
    Completed,
    ForceStopped
}

public enum TransactionResult
{
    Success,
    InsufficientFunds,
    InvalidAmount,
    PlayerNotFound,
    ConcurrencyConflict,
    UnknownFailure
}

public enum BingoWinCondition { OneLine, TwoLine, FourCorners, Blackout, Blitz }
public enum BingoGameMode { Standard, Progressive }

public sealed class BingoProgressiveRound
{
    public BingoWinCondition WinCondition { get; set; } = BingoWinCondition.OneLine;
    public int PayoutPercent { get; set; } = 100;
}

public readonly record struct CmdResult(bool Success, string Message)
{
    public static CmdResult Ok(string message = "OK") => new(true, message);
    public static CmdResult Fail(string message) => new(false, message);
}
