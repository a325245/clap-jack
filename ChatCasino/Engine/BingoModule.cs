using System;
using System.Collections.Generic;
using System.Linq;
using ChatCasino.Models;
using ChatCasino.Services;
using ChatCasino.UI;

namespace ChatCasino.Engine;

public sealed class BingoModule : BaseEngine
{
    private static readonly string[] WordList = BingoWordList.Words;
    private const string BaseUrl = "https://a325245.github.io/WebBingo/";

    private readonly IBankService bank;

    private string roomName = string.Empty;
    private int[] callerSequence = [];
    private string[] cipherList = [];
    private int currentTurn;
    private bool gameOpen;
    private bool gameActive;
    private int progressiveRoundIndex;
    private int totalPot;

    private readonly Dictionary<string, string> playerPhrases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> playerCardCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> catchupUses = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<int> calledNumbers = new();
    private readonly HashSet<string> suspectedBingo = new(StringComparer.OrdinalIgnoreCase);

    public BingoModule(IMessageService msg, IDeckService decks, IPlayerService players, IBankService bank)
        : base(GameType.Bingo, msg, decks, players)
    {
        this.bank = bank;
        StatusText = "Waiting to open — dealer sets price then OPEN";
    }

    private BingoWinCondition ActiveWinCondition
    {
        get
        {
            if (CasinoUI.BingoGameMode == BingoGameMode.Progressive && CasinoUI.BingoProgressiveRounds.Count > 0)
                return CasinoUI.BingoProgressiveRounds[Math.Min(progressiveRoundIndex, CasinoUI.BingoProgressiveRounds.Count - 1)].WinCondition;
            return CasinoUI.BingoWinCondition;
        }
    }

    public override CmdResult Execute(string playerName, string cmd, string[] args)
    {
        return cmd.ToUpperInvariant() switch
        {
            "OPEN"    => OpenBuyin(),
            "DRAW"    => DrawBall(),
            "BUY"     => BuyCards(playerName, args),
            "CATCHUP" => SendCatchup(playerName),
            "BINGO"   => ClaimBingo(playerName),
            "PAY"     => PayBingo(args),
            "RESET"   => ResetGame(),
            _         => CmdResult.Ok(string.Empty)
        };
    }

    public override IEnumerable<string> GetValidCommands()
        => ["OPEN", "DRAW", "BUY [n]", "CATCHUP", "BINGO", "PAY [name]", "RESET"];

    // -------------------------------------------------------------------------
    // Dealer: OPEN
    // -------------------------------------------------------------------------
    public CmdResult OpenBuyin()
    {
        if (gameActive) return CmdResult.Fail("Game already running. Use RESET first.");
        gameOpen = true;
        gameActive = false;
        currentTurn = 0;
        progressiveRoundIndex = 0;
        totalPot = 0;
        calledNumbers.Clear();
        playerPhrases.Clear();
        playerCardCounts.Clear();
        catchupUses.Clear();
        suspectedBingo.Clear();

        roomName = GenerateRandomPhrase(3);
        callerSequence = GetSequenceForRoom(roomName);
        cipherList = GetCipherListForRoom(roomName);

        var mode = CasinoUI.BingoGameMode == BingoGameMode.Progressive ? "Progressive" : "Standard";
        var winName = WinConditionName(ActiveWinCondition);
        BeginRoundRecord();
        StatusText = $"Buy-in open — {CasinoUI.BingoCardPrice}\uE049/card";
        Msg.QueuePartyMessage(
            $"[BINGO] Buy-in open! [{mode}] Win: {winName}. " +
            $"Cards {CasinoUI.BingoCardPrice}\uE049 each. Use >BUY [1-10]. " +
            $"Catchups: {CasinoUI.BingoCatchupLimit}/game.");
        return CmdResult.Ok("Bingo buy-in opened.");
    }

