namespace ChatCasino.Models;

public enum GameType
{
    None,
    Blackjack,
    Roulette,
    Craps,
    Baccarat,
    ChocoboRacing,
    TexasHoldEm,
    Ultima
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

public readonly record struct CmdResult(bool Success, string Message)
{
    public static CmdResult Ok(string message = "OK") => new(true, message);
    public static CmdResult Fail(string message) => new(false, message);
}
