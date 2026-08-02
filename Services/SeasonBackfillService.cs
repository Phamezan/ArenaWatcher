using System.Text.Json;
using DiscordBot.Configuration;
using DiscordBot.Infrastructure.ArenaTracker;
using DiscordBot.Infrastructure.LeagueAssets;
using DiscordBot.Infrastructure.Riot;
using DiscordBot.Models;

namespace DiscordBot.Services;

/// <summary>
/// Rebuilds every tracked player's season-scoped Arena win set from Riot's
/// match-v5 API and pushes full snapshots to the arena-tracker worker.
///
/// The season start date lives in the arena-tracker repo (data/season.json,
/// fetched via the same base URL as the roster). On startup, if the season
/// start changed since the last run (i.e. a new Arena season began), every
/// player is re-scanned from that date and their data file is overwritten —
/// so the dashboard resets to zero exactly like the client's Season Journey.
///
/// Win condition mirrors the client's counter: participant.subteamPlacement == 1.
/// See arena-tracker/SPEC.md for how this was validated.
///
/// Each completed scan stores a per-player watermark and the accumulated
/// win set, so later --backfill-season runs only re-scan matches since the
/// last scan and merge the results into the stored set (snapshots are full
/// overwrites, so the stored set must be included). --backfill-season
/// --full ignores the watermarks and rebuilds from the season start.
/// </summary>
public sealed class SeasonBackfillService(
    IRiotClient riotClient,
    ILeagueAssetProvider leagueAssetProvider,
    IArenaTrackerNotifier arenaTrackerNotifier,
    HttpClient httpClient,
    AppConfig config)
{
    // Dev-key budget is 100 requests / 2 min; pace match fetches under it.
    private static readonly TimeSpan MatchFetchDelay = TimeSpan.FromMilliseconds(1300);

    // Incremental scans re-fetch a window before the last watermark so matches
    // that were still in progress (or unindexed) at scan time are not missed.
    private const long ScanOverlapSeconds = 3 * 60 * 60;

    /// <summary>
    /// Backfills every tracked player that has no season-scoped snapshot yet:
    /// on a season rollover that is everyone, otherwise only players added to
    /// the roster since the last run.
    /// </summary>
    public async Task RunIfSeasonChangedAsync(CancellationToken cancellationToken)
    {
        var seasonStart = await FetchSeasonStartAsync(cancellationToken);
        if (seasonStart is null)
        {
            return;
        }

        var state = await ReadSeasonStateAsync(cancellationToken);
        var isSameSeason = state?.SeasonStart == seasonStart;
        var records = isSameSeason
            ? state!.Players.ToDictionary(player => player.RiotId, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PlayerBackfillState>(StringComparer.OrdinalIgnoreCase);

        var pending = config.TrackedPlayers
            .Where(player => !records.ContainsKey(RiotIdOf(player)))
            .ToList();

        if (pending.Count == 0)
        {
            Console.WriteLine($"Season backfill already done for season start {seasonStart} ({records.Count} player(s)).");
            return;
        }

        Console.WriteLine(isSameSeason
            ? $"Season backfill pending for {pending.Count} new player(s): {string.Join(", ", pending.Select(RiotIdOf))}"
            : $"New Arena season detected (start {seasonStart}, was {state?.SeasonStart ?? "none"}). Running full season backfill...");

        await BackfillPlayersAsync(pending, seasonStart, records, forceFullScan: true, cancellationToken);
    }

    /// <summary>
    /// Re-scans every tracked player and pushes fresh snapshots. Incremental by
    /// default: players with a previous scan record are only scanned from that
    /// watermark and their stored win set is merged in. Pass fullScan to ignore
    /// the watermarks and rebuild from the season start.
    /// </summary>
    public async Task ForceBackfillAsync(bool fullScan, CancellationToken cancellationToken)
    {
        var seasonStart = await FetchSeasonStartAsync(cancellationToken)
            ?? throw new InvalidOperationException("No season config found (data/season.json via RosterUrl).");

        var state = await ReadSeasonStateAsync(cancellationToken);
        var records = !fullScan && state?.SeasonStart == seasonStart
            ? state!.Players.ToDictionary(player => player.RiotId, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PlayerBackfillState>(StringComparer.OrdinalIgnoreCase);

        await BackfillPlayersAsync(config.TrackedPlayers, seasonStart, records, fullScan, cancellationToken);
    }

    /// <summary>
    /// Backfills the given players and persists state after each success, so a
    /// restart mid-run resumes instead of re-scanning finished players.
    /// </summary>
    private async Task BackfillPlayersAsync(
        IReadOnlyList<PlayerConfig> players,
        string seasonStart,
        Dictionary<string, PlayerBackfillState> records,
        bool forceFullScan,
        CancellationToken cancellationToken)
    {
        var seasonStartSeconds = new DateTimeOffset(DateOnly.Parse(seasonStart), TimeOnly.MinValue, TimeSpan.Zero)
            .ToUnixTimeSeconds();
        var champions = await leagueAssetProvider.GetChampionsAsync(cancellationToken);

        // Season identity is written up front so a crash before the first
        // player completes still records which season the set is for.
        await WriteSeasonStateAsync(seasonStart, records.Values, cancellationToken);

        foreach (var player in players)
        {
            try
            {
                records[RiotIdOf(player)] = await BackfillPlayerAsync(
                    player,
                    seasonStartSeconds,
                    seasonStart,
                    records.GetValueOrDefault(RiotIdOf(player)),
                    forceFullScan,
                    champions,
                    cancellationToken);
                await WriteSeasonStateAsync(seasonStart, records.Values, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.GameName}#{player.TagLine}: season backfill failed: {ex.Message}");
            }
        }
    }

    private async Task<PlayerBackfillState> BackfillPlayerAsync(
        PlayerConfig player,
        long seasonStartSeconds,
        string seasonStart,
        PlayerBackfillState? prior,
        bool forceFullScan,
        IReadOnlyList<ChampionInfo> champions,
        CancellationToken cancellationToken)
    {
        var riotId = $"{player.GameName}#{player.TagLine}";
        var account = await riotClient.GetAccountByRiotIdAsync(player.GameName, player.TagLine, cancellationToken);

        // The watermark is captured before the scan; the overlap guards against
        // matches that were still in progress (or not yet indexed) at that moment.
        var scanStartedSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var startSeconds = forceFullScan || prior is null
            ? seasonStartSeconds
            : Math.Max(seasonStartSeconds, prior.LastScanSeconds - ScanOverlapSeconds);

        var matchIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var queueId in ArenaMatchParser.ArenaQueueIds)
        {
            matchIds.UnionWith(await riotClient.GetMatchIdsAsync(account.Puuid, queueId, startSeconds, cancellationToken));
        }

        var wonChampionIds = new HashSet<int>(forceFullScan ? [] : prior?.WonChampionIds ?? []);
        Console.WriteLine(forceFullScan || prior is null
            ? $"[{DateTimeOffset.Now:t}] {riotId}: {matchIds.Count} Arena matches since {seasonStart}. Scanning..."
            : $"[{DateTimeOffset.Now:t}] {riotId}: {matchIds.Count} Arena matches since last scan. Scanning...");
        var scanned = 0;
        foreach (var matchId in matchIds)
        {
            using var match = await riotClient.GetMatchAsync(matchId, cancellationToken);
            var participant = ArenaMatchParser.FindParticipant(match, account.Puuid);
            if (participant is not null
                && ArenaMatchParser.IsArenaWin(participant.Value)
                && participant.Value.TryGetProperty("championId", out var championId)
                && championId.ValueKind == JsonValueKind.Number)
            {
                wonChampionIds.Add(championId.GetInt32());
            }

            scanned++;
            if (scanned % 50 == 0)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:t}] {riotId}: {scanned}/{matchIds.Count} matches scanned, {wonChampionIds.Count} unique wins so far");
            }

            await Task.Delay(MatchFetchDelay, cancellationToken);
        }

        var snapshot = new
        {
            summoner = riotId,
            updatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            seasonStart = $"{seasonStart}T00:00:00.000Z",
            source = "match-v5-season",
            completedCount = wonChampionIds.Count,
            totalChampions = champions.Count,
            champions = champions
                .Select(champion => new { id = champion.Id, name = champion.Name, done = wonChampionIds.Contains(champion.Id) })
                .ToArray(),
        };

        await arenaTrackerNotifier.NotifySnapshotAsync(snapshot, cancellationToken);
        Console.WriteLine($"[{DateTimeOffset.Now:t}] {riotId}: {wonChampionIds.Count} champions won this season, snapshot synced.");

        return new PlayerBackfillState(riotId, scanStartedSeconds, wonChampionIds.OrderBy(id => id).ToArray());
    }

    /// <summary>
    /// Prints unique first-place champion counts for a range of candidate
    /// cutoff dates, so a new season start can be pinned by matching the
    /// number on the client's Season Journey screen. Used at season rollover.
    /// </summary>
    public async Task CalibrateAsync(string riotId, string since, CancellationToken cancellationToken)
    {
        var parts = riotId.Split('#', 2);
        if (parts.Length != 2)
        {
            Console.WriteLine($"Invalid Riot ID '{riotId}'. Expected format: GameName#TagLine.");
            return;
        }

        var account = await riotClient.GetAccountByRiotIdAsync(parts[0], parts[1], cancellationToken);
        var startSeconds = new DateTimeOffset(DateOnly.Parse(since), TimeOnly.MinValue, TimeSpan.Zero)
            .ToUnixTimeSeconds();

        var matchIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var queueId in ArenaMatchParser.ArenaQueueIds)
        {
            matchIds.UnionWith(await riotClient.GetMatchIdsAsync(account.Puuid, queueId, startSeconds, cancellationToken));
        }

        Console.WriteLine($"{riotId}: {matchIds.Count} Arena matches since {since}. Scanning...");

        var wins = new List<(long GameCreationMs, int ChampionId)>();
        var scanned = 0;
        foreach (var matchId in matchIds)
        {
            using var match = await riotClient.GetMatchAsync(matchId, cancellationToken);
            var participant = ArenaMatchParser.FindParticipant(match, account.Puuid);
            if (participant is not null
                && ArenaMatchParser.IsArenaWin(participant.Value)
                && participant.Value.TryGetProperty("championId", out var championId)
                && championId.ValueKind == JsonValueKind.Number
                && match.RootElement.TryGetProperty("info", out var info)
                && info.TryGetProperty("gameCreation", out var gameCreation))
            {
                wins.Add((gameCreation.GetInt64(), championId.GetInt32()));
            }

            scanned++;
            if (scanned % 50 == 0)
            {
                Console.WriteLine($"  {scanned}/{matchIds.Count} matches scanned, {wins.Count} wins so far");
            }

            await Task.Delay(MatchFetchDelay, cancellationToken);
        }

        Console.WriteLine($"{wins.Count} first-place finishes since {since}.");
        Console.WriteLine("unique champions with a first-place finish, by cutoff date:");

        var cursor = DateOnly.Parse(since);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        while (cursor < today)
        {
            foreach (var cutoff in new[] { new DateOnly(cursor.Year, cursor.Month, 1), new DateOnly(cursor.Year, cursor.Month, 15) })
            {
                if (cutoff >= today)
                {
                    continue;
                }

                var cutoffMs = new DateTimeOffset(cutoff, TimeOnly.MinValue, TimeSpan.Zero).ToUnixTimeMilliseconds();
                var unique = wins.Where(win => win.GameCreationMs >= cutoffMs).Select(win => win.ChampionId).Distinct().Count();
                Console.WriteLine($"  {cutoff:yyyy-MM-dd}: {unique}");
            }

            cursor = cursor.AddMonths(1);
        }

        Console.WriteLine("The cutoff matching the client's Season Journey count is the season start — put it in data/season.json.");
    }

    private async Task<string?> FetchSeasonStartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.RosterUrl))
        {
            return null;
        }

        var seasonUrl = config.RosterUrl.Replace("players.json", "season.json", StringComparison.OrdinalIgnoreCase);
        if (seasonUrl == config.RosterUrl)
        {
            Console.WriteLine("RosterUrl does not point at a players.json; season sync disabled.");
            return null;
        }

        try
        {
            await using var stream = await httpClient.GetStreamAsync(seasonUrl, cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("seasonStart", out var value)
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"Could not read season config ({ex.Message}); season sync skipped.");
            return null;
        }
    }

    private string SeasonStatePath() => config.SeenMatchesPath + ".season";

    private static string RiotIdOf(PlayerConfig player) => $"{player.GameName}#{player.TagLine}";

    /// <summary>
    /// State file holds the season start plus, per player, the last scan
    /// watermark and the champions already won this season. The win set is
    /// stored because snapshots overwrite the player's arena-tracker data —
    /// incremental scans must merge with it rather than replace it. Older
    /// formats (a bare date, or a Riot ID list without win data) are read as
    /// "no player records yet" so the next startup rebuilds every snapshot
    /// once and populates the new records.
    /// </summary>
    private sealed record SeasonState(string SeasonStart, IReadOnlyList<PlayerBackfillState> Players);

    private sealed record PlayerBackfillState(string RiotId, long LastScanSeconds, IReadOnlyList<int> WonChampionIds);

    private async Task<SeasonState?> ReadSeasonStateAsync(CancellationToken cancellationToken)
    {
        var statePath = SeasonStatePath();
        if (!File.Exists(statePath))
        {
            return null;
        }

        var content = (await File.ReadAllTextAsync(statePath, cancellationToken)).Trim();
        if (content.Length == 0)
        {
            return null;
        }

        if (!content.StartsWith('{'))
        {
            Console.WriteLine($"Legacy season state found (season {content}, no per-player record); re-running backfill for all players.");
            return new SeasonState(content, []);
        }

        try
        {
            var state = JsonSerializer.Deserialize<SeasonState>(content, SeasonStateJsonOptions);
            return state?.SeasonStart is null ? null : state with { Players = state.Players ?? [] };
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Could not read season state ({ex.Message}); treating as unprocessed.");
            return null;
        }
    }

    private async Task WriteSeasonStateAsync(
        string seasonStart,
        IEnumerable<PlayerBackfillState> players,
        CancellationToken cancellationToken)
    {
        var state = new SeasonState(
            seasonStart,
            players.OrderBy(player => player.RiotId, StringComparer.OrdinalIgnoreCase).ToArray());
        await File.WriteAllTextAsync(
            SeasonStatePath(),
            JsonSerializer.Serialize(state, SeasonStateJsonOptions),
            cancellationToken);
    }

    private static readonly JsonSerializerOptions SeasonStateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
