using System;
using System.Collections.Generic;
using System.Linq;

namespace SamplePlugin.Engine;

public class BlackjackEngine
{
    public Models.Table CurrentTable { get; set; }
    public Models.DealerMode Mode { get; set; } = Models.DealerMode.Auto;
    public Models.ChatMode ChatMode { get; set; } = Models.ChatMode.Say;
    private Stack<Models.Table> StateHistory { get; set; } = new();

    public Action<string>? OnChatMessage { get; set; }
    public Action<string, string>? OnPlayerTell { get; set; }  // (name@server, message)
    public Action<string>? OnAdminEcho { get; set; }
    public Action? OnUIUpdate { get; set; }

    // Chat methods to handle different chat modes
    public void SendChatMessage(string message)
    {
        string chatCommand = ChatMode switch
        {
            Models.ChatMode.Party => $"/party {message}",
            _ => $"/say {message}"
        };
        OnChatMessage?.Invoke(chatCommand);
    }

    private void SendDealerChatMessage(string message)
    {
        DealerMessageQueue.Enqueue(message);
    }

    // Dealer message queue for delays
    private Queue<string> DealerMessageQueue { get; set; } = new();
    private DateTime LastDealerMessage { get; set; } = DateTime.MinValue;
    private int GetDealerDelayMs() => CurrentTable.MessageDelayMs;

    public BlackjackEngine()
    {
        CurrentTable = new Models.Table();
        CurrentTable.BuildDeck();
    }

    public void ProcessDealerMessageQueue()
    {
        if (DealerMessageQueue.Count > 0 && 
            (DateTime.Now - LastDealerMessage).TotalMilliseconds >= GetDealerDelayMs())
        {
            var message = DealerMessageQueue.Dequeue();
            SendChatMessage(message);
            LastDealerMessage = DateTime.Now;
        }
    }

    private void SendDealerMessage(string message)
    {
        SendDealerChatMessage(message);
    }

    public void Announce(string message) => SendDealerChatMessage(message);

    private string DN(string name) => CurrentTable.GetDisplayName(name);

    public void StartGame()
    {
        if (CurrentTable.GameState == Models.GameState.Playing) return; // prevent double-deal

        SaveState();

        // Validate all players have funds
        var validPlayers = CurrentTable.Players.Values
            .Where(p => !p.IsAfk && p.PersistentBet >= CurrentTable.MinBet && p.Bank >= p.PersistentBet)
            .ToList();

        if (validPlayers.Count == 0)
        {
            SendChatMessage("No valid players with sufficient funds to start game.");
            return;
        }

        // Reset all player states and capture pre-deal banks
        foreach (var player in CurrentTable.Players.Values)
        {
            player.Hands.Clear();
            player.CurrentBets.Clear();
            player.IsStanding = false;
            player.HasDoubledDown = false;
            player.HasInsurance = false;
            player.InsuranceBet = 0;
            player.ActiveHandIndex = 0;
            player.MaxSplits = CurrentTable.MaxSplitsAllowed;
            player.PreDealBank = player.Bank;
        }

        // Build turn order and set up hands
        CurrentTable.TurnOrder = validPlayers.Select(p => p.Name).ToList();
        CurrentTable.CurrentTurnIndex = 0;
        if (!CurrentTable.PersistentDeck)
            CurrentTable.BuildDeck();
        else if (CurrentTable.Deck.Count < 10)
        {
            CurrentTable.BuildDeck(); // reshuffle if running low
            CurrentTable.DealtCards.Clear();
        }
        CurrentTable.DealerHand.Clear();
        CurrentTable.HoleCardRevealed = false;
        CurrentTable.TotalGames++;

        // Deal initial cards to each valid player
        foreach (var player in validPlayers)
        {
            player.Hands.Add(new List<Models.Card>());
            player.CurrentBets.Add(player.PersistentBet);
            player.Bank -= player.PersistentBet; // Deduct bet

            // Deal 2 cards
            player.Hands[0].Add(CurrentTable.DrawCard());
            player.Hands[0].Add(CurrentTable.DrawCard());
        }

        // Deal to dealer (2 cards, second is hole card)
        CurrentTable.DealerHand.Add(CurrentTable.DrawCard());
        CurrentTable.DealerHoleCard = CurrentTable.DrawCard();
        CurrentTable.DealerHand.Add(CurrentTable.DealerHoleCard);

        // Check for dealer blackjack
        CurrentTable.DealerHasBlackjack = CurrentTable.GetDealerHasBlackjack();

        CurrentTable.GameState = Models.GameState.Playing;
        CurrentTable.TurnTimeRemaining = CurrentTable.TurnTimeLimit;
        CurrentTable.TurnStartTime = DateTime.Now;
        CurrentTable.TimerWarningShown = false; // Reset warning flag

        LogAction("Game started - cards dealt");
        SendDealerMessage($"Game Started! Cards dealt to {validPlayers.Count} players.");

        // Show dealer upcard only
        SendDealerMessage($"Dealer shows: {FormatCard(CurrentTable.DealerHand[0])} [Hidden]");

        // Check for immediate blackjacks
        CheckForNaturalBlackjacks();

        // Offer insurance if dealer shows ace
        if (CurrentTable.DealerHand[0].IsAce && CurrentTable.InsuranceEnabled)
        {
            OfferInsurance();
        }

        // Start the first player's turn with their cards
        if (CurrentTable.TurnOrder.Count > 0)
        {
            StartPlayerTurn(0);
        }

        OnUIUpdate?.Invoke();
    }

