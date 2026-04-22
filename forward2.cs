using System;
using System.IO;
using System.Collections.Generic;

class Program
{
    static void Main()
    {
        var fwd = new Dictionary<string, string>
        {
            {"engine.CurrentTable.RouletteResult", "engine.CurrentTable.Roulette.Result"},
            {"engine.CurrentTable.RouletteSpinState", "engine.CurrentTable.Roulette.SpinState"},
            {"engine.CurrentTable.RouletteSpinStart", "engine.CurrentTable.Roulette.SpinStart"},
            {"engine.CurrentTable.CrapsPhase", "engine.CurrentTable.Craps.Phase"},
            {"engine.CurrentTable.CrapsPoint", "engine.CurrentTable.Craps.Point"},
            {"engine.CurrentTable.CrapsDie1", "engine.CurrentTable.Craps.Die1"},
            {"engine.CurrentTable.CrapsDie2", "engine.CurrentTable.Craps.Die2"},
            {"engine.CurrentTable.CrapsRolling", "engine.CurrentTable.Craps.Rolling"},
            {"engine.CurrentTable.CrapsRollStart", "engine.CurrentTable.Craps.RollStart"},
            {"engine.CurrentTable.CrapsBets", "engine.CurrentTable.Craps.Bets"},
            {"engine.CurrentTable.CrapsShooterName", "engine.CurrentTable.Craps.ShooterName"},
            {"engine.CurrentTable.CrapsBettingPhase", "engine.CurrentTable.Craps.BettingPhase"},
            {"engine.CurrentTable.CrapsBettingStart", "engine.CurrentTable.Craps.BettingStart"},
            {"engine.CurrentTable.BaccaratPhase", "engine.CurrentTable.Baccarat.Phase"},
            {"engine.CurrentTable.BaccaratPlayerHand", "engine.CurrentTable.Baccarat.PlayerHand"},
            {"engine.CurrentTable.BaccaratBankerHand", "engine.CurrentTable.Baccarat.BankerHand"},
            {"engine.CurrentTable.BaccaratBets", "engine.CurrentTable.Baccarat.Bets"},
            {"engine.CurrentTable.ChocoboRacePhase", "engine.CurrentTable.Chocobo.RacePhase"},
            {"engine.CurrentTable.ChocoboRaceStart", "engine.CurrentTable.Chocobo.RaceStart"},
            {"engine.CurrentTable.ChocoboBets", "engine.CurrentTable.Chocobo.Bets"},
            {"engine.CurrentTable.ChocoboMinBet", "engine.CurrentTable.Chocobo.MinBet"},
            {"engine.CurrentTable.ChocoboMaxBet", "engine.CurrentTable.Chocobo.MaxBet"},
            {"engine.CurrentTable.PokerPhase", "engine.CurrentTable.Poker.Phase"},
            {"engine.CurrentTable.PokerDealerSeat", "engine.CurrentTable.Poker.DealerSeat"},
            {"engine.CurrentTable.PokerCurrentSeat", "engine.CurrentTable.Poker.CurrentSeat"},
            {"engine.CurrentTable.PokerPot", "engine.CurrentTable.Poker.Pot"},
            {"engine.CurrentTable.PokerStreetBet", "engine.CurrentTable.Poker.StreetBet"},
            {"engine.CurrentTable.PokerLastAggressor", "engine.CurrentTable.Poker.LastAggressor"},
            {"engine.CurrentTable.PokerSmallBlind", "engine.CurrentTable.Poker.SmallBlind"},
            {"engine.CurrentTable.PokerAnte", "engine.CurrentTable.Poker.Ante"},
            {"engine.CurrentTable.PokerCommunity", "engine.CurrentTable.Poker.Community"},
            {"engine.CurrentTable.PokerTurnStart", "engine.CurrentTable.Poker.TurnStart"},
            {"engine.CurrentTable.UltimaPhase", "engine.CurrentTable.Ultima.Phase"},
            {"engine.CurrentTable.UltimaDrawPile", "engine.CurrentTable.Ultima.DrawPile"},
            {"engine.CurrentTable.UltimaDiscardPile", "engine.CurrentTable.Ultima.DiscardPile"},
            {"engine.CurrentTable.UltimaHands", "engine.CurrentTable.Ultima.Hands"},
            {"engine.CurrentTable.UltimaPlayerOrder", "engine.CurrentTable.Ultima.PlayerOrder"},
            {"engine.CurrentTable.UltimaCurrentIndex", "engine.CurrentTable.Ultima.CurrentIndex"},
            {"engine.CurrentTable.UltimaClockwise", "engine.CurrentTable.Ultima.Clockwise"},
            {"engine.CurrentTable.UltimaActiveColor", "engine.CurrentTable.Ultima.ActiveColor"},
            {"engine.CurrentTable.UltimaTopCard", "engine.CurrentTable.Ultima.TopCard"},
            {"engine.CurrentTable.UltimaCalled", "engine.CurrentTable.Ultima.Called"},
            {"engine.CurrentTable.UltimaWinner", "engine.CurrentTable.Ultima.Winner"},

            {"CurrentTable.RouletteResult", "CurrentTable.Roulette.Result"},
            {"CurrentTable.RouletteSpinState", "CurrentTable.Roulette.SpinState"},
            {"CurrentTable.RouletteSpinStart", "CurrentTable.Roulette.SpinStart"},
            {"CurrentTable.CrapsPhase", "CurrentTable.Craps.Phase"},
            {"CurrentTable.CrapsPoint", "CurrentTable.Craps.Point"},
            {"CurrentTable.CrapsDie1", "CurrentTable.Craps.Die1"},
            {"CurrentTable.CrapsDie2", "CurrentTable.Craps.Die2"},
            {"CurrentTable.CrapsRolling", "CurrentTable.Craps.Rolling"},
            {"CurrentTable.CrapsRollStart", "CurrentTable.Craps.RollStart"},
            {"CurrentTable.CrapsBets", "CurrentTable.Craps.Bets"},
            {"CurrentTable.CrapsShooterName", "CurrentTable.Craps.ShooterName"},
            {"CurrentTable.CrapsBettingPhase", "CurrentTable.Craps.BettingPhase"},
            {"CurrentTable.CrapsBettingStart", "CurrentTable.Craps.BettingStart"},
            {"CurrentTable.BaccaratPhase", "CurrentTable.Baccarat.Phase"},
            {"CurrentTable.BaccaratPlayerHand", "CurrentTable.Baccarat.PlayerHand"},
            {"CurrentTable.BaccaratBankerHand", "CurrentTable.Baccarat.BankerHand"},
            {"CurrentTable.BaccaratBets", "CurrentTable.Baccarat.Bets"},
            {"CurrentTable.ChocoboRacePhase", "CurrentTable.Chocobo.RacePhase"},
            {"CurrentTable.ChocoboRaceStart", "CurrentTable.Chocobo.RaceStart"},
            {"CurrentTable.ChocoboBets", "CurrentTable.Chocobo.Bets"},
            {"CurrentTable.ChocoboMinBet", "CurrentTable.Chocobo.MinBet"},
            {"CurrentTable.ChocoboMaxBet", "CurrentTable.Chocobo.MaxBet"},
            {"CurrentTable.PokerPhase", "CurrentTable.Poker.Phase"},
            {"CurrentTable.PokerDealerSeat", "CurrentTable.Poker.DealerSeat"},
            {"CurrentTable.PokerCurrentSeat", "CurrentTable.Poker.CurrentSeat"},
            {"CurrentTable.PokerPot", "CurrentTable.Poker.Pot"},
            {"CurrentTable.PokerStreetBet", "CurrentTable.Poker.StreetBet"},
            {"CurrentTable.PokerLastAggressor", "CurrentTable.Poker.LastAggressor"},
            {"CurrentTable.PokerSmallBlind", "CurrentTable.Poker.SmallBlind"},
            {"CurrentTable.PokerAnte", "CurrentTable.Poker.Ante"},
            {"CurrentTable.PokerCommunity", "CurrentTable.Poker.Community"},
            {"CurrentTable.PokerTurnStart", "CurrentTable.Poker.TurnStart"},
            {"CurrentTable.UltimaPhase", "CurrentTable.Ultima.Phase"},
            {"CurrentTable.UltimaDrawPile", "CurrentTable.Ultima.DrawPile"},
            {"CurrentTable.UltimaDiscardPile", "CurrentTable.Ultima.DiscardPile"},
            {"CurrentTable.UltimaHands", "CurrentTable.Ultima.Hands"},
            {"CurrentTable.UltimaPlayerOrder", "CurrentTable.Ultima.PlayerOrder"},
            {"CurrentTable.UltimaCurrentIndex", "CurrentTable.Ultima.CurrentIndex"},
            {"CurrentTable.UltimaClockwise", "engine.CurrentTable.Ultima.Clockwise"},
            {"CurrentTable.UltimaActiveColor", "CurrentTable.Ultima.ActiveColor"},
            {"CurrentTable.UltimaTopCard", "CurrentTable.Ultima.TopCard"},
            {"CurrentTable.UltimaCalled", "CurrentTable.Ultima.Called"},
            {"CurrentTable.UltimaWinner", "CurrentTable.Ultima.Winner"},
            
            {"table.ChocoboMinBet", "table.Chocobo.MinBet"},
            {"table.ChocoboMaxBet", "table.Chocobo.MaxBet"},
            {"table.PokerSmallBlind", "table.Poker.SmallBlind"}
        };

        foreach (var file in Directory.GetFiles("ChatCasino", "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            bool mod = false;
            foreach (var kvp in fwd)
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