    // -------------------------------------------------------------------------
    // Player: BUY [n]
    // -------------------------------------------------------------------------
    private CmdResult BuyCards(string playerName, string[] args)
    {
        if (!gameOpen) return CmdResult.Fail("Buy-in is not open.");

        var p = Players.GetPlayer(playerName);
        if (p is null) return CmdResult.Fail("Player not found.");

        int count = 1;
        if (args.Length >= 1 && int.TryParse(args[0], out var parsed))
            count = Math.Clamp(parsed, 1, 10);

        var totalCost = count * CasinoUI.BingoCardPrice;
        if (totalCost > p.CurrentBank)
            return CmdResult.Fail($"Insufficient funds — {count} card(s) = {totalCost}\uE049, you have {p.CurrentBank}\uE049.");

        if (bank.Deduct(p, totalCost, "Bingo buy-in") != TransactionResult.Success)
            return CmdResult.Fail("Could not deduct funds.");

        if (!playerPhrases.ContainsKey(playerName))
            playerPhrases[playerName] = GeneratePhraseForPlayer(playerName, roomName);

        playerCardCounts[playerName] = (playerCardCounts.TryGetValue(playerName, out var existing) ? existing : 0) + count;
        catchupUses.TryAdd(playerName, 0);
        totalPot += totalCost;

        var phrase = playerPhrases[playerName];
        var cardCount = playerCardCounts[playerName];
        var server = p.HomeWorld ?? "Unknown";
        var urlPhrase = phrase.Replace(' ', '-');
        var directUrl = $"{BaseUrl}?phrase={urlPhrase}&count={cardCount}";

        Msg.QueueTell(playerName, server,
            $"[BINGO] Your phrase: \"{phrase}\" — {cardCount} card(s). " +
            $"Direct link: {directUrl} " +
            $"(or visit {BaseUrl} and enter phrase + count manually)");

        Msg.QueuePartyMessage($"[BINGO] {playerName} bought {count} card(s). Total: {cardCount}.");
        LogRound($"{playerName} bought {count} cards");
        return CmdResult.Ok("Cards purchased.");
    }

    // -------------------------------------------------------------------------
    // Dealer: DRAW
    // -------------------------------------------------------------------------
    public CmdResult DrawBall()
    {
        if (!gameOpen) return CmdResult.Fail("Game not open.");
        if (currentTurn >= 75) return CmdResult.Fail("All 75 balls have been drawn.");
        if (playerPhrases.Count == 0) return CmdResult.Fail("No players have bought in yet.");

        gameActive = true;
        var ball = callerSequence[currentTurn];
        currentTurn++;
        calledNumbers.Add(ball);

        var letter = BallLetter(ball);
        StatusText = $"Last: {letter} {ball}  ({currentTurn}/75)";
        Msg.QueuePartyMessage($"[BINGO] {letter} {ball}");

        // Detect possible wins for current win condition
        suspectedBingo.Clear();
        var wc = ActiveWinCondition;
        foreach (var kvp in playerPhrases)
        {
            var cards = playerCardCounts.TryGetValue(kvp.Key, out var cc) ? cc : 1;
            for (var ci = 1; ci <= cards; ci++)
            {
                if (CheckWinConditionOnCard(kvp.Value, ci, calledNumbers, wc))
                {
                    suspectedBingo.Add(kvp.Key);
                    break;
                }
            }
        }

        LogRound($"Ball {currentTurn}: {letter} {ball}");
        return CmdResult.Ok($"Drew {letter} {ball}.");
    }

    // -------------------------------------------------------------------------
    // Player: CATCHUP
    // -------------------------------------------------------------------------
    private CmdResult SendCatchup(string playerName)
    {
        if (!gameOpen && !gameActive) return CmdResult.Fail("No active game.");
        if (!playerPhrases.ContainsKey(playerName)) return CmdResult.Fail("You have not bought any cards.");

        catchupUses.TryGetValue(playerName, out var uses);
        if (uses >= CasinoUI.BingoCatchupLimit)
            return CmdResult.Fail($"You have used all {CasinoUI.BingoCatchupLimit} catchup(s) for this game.");

        catchupUses[playerName] = uses + 1;

        var catchup = currentTurn == 0
            ? "No balls drawn yet."
            : $"{roomName} {cipherList[currentTurn - 1]}";

        var p = Players.GetPlayer(playerName);
        var server = p?.HomeWorld ?? "Unknown";
        Msg.QueueTell(playerName, server,
            $"[BINGO] Catchup code ({uses + 1}/{CasinoUI.BingoCatchupLimit}): {catchup}");
        return CmdResult.Ok("Catchup sent.");
    }

