using System;
using System.Collections.Generic;
using System.Linq;
using ChatCasino.Models;
using ChatCasino.Services;
using ChatCasino.UI;

namespace ChatCasino.Engine;

public sealed class BaccaratModule : BaseEngine
{
    private readonly IBankService bank;
    private readonly DeckShoe<Card> shoe;
    private List<Card> lastPlayerHand = new();
    private List<Card> lastBankerHand = new();
    private string lastWinner = string.Empty;

    public BaccaratModule(IMessageService msg, IDeckService decks, IPlayerService players, IBankService bank)
        : base(GameType.Baccarat, msg, decks, players)
    {
        this.bank = bank;
        shoe = decks.GetStandardDeck(8, shuffled: true);
        StatusText = "Waiting for bets";
    }

    public override CmdResult Execute(string playerName, string cmd, string[] args)
    {
        var p = Players.GetPlayer(playerName);
        if (p is null) return CmdResult.Fail("Player not found.");

        cmd = cmd.ToUpperInvariant();
        return cmd switch
        {
            "BET" => PlaceBet(p, args),
            "DEAL" => Deal(),
            _ => CmdResult.Ok(string.Empty)
        };
    }

    public override IEnumerable<string> GetValidCommands() => ["BET", "DEAL"];

    public override ICasinoViewModel GetViewModel()
    {
        var seats = new List<PlayerSlotViewModel>
        {
            new()
            {
                PlayerName = "Player Hand",
                IsDealer = true,
                Cards = lastPlayerHand.Select(c => c.GetCardDisplay()).ToList(),
                ResultText = lastPlayerHand.Count == 0 ? string.Empty : $"Score {Score(lastPlayerHand)}"
            },
            new()
            {
                PlayerName = "Banker Hand",
                IsDealer = true,
                Cards = lastBankerHand.Select(c => c.GetCardDisplay()).ToList(),
                ResultText = lastBankerHand.Count == 0 ? string.Empty : $"Score {Score(lastBankerHand)}"
            }
        };

        seats.AddRange(Players.GetAllActivePlayers().Select(p =>
        {
            var bt = p.Metadata.TryGetValue("Baccarat.BetType", out var t) ? t as string : string.Empty;
            var ba = p.Metadata.TryGetValue("Baccarat.BetAmount", out var a) && a is int i ? i : 0;
            return new PlayerSlotViewModel
            {
                PlayerName = p.Name,
                Bank = p.CurrentBank,
                BetAmount = ba,
                ResultText = string.IsNullOrWhiteSpace(bt) ? string.Empty : $"{bt} {ba}\uE049"
            };
        }));

        return new BaccaratViewModel
        {
            GameTitle = "Baccarat",
            GameStatus = string.IsNullOrWhiteSpace(lastWinner) ? StatusText : $"{StatusText} | Winner: {lastWinner}",
            Seats = seats,
            Actions = GetValidCommands().ToList()
        };
    }

    private CmdResult PlaceBet(Player player, string[] args)
    {
        // Support reversed order: BET PLAYER 100 as well as BET 100 PLAYER
        if (args.Length >= 2 && !int.TryParse(args[0], out _) && int.TryParse(args[^1], out _))
            args = [args[^1], .. args[..^1]];

        if (args.Length < 2 || !int.TryParse(args[0], out var amount) || amount <= 0)
            return CmdResult.Fail("Usage: BET [amount] [PLAYER|BANKER|TIE]");

        if (amount < CasinoUI.GlobalMinBet || amount > CasinoUI.GlobalMaxBet)
            return CmdResult.Fail($"Bet must be between {CasinoUI.GlobalMinBet} and {CasinoUI.GlobalMaxBet}. ");

        var target = args[1].ToUpperInvariant();
        if (target is not ("PLAYER" or "BANKER" or "TIE"))
            return CmdResult.Fail("Target must be PLAYER, BANKER, or TIE.");

        if (player.Metadata.TryGetValue("Baccarat.BetAmount", out var existingBet) && existingBet is int eb && eb > 0)
            return CmdResult.Fail("You already have a bet placed. One bet per player per round.");

        if (bank.Deduct(player, amount, $"Baccarat bet {target}") != TransactionResult.Success)
            return CmdResult.Fail("Insufficient funds.");

        player.Metadata["Baccarat.BetType"] = target;
        player.Metadata["Baccarat.BetAmount"] = amount;
        StatusText = "Bets placed";
        Msg.QueuePartyMessage($"[BACCARAT] {player.Name} bets {StandardizedFormatting.FormatCurrency(amount)} on {target}");
        return CmdResult.Ok("Bet accepted.");
    }

