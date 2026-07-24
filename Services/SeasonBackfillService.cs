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

    public async Task RunIfSeasonChangedAsync(CancellationToken cancellationToken)
    {
        var seasonStart = await FetchSeasonStartAsync(cancellationToken);
        if (seasonStart is null)
        {
            return;
        }

        var statePath = SeasonStatePath();
        var lastProcessed = File.Exists(statePath)
            ? (await File.ReadAllTextAsync(statePath, cancellationToken)).Trim()
            : null;

        if (lastProcessed == seasonStart)
        {
            Console.WriteLine($"Season backfill already done for season start {seasonStart}.");
            return;
        }

        Console.WriteLine($"New Arena season detected (start {seasonStart}, was {lastProcessed ?? "none"}). Running full season backfill...");
        await BackfillAllPlayersAsync(seasonStart, cancellationToken);
        await File.WriteAllTextAsync(statePath, seasonStart, cancellationToken);
    }

    public async Task ForceBackfillAsync(CancellationToken cancellationToken)
    {
        var seasonStart = await FetchSeasonStartAsync(cancellationToken)
            ?? throw new InvalidOperationException("No season config found (data/season.json via RosterUrl).");
        await BackfillAllPlayersAsync(seasonStart, cancellationToken);
        await File.WriteAllTextAsync(SeasonStatePath(), seasonStart, cancellationToken);
    }

    private async Task BackfillAllPlayersAsync(string seasonStart, CancellationToken cancellationToken)
    {
        var startSeconds = new DateTimeOffset(DateOnly.Parse(seasonStart), TimeOnly.MinValue, TimeSpan.Zero)
            .ToUnixTimeSeconds();
        var champions = await leagueAssetProvider.GetChampionsAsync(cancellationToken);

        foreach (var player in config.TrackedPlayers)
        {
            try
            {
                await BackfillPlayerAsync(player, startSeconds, seasonStart, champions, cancellationToken);
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
}
