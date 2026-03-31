namespace SamplePlugin.Models;

public enum GameState
{
    Lobby,
    Playing
}

public enum DealerMode
{
    Auto,
    Manual
}

public enum DealerRules
{
    HitsOnSoft17,      // H17 - Default
    StandsOnSoft17,    // S17
    HitsOn16OrLower    // Alternative rule
}

public enum ChatMode
{
    Say,
    Party
}

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

public enum RouletteSpinState
{
    Idle,
    Spinning,
    Resolving
}

public enum CrapsPhase
{
    WaitingForBets,
    PointEstablished
}

public enum BaccaratPhase
{
    WaitingForBets,
    Dealing,
    Resolved
}

public enum ChocoboRacePhase
{
    Idle,
    WaitingForBets,
    Racing,
    Complete
}

public enum PokerPhase
{
    WaitingForPlayers,
    PreFlop,
    Flop,
    Turn,
    River,
    Showdown,
    Complete
}

public enum PokerPlayerStatus
{
    Empty,
    Active,
    Folded,
    AllIn
}

public enum UltimaPhase
{
    WaitingForPlayers,
    Playing,
    Complete
}
