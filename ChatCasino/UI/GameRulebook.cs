using System.Collections.Generic;
using ChatCasino.Models;

namespace ChatCasino.UI;

public static class GameRulebook
{
    public static IReadOnlyList<GameType> OrderedGames { get; } =
    [
        GameType.Blackjack,
        GameType.Roulette,
        GameType.Craps,
        GameType.Baccarat,
        GameType.TexasHoldEmPvP,
        GameType.TexasHoldEmPvD,
        GameType.ChocoboRacing,
        GameType.Ultima
    ];

    public static IReadOnlyList<string> GetRules(GameType game) => game switch
    {
        GameType.Blackjack =>
        [
            "Beat dealer without busting.",
            "Face cards are 10, aces are 1 or 11.",
            "Dealer follows table rule set (H17/S17)."
        ],
        GameType.Roulette =>
        [
            "Bet on number or outside targets (RED/BLACK/EVEN/ODD).",
            "Single number pays highest; outside bets pay lower odds.",
            "No bets once spin starts."
        ],
        GameType.Craps =>
        [
            "Shooter must place PASS or DONTPASS before rolling.",
            "Betting duration is a minimum open window before roll.",
            "Line and side bets resolve per roll/point state."
        ],
        GameType.Baccarat =>
        [
            "Bet PLAYER, BANKER, or TIE.",
            "Highest total closest to 9 wins.",
            "Round resolves automatically after deal."
        ],
        GameType.TexasHoldEmPvP =>
        [
            "Each player gets 2 hole cards plus 5 shared board cards.",
            "Best 5-card hand wins; side pots can split.",
            "Rounds auto-start after showdown/fold resolution."
        ],
        GameType.TexasHoldEmPvD =>
        [
            "Place an ante bet, then receive 2 hole cards + 5 board cards.",
            "Dealer needs at least a pair to qualify; no qualify = ante push.",
            "Payouts: Pair 1:1 | Trips 3:2 | Straight 2:1 | Flush 5:2 | Full House 3:1 | Quads 10:1 | SF 50:1 | Royal 250:1.",
            "Type FOLD before DEAL resolution to forfeit your ante.",
            "Dealer types DEAL twice: first to deal, second to resolve."
        ],
        GameType.ChocoboRacing =>
        [
            "Bets open on rostered racers with listed odds.",
            "Players can place multiple bets one ticket at a time.",
            "Race resolves with payout on winning tickets."
        ],
        GameType.Ultima =>
        [
            "Play valid card/color combinations per Ultima rules.",
            "Draw/play cycle continues until resolution condition.",
            "Use table prompts for legal actions."
        ],
        _ => ["No rules available."]
    };
}