    // -------------------------------------------------------------------------
    // Player: BINGO
    // -------------------------------------------------------------------------
    private CmdResult ClaimBingo(string playerName)
    {
        if (!gameActive) return CmdResult.Fail("No active game.");
        if (!playerPhrases.ContainsKey(playerName)) return CmdResult.Fail("You have not bought any cards.");

        Msg.QueuePartyMessage($"[BINGO] {playerName} claims BINGO! Dealer — please verify.");
        Msg.QueueAdminEcho($"[BINGO] Claim by {playerName} — verify via the dealer panel.");
        return CmdResult.Ok("Bingo claimed.");
    }

    // -------------------------------------------------------------------------
    // Dealer: PAY [playerName]
    // -------------------------------------------------------------------------
    private CmdResult PayBingo(string[] args)
    {
        if (args.Length == 0) return CmdResult.Fail("Usage: PAY [playerName]");
        var targetName = string.Join(' ', args);
        var p = Players.GetPlayer(targetName);
        if (p is null) return CmdResult.Fail($"Player '{targetName}' not found.");

        if (CasinoUI.BingoGameMode == BingoGameMode.Progressive && CasinoUI.BingoProgressiveRounds.Count > 0)
        {
            var rounds = CasinoUI.BingoProgressiveRounds;
            var ri = Math.Min(progressiveRoundIndex, rounds.Count - 1);
            var pct = rounds[ri].PayoutPercent;
            var payout = (int)Math.Round(totalPot * pct / 100.0);

            bank.Award(p, payout, $"Bingo progressive round {ri + 1} payout");
            Msg.QueuePartyMessage(
                $"[BINGO] \uE06C Round {ri + 1} winner: {targetName} wins {payout}\uE049 ({pct}% of pot)!");
            LogRound($"Round {ri + 1} winner: {targetName} — payout {payout}");

            progressiveRoundIndex++;
            if (progressiveRoundIndex < rounds.Count)
            {
                suspectedBingo.Clear();
                var nextWin = WinConditionName(rounds[progressiveRoundIndex].WinCondition);
                StatusText = $"Round {progressiveRoundIndex + 1}/{rounds.Count} — {nextWin}";
                Msg.QueuePartyMessage(
                    $"[BINGO] Round {progressiveRoundIndex + 1}/{rounds.Count} begins! " +
                    $"Win: {nextWin}. Cards and daubs carry over — keep drawing!");
                return CmdResult.Ok($"Round {ri + 1} paid. Round {progressiveRoundIndex + 1} now active.");
            }
            else
            {
                Msg.QueuePartyMessage("[BINGO] All progressive rounds complete! Thanks for playing.");
                LogRound("Progressive game complete.");
                StatusText = "Progressive game complete.";
                gameActive = false;
                gameOpen = false;
                OnRoundComplete();
                return CmdResult.Ok("All progressive rounds paid out.");
            }
        }
        else
        {
            bank.Award(p, totalPot, "Bingo winner payout");
            Msg.QueuePartyMessage($"[BINGO] \uE06C BINGO! {targetName} wins {totalPot}\uE049! Congratulations!");
            LogRound($"WINNER: {targetName} — payout {totalPot}");
            StatusText = $"Game over — winner: {targetName}";
            gameActive = false;
            gameOpen = false;
            OnRoundComplete();
            return CmdResult.Ok("Payout complete.");
        }
    }

    // -------------------------------------------------------------------------
    // Dealer: RESET
    // -------------------------------------------------------------------------
    public CmdResult ResetGame()
    {
        gameOpen = false;
        gameActive = false;
        currentTurn = 0;
        progressiveRoundIndex = 0;
        totalPot = 0;
        calledNumbers.Clear();
        playerPhrases.Clear();
        playerCardCounts.Clear();
        catchupUses.Clear();
        suspectedBingo.Clear();
        roomName = string.Empty;
        StatusText = "Waiting to open — dealer sets price then OPEN";
        return CmdResult.Ok("Bingo reset.");
    }

