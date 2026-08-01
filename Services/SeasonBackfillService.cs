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
        var alreadyBackfilled = isSameSeason
            ? new HashSet<string>(state!.BackfilledPlayers, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var pending = config.TrackedPlayers
            .Where(player => !alreadyBackfilled.Contains(RiotIdOf(player)))
            .ToList();

        if (pending.Count == 0)
        {
            Console.WriteLine($"Season backfill already done for season start {seasonStart} ({alreadyBackfilled.Count} player(s)).");
            return;
        }

        Console.WriteLine(isSameSeason
            ? $"Season backfill pending for {pending.Count} new player(s): {string.Join(", ", pending.Select(RiotIdOf))}"
            : $"New Arena season detected (start {seasonStart}, was {state?.SeasonStart ?? "none"}). Running full season backfill...");

        await BackfillPlayersAsync(pending, seasonStart, alreadyBackfilled, cancellationToken);
    }

    public async Task ForceBackfillAsync(CancellationToken cancellationToken)
    {
        var seasonStart = await FetchSeasonStartAsync(cancellationToken)
            ?? throw new InvalidOperationException("No season config found (data/season.json via RosterUrl).");
        await BackfillPlayersAsync(
            config.TrackedPlayers,
            seasonStart,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
    }

    /// <summary>
    /// Backfills the given players and persists state after each success, so a
    /// restart mid-run resumes instead of re-scanning finished players.
    /// </summary>
    private async Task BackfillPlayersAsync(
        IReadOnlyList<PlayerConfig> players,
        string seasonStart,
        HashSet<string> alreadyBackfilled,
        CancellationToken cancellationToken)
    {
        var startSeconds = new DateTimeOffset(DateOnly.Parse(seasonStart), TimeOnly.MinValue, TimeSpan.Zero)
            .ToUnixTimeSeconds();
        var champions = await leagueAssetProvider.GetChampionsAsync(cancellationToken);

        // Season identity is written up front so a crash before the first
        // player completes still records which season the (empty) set is for.
        var completed = new HashSet<string>(alreadyBackfilled, StringComparer.OrdinalIgnoreCase);
        await WriteSeasonStateAsync(seasonStart, completed, cancellationToken);

        foreach (var player in players)
        {
            try
            {
                await BackfillPlayerAsync(player, startSeconds, seasonStart, champions, cancellationToken);
                completed.Add(RiotIdOf(player));
                await WriteSeasonStateAsync(seasonStart, completed, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.GameName}#{player.TagLine}: season backfill failed: {ex.Message}");
            }
        }
    }

    private async Task BackfillPlayerAsync(
        PlayerConfig player,
        long startSeconds,
        string seasonStart,
        IReadOnlyList<ChampionInfo> champions,
        CancellationToken cancellationToken)
    {
        var riotId = $"{player.GameName}#{player.TagLine}";
        var account = await riotClient.GetAccountByRiotIdAsync(player.GameName, player.TagLine, cancellationToken);

        var matchIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var queueId in ArenaMatchParser.ArenaQueueIds)
        {
            matchIds.UnionWith(await riotClient.GetMatchIdsAsync(account.Puuid, queueId, startSeconds, cancellationToken));
        }

        Console.WriteLine($"[{DateTimeOffset.Now:t}] {riotId}: {matchIds.Count} Arena matches since {seasonStart}. Scanning...");

        var wonChampionIds = new HashSet<int>();
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
    /// State file holds the season start plus the Riot IDs already backfilled
    /// for it. Files written before per-player tracking existed contain a bare
    /// date; those are read as "no player backfilled yet" so the next startup
    /// rebuilds every snapshot once.
    /// </summary>
    private sealed record SeasonState(string SeasonStart, IReadOnlyList<string> BackfilledPlayers);

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
            return state?.SeasonStart is null ? null : state with { BackfilledPlayers = state.BackfilledPlayers ?? [] };
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Could not read season state ({ex.Message}); treating as unprocessed.");
            return null;
        }
    }

    private async Task WriteSeasonStateAsync(
        string seasonStart,
        IEnumerable<string> backfilledPlayers,
        CancellationToken cancellationToken)
    {
        var state = new SeasonState(seasonStart, backfilledPlayers.OrderBy(id => id, StringComparer.Ordinal).ToArray());
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