    private void StartPlayerTurn(int playerIndex)
    {
        if (playerIndex >= CurrentTable.TurnOrder.Count) return;

        var playerName = CurrentTable.TurnOrder[playerIndex];
        var player = GetPlayer(playerName);
        if (player == null) return;

        // Capture starting bank for this turn
        player.TurnStartBank = player.Bank;

        // Announce player's cards at the start of their turn
        var handInfo = player.GetHandInfo(0);
        string handDisplay = string.Join("", handInfo.Cards.Select(c => FormatCard(c)));
        SendDealerMessage($"{DN(playerName)}: {handDisplay} ({handInfo.GetHandDescription()})");

        // Auto-complete if blackjack — no announcement, payout handles it
        if (handInfo.IsBlackjack)
        {
            player.IsStanding = true;
            LogAction($"{playerName} auto-completed with blackjack");
            AdvanceToNextTurn(playerName);
            return;
        }

        // Build available commands
        var availableCommands = new List<string> { ">HIT", ">STAND" };

        if (player.CanDoubleDown())
            availableCommands.Add(">DOUBLE");

        if (player.CanSplit())
            availableCommands.Add(">SPLIT");

        string commandsText = string.Join(" or ", availableCommands);
        SendDealerMessage($"{DN(playerName)}, it's your turn. Type {commandsText}");
        LogAction($"Starting {playerName}'s turn");
    }

    private void CheckForNaturalBlackjacks()
    {
        bool anyBlackjacks = false;

        foreach (var player in CurrentTable.Players.Values.Where(p => !p.IsAfk && p.Hands.Count > 0))
        {
            if (player.IsNaturalBlackjack(player.Hands[0]))
            {
                // No announcement here — payout message handles it
                LogAction($"{player.Name} has natural blackjack");
                player.IsStanding = true;
                anyBlackjacks = true;
            }
        }

        if (anyBlackjacks)
        {
            OnUIUpdate?.Invoke();
        }
    }

    private void OfferInsurance()
    {
        SendDealerMessage("Insurance available! Dealer shows Ace. Type '>INSURANCE' to buy insurance.");
        LogAction("Insurance offered - dealer shows ace");
    }

