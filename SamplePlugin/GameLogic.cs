using System;
using System.Collections.Generic;
using System.Linq;

namespace SamplePlugin
{
    public enum Suit { Hearts, Diamonds, Clubs, Spades }
    public enum Rank { Two = 2, Three, Four, Five, Six, Seven, Eight, Nine, Ten, Jack, Queen, King, Ace }
    public enum GameState { Betting, Playing, DealerTurn, GameOver }
    public enum OperatingMode { Auto, Manual }  // Auto: normal play, Manual: dealer controls everything

    public class Card
    {
        public Suit Suit { get; }
        public Rank Rank { get; }
        public int Value => Rank >= Rank.Jack && Rank <= Rank.King ? 10 : (Rank == Rank.Ace ? 11 : (int)Rank);

        public Card(Suit suit, Rank rank)
        {
            Suit = suit;
            Rank = rank;
        }

        // Unicode card suit symbols
        private string GetSuitSymbol()
        {
            return Suit switch
            {
                Suit.Hearts => "♥",
                Suit.Diamonds => "♦",
                Suit.Clubs => "♣",
                Suit.Spades => "♠",
                _ => "?"
            };
        }

        // Get short rank display (2-10, J, Q, K, A)
        private string GetRankSymbol()
        {
            return Rank switch
            {
                Rank.Ten => "10",
                Rank.Jack => "J",
                Rank.Queen => "Q",
                Rank.King => "K",
                Rank.Ace => "A",
                _ => ((int)Rank).ToString()
            };
        }

        // Unicode format: [♥J]
        public override string ToString() => $"[{GetSuitSymbol()}{GetRankSymbol()}]";
    }

    public class Hand
    {
        public List<Card> Cards { get; } = new();

        public void Add(Card card) => Cards.Add(card);
        public void Clear() => Cards.Clear();

        public int GetTotal()
        {
            int total = 0, aces = 0;
            foreach (var c in Cards)
            {
                total += c.Value;
                if (c.Rank == Rank.Ace) aces++;
            }
            while (total > 21 && aces > 0)
            {
                total -= 10;
                aces--;
            }
            return total;
        }

        public bool IsBlackjack => Cards.Count == 2 && GetTotal() == 21;
    }

    public class Deck
    {
        private List<Card> cards = new();
        private Random rng = new Random();

        public Deck() { Reset(); }

        public void Reset()
        {
            cards.Clear();
            foreach (Suit s in Enum.GetValues(typeof(Suit)))
                foreach (Rank r in Enum.GetValues(typeof(Rank)))
                    cards.Add(new Card(s, r));
            Shuffle();
        }

        public void Shuffle()
        {
            int n = cards.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                (cards[k], cards[n]) = (cards[n], cards[k]);
            }
        }

