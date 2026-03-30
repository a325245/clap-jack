using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SamplePlugin.Models;

namespace SamplePlugin.Chat;

public class PlayerChatParser
{
    public PlayerViewState State { get; } = new();

    private static readonly Random Rng = new();

    // ── Card extraction helpers ───────────────────────────────────────────────

    private static List<string> ExtractBJCards(string text)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(text, @"【([^】]+)】"))
            list.Add(m.Groups[1].Value);
        return list;
    }

    private static List<string> ExtractPokerCards(string text)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(text, @"(?:10|[2-9TJQKA])[♠♥♦♣]"))
            list.Add(m.Value);
        return list;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <param name="sender">Cleaned sender name from chat</param>
    /// <param name="message">Raw message text</param>
    /// <param name="isTell">True for TellIncoming messages directed at us</param>
    public void ParseMessage(string sender, string message, bool isTell)
    {
        if (isTell)
        {
            ParseTell(message);
            return;
        }

        // If dealer is designated, only trust their messages
        if (!string.IsNullOrEmpty(State.DealerName) &&
            !sender.Equals(State.DealerName, StringComparison.OrdinalIgnoreCase))
            return;

        State.AddFeed($"{sender}: {message}");
        ParseBJ(message);
        ParseCraps(message);
        ParseRoulette(message);
        ParsePoker(message);
    }

    /// <summary>Call every frame to animate craps dice.</summary>
    public void Tick()
    {
        if (!State.CrapsDiceRolling) return;
        double elapsed = (DateTime.Now - State.CrapsRollStart).TotalMilliseconds;
        if (elapsed < 2200)
        {
            // Animate die faces ~every 120 ms
            if ((long)(elapsed / 120) != (long)((elapsed - 16) / 120))
            {
                State.CrapsDie1 = Rng.Next(1, 7);
                State.CrapsDie2 = Rng.Next(1, 7);
            }
        }
        else
        {
            State.CrapsDiceRolling = false;
            State.CrapsHasResult   = true;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetDetectedGame(string game)
    {
        if (string.IsNullOrEmpty(State.DetectedGame) && !string.IsNullOrEmpty(game))
            State.ShouldAutoSwitch = true;
        State.DetectedGame = game;
    }

    // ── Blackjack ─────────────────────────────────────────────────────────────

    private void ParseBJ(string msg)
    {
        // New game
        if (Regex.IsMatch(msg, @"Game Started|Cards dealt|Let.s play|Round \d+:\s*dealing|dealing to \d+",
                RegexOptions.IgnoreCase))
        {
            State.BJActive        = true;
            State.BJHoleRevealed  = false;
            State.BJPlayers.Clear();
            State.BJDealerCards.Clear();
            SetDetectedGame("Blackjack");
            return;
        }

        // Round end
        if (Regex.IsMatch(msg, @"^(FINAL RESULTS|Round over|Settling up|Let.s see the results|Payouts:)",
                RegexOptions.IgnoreCase))
        {
            State.BJActive = false;
            return;
        }

        // Dealer upcard: "Dealer shows: 【A♠】 [Hidden]"
        if (Regex.IsMatch(msg, @"Dealer shows?:", RegexOptions.IgnoreCase) && msg.Contains('【'))
        {
            var cards = ExtractBJCards(msg);
            State.BJDealerCards.Clear();
            if (cards.Count > 0) State.BJDealerCards.Add(cards[0]);
            State.BJHoleRevealed = false;
            SetDetectedGame("Blackjack");
            return;
        }

        // Dealer full reveal: "Dealer has/reveals: 【A♠】【K♥】..." or "Hole card out! ..."
        if (Regex.IsMatch(msg, @"(?:Dealer (?:has|reveals?|holds?|done at)|Hole card out)",
                RegexOptions.IgnoreCase) && msg.Contains('【'))
        {
            var cards = ExtractBJCards(msg);
            if (cards.Count > 0)
            {
                State.BJDealerCards.Clear();
                State.BJDealerCards.AddRange(cards);
                State.BJHoleRevealed = true;
            }
            return;
        }

        // Dealer hits: "Dealer hits: 【5♥】 -> Total: 18"
        if (Regex.IsMatch(msg, @"Dealer hits?:", RegexOptions.IgnoreCase) && msg.Contains('【'))
        {
            var cards = ExtractBJCards(msg);
            if (cards.Count > 0)
            {
                if (!State.BJHoleRevealed) State.BJHoleRevealed = true;
                State.BJDealerCards.Add(cards[0]);
            }
            return;
        }

        // Player hand at turn start: "Jess: 【A♠】【K♥】 (20)"
        var m = Regex.Match(msg, @"^(.+?):\s*((?:【[^】]+】)+)\s*\((.+?)\)\s*$");
        if (m.Success && !m.Groups[1].Value.Trim()
                .StartsWith("Dealer", StringComparison.OrdinalIgnoreCase))
        {
            string pName = m.Groups[1].Value.Trim();
            string desc  = m.Groups[3].Value.Trim();
            var cards = ExtractBJCards(msg);
            if (cards.Count == 0) return;

            int idx = State.BJPlayers.FindIndex(p =>
                p.Name.Equals(pName, StringComparison.OrdinalIgnoreCase));
            var hand = idx >= 0 ? State.BJPlayers[idx] : new PVBJHand { Name = pName };
            hand.Cards.Clear();
            hand.Cards.AddRange(cards);
            hand.Desc   = desc;
            hand.IsBust = desc.Contains("BUST",      StringComparison.OrdinalIgnoreCase);
            hand.IsBJ   = desc.Equals("BLACKJACK",  StringComparison.OrdinalIgnoreCase);

            if (idx < 0) State.BJPlayers.Add(hand);
            else         State.BJPlayers[idx] = hand;
        }
    }

    // ── Craps ─────────────────────────────────────────────────────────────────

    private void ParseCraps(string msg)
    {
        // Bet placement: "Jess bets 100 on PASS." / "... on Place 6."
        var bm = Regex.Match(msg,
            @"^.+? bets [\d,]+. on (PASS|DONTPASS|FIELD|BIG6|BIG8|Place (\d+))\.",
            RegexOptions.IgnoreCase);
        if (bm.Success)
        {
            string key = bm.Groups[2].Success
                ? $"PLACE{bm.Groups[2].Value}"
                : bm.Groups[1].Value.ToUpperInvariant();
            State.CrapsBetTotals[key] = State.CrapsBetTotals.GetValueOrDefault(key) + 1;
            SetDetectedGame("Craps");
            return;
        }

        // "X is rolling!"
        if (Regex.IsMatch(msg, @"is rolling!", RegexOptions.IgnoreCase))
        {
            State.CrapsDiceRolling = true;
            State.CrapsRollStart   = DateTime.Now;
            State.CrapsHasResult   = false;
            SetDetectedGame("Craps");
            var sm = Regex.Match(msg, @"^(.+?)\s+is rolling!");
            if (sm.Success) State.CrapsShooter = sm.Groups[1].Value.Trim();
            return;
        }

        // "Dice: 3 + 4 = 7!"
        var dm = Regex.Match(msg, @"Dice:\s*(\d+)\s*\+\s*(\d+)");
        if (dm.Success &&
            int.TryParse(dm.Groups[1].Value, out int d1) &&
            int.TryParse(dm.Groups[2].Value, out int d2))
        {
            State.CrapsDie1        = d1;
            State.CrapsDie2        = d2;
            State.CrapsDiceRolling = false;
            State.CrapsHasResult   = true;
            SetDetectedGame("Craps");
            int total = d1 + d2;
            // Field bet is one-roll; clear it after every result
            State.CrapsBetTotals.Remove("FIELD");
            if (!State.CrapsPointSet)
            {
                if (total is 4 or 5 or 6 or 8 or 9 or 10)
                { State.CrapsPoint = total; State.CrapsPointSet = true; }
                else
                {
                    // 7/11/craps: come-out settled — clear all
                    State.CrapsPoint = 0; State.CrapsPointSet = false;
                    State.CrapsBetTotals.Clear();
                }
            }
            else
            {
                if (total == State.CrapsPoint || total == 7)
                {
                    State.CrapsPoint = 0; State.CrapsPointSet = false;
                    State.CrapsBetTotals.Clear();
                }
            }
        }
    }

    // ── Roulette ─────────────────────────────────────────────────────────────

    private void ParseRoulette(string msg)
    {
        // Bet: "PlayerName risks N\uE049 on TARGET."
        var bm = Regex.Match(msg, @"^(.+?)\s+risks\s+[\d,]+.\s+on\s+(.+?)\.", RegexOptions.IgnoreCase);
        if (bm.Success)
        {
            string pName = bm.Groups[1].Value.Trim();
            foreach (var t in bm.Groups[2].Value.Split(','))
                State.RouletteBets.Add(new PVRouletteBet
                {
                    PlayerName = pName,
                    Target     = t.Trim().ToUpperInvariant()
                });
            SetDetectedGame("Roulette");
            return;
        }

        // Spin start
        if (Regex.IsMatch(msg, @"wheel is spinning|No more bets", RegexOptions.IgnoreCase))
        {
            State.RouletteSpinning  = true;
            State.RouletteSpinStart = DateTime.Now;
            State.RouletteResult    = null;
            SetDetectedGame("Roulette");
            return;
        }

        // Result: "The ball lands on: 🔴 14 RED!"
        var rm = Regex.Match(msg, @"ball lands on.*?(\d+)\s+(RED|BLACK|GREEN)", RegexOptions.IgnoreCase);
        if (rm.Success && int.TryParse(rm.Groups[1].Value, out int rn))
        {
            State.RouletteResult   = rn;
            State.RouletteSpinning = false;
            State.RouletteBets.Clear();
            SetDetectedGame("Roulette");
        }
    }

    // ── Poker ─────────────────────────────────────────────────────────────────

    private void ParsePoker(string msg)
    {
        if (msg.Contains("♠ New hand!"))
        {
            State.ResetPokerForNewHand();   // preserves hole cards — they arrive before this message
            SetDetectedGame("Poker");
            return;
        }

        var pm = Regex.Match(msg, @"Pot:\s*(\d+)");
        if (pm.Success && int.TryParse(pm.Groups[1].Value, out int pot))
            State.PokerPot = pot;

        var flop  = Regex.Match(msg, @"\*\*\* FLOP \*\*\*\s*\[(.+?)\]");
        var turn  = Regex.Match(msg, @"\*\*\* TURN \*\*\*\s*\[(.+?)\]");
        var river = Regex.Match(msg, @"\*\*\* RIVER \*\*\*\s*\[(.+?)\]");

        if (flop.Success)  { SetCommunity(flop.Groups[1].Value,  "Flop");     return; }
        if (turn.Success)  { SetCommunity(turn.Groups[1].Value,  "Turn");     return; }
        if (river.Success) { SetCommunity(river.Groups[1].Value, "River");    return; }

        if (msg.Contains("*** SHOWDOWN ***"))
        {
            State.PokerShowdown.Clear();
            State.PokerPhaseLabel = "Showdown";
            State.DetectedGame    = "Poker";
            var board = Regex.Match(msg, @"Board:\s*\[(.+?)\]");
            if (board.Success) SetCommunity(board.Groups[1].Value, "Showdown");
            return;
        }

        // Showdown hand reveal: "Jess: A♠ K♥ — Royal Flush"
        if (State.PokerPhaseLabel == "Showdown")
        {
            var se = Regex.Match(msg,
                @"^(.+?):\s*((?:10|[2-9TJQKA])[♠♥♦♣])\s+((?:10|[2-9TJQKA])[♠♥♦♣])\s+[—–-]\s+(.+)$");
            if (se.Success)
                State.PokerShowdown.Add(new PVPokerShowdown
                {
                    Name     = se.Groups[1].Value.Trim(),
                    Card1    = se.Groups[2].Value,
                    Card2    = se.Groups[3].Value,
                    HandDesc = se.Groups[4].Value.Trim()
                });
        }
    }

    private void SetCommunity(string bracketContent, string phase)
    {
        var cards = ExtractPokerCards(bracketContent);
        if (cards.Count > 0)
        {
            State.PokerCommunity.Clear();
            State.PokerCommunity.AddRange(cards);
        }
        State.PokerPhaseLabel = phase;
        SetDetectedGame("Poker");
    }

    // ── Tell (poker hole cards) ────────────────────────────────────────────────

    private void ParseTell(string msg)
    {
        var hm = Regex.Match(msg,
            @"Your hole cards?:\s*((?:10|[2-9TJQKA])[♠♥♦♣])\s+((?:10|[2-9TJQKA])[♠♥♦♣])",
            RegexOptions.IgnoreCase);
        if (hm.Success)
        {
            State.MyHoleCard1    = hm.Groups[1].Value.Trim();
            State.MyHoleCard2    = hm.Groups[2].Value.Trim();
            State.MyHoleReceived = true;
            SetDetectedGame("Poker");
            State.AddFeed($"[Tell] Hole cards: {State.MyHoleCard1} {State.MyHoleCard2}");
        }
    }
}