    public void PlayerInsurance(string playerName)
    {
        var player = GetPlayer(playerName);
        if (player == null || !CurrentTable.InsuranceEnabled || !CurrentTable.DealerHand[0].IsAce) return;

        int insuranceCost = player.CurrentBets[0] / 2;
        if (player.Bank < insuranceCost) return;

        player.InsuranceBet = insuranceCost;
        player.HasInsurance = true;
        player.Bank -= insuranceCost;

        SendDealerMessage($" {playerName} buys insurance for {insuranceCost}");
        LogAction($"{playerName} bought insurance for {insuranceCost}");
    }

    public void UpdateTimer()
    {
        // Never run the blackjack turn timer when playing roulette
        if (CurrentTable.GameType != Models.GameType.Blackjack) return;
        if (CurrentTable.GameState != Models.GameState.Playing) return;

        int elapsed = (int)(DateTime.Now - CurrentTable.TurnStartTime).TotalSeconds;
        CurrentTable.TurnTimeRemaining = CurrentTable.TurnTimeLimit - elapsed;

        // Warning at 1/3 time (only once)
        if (CurrentTable.TurnTimeRemaining <= CurrentTable.TurnTimeLimit / 3 && !CurrentTable.TimerWarningShown)
        {
            if (Mode == Models.DealerMode.Auto)
            {
                var playerName = CurrentTable.CurrentTurnIndex < CurrentTable.TurnOrder.Count 
                    ? CurrentTable.TurnOrder[CurrentTable.CurrentTurnIndex] 
                    : "Unknown";
                SendDealerMessage($"{DN(playerName)} has {CurrentTable.TurnTimeRemaining}s remaining!");
                LogAction($"Timer warning for {playerName}: {CurrentTable.TurnTimeRemaining}s");
                CurrentTable.TimerWarningShown = true;
            }
        }

        // Auto-stand on timeout
        if (CurrentTable.TurnTimeRemaining <= 0)
        {
            if (CurrentTable.CurrentTurnIndex < CurrentTable.TurnOrder.Count)
            {
                var playerName = CurrentTable.TurnOrder[CurrentTable.CurrentTurnIndex];
                if (Mode == Models.DealerMode.Auto)
                {
                    var player = GetPlayer(playerName);
                    if (player != null && !player.IsStanding)
                    {
                        player.IsStanding = true;
                        player.IsAfk = true;
                        SendDealerMessage($"{DN(playerName)} timed out and is now AFK!");
                        LogAction($"{playerName} timed out - auto AFK");
                        AdvanceToNextTurn(playerName);
                    }
                }
                else
                {
                    // Manual mode — announce once in /say that time is up
                    if (!CurrentTable.TimerTimeoutShown)
                    {
                        CurrentTable.TimerTimeoutShown = true;
                        SendDealerMessage($"{DN(playerName)}'s time is up. No commands needed — dealer controls the game.");
                        LogAction($"{playerName} time limit exceeded (manual mode)");
                    }
                }
            }
        }
    }

    public void PlayerHit(string playerName)
    {
        if (!IsPlayerTurn(playerName)) return;

        var player = GetPlayer(playerName);
        if (player == null || player.ActiveHandIndex >= player.Hands.Count) return;

        var newCard = CurrentTable.DrawCard();
        player.Hands[player.ActiveHandIndex].Add(newCard);

        var handInfo = player.GetHandInfo();
        string allCards = string.Join("", handInfo.Cards.Select(c => FormatCard(c)));
        SendDealerMessage($"{DN(playerName)} hits: {allCards} -> {handInfo.GetHandDescription()}");
        LogAction($"{playerName} hits: {newCard.GetCardDisplay()} -> total {handInfo.Score}");

        if (handInfo.IsBust)
        {
            SendDealerMessage($"{DN(playerName)} BUSTS with {handInfo.Score}!");
            LogAction($"{playerName} busts with {handInfo.Score}");
            AdvanceToNextHandOrPlayer(playerName);
        }
        else if (handInfo.Score == 21)
        {
            SendDealerMessage($"{DN(playerName)} reaches 21!");
            LogAction($"{playerName} reaches 21");
            AdvanceToNextHandOrPlayer(playerName);
        }

        OnUIUpdate?.Invoke();
    }

