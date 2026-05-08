using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ChatCasino.Models;
using ChatCasino.Services;
using ChatCasino.UI;

namespace ChatCasino.Engine;

public sealed class ChocoboRacingModule : BaseEngine
{
    private static readonly float[] OddsByRank = [2.0f, 2.5f, 3.0f, 3.5f, 4.5f, 5.0f, 7.0f, 9.0f];
    private const string HashAlpha = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmn"; // 40 chars for 0-39

    private readonly IBankService bank;
    private readonly ITimerService timer;
    private readonly List<ChocoboRacerProfile> allRosters;
    private readonly List<ChocoboRacerProfile> activeRacers = new();
    private readonly Dictionary<string, float> progress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> raceOdds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float[]> raceSegmentPace = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random rng = new();

    private Guid? tickHandle;
    private bool racing;
    private bool betsOpen;
    private DateTime raceStartUtc;
    private int lastFlavorSegment = -1;
    private string raceHash = string.Empty;
    private List<string> podiumOrder = new();
    private string lastWinner = string.Empty;

    private sealed record ChocoboBetEntry(string Racer, int Amount);
    public readonly record struct RaceProfile(int Speed, int Endurance, int SurgeToken, int StumbleToken);

    public ChocoboRacingModule(IMessageService msg, IDeckService decks, IPlayerService players, IBankService bank, ITimerService timer)
        : base(GameType.ChocoboRacing, msg, decks, players)
    {
        this.bank = bank;
        this.timer = timer;
        allRosters = LoadRoster();
        if (allRosters.Count == 0)
            allRosters.AddRange(GetFallbackRoster());
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
            "OPENBETS" => OpenBets(),
            "START" => StartRace(),
            _ => CmdResult.Ok(string.Empty)
        };
    }

    public override IEnumerable<string> GetValidCommands() => ["BET", "OPENBETS", "START"];

    public override ICasinoViewModel GetViewModel()
    {
        var orderedRacers = activeRacers
            .OrderBy(r => raceOdds.GetValueOrDefault(r.Name, float.MaxValue))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var seats = orderedRacers.Select(r =>
        {
            var p = progress.TryGetValue(r.Name, out var meters) ? meters : 0f;
            var odds = raceOdds.TryGetValue(r.Name, out var o) ? o : 0f;
            var oddsText = odds == (int)odds ? $"{(int)odds}:1" : $"{odds:0.#}:1";
            var status = racing
                ? string.Empty
                : betsOpen
                    ? $"Odds {oddsText}"
                    : string.Empty;

            return new PlayerSlotViewModel
            {
                PlayerName = r.Name,
                IsDealer = true,
                BetAmount = (int)p,
                ResultText = status,
                HandResultTexts = [$"SPD {r.Speed} | END {r.Endurance}", oddsText]
            };
        }).ToList();

        seats.AddRange(Players.GetAllActivePlayers().Select(p =>
        {
            var bets = GetBets(p);
            var amount = bets.Sum(b => b.Amount);
            var summary = bets.Count == 0
                ? string.Empty
                : string.Join(" | ", bets.Select(b => $"{b.Racer}:{b.Amount}"));
            return new PlayerSlotViewModel
            {
                PlayerName = p.Name,
                Bank = p.CurrentBank,
                BetAmount = amount,
                ResultText = summary,
                HandResultTexts = bets.Select(b => $"{b.Racer}:{b.Amount}").ToList()
            };
        }));

        var status2 = string.IsNullOrWhiteSpace(lastWinner) ? StatusText : $"{StatusText} | Winner: {lastWinner}";
        if (!string.IsNullOrWhiteSpace(raceHash))
            status2 += $" | Hash: {raceHash}";

        return new ChocoboViewModel
        {
            GameTitle = "Chocobo Racing",
            GameStatus = status2,
            Seats = seats,
            Actions = GetValidCommands().ToList()
        };
    }

    private CmdResult PlaceBet(Player p, string[] args)
    {
        if (racing) return CmdResult.Fail("Race in progress.");
        if (!betsOpen || activeRacers.Count == 0)
            return CmdResult.Fail("Bets are not open. Dealer must use OPENBETS first.");

        // Support reversed order: BET RacerName 100 as well as BET 100 RacerName
        if (args.Length >= 2 && !int.TryParse(args[0], out _) && int.TryParse(args[^1], out _))
            args = [args[^1], .. args[..^1]];

        if (args.Length < 2 || !int.TryParse(args[0], out var amount) || amount <= 0)
            return CmdResult.Fail("Usage: BET [amount] [racer]");

        if (amount < CasinoUI.GlobalMinBet || amount > CasinoUI.GlobalMaxBet)
            return CmdResult.Fail($"Bet must be between {CasinoUI.GlobalMinBet} and {CasinoUI.GlobalMaxBet}. ");

        var racerName = string.Join(' ', args.Skip(1));
        var racer = activeRacers.FirstOrDefault(r => r.Name.Equals(racerName, StringComparison.OrdinalIgnoreCase));
        if (racer is null) return CmdResult.Fail("Unknown racer for this race.");

        if (bank.Deduct(p, amount, $"Chocobo bet {racer.Name}") != TransactionResult.Success)
            return CmdResult.Fail("Insufficient funds.");

        var bets = GetBets(p);
        bets.Add(new ChocoboBetEntry(racer.Name, amount));
        SetBets(p, bets);

        var odds = raceOdds.TryGetValue(racer.Name, out var o) ? o : 0f;
        var oddsText = odds == (int)odds ? $"{(int)odds}:1" : $"{odds:0.#}:1";
        Msg.QueuePartyMessage($"[CHOCOBO] {p.Name} bets {amount}\uE049 on {racer.Name} ({oddsText})");
        StatusText = "Bets open";
        return CmdResult.Ok("Bet accepted.");
    }

    private CmdResult StartRace()
    {
        if (racing) return CmdResult.Fail("Race already running.");
        if (!betsOpen || activeRacers.Count == 0)
            return CmdResult.Fail("Open bets first.");

        racing = true;
        lastWinner = string.Empty;
        StatusText = "Race in progress";
        raceStartUtc = DateTime.UtcNow;
        lastFlavorSegment = -1;

        progress.Clear();
        raceSegmentPace.Clear();
        foreach (var r in activeRacers)
            progress[r.Name] = 0;

        // Hash v2 format:
        // [0] rotation char
        // [1] format marker (derotated value 39)
        // per racer: [speed][endurance][surgeToken][stumbleToken]
        var rot = rng.Next(1, 40);
        var hashChars = new char[2 + activeRacers.Count * 4];
        hashChars[0] = HashAlpha[rot];
        hashChars[1] = HashAlpha[(39 + rot) % 40];

        for (var i = 0; i < activeRacers.Count; i++)
        {
            var s = Math.Clamp(activeRacers[i].Speed - 60, 0, 39);
            var e = Math.Clamp(activeRacers[i].Endurance - 60, 0, 39);
            var surge = rng.Next(0, 36);   // segment/power packed into one token
            var stumble = rng.Next(0, 36); // segment/power packed into one token

            var offset = 2 + i * 4;
            hashChars[offset] = HashAlpha[(s + rot) % 40];
            hashChars[offset + 1] = HashAlpha[(e + rot) % 40];
            hashChars[offset + 2] = HashAlpha[(surge + rot) % 40];
            hashChars[offset + 3] = HashAlpha[(stumble + rot) % 40];
        }

        raceHash = new string(hashChars);

        var profiles = DecodeRaceProfiles(raceHash);
        for (var i = 0; i < Math.Min(activeRacers.Count, profiles.Count); i++)
            raceSegmentPace[activeRacers[i].Name] = BuildSegmentPace(raceHash, profiles[i], i);

        podiumOrder = BuildPodiumFromHash(raceHash, activeRacers.Select(r => r.Name).ToList()).Take(3).ToList();

        Msg.QueuePartyMessage($"[CHOCOBO] Forecast hash: {raceHash}");

        // Delay first tick to ensure hash message is sent to chat
        tickHandle = timer.Schedule(TimeSpan.FromSeconds(3), TickRace);
        Msg.QueuePartyMessage("[CHOCOBO] Race started!");
        return CmdResult.Ok("Race started.");
    }

    private static int DecodeRotated(char value, int rot)
    {
        var idx = HashAlpha.IndexOf(value);
        if (idx < 0) return -1;
        return (idx - rot + 40) % 40;
    }

    /// <summary>Decodes (speed, endurance) pairs from the race hash for each racer index (v1 + v2).</summary>
    public static List<(int Speed, int Endurance)> DecodeHash(string hash)
        => DecodeRaceProfiles(hash).Select(p => (p.Speed, p.Endurance)).ToList();

    /// <summary>Decodes race profiles from hash. v1 hashes default surge/stumble tokens to zero.</summary>
    public static List<RaceProfile> DecodeRaceProfiles(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length < 3)
            return [];

        var rot = HashAlpha.IndexOf(hash[0]);
        if (rot < 0) return [];

        var isV2 = hash.Length >= 2 && DecodeRotated(hash[1], rot) == 39;
        var start = isV2 ? 2 : 1;
        var stride = isV2 ? 4 : 2;

        var result = new List<RaceProfile>();
        for (var i = start; i + 1 < hash.Length; i += stride)
        {
            var sRaw = DecodeRotated(hash[i], rot);
            var eRaw = DecodeRotated(hash[i + 1], rot);
            if (sRaw < 0 || eRaw < 0) continue;

            var surge = 0;
            var stumble = 0;
            if (isV2 && i + 3 < hash.Length)
            {
                surge = Math.Clamp(DecodeRotated(hash[i + 2], rot), 0, 35);
                stumble = Math.Clamp(DecodeRotated(hash[i + 3], rot), 0, 35);
            }

            result.Add(new RaceProfile(
                Speed: sRaw + 60,
                Endurance: eRaw + 60,
                SurgeToken: surge,
                StumbleToken: stumble));
        }

        return result;
    }

    public static float[] BuildSegmentPace(string hash, RaceProfile profile, int racerIndex)
    {
        var gains = new float[6];
        var basePace = profile.Speed * 0.095f + profile.Endurance * 0.062f;

        var surgeSeg = profile.SurgeToken / 6;
        var surgePow = profile.SurgeToken % 6;
        var stumbleSeg = profile.StumbleToken / 6;
        var stumblePow = profile.StumbleToken % 6;

        for (var seg = 0; seg < 6; seg++)
        {
            var lateFactor = seg / 5f;
            var enduranceBias = (profile.Endurance - 75) * 0.12f * lateFactor;
            var wave = MathF.Sin((StableHash($"{hash}|{racerIndex}|{seg}") % 360) * (MathF.PI / 180f)) * 1.35f;

            var surgeDist = MathF.Abs(seg - surgeSeg);
            var surge = MathF.Max(0f, (2.25f - surgeDist)) * (1.1f + surgePow * 0.45f);

            var stumbleDist = MathF.Abs(seg - stumbleSeg);
            var stumble = MathF.Max(0f, (2.0f - stumbleDist)) * (0.85f + stumblePow * 0.4f);

            gains[seg] = MathF.Max(2.2f, basePace + enduranceBias + surge - stumble + wave);
        }

        return gains;
    }

    private static float ComputeRaceScore(string hash, RaceProfile profile, int racerIndex)
    {
        var gains = BuildSegmentPace(hash, profile, racerIndex);
        return gains.Sum();
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in value)
                hash = (hash ^ c) * 16777619;
            return hash;
        }
    }

    /// <summary>Builds a podium order from decoded profile pace. Shared between dealer and player views.</summary>
    public static List<string> BuildPodiumFromHash(string hash, List<string> racerNames)
    {
        var profiles = DecodeRaceProfiles(hash);
        var scored = new List<(string Name, float Score)>();
        for (var i = 0; i < Math.Min(profiles.Count, racerNames.Count); i++)
            scored.Add((racerNames[i], ComputeRaceScore(hash, profiles[i], i)));

        var ordered = scored.OrderByDescending(x => x.Score).Select(x => x.Name).ToList();

        // Append any racers not in hash
        foreach (var r in racerNames)
            if (!ordered.Contains(r, StringComparer.OrdinalIgnoreCase))
                ordered.Add(r);

        return ordered;
    }

    private void TickRace()
    {
        var elapsed = (DateTime.UtcNow - raceStartUtc).TotalSeconds;
        var segment = Math.Min(5, (int)(elapsed / 5.0));

        foreach (var racer in activeRacers)
        {
            if (!raceSegmentPace.TryGetValue(racer.Name, out var gains) || gains.Length < 6)
            {
                var fallback = new RaceProfile(racer.Speed, racer.Endurance, 0, 0);
                gains = BuildSegmentPace(raceHash, fallback, 0);
                raceSegmentPace[racer.Name] = gains;
            }

            var noise = (float)(rng.NextDouble() * 1.2 - 0.6);
            var segmentGain = gains[segment] + noise;
            progress[racer.Name] += MathF.Max(2f, segmentGain);
        }

        if (segment > lastFlavorSegment)
        {
            lastFlavorSegment = segment;
            AnnounceFlavor(segment);
        }

        if (elapsed >= 30.0)
        {
            var winner = podiumOrder.Count > 0 ? podiumOrder[0] : activeRacers[rng.Next(activeRacers.Count)].Name;
            ResolveRace(winner);
            return;
        }

        tickHandle = timer.Schedule(TimeSpan.FromSeconds(1), TickRace);
    }

    private void AnnounceFlavor(int segment)
    {
        var ordered = progress.OrderByDescending(x => x.Value).Take(3).Select(x => x.Key).ToList();
        if (ordered.Count == 0) return;

        var lead = ordered[0];
        var second = ordered.Count > 1 ? ordered[1] : lead;
        var third = ordered.Count > 2 ? ordered[2] : second;

        string[] lines = segment switch
        {
            0 =>
            [
                $"[CHOCOBO] They're off! {lead} rockets out of the gate!",
                $"[CHOCOBO] Clean break! {lead} takes the first stride!",
                $"[CHOCOBO] Thunder at the start! {lead} steals early position!",
                $"[CHOCOBO] Fast launch! {lead} bursts ahead right away!"
            ],
            1 =>
            [
                $"[CHOCOBO] Coming into stride: {lead} keeps the lead!",
                $"[CHOCOBO] Pace is settling in—{lead} still in front!",
                $"[CHOCOBO] The pack forms up; {lead} remains first!",
                $"[CHOCOBO] First bend complete, {lead} controls the tempo!"
            ],
            2 =>
            [
                $"[CHOCOBO] Mid-race surge! {lead}, {second}, {third} are battling hard!",
                $"[CHOCOBO] Midfield chaos! Top three: {lead}, {second}, {third}!",
                $"[CHOCOBO] The race tightens! {lead} barely ahead of {second} and {third}!",
                $"[CHOCOBO] Big push in the center stretch—{lead}, {second}, {third}!"
            ],
            3 =>
            [
                $"[CHOCOBO] Turn for home: {lead} still ahead!",
                $"[CHOCOBO] Home turn reached—{lead} refuses to yield!",
                $"[CHOCOBO] Stretch run begins and {lead} remains in command!",
                $"[CHOCOBO] Closing section now, {lead} leads the field!"
            ],
            4 =>
            [
                $"[CHOCOBO] Final stretch! {lead} > {second} > {third}",
                $"[CHOCOBO] Sprint to the line! Current order: {lead}, {second}, {third}",
                $"[CHOCOBO] Last hundred meters! {lead} clings to first over {second}",
                $"[CHOCOBO] Neck and neck near the finish—{lead}, {second}, {third}!"
            ],
            _ =>
            [
                "[CHOCOBO] Last push to the finish line!",
                "[CHOCOBO] Final kicks! Every stride counts now!",
                "[CHOCOBO] Desperation sprint! The line is right there!",
                "[CHOCOBO] One final burst—this race ends in moments!"
            ]
        };

        var pick = lines[rng.Next(lines.Length)];
        Msg.QueuePartyMessage(pick);
    }

    private void ResolveRace(string winner)
    {
        racing = false;
        betsOpen = false;
        lastWinner = winner;

        foreach (var player in Players.GetAllActivePlayers())
        {
            var bets = GetBets(player);
            foreach (var bet in bets)
            {
                if (!bet.Racer.Equals(winner, StringComparison.OrdinalIgnoreCase))
                    continue;

                var fixedOdds = raceOdds.TryGetValue(winner, out var o) ? o : 1f;
                var profit = (int)Math.Floor(bet.Amount * fixedOdds);
                bank.Award(player, bet.Amount + profit, "Chocobo race payout");
            }

            player.Metadata.Remove("Chocobo.Bets");
            player.Metadata.Remove("Chocobo.BetRacer");
            player.Metadata.Remove("Chocobo.BetAmount");
        }

        StatusText = "Race complete";

        // Delay winner announcement so the visualization finishes first
        var w = winner;
        timer.Schedule(TimeSpan.FromSeconds(2), () =>
        {
            Msg.QueuePartyMessage($"[CHOCOBO] Winner: {w}");
            // Delay round complete/payout info to after winner announcement
            timer.Schedule(TimeSpan.FromSeconds(1.5), OnRoundComplete);
        });
    }

    private static List<ChocoboBetEntry> GetBets(Player p)
    {
        if (p.Metadata.TryGetValue("Chocobo.Bets", out var obj) && obj is List<ChocoboBetEntry> list)
            return list;

        var migrated = new List<ChocoboBetEntry>();
        if (p.Metadata.TryGetValue("Chocobo.BetRacer", out var rObj)
            && rObj is string racer
            && p.Metadata.TryGetValue("Chocobo.BetAmount", out var aObj)
            && aObj is int amount
            && amount > 0)
        {
            migrated.Add(new ChocoboBetEntry(racer, amount));
        }

        p.Metadata["Chocobo.Bets"] = migrated;
        return migrated;
    }

    private static void SetBets(Player p, List<ChocoboBetEntry> bets)
        => p.Metadata["Chocobo.Bets"] = bets;

    private static List<ChocoboRacerProfile> LoadRoster()
    {
        var candidates = new List<string>();
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(dir); i++)
        {
            candidates.Add(Path.Combine(dir, "Configuration", "ChocoboRoster.json"));
            candidates.Add(Path.Combine(dir, "ChatCasino", "Configuration", "ChocoboRoster.json"));
            dir = Directory.GetParent(dir)?.FullName;
        }

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "ChatCasino", "Configuration", "ChocoboRoster.json"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "Configuration", "ChocoboRoster.json"));

        var path = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(path))
            return new List<ChocoboRacerProfile>();

        try
        {
            var json = File.ReadAllText(path);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var model = JsonSerializer.Deserialize<ChocoboRoster>(json, opts);
            return model?.Racers?.Where(r => !string.IsNullOrWhiteSpace(r.Name)).ToList() ?? new List<ChocoboRacerProfile>();
        }
        catch
        {
            return new List<ChocoboRacerProfile>();
        }
    }

    private static List<ChocoboRacerProfile> GetFallbackRoster()
        =>
        [
            new() { Name = "Gilded Gale" }, new() { Name = "Thunderplume" },
            new() { Name = "Crimson Flash" }, new() { Name = "Dusty Wings" },
            new() { Name = "Pale Comet" }, new() { Name = "Cloud Dancer" },
            new() { Name = "Ivory Sprint" }, new() { Name = "Plucky Pete" },
            new() { Name = "Bronze Tempest" }, new() { Name = "Velvet Talon" },
            new() { Name = "Moonfeather" }, new() { Name = "Ashen Rocket" },
            new() { Name = "Storm Sable" }, new() { Name = "Mistral Wing" },
            new() { Name = "Goldrush" }, new() { Name = "Pine Gallop" },
            new() { Name = "Dawn Courier" }, new() { Name = "Cinder Dash" },
            new() { Name = "River Quill" }, new() { Name = "Whisper Spur" },
            new() { Name = "Azure Bolt" }, new() { Name = "Copper Breeze" },
            new() { Name = "Night Ember" }, new() { Name = "Silver Relay" }
        ];

    private CmdResult OpenBets()
    {
        if (racing) return CmdResult.Fail("Race already running.");

        if (allRosters.Count == 0)
            allRosters.AddRange(LoadRoster());
        if (allRosters.Count == 0)
            allRosters.AddRange(GetFallbackRoster());
        if (allRosters.Count == 0)
            return CmdResult.Fail("No roster loaded.");

        raceHash = string.Empty;
        podiumOrder.Clear();
        activeRacers.Clear();
        raceOdds.Clear();
        progress.Clear();
        BuildRaceRoster();

        // Sort activeRacers by odds so internal order matches VM display order and hash encoding
        var sorted = activeRacers
            .OrderBy(r => raceOdds.GetValueOrDefault(r.Name, float.MaxValue))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        activeRacers.Clear();
        activeRacers.AddRange(sorted);

        betsOpen = true;

        StatusText = "Bets Open";
        Msg.QueuePartyMessage("[CHOCOBO] Bets are now OPEN!");

        var packA = string.Join(" | ", activeRacers.Take(4).Select(r => FormatOdds(r)));
        var packB = string.Join(" | ", activeRacers.Skip(4).Take(4).Select(r => FormatOdds(r)));
        Msg.QueuePartyMessage($"[CHOCOBO] Roster A: {packA}");
        Msg.QueuePartyMessage($"[CHOCOBO] Roster B: {packB}");

        return CmdResult.Ok("Bets opened.");
    }

    private void BuildRaceRoster()
    {
        activeRacers.Clear();
        raceOdds.Clear();
        raceSegmentPace.Clear();

        var picks = allRosters.OrderBy(_ => rng.Next()).Take(Math.Min(8, allRosters.Count)).ToList();
        activeRacers.AddRange(picks);

        // Generate random stats for each racer
        foreach (var r in activeRacers)
        {
            r.Speed = rng.Next(60, 100);
            r.Endurance = rng.Next(60, 100);
        }

        // Shuffle odds randomly
        var shuffledOdds = OddsByRank.OrderBy(_ => rng.Next()).ToList();
        for (var i = 0; i < activeRacers.Count; i++)
            raceOdds[activeRacers[i].Name] = shuffledOdds[Math.Min(i, shuffledOdds.Count - 1)];

        progress.Clear();
        foreach (var r in activeRacers)
            progress[r.Name] = 0f;
    }

    private string FormatOdds(ChocoboRacerProfile r)
    {
        var odds = raceOdds.GetValueOrDefault(r.Name, 0f);
        var oddsText = odds == (int)odds ? $"{(int)odds}:1" : $"{odds:0.#}:1";
        return $"{r.Name} {oddsText}";
    }

    private static int GetInt(Player p, string key)
        => p.Metadata.TryGetValue(key, out var v) && v is int i ? i : 0;

    private sealed class ChocoboViewModel : BaseViewModel
    {
        public List<string> Actions { get; set; } = new();
        public override IReadOnlyList<string> GetActionButtons() => Actions;
    }
}