    // -------------------------------------------------------------------------
    // Public accessors for dealer UI
    // -------------------------------------------------------------------------
    public bool IsGameOpen => gameOpen;
    public bool IsGameActive => gameActive;
    public IReadOnlyList<int> CalledNumbers => calledNumbers;
    public IReadOnlySet<string> SuspectedBingo => suspectedBingo;
    public int CurrentTurn => currentTurn;
    public string RoomName => roomName;
    public int ProgressiveRoundIndex => progressiveRoundIndex;
    public string CurrentCatchupCode => currentTurn > 0 ? $"{roomName} {cipherList[currentTurn - 1]}" : string.Empty;

    // -------------------------------------------------------------------------
    // ViewModel
    // -------------------------------------------------------------------------
    public override ICasinoViewModel GetViewModel()
    {
        var seats = Players.GetAllActivePlayers().Select(p =>
        {
            var cards = playerCardCounts.TryGetValue(p.Name, out var cc) ? cc : 0;
            var hasBingo = suspectedBingo.Contains(p.Name);
            return new PlayerSlotViewModel
            {
                PlayerName = p.Name,
                Bank = p.CurrentBank,
                BetAmount = cards * CasinoUI.BingoCardPrice,
                ResultText = cards == 0 ? string.Empty : $"{cards} card(s){(hasBingo ? " \u26A0 BINGO?" : string.Empty)}",
                HandResultTexts = hasBingo ? ["\u26A0 Possible BINGO"] : []
            };
        }).ToList();

        var isProgressive = CasinoUI.BingoGameMode == BingoGameMode.Progressive;
        var progRounds = isProgressive ? CasinoUI.BingoProgressiveRounds.Count : 0;
        var riClamped = Math.Min(progressiveRoundIndex, isProgressive && CasinoUI.BingoProgressiveRounds.Count > 0 ? CasinoUI.BingoProgressiveRounds.Count - 1 : 0);
        var roundPot = isProgressive && CasinoUI.BingoProgressiveRounds.Count > 0
            ? (int)Math.Round(totalPot * CasinoUI.BingoProgressiveRounds[riClamped].PayoutPercent / 100.0)
            : totalPot;

        return new BingoViewModel
        {
            GameTitle = "Bingo",
            GameStatus = StatusText,
            Seats = seats,
            CalledNumbers = calledNumbers.ToList(),
            SuspectedBingo = suspectedBingo.ToHashSet(StringComparer.OrdinalIgnoreCase),
            CurrentTurn = currentTurn,
            RoomName = roomName,
            CurrentCatchupCode = CurrentCatchupCode,
            IsGameOpen = gameOpen,
            IsGameActive = gameActive,
            ActiveWinCondition = ActiveWinCondition,
            GameMode = CasinoUI.BingoGameMode,
            ProgressiveRoundIndex = progressiveRoundIndex,
            TotalProgressiveRounds = progRounds,
            CurrentRoundPot = roundPot
        };
    }

    // -------------------------------------------------------------------------
    // Win condition checking
    // -------------------------------------------------------------------------
    private static bool CheckWinConditionOnCard(string phrase, int cardIndex, List<int> called, BingoWinCondition wc)
    {
        var card = GenerateBingoCard(phrase, cardIndex);
        var hit = new bool[5, 5];
        for (var col = 0; col < 5; col++)
            for (var row = 0; row < 5; row++)
                hit[col, row] = card[col, row] is null || called.Contains(card[col, row]!.Value);

        return wc switch
        {
            BingoWinCondition.OneLine     => CountLines(hit) >= 1,
            BingoWinCondition.TwoLine     => CountLines(hit) >= 2,
            BingoWinCondition.FourCorners => hit[0, 0] && hit[4, 0] && hit[0, 4] && hit[4, 4],
            BingoWinCondition.Blackout    => AllHit(hit),
            BingoWinCondition.Blitz       => TotalHit(hit) >= 5,
            _                             => CountLines(hit) >= 1
        };
    }

    private static int CountLines(bool[,] hit)
    {
        var lines = 0;
        for (var row = 0; row < 5; row++)
            if (Enumerable.Range(0, 5).All(c => hit[c, row])) lines++;
        for (var col = 0; col < 5; col++)
            if (Enumerable.Range(0, 5).All(r => hit[col, r])) lines++;
        if (Enumerable.Range(0, 5).All(i => hit[i, i])) lines++;
        if (Enumerable.Range(0, 5).All(i => hit[i, 4 - i])) lines++;
        return lines;
    }