    public void PlayerStand(string playerName)
    {
        if (!IsPlayerTurn(playerName)) return;
        var player = GetPlayer(playerName);
        if (player == null) return;

        var handInfo = player.GetHandInfo();
        LogAction($"{playerName} stands with {handInfo.Score}");

        AdvanceToNextHandOrPlayer(playerName);
    }

    public void PlayerDouble(string playerName)
    {
        if (!IsPlayerTurn(playerName)) return;

        var player = GetPlayer(playerName);
        if (player == null || !player.CanDoubleDown()) return;

        int betAmount = player.CurrentBets[player.ActiveHandIndex];
        player.Bank -= betAmount;
        player.CurrentBets[player.ActiveHandIndex] *= 2;
        player.HasDoubledDown = true;

        var newCard = CurrentTable.DrawCard();
        player.Hands[player.ActiveHandIndex].Add(newCard);

        var handInfo = player.GetHandInfo();
        string allCards = string.Join("", handInfo.Cards.Select(c => FormatCard(c)));
        SendDealerMessage($"{DN(playerName)} doubles down: {allCards} -> {handInfo.GetHandDescription()}");
        LogAction($"{playerName} doubled down: {newCard.GetCardDisplay()} -> total {handInfo.Score}");

        if (handInfo.IsBust)
        {
            SendDealerMessage($"{DN(playerName)} BUSTS after doubling with {handInfo.Score}!");
            LogAction($"{playerName} busts after doubling with {handInfo.Score}");
        }

        // Always advance after double down
        AdvanceToNextHandOrPlayer(playerName);
        OnUIUpdate?.Invoke();
    }

    public void PlayerSplit(string playerName)
    {
        if (!IsPlayerTurn(playerName)) return;

        var player = GetPlayer(playerName);
        if (player == null || !player.CanSplit()) return;

        var originalHand = player.Hands[player.ActiveHandIndex];
        int betAmount = player.CurrentBets[player.ActiveHandIndex];

        // Deduct additional bet
        player.Bank -= betAmount;

        // Create split hand
        var secondHand = new List<Models.Card> { originalHand[1] };
        originalHand.RemoveAt(1);

        // Add new cards to both hands
        var newCard1 = CurrentTable.DrawCard();
        var newCard2 = CurrentTable.DrawCard();
        originalHand.Add(newCard1);
        secondHand.Add(newCard2);

        // Add the new hand and bet
        player.Hands.Add(secondHand);
        player.CurrentBets.Add(betAmount);

        SendDealerMessage($"{DN(playerName)} splits! Now playing {player.Hands.Count} hands.");

        // Show the new first hand
        var handInfo = player.GetHandInfo(0);
        string handDisplay = string.Join("", handInfo.Cards.Select(c => FormatCard(c)));
        SendDealerMessage($"{DN(playerName)}, hand 1: {handDisplay} ({handInfo.GetHandDescription()})");

        LogAction($"{playerName} split into {player.Hands.Count} hands");

        // Special rule: If splitting aces, only one card per hand and auto-stand
        if (originalHand[0].IsAce)
        {
            SendDealerMessage($" Split aces rule: One card each, auto-stand.");
            LogAction($"{playerName} split aces - auto-stand rule applied");
            AdvanceToNextHandOrPlayer(playerName);
        }
        else
        {
            // Build available commands for the first hand
            var availableCommands = new List<string> { ">HIT", ">STAND" };

            if (player.CanDoubleDown())
                availableCommands.Add(">DOUBLE");

            string commandsText = string.Join(" or ", availableCommands);
            SendDealerMessage($"{DN(playerName)}, playing hand 1. Type {commandsText}");
        }

        OnUIUpdate?.Invoke();
    }