    private CmdResult Deal()
    {
        var playerHand = new List<Card> { shoe.Draw(), shoe.Draw() };
        var bankerHand = new List<Card> { shoe.Draw(), shoe.Draw() };

        var pScore = Score(playerHand);
        var bScore = Score(bankerHand);

        var natural = pScore >= 8 || bScore >= 8;
        if (!natural)
        {
            if (pScore <= 5)
            {
                playerHand.Add(shoe.Draw());
                pScore = Score(playerHand);
            }

            if (ShouldBankerDraw(bScore, playerHand.Count == 3 ? BaccaratValue(playerHand[^1]) : null))
            {
                bankerHand.Add(shoe.Draw());
                bScore = Score(bankerHand);
            }
        }

        var winner = pScore == bScore ? "TIE" : (pScore > bScore ? "PLAYER" : "BANKER");

        lastPlayerHand = playerHand;
        lastBankerHand = bankerHand;
        lastWinner = winner;

        ResolveBets(winner);

        StatusText = "Round complete";

        Msg.QueuePartyMessage($"[BACCARAT] Player hand: {string.Join(" ", playerHand.Select(c => c.GetCardDisplay()))} (Score: {pScore})");
        Msg.QueuePartyMessage($"[BACCARAT] Banker hand: {string.Join(" ", bankerHand.Select(c => c.GetCardDisplay()))} (Score: {bScore})");
        Msg.QueuePartyMessage($"[BACCARAT] Winner: {winner}!");

        var reveal = $"[BACCARAT REVEAL] Player: {string.Join(" ", playerHand.Select(c => c.GetCardDisplay()))} ({pScore}) | Banker: {string.Join(" ", bankerHand.Select(c => c.GetCardDisplay()))} ({bScore}) | Winner: {winner}";
        Msg.QueuePartyMessage(reveal);

        OnRoundComplete();
        return CmdResult.Ok("Deal resolved.");
    }

    private void ResolveBets(string winner)
    {
        foreach (var player in Players.GetAllActivePlayers())
        {
            if (!player.Metadata.TryGetValue("Baccarat.BetType", out var tObj) || tObj is not string type) continue;
            if (!player.Metadata.TryGetValue("Baccarat.BetAmount", out var aObj) || aObj is not int amount) continue;

            if (type == winner)
            {
                if (winner == "PLAYER")
                {
                    bank.Award(player, amount * 2, "Baccarat Player win");
                }
                else if (winner == "BANKER")
                {
                    var payout = CasinoUI.BaccaratCommissionEnabled
                        ? amount + amount + (int)Math.Floor(amount * -0.05)
                        : amount * 2;
                    var reason = CasinoUI.BaccaratCommissionEnabled
                        ? "Baccarat Banker win (5% commission)"
                        : "Baccarat Banker win";
                    bank.Award(player, payout, reason);
                }
                else
                {
                    bank.Award(player, amount * 9, "Baccarat Tie win");
                }
            }

            player.Metadata.Remove("Baccarat.BetType");
            player.Metadata.Remove("Baccarat.BetAmount");
        }
    }

    private static int Score(List<Card> hand)
        => hand.Sum(BaccaratValue) % 10;

    private static int BaccaratValue(Card c)
        => c.Value switch
        {
            "A" => 1,
            "10" or "J" or "Q" or "K" => 0,
            _ => int.TryParse(c.Value, out var n) ? n : 0
        };

    private static bool ShouldBankerDraw(int bankerScore, int? playerThird)
    {
        if (playerThird is null)
            return bankerScore <= 5;

        return bankerScore switch
        {
            <= 2 => true,
            3 => playerThird != 8,
            4 => playerThird is >= 2 and <= 7,
            5 => playerThird is >= 4 and <= 7,
            6 => playerThird is 6 or 7,
            _ => false
        };
    }

    private sealed class BaccaratViewModel : BaseViewModel
    {
        public List<string> Actions { get; set; } = new();
        public override IReadOnlyList<string> GetActionButtons() => Actions;
    }

    public override void OnForceStop()
    {
        lastPlayerHand.Clear();
        lastBankerHand.Clear();
        lastWinner = string.Empty;
        StatusText = "Waiting for bets";

        foreach (var player in Players.GetAllActivePlayers())
        {
            player.Metadata.Remove("Baccarat.BetType");
            player.Metadata.Remove("Baccarat.BetAmount");
        }
    }
}