        public Card Draw()
        {
            if (cards.Count == 0) Reset();
            var c = cards[0];
            cards.RemoveAt(0);
            return c;
        }
    }

    public class PlayerSession
    {
        public string Name { get; set; } = string.Empty;
        public Hand Hand { get; set; } = new();
        public bool IsStanding { get; set; }
        public bool IsBust => Hand.GetTotal() > 21;
        public decimal Bank { get; set; } = 10000m;  // Starting bank amount
        public bool IsAFK { get; set; } = false;  // AFK status
        public DateTime TurnStartTime { get; set; } = DateTime.Now;  // When their turn started
        public decimal CurrentBet { get; set; } = 0m;  // Current bet amount
        public bool HasDoubledDown { get; set; } = false;  // Has player doubled down
        public bool HasSplit { get; set; } = false;  // Has player split
        public string Server { get; set; } = "Ultros";  // Server name
    }

    public class CasinoBlackjackManager
    {
        public Deck Deck { get; } = new();
        public Hand DealerHand { get; } = new();
        public List<PlayerSession> Players { get; } = new();
        public GameState State { get; private set; } = GameState.Betting;

        // Admin & Operating Mode
        public string AdminName { get; set; } = string.Empty;  // Admin player name
        public OperatingMode Mode { get; set; } = OperatingMode.Auto;  // Auto or Manual mode
        public bool IsAdminOnline { get; set; }  // True if admin player is currently online

        // Table Limits
        public decimal MinBet { get; set; } = 10m;  // Minimum bet
        public decimal MaxBet { get; set; } = 1000m;  // Maximum bet

        // Turn Timer (in seconds)
        public int TurnTimerSeconds { get; set; } = 60;  // Default 60 seconds
        public string? CurrentPlayerTurn { get; set; }  // Name of player whose turn it is
        public int CurrentPlayerIndex { get; set; } = -1;  // Index of current player
        public DateTime CurrentTurnStart { get; set; } = DateTime.Now;  // When current turn started

        // Game Log
        public List<string> GameLog { get; } = new();  // Log of all game actions

        // Callback for chat messages
        public Action<string>? OnChatMessage { get; set; }

        // Callback for admin messages (sent silently)
        public Action<string, string>? OnAdminMessage { get; set; }  // (adminName, message)

        // Callback to trigger automatic dealer play
        public Action? OnAllPlayersStanding { get; set; }

        // Callback for UI updates
        public Action? OnUIUpdate { get; set; }  // Called when GUI needs refresh

        private void LogAction(string action)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            GameLog.Add($"[{timestamp}] {action}");
        }

        public void OpenTable()
        {
            State = GameState.Betting;
            Players.Clear();
            DealerHand.Clear();
            Deck.Reset();
            GameLog.Clear();
            LogAction("Table opened");
            OnChatMessage?.Invoke("[BLACKJACK] TABLE NOW OPEN");
        }

        public void AddPlayer(string name)
        {
            if (Players.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
            Players.Add(new PlayerSession { Name = name, Server = "Ultros" });
            LogAction($"Player added: {name}");
            OnChatMessage?.Invoke($" {name} has been added to the table! ({Players.Count} player{(Players.Count != 1 ? "s" : "")})");
        }

        public int GetTimeRemainingSeconds()
        {
            int elapsed = (int)(DateTime.Now - CurrentTurnStart).TotalSeconds;
            int remaining = TurnTimerSeconds - elapsed;
            return remaining > 0 ? remaining : 0;
        }

        private bool timerWarningShown = false;

        public void CheckTimerWarning()
        {
            if (State != GameState.Playing || string.IsNullOrEmpty(CurrentPlayerTurn)) return;

            int remaining = GetTimeRemainingSeconds();
            int warningThreshold = TurnTimerSeconds / 3;

            // Show warning at 1/3 time
            if (remaining <= warningThreshold && !timerWarningShown)
            {
                timerWarningShown = true;
                OnChatMessage?.Invoke($"⏰ WARNING: {CurrentPlayerTurn} has {remaining}s remaining!");
            }

            // Auto-stand when time runs out
            if (remaining <= 0 && !string.IsNullOrEmpty(CurrentPlayerTurn))
            {
                var player = Players.FirstOrDefault(p => p.Name.Equals(CurrentPlayerTurn, StringComparison.OrdinalIgnoreCase));
                if (player != null && !player.IsStanding)
                {
                    player.IsStanding = true;
                    player.IsAFK = true;
                    OnChatMessage?.Invoke($"⏰ {CurrentPlayerTurn}'s time ran out! Standing and marked AFK.");
                    NextPlayerTurn();
                }
            }
        }

        public void NextPlayerTurn()
        {
            timerWarningShown = false;  // Reset warning flag for new turn
            CurrentPlayerIndex++;
            if (CurrentPlayerIndex >= Players.Count)
            {
                // All players have acted
                State = GameState.DealerTurn;
                CurrentPlayerTurn = null;
                OnChatMessage?.Invoke(" All players have acted - Dealer's turn!");
                OnAllPlayersStanding?.Invoke();
            }
            else
            {
                var player = Players[CurrentPlayerIndex];

                // Check if AFK - skip turn
                if (player.IsAFK)
                {
                    OnChatMessage?.Invoke($"  {player.Name} is AFK - skipped");
                    NextPlayerTurn();  // Recursively move to next
                    return;
                }

                // Check for blackjack - skip turn
                if (player.Hand.IsBlackjack)
                {
                    OnChatMessage?.Invoke($" {player.Name} has BLACKJACK! Skipping turn.");
                    player.IsStanding = true;
                    NextPlayerTurn();  // Recursively move to next
                    return;
                }

                CurrentPlayerTurn = player.Name;
                CurrentTurnStart = DateTime.Now;
                OnChatMessage?.Invoke($" {player.Name}'s turn! (Timer: {TurnTimerSeconds}s)");
                OnChatMessage?.Invoke($"  {player.Name}: {string.Join(", ", player.Hand.Cards)} (Total: {player.Hand.GetTotal()})");
                OnUIUpdate?.Invoke();
            }
        }

        public void StartTurns()
        {
            CurrentPlayerIndex = -1;
            State = GameState.Playing;
            NextPlayerTurn();
        }
        public void RemovePlayer(string name)
        {
            var removed = Players.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                OnChatMessage?.Invoke($" {name} has left the table.");
            }
        }

        public void SetPlayerBank(string name, decimal amount)
        {
            var p = Players.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p != null)
            {
                p.Bank = amount;
                OnChatMessage?.Invoke($"[Admin] {name}'s bank set to {amount}");
            }
        }

        public void AddPlayerBank(string name, decimal amount)
        {
            var p = Players.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p != null)
            {
                p.Bank += amount;
                OnChatMessage?.Invoke($"[Admin] Added {amount} to {name}'s bank (Total: {p.Bank})");
            }
        }

        public void SetPlayerBet(string name, decimal amount)
        {
            var p = Players.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p != null)
            {
                if (amount < MinBet || amount > MaxBet)
                {
                    OnChatMessage?.Invoke($" {name}: Bet must be between {MinBet} and {MaxBet}");
                }
                else if (amount > p.Bank)
                {
                    OnChatMessage?.Invoke($" {name}: Insufficient funds! Bank: {p.Bank}, Bet: {amount}");
                }
                else
                {
                    p.CurrentBet = amount;
                    OnChatMessage?.Invoke($" {name} bets {amount}");
                }
            }
        }

        public void SetPlayerAFK(string name, bool isAFK)
        {
            var p = Players.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p != null)
            {
                p.IsAFK = isAFK;
                OnChatMessage?.Invoke($" {name} is now {(isAFK ? "AFK" : "ACTIVE")}");
            }
        }

        public void SetPlayerName(string oldName, string newName)
        {
            var p = Players.FirstOrDefault(x => x.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            if (p != null)
            {
                p.Name = newName;
                OnChatMessage?.Invoke($"[Admin] Player name changed to {newName}");
            }
        }

        public void SetPlayerServer(string name, string server)
        {
            var p = Players.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p != null)
            {
                p.Server = server;
                OnChatMessage?.Invoke($"[Admin] {name}'s server set to {server}");
            }
        }

        public void SetTableLimits(decimal minBet, decimal maxBet)
        {
            MinBet = minBet;
            MaxBet = maxBet;
            OnChatMessage?.Invoke($"[Admin] Table limits set: Min {minBet}, Max {maxBet}");
        }

        public void SetTurnTimer(int seconds)
        {
            TurnTimerSeconds = seconds;
            OnChatMessage?.Invoke($"[Admin] Turn timer set to {seconds} seconds");
        }

        public string GetGameStatus()
        {
            var playerInfo = Players.Select(p => $"{p.Name}(Bank:{p.Bank},Bet:{p.CurrentBet})").ToList();
            return $" STATUS: Limits: {MinBet}-{MaxBet} | Timer: {TurnTimerSeconds}s | Players: {string.Join(" | ", playerInfo)}";
        }

        public string GetRulesText()
        {
            return @"[BLACKJACK] RULES:
• Goal: Beat dealer's hand without going over 21
• Card Values: 2-10 = face value, J/Q/K = 10, A = 1 or 11
• Blackjack: First 2 cards = 21 (auto-win)
• Hit: Take another card
• Stand: Stop taking cards
• Double Down: Double bet, get 1 card, auto-stand
• Split: Split matching pair, double bet
• Dealer: Hits on 16, stands on 17+
• Winning: Player > Dealer OR Dealer busts = WIN
• Busting: Going over 21 = LOSE
• Push: Tie = No change to bank";
        }

        public string GetPlayerHelpText()
        {
            return @"[BLACKJACK] PLAYER COMMANDS:
> join - Join game
> bet [amount] - Place bet
> hit - Take card
> stand - Stop
> doubledown - Double bet, 1 card
> split - Split pair
> status - Check game info
> rules - Show rules
> help - Show this help
> afk - Toggle AFK status";
        }

        public string GetAdminHelpText()
        {
            return @"[BLACKJACK] ADMIN COMMANDS:
> admin set [name] - Set admin
> player add [name] - Add player
> player remove [name] - Remove player
> player afk [name] - Toggle player AFK
> mode [auto|manual] - Set mode
> bank set [name] [amount] - Set bank
> bank add [name] [amount] - Add to bank
> limits [min] [max] - Set bet limits
> timer [seconds] - Set timer
> status - Check game info
> rules - Show rules
> help - Show this help";
        }

        public void StartRound()
        {
            if (Players.Count == 0) return;
            Deck.Reset();
            DealerHand.Clear();

            OnChatMessage?.Invoke($" DEALING HANDS...");

            // Only deal to non-AFK players
            foreach (var p in Players)
            {
                if (p.IsAFK)
                {
                    OnChatMessage?.Invoke($"  {p.Name} is AFK - skipped");
                    continue;
                }

                p.Hand.Clear();
                p.IsStanding = false;
                p.HasDoubledDown = false;
                p.HasSplit = false;
                p.Hand.Add(Deck.Draw());
                p.Hand.Add(Deck.Draw());
                // Don't show hand here - wait for their turn
            }

            DealerHand.Add(Deck.Draw());
            DealerHand.Add(Deck.Draw());
            OnChatMessage?.Invoke($"  Dealer: [Hidden Card], {DealerHand.Cards[1]}");

            StartTurns();
        }

        public void HandleHit(string name)
        {
            if (State != GameState.Playing) return;
            if (!name.Equals(CurrentPlayerTurn, StringComparison.OrdinalIgnoreCase)) return;  // Not their turn

            var p = Players.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p != null && !p.IsStanding && !p.IsBust)
            {
                p.Hand.Add(Deck.Draw());
                OnChatMessage?.Invoke($"  {p.Name} hits: {string.Join(", ", p.Hand.Cards)} (Total: {p.Hand.GetTotal()})");
                OnUIUpdate?.Invoke();

                if (p.IsBust)
                {
                    p.IsStanding = true;
                    OnChatMessage?.Invoke($"    {p.Name} BUSTS!");
                    NextPlayerTurn();
                }
            }
        }

        public void HandleStand(string name)
        {
            if (State != GameState.Playing) return;
            if (!name.Equals(CurrentPlayerTurn, StringComparison.OrdinalIgnoreCase)) return;  // Not their turn

            var p = Players.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p != null)
            {
                p.IsStanding = true;
                OnChatMessage?.Invoke($"  {p.Name} stands with {p.Hand.GetTotal()}");
                OnUIUpdate?.Invoke();
                NextPlayerTurn();
            }
        }

        public void HandleDoubleDown(string name)
        {
            if (State != GameState.Playing) return;
            if (!name.Equals(CurrentPlayerTurn, StringComparison.OrdinalIgnoreCase)) return;  // Not their turn

            var p = Players.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p != null && !p.IsStanding && !p.IsBust && !p.HasDoubledDown && p.Hand.Cards.Count == 2)
            {
                p.HasDoubledDown = true;
                p.CurrentBet *= 2;  // Double the bet
                p.Hand.Add(Deck.Draw());
                OnChatMessage?.Invoke($"  {p.Name} doubles down: {string.Join(", ", p.Hand.Cards)} (Total: {p.Hand.GetTotal()})");
                OnUIUpdate?.Invoke();

                if (p.IsBust)
                {
                    p.IsStanding = true;
                    OnChatMessage?.Invoke($"    {p.Name} BUSTS!");
                }
                else
                {
                    p.IsStanding = true;  // Auto-stand after double down
                }
                NextPlayerTurn();
            }
        }

        public void HandleSplit(string name)
        {
            if (State != GameState.Playing) return;
            if (!name.Equals(CurrentPlayerTurn, StringComparison.OrdinalIgnoreCase)) return;  // Not their turn

            var p = Players.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p != null && !p.IsStanding && !p.IsBust && !p.HasSplit && p.Hand.Cards.Count == 2 && 
                p.Hand.Cards[0].Rank == p.Hand.Cards[1].Rank)
            {
                p.HasSplit = true;
                p.CurrentBet *= 2;  // Double the bet for split hand

                // Move second card to new hand (we'll use a simple approach - just track split happened)
                OnChatMessage?.Invoke($"  {p.Name} splits: New hand dealt");

                // Add a new card to the existing hand (one card remains, one new card added)
                p.Hand.Add(Deck.Draw());
                OnChatMessage?.Invoke($"  {p.Name} first hand: {string.Join(", ", p.Hand.Cards)} (Total: {p.Hand.GetTotal()})");
                OnUIUpdate?.Invoke();

                if (p.IsBust)
                {
                    p.IsStanding = true;
                    OnChatMessage?.Invoke($"    {p.Name} BUSTS!");
                }
                NextPlayerTurn();
            }
        }

        public void CheckAllStanding()
        {
            if (State == GameState.Playing && Players.All(p => p.IsStanding))
            {
                State = GameState.DealerTurn;
                OnChatMessage?.Invoke(" All players standing - Dealer automatically plays!");

                // Automatically trigger dealer play after a brief delay
                OnAllPlayersStanding?.Invoke();
            }
        }
        public void PlayDealer()
        {
            if (State != GameState.DealerTurn) return;

            // Reveal dealer's first card
            OnChatMessage?.Invoke($"  Dealer reveals: {string.Join(", ", DealerHand.Cards)} (Total: {DealerHand.GetTotal()})");

            while (DealerHand.GetTotal() < 17)
            {
                var newCard = Deck.Draw();
                DealerHand.Add(newCard);
                OnChatMessage?.Invoke($"  Dealer hits: {string.Join(", ", DealerHand.Cards)} (Total: {DealerHand.GetTotal()})");
            }

            if (DealerHand.GetTotal() > 21)
            {
                OnChatMessage?.Invoke($"  Dealer BUSTS at {DealerHand.GetTotal()}!");
            }
            else
            {
                OnChatMessage?.Invoke($"  Dealer stands with {DealerHand.GetTotal()}");
            }

            State = GameState.GameOver;
            DisplayResults();
        }

        private void DisplayResults()
        {
            OnChatMessage?.Invoke(" FINAL RESULTS");

            int dTotal = DealerHand.GetTotal();

            foreach (var p in Players)
            {
                // Skip AFK players - don't charge or pay them
                if (p.IsAFK)
                {
                    OnChatMessage?.Invoke($"  {p.Name}: AFK - not charged");
                    p.CurrentBet = 0;
                    continue;
                }

                int pTotal = p.Hand.GetTotal();
                string result = "PUSH";
                decimal payout = 0;
                decimal amountWon = 0;

                if (p.IsBust)
                {
                    result = "LOSE (BUST)";
                    p.Bank -= p.CurrentBet;
                }
                else if (dTotal > 21)
                {
                    result = "WIN (Dealer Busts)";
                    payout = p.CurrentBet * 2;  // Win: 2x bet
                    amountWon = payout - p.CurrentBet;
                    p.Bank += payout;
                }
                else if (pTotal > dTotal)
                {
                    result = "WIN";
                    payout = p.CurrentBet * 2;  // Win: 2x bet
                    amountWon = payout - p.CurrentBet;  // Profit
                    p.Bank += payout;
                }
                else if (dTotal > pTotal)
                {
                    result = "LOSE";
                    p.Bank -= p.CurrentBet;
                }
                else
                {
                    result = "PUSH";
                    // No change to bank on push
                }

                if (result == "WIN")
                {
                    OnChatMessage?.Invoke($"  {p.Name}: {result} - Won {amountWon}! (Bank: {p.Bank})");
                }
                else
                {
                    OnChatMessage?.Invoke($"  {p.Name}: {result} (Bank: {p.Bank})");
                }
                // Keep bet for next round - don't reset to 0
            }

            OnUIUpdate?.Invoke();
            OnChatMessage?.Invoke(" Round complete!");
        }
    }
}