    private void AdvanceToNextHandOrPlayer(string playerName)
    {
        var player = GetPlayer(playerName);
        if (player == null) return;

        // Check if player has more hands to play
        if (player.ActiveHandIndex + 1 < player.Hands.Count)
        {
            player.ActiveHandIndex++;
            player.HasDoubledDown = false; // reset per-hand flag
            CurrentTable.TurnTimeRemaining = CurrentTable.TurnTimeLimit;
            CurrentTable.TurnStartTime = DateTime.Now;
            CurrentTable.TimerWarningShown = false;
            CurrentTable.TimerTimeoutShown = false;

            // Announce the next hand with cards
            var handInfo = player.GetHandInfo();
            string handDisplay = string.Join("", handInfo.Cards.Select(c => FormatCard(c)));
            SendDealerMessage($"{DN(playerName)}, hand {player.ActiveHandIndex + 1} of {player.Hands.Count}: {handDisplay} ({handInfo.GetHandDescription()})");

            // Build available commands for this hand
            var availableCommands = new List<string> { ">HIT", ">STAND" };

            if (player.CanDoubleDown())
                availableCommands.Add(">DOUBLE");

            string commandsText = string.Join(" or ", availableCommands);
            SendDealerMessage($"{DN(playerName)}, playing hand {player.ActiveHandIndex + 1}. Type {commandsText}");
            LogAction($"{playerName} advancing to hand {player.ActiveHandIndex + 1}");
        }
        else
        {
            // Player finished all hands — only announce if they split
            player.IsStanding = true;
            if (player.Hands.Count > 1)
                SendDealerMessage($"{DN(playerName)} finished playing all hands");
            AdvanceToNextTurn(playerName);
        }
    }

    private bool IsPlayerTurn(string playerName)
    {
        if (CurrentTable.CurrentTurnIndex >= CurrentTable.TurnOrder.Count) return false;
        return CurrentTable.TurnOrder[CurrentTable.CurrentTurnIndex].Equals(playerName, StringComparison.OrdinalIgnoreCase);
    }

    private void AdvanceToNextTurn(string playerName)
    {
        CurrentTable.CurrentTurnIndex++;
        CurrentTable.TimerWarningShown = false;
        CurrentTable.TimerTimeoutShown = false;

        if (CurrentTable.CurrentTurnIndex >= CurrentTable.TurnOrder.Count)
        {
            // All players done - dealer plays
            SendDealerMessage($"All players have finished. Dealer's turn.");
            DealerPlay();
        }
        else
        {
            CurrentTable.TurnTimeRemaining = CurrentTable.TurnTimeLimit;
            CurrentTable.TurnStartTime = DateTime.Now;
            CurrentTable.TimerWarningShown = false; // Reset warning flag

            StartPlayerTurn(CurrentTable.CurrentTurnIndex);
        }
    }

    private void DealerPlay()
    {
        CurrentTable.HoleCardRevealed = true;

        // Show dealer cards with proper formatting
        string dealerCards = string.Join("", CurrentTable.DealerHand.Select(c => FormatCard(c)));
        SendDealerMessage($"Dealer has: {dealerCards} ({CurrentTable.GetDealerScore()})");
        LogAction($"Dealer reveals: {CurrentTable.GetDealerHandDisplay()} = {CurrentTable.GetDealerScore()}");

        // Handle insurance payouts
        if (CurrentTable.DealerHasBlackjack)
        {
            HandleInsurancePayouts();
        }

        // Dealer hits according to rules
        while (CurrentTable.ShouldDealerHit())
        {
            var newCard = CurrentTable.DrawCard();
            CurrentTable.DealerHand.Add(newCard);

            SendDealerMessage($"Dealer hits: {FormatCard(newCard)} -> Total: {CurrentTable.GetDealerScore()}");
            LogAction($"Dealer hits: {newCard.GetCardDisplay()} -> total {CurrentTable.GetDealerScore()}");
        }

        if (CurrentTable.GetDealerScore() > 21)
        {
            SendDealerMessage($"Dealer BUSTS with {CurrentTable.GetDealerScore()}!");
            LogAction($"Dealer busts with {CurrentTable.GetDealerScore()}");
        }
        else
        {
            SendDealerMessage($"Dealer stands with {CurrentTable.GetDealerScore()}");
            LogAction($"Dealer stands with {CurrentTable.GetDealerScore()}");
        }

        ResolvePayouts();
    }