    private static bool AllHit(bool[,] hit)
    {
        for (var col = 0; col < 5; col++)
            for (var row = 0; row < 5; row++)
                if (!hit[col, row]) return false;
        return true;
    }

    private static int TotalHit(bool[,] hit)
    {
        var count = 0;
        for (var col = 0; col < 5; col++)
            for (var row = 0; row < 5; row++)
                if (hit[col, row]) count++;
        return count;
    }

    // -------------------------------------------------------------------------
    // Card generation — mirrors WebBingo JS exactly
    // -------------------------------------------------------------------------
    private static int[] GetSequenceForRoom(string seed)
    {
        var rng = CreatePrng(HashString(seed));
        var pool = Enumerable.Range(1, 75).ToList();
        var seq = new int[75];
        for (var i = 0; i < 75; i++)
        {
            var idx = (int)(rng() * pool.Count);
            seq[i] = pool[idx];
            pool.RemoveAt(idx);
        }
        return seq;
    }

    private static string[] GetCipherListForRoom(string seed)
    {
        var rng = CreatePrng(HashString(seed));
        var list = new List<string>();
        while (list.Count < 75)
        {
            var word = WordList[(int)(rng() * WordList.Length)];
            if (!list.Contains(word)) list.Add(word);
        }
        return [.. list];
    }

    private static int?[,] GenerateBingoCard(string masterPhrase, int cardIndex)
    {
        var rng = CreatePrng(HashString($"{masterPhrase}-{cardIndex}"));
        int[] minVal = [1, 16, 31, 46, 61];
        int[] maxVal = [15, 30, 45, 60, 75];
        var card = new int?[5, 5];
        for (var col = 0; col < 5; col++)
        {
            var pool = Enumerable.Range(minVal[col], maxVal[col] - minVal[col] + 1).ToList();
            for (var row = 0; row < 5; row++)
            {
                if (col == 2 && row == 2) { card[col, row] = null; continue; }
                var idx = (int)(rng() * pool.Count);
                card[col, row] = pool[idx];
                pool.RemoveAt(idx);
            }
        }
        return card;
    }

    // -------------------------------------------------------------------------
    // Mulberry32 PRNG — matches WebBingo JS createPRNG exactly
    // -------------------------------------------------------------------------
    private static uint HashString(string s)
    {
        unchecked
        {
            int hash = 0;
            foreach (var c in s)
                hash = (hash << 5) - hash + c;
            return (uint)Math.Abs(hash);
        }
    }

    private static Func<double> CreatePrng(uint seed)
    {
        uint state = seed;
        return () =>
        {
            unchecked
            {
                state += 0x6D2B79F5u;
                uint t = state;
                t = (t ^ (t >> 15)) * (t | 1u);
                t ^= t + ((t ^ (t >> 7)) * (t | 61u));
                return ((t ^ (t >> 14)) & 0xFFFF_FFFFu) / 4294967296.0;
            }
        };
    }

    private static string GenerateRandomPhrase(int words)
    {
        var rng = new Random();
        return string.Join(' ', Enumerable.Range(0, words).Select(_ => WordList[rng.Next(WordList.Length)]));
    }

    private static string GeneratePhraseForPlayer(string playerName, string room)
    {
        var rng = CreatePrng(HashString($"{room}|{playerName}"));
        return string.Join(' ', Enumerable.Range(0, 3).Select(_ => WordList[(int)(rng() * WordList.Length)]));
    }

    private static string BallLetter(int n) => n switch
    {
        <= 15 => "B",
        <= 30 => "I",
        <= 45 => "N",
        <= 60 => "G",
        _     => "O"
    };

    public static string WinConditionName(BingoWinCondition wc) => wc switch
    {
        BingoWinCondition.OneLine     => "1 Line",
        BingoWinCondition.TwoLine     => "2 Lines",
        BingoWinCondition.FourCorners => "Four Corners",
        BingoWinCondition.Blackout    => "Blackout",
        BingoWinCondition.Blitz       => "Blitz (any 5)",
        _                             => "1 Line"
    };
}
