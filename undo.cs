using System;
using System.IO;
using System.Collections.Generic;

class Program
{
    static void Main()
    {
        var rev = new Dictionary<string, string>
        {
            {"Roulette.Result", "RouletteResult"},
            {"Roulette.SpinState", "RouletteSpinState"},
            {"Roulette.SpinStart", "RouletteSpinStart"},
            {"Craps.Phase", "CrapsPhase"},
            {"Craps.Point", "CrapsPoint"},
            {"Craps.Die1", "CrapsDie1"},
            {"Craps.Die2", "CrapsDie2"},
            {"Craps.Rolling", "CrapsRolling"},
            {"Craps.RollStart", "CrapsRollStart"},
            {"Craps.Bets", "CrapsBets"},
            {"Craps.ShooterName", "CrapsShooterName"},
            {"Craps.BettingPhase", "CrapsBettingPhase"},
            {"Craps.BettingStart", "CrapsBettingStart"},
            {"Baccarat.Phase", "BaccaratPhase"},
            {"Baccarat.PlayerHand", "BaccaratPlayerHand"},
            {"Baccarat.BankerHand", "BaccaratBankerHand"},
            {"Baccarat.Bets", "BaccaratBets"},
            {"Chocobo.RacePhase", "ChocoboRacePhase"},
            {"Chocobo.RaceStart", "ChocoboRaceStart"},
            {"Chocobo.Bets", "ChocoboBets"},
            {"Chocobo.MinBet", "ChocoboMinBet"},
            {"Chocobo.MaxBet", "ChocoboMaxBet"},
            {"Poker.Phase", "PokerPhase"},
            {"Poker.DealerSeat", "PokerDealerSeat"},
            {"Poker.CurrentSeat", "PokerCurrentSeat"},
            {"Poker.Pot", "PokerPot"},
            {"Poker.StreetBet", "PokerStreetBet"},
            {"Poker.LastAggressor", "PokerLastAggressor"},
            {"Poker.SmallBlind", "PokerSmallBlind"},
            {"Poker.Ante", "PokerAnte"},
            {"Poker.Community", "PokerCommunity"},
            {"Poker.TurnStart", "PokerTurnStart"},
            {"Ultima.Phase", "UltimaPhase"},
            {"Ultima.DrawPile", "UltimaDrawPile"},
            {"Ultima.DiscardPile", "UltimaDiscardPile"},
            {"Ultima.Hands", "UltimaHands"},
            {"Ultima.PlayerOrder", "UltimaPlayerOrder"},
            {"Ultima.CurrentIndex", "UltimaCurrentIndex"},
            {"Ultima.Clockwise", "UltimaClockwise"},
            {"Ultima.ActiveColor", "UltimaActiveColor"},
            {"Ultima.TopCard", "UltimaTopCard"},
            {"Ultima.Called", "UltimaCalled"},
            {"Ultima.Winner", "UltimaWinner"}
        };

        foreach (var file in Directory.GetFiles("ChatCasino", "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            bool mod = false;
            foreach (var kvp in rev)
            {
                if (text.Contains(kvp.Key))
                {
                    text = text.Replace(kvp.Key, kvp.Value);
                    mod = true;
                }
            }
            if (mod) File.WriteAllText(file, text);
        }
    }
}
