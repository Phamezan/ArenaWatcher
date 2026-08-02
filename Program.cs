using DiscordBot.Configuration;
using DiscordBot.Infrastructure.ArenaTracker;
using DiscordBot.Infrastructure.Discord;
using DiscordBot.Infrastructure.LeagueAssets;
using DiscordBot.Infrastructure.Riot;
using DiscordBot.Persistence;
using DiscordBot.Rendering;
using DiscordBot.Services;
using System.Runtime.InteropServices;

var config = AppConfigLoader.Load(GetConfigPath(args));
using var httpClient = new HttpClient();
config = await RosterClient.ApplyRosterAsync(httpClient, config);

var riotClient = new RiotClient(httpClient, config.RiotApiKey, config.RegionalRoute);
var discordClient = new DiscordWebhookClient(httpClient, config.DiscordWebhookUrl);
IArenaTrackerNotifier arenaTrackerNotifier =
    string.IsNullOrWhiteSpace(config.ArenaTrackerWebhookUrl) || string.IsNullOrWhiteSpace(config.ArenaTrackerSyncKey)
        ? new NullArenaTrackerNotifier()
        : new ArenaTrackerSyncClient(httpClient, config.ArenaTrackerWebhookUrl, config.ArenaTrackerSyncKey);
var leagueAssetProvider = new LeagueAssetProvider(httpClient);
var matchCardRenderer = new MatchCardRenderer(httpClient);
var seenMatchStore = await SeenMatchStore.LoadAsync(config.SeenMatchesPath);
var watcher = new ArenaWatcherService(riotClient, discordClient, arenaTrackerNotifier, leagueAssetProvider, matchCardRenderer, seenMatchStore, config);
var seasonBackfill = new SeasonBackfillService(riotClient, leagueAssetProvider, arenaTrackerNotifier, httpClient, config);

if (args.Contains("--backfill-season", StringComparer.OrdinalIgnoreCase))
{
    await seasonBackfill.ForceBackfillAsync(args.Contains("--full", StringComparer.OrdinalIgnoreCase), CancellationToken.None);
    return;
}

if (args.Contains("--calibrate-season", StringComparer.OrdinalIgnoreCase))
{
    var riotId = GetFlagValue(args, "--calibrate-season")
        ?? throw new ArgumentException("--calibrate-season requires a Riot ID, e.g.: --calibrate-season \"GameName#TagLine\"");
    var since = GetFlagValue(args, "--since") ?? "2026-01-01";
    await seasonBackfill.CalibrateAsync(riotId, since, CancellationToken.None);
    return;
}

if (args.Contains("--post-latest", StringComparer.OrdinalIgnoreCase))
{
    await watcher.PostLatestMatchForTrackedPlayersAsync();
    return;
}

if (args.Contains("--post-latest-for", StringComparer.OrdinalIgnoreCase))
{
    await watcher.PostLatestMatchForPlayerAsync(GetPostLatestForRiotId(args));
    return;
}

if (args.Contains("--inspect-latest", StringComparer.OrdinalIgnoreCase))
{
    await watcher.InspectLatestMatchForTrackedPlayersAsync();
    return;
}

if (args.Contains("--post-latest-group-test", StringComparer.OrdinalIgnoreCase))
{
    await watcher.PostLatestGroupTestForTrackedPlayersAsync();
    return;
}

if (args.Contains("--render-layout-test", StringComparer.OrdinalIgnoreCase))
{
    var outputPath = Path.Combine(AppContext.BaseDirectory, "layout-test-group.png");
    var testCards = LayoutTestData.CreateGroupCards();
    var imageBytes = await matchCardRenderer.RenderGroupAsync(testCards, CancellationToken.None);
    await File.WriteAllBytesAsync(outputPath, imageBytes);
    Console.WriteLine($"Rendered layout test card: {outputPath}");
    return;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

using var sigTermRegistration = RegisterSigTermHandler(shutdown);

await seasonBackfill.RunIfSeasonChangedAsync(shutdown.Token);
await watcher.RunAsync(shutdown.Token);

static string? GetFlagValue(string[] args, string flag)
{
    for (var index = 0; index < args.Length; index++)
    {
        if (args[index].Equals(flag, StringComparison.OrdinalIgnoreCase)
            && index + 1 < args.Length
            && !string.IsNullOrWhiteSpace(args[index + 1]))
        {
            return args[index + 1];
        }
    }

    return null;
}

static string? GetConfigPath(string[] args)
{
    for (var index = 0; index < args.Length; index++)
    {
        if (!args[index].Equals("--config", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException("--config requires a path, for example: --config appsettings.test.json");
        }

        return args[index + 1];
    }

    return null;
}

static string GetPostLatestForRiotId(string[] args)
{
    for (var index = 0; index < args.Length; index++)
    {
        if (!args[index].Equals("--post-latest-for", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException("--post-latest-for requires a Riot ID, for example: --post-latest-for \"GameName#TagLine\"");
        }

        return args[index + 1];
    }

    throw new ArgumentException("--post-latest-for requires a Riot ID, for example: --post-latest-for \"GameName#TagLine\"");
}

static IDisposable? RegisterSigTermHandler(CancellationTokenSource shutdown)
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        return null;
    }

    return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        shutdown.Cancel();
    });
}
