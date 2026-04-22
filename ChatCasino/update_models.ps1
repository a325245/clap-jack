$Replacements = @{
    'RouletteResult' = 'Roulette.Result';
    'RouletteSpinState' = 'Roulette.SpinState';
    'RouletteSpinStart' = 'Roulette.SpinStart';
    'CrapsPhase' = 'Craps.Phase';
    'CrapsPoint' = 'Craps.Point';
    'CrapsDie1' = 'Craps.Die1';
    'CrapsDie2' = 'Craps.Die2';
    'CrapsRolling' = 'Craps.Rolling';
    'CrapsRollStart' = 'Craps.RollStart';
    'CrapsBets' = 'Craps.Bets';
    'CrapsShooterName' = 'Craps.ShooterName';
    'CrapsBettingPhase' = 'Craps.BettingPhase';
    'CrapsBettingStart' = 'Craps.BettingStart';
    'BaccaratPhase' = 'Baccarat.Phase';
    'BaccaratPlayerHand' = 'Baccarat.PlayerHand';
    'BaccaratBankerHand' = 'Baccarat.BankerHand';
    'BaccaratBets' = 'Baccarat.Bets';
    'ChocoboRacePhase' = 'Chocobo.RacePhase';
    'ChocoboRaceStart' = 'Chocobo.RaceStart';
    'ChocoboBets' = 'Chocobo.Bets';
    'ChocoboMinBet' = 'Chocobo.MinBet';
    'ChocoboMaxBet' = 'Chocobo.MaxBet';
    'PokerPhase' = 'Poker.Phase';
    'PokerDealerSeat' = 'Poker.DealerSeat';
    'PokerCurrentSeat' = 'Poker.CurrentSeat';
    'PokerPot' = 'Poker.Pot';
    'PokerStreetBet' = 'Poker.StreetBet';
    'PokerLastAggressor' = 'Poker.LastAggressor';
    'PokerSmallBlind' = 'Poker.SmallBlind';
    'PokerAnte' = 'Poker.Ante';
    'PokerCommunity' = 'Poker.Community';
    'PokerTurnStart' = 'Poker.TurnStart';
    'UltimaPhase' = 'Ultima.Phase';
    'UltimaDrawPile' = 'Ultima.DrawPile';
    'UltimaDiscardPile' = 'Ultima.DiscardPile';
    'UltimaHands' = 'Ultima.Hands';
    'UltimaPlayerOrder' = 'Ultima.PlayerOrder';
    'UltimaCurrentIndex' = 'Ultima.CurrentIndex';
    'UltimaClockwise' = 'Ultima.Clockwise';
    'UltimaActiveColor' = 'Ultima.ActiveColor';
    'UltimaTopCard' = 'Ultima.TopCard';
    'UltimaCalled' = 'Ultima.Called';
    'UltimaWinner' = 'Ultima.Winner'
}

Get-ChildItem -Path ChatCasino\ -Recurse -Filter *.cs | ForEach-Object {
    $content = Get-Content $_.FullName -Raw
    $modified = $false
    foreach ($key in $Replacements.Keys) {
        $val = $Replacements[$key]
        if ($content.Contains($key)) {
            $content = $content.Replace($key, $val)
            $modified = $true
        }
    }
    if ($modified) {
        Set-Content -Path $_.FullName -Value $content -NoNewline
    }
}