    private void HandleInsurancePayouts()
    {
        foreach (var player in CurrentTable.Players.Values.Where(p => p.HasInsurance))
        {
            if (CurrentTable.DealerHasBlackjack)
            {
                int payout = player.InsuranceBet * 2; // Insurance pays 2:1
                player.Bank += payout;
                SendDealerMessage($"{DN(player.Name)} insurance pays {payout}!");
                LogAction($"{player.Name} insurance won {payout}");
            }
            else
            {
                SendDealerMessage($"{DN(player.Name)} loses insurance bet");
                LogAction($"{player.Name} insurance lost {player.InsuranceBet}");
            }
        }
    }

    private void ResolvePayouts()
    {
        int dealerScore = CurrentTable.GetDealerScore();
        bool dealerBust = dealerScore > 21;

        SendDealerMessage("FINAL RESULTS");
        LogAction("Round ended - calculating results");

        foreach (var player in CurrentTable.Players.Values)
        {
            if (player.IsAfk || player.Hands.Count == 0) continue;

            // Resolve all hands and collect results BEFORE sending anything
            var handResults = new List<string>();
            int bankBefore = player.PreDealBank; // bank before bets were deducted at deal

            for (int handIndex = 0; handIndex < player.Hands.Count; handIndex++)
            {
                var handInfo = player.GetHandInfo(handIndex);
                int bet = player.CurrentBets[handIndex];
                string result;
                int winAmount = 0;

                if (handInfo.IsBust)
                {
                    result = "BUST";
                }
                else if (handInfo.IsBlackjack && CurrentTable.DealerHasBlackjack)
                {
                    result = "PUSH";
                    player.Bank += bet;
                }
                else if (handInfo.IsBlackjack)
                {
                    result = "BLACKJACK";
                    winAmount = (int)(bet * 1.5);
                    player.Bank += bet + winAmount;
                }
                else if (CurrentTable.DealerHasBlackjack)
                {
                    result = "LOSE";
                }
                else if (dealerBust || handInfo.Score > dealerScore)
                {
                    result = "WIN";
                    winAmount = bet;
                    player.Bank += bet + winAmount;
                }
                else if (handInfo.Score == dealerScore)
                {
                    result = "PUSH";
                    player.Bank += bet;
                }
                else
                {
                    result = "LOSE";
                }

                player.AddBetResult(new Models.BetResult
                {
                    BetAmount = bet,
                    Result = result,
                    AmountWon = winAmount,
                    AmountLost = result is "LOSE" or "BUST" ? bet : 0,
                    HandDescription = handInfo.GetHandDescription()
                });

                string handLabel = player.Hands.Count > 1 ? $"H{handIndex + 1}:" : string.Empty;

                string part = result switch
                {
                    "BLACKJACK" => $"{handLabel}BLACKJACK +{winAmount}\uE049",
                    "WIN"       => $"{handLabel}WIN +{winAmount}\uE049",
                    "PUSH"      => $"{handLabel}PUSH",
                    "BUST"      => $"{handLabel}BUST -{bet}\uE049",
                    _           => $"{handLabel}LOSE -{bet}\uE049"
                };
                handResults.Add(part);

                LogAction($"{player.Name} H{handIndex+1}: {result} bet={bet} won={winAmount} bank={player.Bank}");
            }

            // One message per player: all hands + final bank
            int bankNow = player.Bank;
            int net = bankNow - bankBefore;
            string handsStr = string.Join(" | ", handResults);
            SendDealerMessage($"{DN(player.Name)}: {handsStr} | Bank: {bankNow}\uE049");
        }

        CurrentTable.GameState = Models.GameState.Lobby;
        LogAction("Round complete - reset to lobby");
        OnUIUpdate?.Invoke();
    }

