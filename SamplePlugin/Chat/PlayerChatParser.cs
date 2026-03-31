using System;
using System.Collections.Generic;
using System.Linq;
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

        // Auto-detect the dealer from any game-start broadcast (runs before the
        // dealer filter so a game started by a new dealer is always caught).
        if (Regex.IsMatch(message,
                @"Ultima! begins!|Game Started|Cards dealt|Let.s play|Round \d+:\s*dealing|dealing to \d+" +
                @"|New hand!|is rolling!|Bets are open|Place your bets|Chocobo betting is now OPEN" +
                @"|Now playing:",
                RegexOptions.IgnoreCase))
            State.DealerName = sender;

        // If dealer is designated, only trust their messages
        if (!string.IsNullOrEmpty(State.DealerName) &&
            !sender.Equals(State.DealerName, StringComparison.OrdinalIgnoreCase))
            return;

        State.AddFeed($"{sender}: {message}");

        // "Now playing: Blackjack!" — switch player view to the correct game
        var npM = Regex.Match(message, @"Now playing:\s*(.+?)!", RegexOptions.IgnoreCase);
        if (npM.Success)
        {
            string game = npM.Groups[1].Value.Trim();
            string detected = game.ToLowerInvariant() switch
            {
                "blackjack"      => "Blackjack",
                "roulette"       => "Roulette",
                "craps"          => "Craps",
                "mini baccarat"  => "Baccarat",
                "chocobo racing" => "Chocobo",
                "texas hold'em"  => "Poker",
                "ultima"         => "Ultima",
                "ultima!"        => "Ultima",
                _                => game
            };
            SetDetectedGame(detected);
        }

        ParseBJ(message);
        ParseCraps(message);
        ParseRoulette(message);
        ParsePoker(message);
        ParseBaccarat(message);
        ParseChocobo(message);
        ParseUltima(message);
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
            State.BJPlayers       = new();
            State.BJDealerCards   = new();
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
            State.BJDealerCards = cards.Count > 0 ? new List<string> { cards[0] } : new();
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
                State.BJDealerCards  = new List<string>(cards);
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
            UpdateBJPlayerHand(m.Groups[1].Value.Trim(), ExtractBJCards(msg), m.Groups[3].Value.Trim());
            return;
        }

        // Player hit / ongoing: "Jess hits: 【A♠】【K♥】【5♥】 -> 15"
        var hitM = Regex.Match(msg, @"^(.+?)\s+hits?:\s*((?:【[^】]+】)+)\s*(?:->|→)\s*(.+)$", RegexOptions.IgnoreCase);
        if (hitM.Success)
        {
            UpdateBJPlayerHand(hitM.Groups[1].Value.Trim(), ExtractBJCards(msg), hitM.Groups[3].Value.Trim());
            return;
        }

        // Player bust: "Jess BUSTS with 24!"
        var bustM = Regex.Match(msg, @"^(.+?)\s+(?:BUSTS?|goes over)\b.*?(\d+)", RegexOptions.IgnoreCase);
        if (bustM.Success)
        {
            string pName = bustM.Groups[1].Value.Trim();
            int idx = State.BJPlayers.FindIndex(p =>
                p.Name.Equals(pName, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) { State.BJPlayers[idx].IsBust = true; State.BJPlayers[idx].Desc = $"BUST {bustM.Groups[2].Value}"; }
            return;
        }

        // Player stands: "Jess stands at 19." / "Jess stands with 19."
        var standM = Regex.Match(msg, @"^(.+?)\s+stands?\b.*?(\d+)", RegexOptions.IgnoreCase);
        if (standM.Success)
        {
            string pName = standM.Groups[1].Value.Trim();
            int idx = State.BJPlayers.FindIndex(p =>
                p.Name.Equals(pName, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) State.BJPlayers[idx].Desc = $"Stand {standM.Groups[2].Value}";
            return;
        }

        // Player reaches 21: "Jess reaches 21!" / "Jess hits 21!"
        var twentyOneM = Regex.Match(msg, @"^(.+?)\s+(?:reaches|hits)\s+21", RegexOptions.IgnoreCase);
        if (twentyOneM.Success)
        {
            string pName = twentyOneM.Groups[1].Value.Trim();
            int idx = State.BJPlayers.FindIndex(p =>
                p.Name.Equals(pName, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) State.BJPlayers[idx].Desc = "21!";
        }
    }

    private void UpdateBJPlayerHand(string pName, List<string> cards, string desc)
    {
        if (cards.Count == 0) return;
        int idx = State.BJPlayers.FindIndex(p =>
            p.Name.Equals(pName, StringComparison.OrdinalIgnoreCase));
        var hand = idx >= 0 ? State.BJPlayers[idx] : new PVBJHand { Name = pName };
        hand.Cards.Clear();
        hand.Cards.AddRange(cards);
        hand.Desc   = desc;
        hand.IsBust = desc.Contains("BUST", StringComparison.OrdinalIgnoreCase);
        hand.IsBJ   = desc.Equals("BLACKJACK", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) State.BJPlayers.Add(hand);
        else         State.BJPlayers[idx] = hand;
        SetDetectedGame("Blackjack");
    }

    // ── Craps ─────────────────────────────────────────────────────────────────

    private void ParseCraps(string msg)
    {
        // Bets open with shooter: "[CRAPS] Bets open (60s) — ... | Shooter: X (>ROLL)"
        var openM = Regex.Match(msg, @"Bets open.*?Shooter:\s*(.+?)(?:\s*\(|$)", RegexOptions.IgnoreCase);
        if (openM.Success)
        {
            State.CrapsShooter = openM.Groups[1].Value.Trim();
            SetDetectedGame("Craps");
        }

        // New shooter: "New shooter: Jess!"
        var newShM = Regex.Match(msg, @"New shooter:\s*(.+?)!", RegexOptions.IgnoreCase);
        if (newShM.Success)
        {
            State.CrapsShooter = newShM.Groups[1].Value.Trim();
            SetDetectedGame("Craps");
        }

        // Bet placement: "Jess bets 100
        var bm = Regex.Match(msg,
            @"^.+? bets [\d,]+. on (PASS|DONTPASS|FIELD|BIG6|BIG8|Place (\d+))\.",
            RegexOptions.IgnoreCase);
        if (bm.Success)
        {
            string playerName = bm.Groups[1].Value.Trim();
            string key = bm.Groups[2].Success
                ? $"PLACE{bm.Groups[2].Value}"
                : bm.Groups[1].Value.ToUpperInvariant();
            if (!State.CrapsBets.TryGetValue(key, out var cbl))
                State.CrapsBets[key] = cbl = new();
            if (!cbl.Contains(playerName)) cbl.Add(playerName);
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
            State.CrapsBets.Remove("FIELD");
            if (!State.CrapsPointSet)
            {
                if (total is 4 or 5 or 6 or 8 or 9 or 10)
                { State.CrapsPoint = total; State.CrapsPointSet = true; }
                else
                {
                    // 7/11/craps: come-out settled — clear all
                    State.CrapsPoint = 0; State.CrapsPointSet = false;
                    State.CrapsBets.Clear();
                }
            }
            else
            {
                if (total == State.CrapsPoint || total == 7)
                {
                    State.CrapsPoint = 0; State.CrapsPointSet = false;
                    State.CrapsBets.Clear();
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
            State.PokerCommunity  = new();
            State.PokerShowdown   = new();
            State.PokerPhaseLabel = string.Empty;
            State.PokerPot        = 0;
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
            State.PokerCommunity = new List<string>(cards);
        State.PokerPhaseLabel = phase;
        SetDetectedGame("Poker");
    }

    // ── Baccarat ──────────────────────────────────────────────────────────────

    private void ParseBaccarat(string msg)
    {
        // Bet: "Jess bets 100₩ on PLAYER."
        if (Regex.IsMatch(msg, @"bets \d+. on (PLAYER|BANKER|TIE)\.", RegexOptions.IgnoreCase))
        {
            SetDetectedGame("Baccarat");
            State.BacActive = true;
            State.BacResult = string.Empty;  // clear previous round result
            return;
        }

        // Deal: "Player: A♠ 3♥ (3) | Banker: K♦ 7♣ (7)"
        var dealM = Regex.Match(msg, @"Player:\s*(.+?)\s*\((\d+)\)\s*\|\s*Banker:\s*(.+?)\s*\((\d+)\)", RegexOptions.IgnoreCase);
        if (dealM.Success)
        {
            State.BacPlayerCards = dealM.Groups[1].Value.Trim();
            State.BacPlayerScore = int.TryParse(dealM.Groups[2].Value, out int ps) ? ps : 0;
            State.BacBankerCards = dealM.Groups[3].Value.Trim();
            State.BacBankerScore = int.TryParse(dealM.Groups[4].Value, out int bs) ? bs : 0;
            State.BacActive = true;
            SetDetectedGame("Baccarat");
            return;
        }

        // Third card draw: "Player draws A♠ → 5" or "Banker draws K♦ → 3"
        var drawM = Regex.Match(msg, @"(Player|Banker) draws (.+?)\s*\u2192\s*(\d+)", RegexOptions.IgnoreCase);
        if (drawM.Success)
        {
            string side = drawM.Groups[1].Value;
            int newScore = int.TryParse(drawM.Groups[3].Value, out int ns) ? ns : 0;
            if (side.Equals("Player", StringComparison.OrdinalIgnoreCase))
            {
                State.BacPlayerCards += " " + drawM.Groups[2].Value.Trim();
                State.BacPlayerScore = newScore;
            }
            else
            {
                State.BacBankerCards += " " + drawM.Groups[2].Value.Trim();
                State.BacBankerScore = newScore;
            }
            SetDetectedGame("Baccarat");
            return;
        }

        // Result: "Final — Player: 5 | Banker: 7 | BANKER wins!"
        var resM = Regex.Match(msg, @"Final.*?Player:\s*(\d+).*?Banker:\s*(\d+).*?(PLAYER|BANKER|TIE)\s+wins!", RegexOptions.IgnoreCase);
        if (resM.Success)
        {
            State.BacPlayerScore = int.TryParse(resM.Groups[1].Value, out int fp) ? fp : 0;
            State.BacBankerScore = int.TryParse(resM.Groups[2].Value, out int fb) ? fb : 0;
            State.BacResult = resM.Groups[3].Value.Trim().ToUpperInvariant();
            SetDetectedGame("Baccarat");
        }
    }

    // ── Chocobo Racing ────────────────────────────────────────────────────────

    private void ParseChocobo(string msg)
    {
        // Betting open: "🐦 Chocobo betting is now OPEN!"
        if (msg.Contains("Chocobo betting is now OPEN", StringComparison.OrdinalIgnoreCase))
        {
            State.ChocoboRacers = new List<PVChocoboRacer>();
            State.ChocoboBettingOpen = true;
            SetDetectedGame("Chocobo");
            return;
        }

        // Racer roster: "#1 Crimson Flash 3.2x | #2 Golden Bolt 4.0x | ..."
        var racerMatches = Regex.Matches(msg, @"#(\d+)\s+(.+?)\s+([\d.]+)x");
        if (racerMatches.Count > 0)
        {
            foreach (Match rm in racerMatches)
            {
                int num = int.TryParse(rm.Groups[1].Value, out int rn) ? rn : 0;
                // Don't add duplicates
                if (!State.ChocoboRacers.Any(r => r.Number == num))
                {
                    State.ChocoboRacers.Add(new PVChocoboRacer
                    {
                        Number = num,
                        Name   = rm.Groups[2].Value.Trim(),
                        Odds   = rm.Groups[3].Value.Trim()
                    });
                }
            }
            SetDetectedGame("Chocobo");
            return;
        }

        // Bet confirmation: "Jess bets 100₩ on #3 Crimson Flash (3.2x odds)"
        if (Regex.IsMatch(msg, @"bets \d+. on #\d+", RegexOptions.IgnoreCase))
        {
            SetDetectedGame("Chocobo");
            return;
        }

        // Race finished: "FINISH! 1.CrimsonFlash  2.GoldenBolt  3...."
        if (Regex.IsMatch(msg, @"^FINISH!", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(msg, @"wins the race|Race complete", RegexOptions.IgnoreCase))
        {
            State.ChocoboBettingOpen = false;
            SetDetectedGame("Chocobo");
        }
    }

    // ── Ultima! ───────────────────────────────────────────────────────────────

    /// <summary>
    /// If a player name is seen in a play/draw message but isn't in the player
    /// order yet, add them dynamically so they appear on the oval table.
    /// </summary>
    private void EnsureUltimaPlayer(string pname, int? cardCount = null)
    {
        if (string.IsNullOrEmpty(pname)) return;
        if (!State.UltimaPlayerOrder.Contains(pname, StringComparer.OrdinalIgnoreCase))
            State.UltimaPlayerOrder.Add(pname);
        if (!State.UltimaCardCounts.ContainsKey(pname))
            State.UltimaCardCounts[pname] = cardCount ?? 7;
    }

    private void ParseUltima(string msg)
    {
        // ── Absolute card-count from "(N cards)" suffix ───────────────────────
        // Every play/draw message from the engine includes the acting player's
        // post-action card count.  This is the authoritative source for remote
        // players who don't have engine access.
        // We store it and let the play/draw handlers use it instead of doing
        // their own arithmetic.
        int? absCardCount = null;
        var absM = Regex.Match(msg, @"\((\d+)\s+cards?\)\s*$", RegexOptions.IgnoreCase);
        if (absM.Success && int.TryParse(absM.Groups[1].Value, out int absCount))
            absCardCount = absCount;

        // Game start: "✦ Ultima! begins! ✦  [W3] Water 3  Turn order: Jess → Bob → ..."
        if (Regex.IsMatch(msg, @"Ultima! begins!", RegexOptions.IgnoreCase))
        {
            State.UltimaHand       = new List<UltimaCard>();
            State.UltimaTopCard       = null;
            State.UltimaWinner        = string.Empty;
            State.UltimaClockwise     = true;
            State.UltimaCurrentPlayer = string.Empty;
            SetDetectedGame("Ultima");

            var om = Regex.Match(msg, @"Turn order:\s*(.+)$", RegexOptions.IgnoreCase);
            if (om.Success)
            {
                var names = om.Groups[1].Value
                    .Split(new[] { " \u2192 ", " -> " },
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                // Atomic replacement — render thread never sees an empty list
                State.UltimaPlayerOrder = new List<string>(names);
                var counts = new Dictionary<string, int>();
                foreach (var n in names) counts[n] = 7;
                State.UltimaCardCounts = counts;
            }
            else
            {
                State.UltimaPlayerOrder = new List<string>();
                State.UltimaCardCounts  = new Dictionary<string, int>();
            }
            return;
        }

        // Top card + turn announcement: "Top card: [W3] Water 3  ► Jess's turn!"
        var turnM = Regex.Match(msg,
            @"Top card:\s*\[([^\]]+)\].*?►\s*(.+?)(?:'s|'s) turn",
            RegexOptions.IgnoreCase);
        if (turnM.Success)
        {
            State.UltimaTopCard       = UltimaCard.Parse(turnM.Groups[1].Value);
            State.UltimaCurrentPlayer = turnM.Groups[2].Value.Trim();
            EnsureUltimaPlayer(State.UltimaCurrentPlayer);
            SetDetectedGame("Ultima");
            return;
        }

        // Color change: "Color changed to Water!"
        var colorM = Regex.Match(msg, @"Color changed to (Water|Fire|Grass|Love)!", RegexOptions.IgnoreCase);
        if (colorM.Success)
        {
            State.UltimaActiveColor = UltimaCard.ParseColor(colorM.Groups[1].Value) ?? UltimaColor.Wild;
            return;
        }

        // Direction reversal
        if (Regex.IsMatch(msg, @"now clockwise", RegexOptions.IgnoreCase))
        { State.UltimaClockwise = true; return; }
        if (Regex.IsMatch(msg, @"now counter-clockwise", RegexOptions.IgnoreCase))
        { State.UltimaClockwise = false; return; }

        // Combined draw-until-play: "X drew N cards until they could play — [Y] ..."
        //                     or:  "X drew a card and plays [Y] ..."
        var drawPlayM = Regex.Match(msg,
            @"^(.+?) drew.*?(?:play|plays).*?\[([^\]]+)\]",
            RegexOptions.IgnoreCase);
        if (drawPlayM.Success)
        {
            string pname = drawPlayM.Groups[1].Value.Trim();
            EnsureUltimaPlayer(pname);
            var card = UltimaCard.Parse(drawPlayM.Groups[2].Value);
            if (card != null) State.UltimaTopCard = card;
            if (absCardCount.HasValue)
                State.UltimaCardCounts[pname] = absCardCount.Value;
            else
            {
                var numM = Regex.Match(msg, @"drew (\d+) cards?");
                int netDrew = numM.Success && int.TryParse(numM.Groups[1].Value, out int d) ? d - 1 : 0;
                if (State.UltimaCardCounts.ContainsKey(pname))
                    State.UltimaCardCounts[pname] = Math.Max(0, State.UltimaCardCounts[pname] + netDrew);
            }
            SetDetectedGame("Ultima");
            return;
        }

        // Player plays a card: "{name} plays [W3] Water 3!"
        var playM = Regex.Match(msg, @"^(.+?) plays \[([^\]]+)\]", RegexOptions.IgnoreCase);
        if (playM.Success)
        {
            string pname = playM.Groups[1].Value.Trim();
            EnsureUltimaPlayer(pname);
            var    card  = UltimaCard.Parse(playM.Groups[2].Value);
            if (card != null) State.UltimaTopCard = card;
            if (absCardCount.HasValue)
                State.UltimaCardCounts[pname] = absCardCount.Value;
            else if (State.UltimaCardCounts.ContainsKey(pname))
                State.UltimaCardCounts[pname] = Math.Max(0, State.UltimaCardCounts[pname] - 1);
            SetDetectedGame("Ultima");
            return;
        }

        // Player draws one card
        var drawM = Regex.Match(msg, @"^(.+?) draws a card", RegexOptions.IgnoreCase);
        if (drawM.Success)
        {
            string pname = drawM.Groups[1].Value.Trim();
            EnsureUltimaPlayer(pname);
            if (absCardCount.HasValue)
                State.UltimaCardCounts[pname] = absCardCount.Value;
            else if (State.UltimaCardCounts.ContainsKey(pname))
                State.UltimaCardCounts[pname]++;
            SetDetectedGame("Ultima");
            return;
        }

        // Forced draws (Summon+2 / PolymorphSummon+4): "{name} draws N cards"
        var forcedM = Regex.Match(msg, @"^(.+?) draws (\d+) cards?", RegexOptions.IgnoreCase);
        if (forcedM.Success && int.TryParse(forcedM.Groups[2].Value, out int dc))
        {
            string pname = forcedM.Groups[1].Value.Trim();
            EnsureUltimaPlayer(pname);
            if (absCardCount.HasValue)
                State.UltimaCardCounts[pname] = absCardCount.Value;
            else if (State.UltimaCardCounts.ContainsKey(pname))
                State.UltimaCardCounts[pname] += dc;
            SetDetectedGame("Ultima");
            return;
        }

        // Win: "❖ Jess Dee wins Ultima! GG!"
        var winM = Regex.Match(msg, @"(.+?)\s+wins Ultima!", RegexOptions.IgnoreCase);
        if (winM.Success)
        {
            State.UltimaWinner = winM.Groups[1].Value.Trim().TrimStart('\u2756', ' ');
            SetDetectedGame("Ultima");
            return;
        }

        // ULTIMA call, or any other Ultima marker
        if (Regex.IsMatch(msg, @"calls ULTIMA!|— ULTIMA!", RegexOptions.IgnoreCase))
            SetDetectedGame("Ultima");
    }

    // ── Tell (poker hole cards + Ultima hand) ─────────────────────────────────

    private void ParseTell(string msg)
    {
        // >JOIN response: "Game: Blackjack | Bank: 5000₩. | Players: Jess(5000), Bob(3000)"
        var joinM = Regex.Match(msg, @"Game:\s*(.+?)\s*\|", RegexOptions.IgnoreCase);
        if (joinM.Success)
        {
            string game = joinM.Groups[1].Value.Trim();
            string detected = game.ToLowerInvariant() switch
            {
                "blackjack"      => "Blackjack",
                "roulette"       => "Roulette",
                "craps"          => "Craps",
                "baccarat"       => "Baccarat",
                "mini baccarat"  => "Baccarat",
                "chocoboracing"  => "Chocobo",
                "chocobo racing" => "Chocobo",
                "texasholdem"    => "Poker",
                "texas hold'em"  => "Poker",
                "ultima"         => "Ultima",
                "ultima!"        => "Ultima",
                _                => ""
            };
            if (!string.IsNullOrEmpty(detected))
                SetDetectedGame(detected);
            State.AddFeed($"[Sync] {msg}");
            return;
        }

        // Poker hole cards
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
            return;
        }

        // Ultima! initial hand tell: "Your Ultima! hand: W0 F3 G+2 L7 (7 cards)"
        var uhm = Regex.Match(msg, @"Your Ultima! hand:\s*(.+?)\s*\(\d+ cards?\)", RegexOptions.IgnoreCase);
        if (uhm.Success)
        {
            ParseUltimaHandCodes(uhm.Groups[1].Value);
            SetDetectedGame("Ultima");
            return;
        }

        // Ultima! hand update (after sort or draw): "[Hand: W0 F3 ...]" or "Hand: W0 F3 ..."
        var handM = Regex.Match(msg, @"(?:^\[?Hand:\s*(.+?)\]?\s*$)", RegexOptions.IgnoreCase);
        if (handM.Success && State.DetectedGame == "Ultima")
        {
            ParseUltimaHandCodes(handM.Groups[1].Value);
            return;
        }

        // Ultima! sort confirmation: "Hand sorted by color: W0 F3 ..."
        var sortM = Regex.Match(msg, @"Hand sorted by \w+:\s*(.+)$", RegexOptions.IgnoreCase);
        if (sortM.Success && State.DetectedGame == "Ultima")
        {
            ParseUltimaHandCodes(sortM.Groups[1].Value);
        }

        // Ultima! draw result: "You drew: [W5] Water 5  [Hand: W0 F3 ...]"
        var drewM = Regex.Match(msg, @"\[Hand:\s*(.+?)\]\s*$", RegexOptions.IgnoreCase);
        if (drewM.Success && State.DetectedGame == "Ultima")
        {
            ParseUltimaHandCodes(drewM.Groups[1].Value);
        }
    }

    /// <summary>Parse space-separated Ultima card codes and replace the current hand.</summary>
    private void ParseUltimaHandCodes(string codes)
    {
        var newHand = new List<UltimaCard>();
        foreach (var code in codes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var card = UltimaCard.Parse(code.Trim('[', ']'));
            if (card != null) newHand.Add(card);
        }
        State.UltimaHand = newHand;
    }
}

