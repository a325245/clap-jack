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

public enum UIMode
{
    Dealer,
    Player
}