    private void LogAction(string action)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        CurrentTable.GameLog.Add($"[{timestamp}] {action}");
    }

    private string FormatCard(Models.Card card)
    {
        return $"【{card.GetCardDisplay()}】";
    }

    public Models.Player? GetPlayer(string name)
    {
        return CurrentTable.Players.Values.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public void AddPlayer(string name)
    {
        string nameUpper = name.ToUpper();

        // Auto-detect server if included
        string server = "Ultros"; // Default to Ultros instead of Local
        if (name.Contains("@"))
        {
            var parts = name.Split('@');
            name = parts[0];
            server = parts[1];
        }

        if (!CurrentTable.Players.ContainsKey(nameUpper))
        {
            var player = new Models.Player(name, server);
            player.PersistentBet = CurrentTable.MinBet;
            CurrentTable.Players.Add(nameUpper, player);
            LogAction($"Player added: {name} with bet {CurrentTable.MinBet}");

            if (CurrentTable.AnnounceNewPlayers)
                SendChatMessage($"{name} has been added to the table.");
        }
    }

    public void RemovePlayer(string name)
    {
        string nameUpper = name.ToUpper();
        var wasInGame = CurrentTable.Players.ContainsKey(nameUpper);
        CurrentTable.Players.Remove(nameUpper);
        LogAction($"Player removed: {name}");

        // Show message if removed during lobby
        if (CurrentTable.GameState == Models.GameState.Lobby)
        {
            SendChatMessage($"{name} was removed from the table");
        }

        // If player was mid-turn during active game, advance turn
        int _turnIdx = CurrentTable.TurnOrder.FindIndex(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (CurrentTable.GameState == Models.GameState.Playing && _turnIdx >= 0)
        {
            int playerIndex = _turnIdx;
            CurrentTable.TurnOrder.RemoveAt(playerIndex);

            // If it was current player's turn, advance
            if (playerIndex == CurrentTable.CurrentTurnIndex)
            {
                SendDealerMessage($"{name} was kicked from the game");

                // Adjust current turn index if needed
                if (CurrentTable.CurrentTurnIndex >= CurrentTable.TurnOrder.Count)
                {
                    // Last player was kicked, dealer's turn
                    SendDealerMessage($"All players have finished. Dealer's turn.");
                    DealerPlay();
                }
                else
                {
                    // Start next player's turn
                    StartPlayerTurn(CurrentTable.CurrentTurnIndex);
                }
            }
            else if (playerIndex < CurrentTable.CurrentTurnIndex)
            {
                // Adjust current turn index since a player before current was removed
                CurrentTable.CurrentTurnIndex--;
            }
        }
    }

    public void SetPlayerBank(string name, int amount)
    {
        var player = GetPlayer(name);
        if (player != null)
        {
            int oldBank = player.Bank;
            player.Bank = amount;
            LogAction($"Set {name} bank to {amount} (was {oldBank})");
        }
    }

    public void AddPlayerBank(string name, int amount)
    {
        var player = GetPlayer(name);
        if (player != null)
        {
            int oldBank = player.Bank;
            player.Bank += amount;
            LogAction($"Added {amount} to {name} bank (was {oldBank}, now {player.Bank})");
        }
    }

    public void SetPlayerBet(string name, int amount, SamplePlugin.Chat.ChatChannel? responseChannel = null)
    {
        LogAction($"SetPlayerBet called: {name}, {amount}, responseChannel={responseChannel}");

        if (CurrentTable.GameState != Models.GameState.Lobby) return;

        var player = GetPlayer(name);
        if (player != null && amount >= CurrentTable.MinBet && amount <= CurrentTable.MaxBet && amount <= player.Bank)
        {
            int oldBet = player.PersistentBet;
            player.PersistentBet = amount;
            player.IsAfk = false;
            LogAction($"{name} set bet to {amount} (was {oldBet})");

            // Send response in the same chat channel the command came from
            if (responseChannel.HasValue)
            {
                string chatCmd = responseChannel.Value == SamplePlugin.Chat.ChatChannel.Party ? "/party " : "/say ";
                LogAction($"Sending bet response via {responseChannel.Value}: {chatCmd}{name} bet updated to {amount}");
                OnChatMessage?.Invoke($"{chatCmd}{name} bet updated to {amount}");
            }
            else
            {
                LogAction($"Using default SendChatMessage for bet response: {name} bet updated to {amount}");
                SendChatMessage($"{name} bet updated to {amount}");
            }

            OnUIUpdate?.Invoke();
        }
        else if (player != null)
        {
            string reason = "";
            if (amount < CurrentTable.MinBet) reason = $"minimum bet is {CurrentTable.MinBet}";
            else if (amount > CurrentTable.MaxBet) reason = $"maximum bet is {CurrentTable.MaxBet}";
            else if (amount > player.Bank) reason = $"insufficient funds (have {player.Bank})";

            // Send error response in the same chat channel
            if (responseChannel.HasValue)
            {
                string chatCmd = responseChannel.Value == SamplePlugin.Chat.ChatChannel.Party ? "/party " : "/say ";
                OnChatMessage?.Invoke($"{chatCmd}{name} bet update failed: {reason}");
            }
            else
            {
                SendChatMessage($"{name} bet update failed: {reason}");
            }
        }
    }

    // Keep backwards compatibility
    public void SetPlayerBet(string name, int amount, Models.ChatMode? responseChatMode = null)
    {
        var channel = responseChatMode == Models.ChatMode.Party ? SamplePlugin.Chat.ChatChannel.Party : SamplePlugin.Chat.ChatChannel.Say;
        SetPlayerBet(name, amount, channel);
    }

    public void SetPlayerBet(string name, int amount)
    {
        SetPlayerBet(name, amount, (SamplePlugin.Chat.ChatChannel?)null);
    }

    public void ToggleAFK(string name)
    {
        var player = GetPlayer(name);
        if (player != null)
        {
            player.IsAfk = !player.IsAfk;
            LogAction($"Toggled {name} AFK to {player.IsAfk}");
            SendChatMessage(player.IsAfk ? $"{name} is now AFK." : $"{name} is back.");
        }
    }

    public void ForceStop()
    {
        DealerMessageQueue.Clear();
        SendDealerMessage("Game force stopped by dealer. All bets refunded.");
        LogAction("Game force stopped - refunding all bets");

        foreach (var player in CurrentTable.Players.Values)
        {
            int refund = player.CurrentBets.Sum() + player.InsuranceBet;
            if (refund > 0)
            {
                player.Bank += refund;
                SendDealerMessage($"{player.Name}: {refund}\uE049 refunded \u2192 Bank: {player.Bank}\uE049");
            }
            player.Hands.Clear();
            player.CurrentBets.Clear();
            player.IsStanding = false;
            player.HasDoubledDown = false;
            player.HasInsurance = false;
            player.InsuranceBet = 0;
        }

        CurrentTable.DealerHand.Clear();
        CurrentTable.TurnOrder.Clear();
        CurrentTable.CurrentTurnIndex = 0;
        CurrentTable.HoleCardRevealed = false;
        CurrentTable.GameState = Models.GameState.Lobby;
        LogAction("Game force stopped - state reset to Lobby");
        OnUIUpdate?.Invoke();
    }

    public void SaveState()
    {
        // For now, simplified - would need proper deep clone
        StateHistory.Push(CurrentTable);
    }

    public void Undo()
    {
        if (StateHistory.Count > 0)
        {
            CurrentTable = StateHistory.Pop();
            OnUIUpdate?.Invoke();
            LogAction("Game state undone");
        }
    }
}
