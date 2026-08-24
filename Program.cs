using DiscordBot.Configuration;
using DiscordBot.Infrastructure.ArenaTracker;
using DiscordBot.Infrastructure.Discord;
using DiscordBot.Infrastructure.LeagueAssets;
using DiscordBot.Infrastructure.Riot;
using DiscordBot.Models;
using DiscordBot.Persistence;
using DiscordBot.Rendering;
using DiscordBot.Services;
using System.Runtime.InteropServices;

var configPath = GetConfigPath(args)
    ?? Environment.GetEnvironmentVariable("ARENA_BOT_CONFIG")
    ?? "appsettings.json";
var config = AppConfigLoader.Load(configPath);
// Recycle pooled connections so DNS changes (and stale upstream sockets) are
// picked up instead of being held for the life of the process.
using var httpHandler = new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
    ConnectTimeout = TimeSpan.FromSeconds(15),
};
// No global request timeout: a Riot call and an arena-tracker sync (which
// drives a chain of GitHub commits) have very different budgets. RiotClient
// applies its own per-request deadline; ConnectTimeout above still bounds a
// dead host, which is what the handler was added for.
using var httpClient = new HttpClient(httpHandler);
IArenaTrackerNotifier arenaTrackerNotifier =
    string.IsNullOrWhiteSpace(config.ArenaTrackerWebhookUrl) || string.IsNullOrWhiteSpace(config.ArenaTrackerSyncKey)
        ? new NullArenaTrackerNotifier()
        : new ArenaTrackerSyncClient(httpClient, config.ArenaTrackerWebhookUrl, config.ArenaTrackerSyncKey);

try
{
    config = await RosterClient.ApplyRosterAsync(httpClient, config);
}
catch (Exception ex)
{
    // Startup failures never reach a poll cycle, so without this the dashboard
    // would only see silence and blame the watcher for "not reporting in".
    await ReportStartupFailureAsync(arenaTrackerNotifier, ex, config.PollIntervalSeconds);
    throw;
}

var riotClient = new RiotClient(httpClient, config.RiotApiKey, config.RegionalRoute);
var discordClient = new DiscordWebhookClient(httpClient, config.DiscordWebhookUrl);
var leagueAssetProvider = new LeagueAssetProvider(httpClient);
var matchCardRenderer = new MatchCardRenderer(httpClient);
var seenMatchStore = await SeenMatchStore.LoadAsync(config.SeenMatchesPath);
var pendingWinStore = await PendingWinStore.LoadAsync(
    Path.Combine(Path.GetDirectoryName(config.SeenMatchesPath) ?? ".", "pending-wins.json"));
var watcher = new ArenaWatcherService(riotClient, discordClient, arenaTrackerNotifier, leagueAssetProvider, matchCardRenderer, seenMatchStore, pendingWinStore, config);
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

if (args.Contains("--admin-ui-only", StringComparer.OrdinalIgnoreCase))
{
    // Diagnostic: serve just the config admin UI without the Riot polling loop.
    var port = config.WebUiPort ?? throw new InvalidOperationException("Set WebUiPort to use --admin-ui-only.");
    await new AdminUiServer(config, configPath, port, config.WebUiToken, shutdown.Cancel).RunAsync(shutdown.Token);
    return;
}

using var sigTermRegistration = RegisterSigTermHandler(shutdown);

Task? adminUiTask = null;
if (config.WebUiPort is int webUiPort)
{
    var commandRunner = new AdminCommandRunner(watcher, seasonBackfill, matchCardRenderer, shutdown.Cancel);
    var adminUi = new AdminUiServer(config, configPath, webUiPort, config.WebUiToken, shutdown.Cancel, commandRunner);
    adminUiTask = adminUi.RunAsync(shutdown.Token);
    if (string.IsNullOrWhiteSpace(config.WebUiToken))
    {
        Console.WriteLine("WebUiToken is not set — admin UI is unauthenticated (page shows no secrets).");
    }
}

await seasonBackfill.RunIfSeasonChangedAsync(shutdown.Token);
await watcher.RunAsync(shutdown.Token);

if (adminUiTask is not null)
{
    await adminUiTask;
}

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

static async Task ReportStartupFailureAsync(
    IArenaTrackerNotifier notifier,
    Exception ex,
    int pollIntervalSeconds)
{
    var health = WatcherHealth.Startup($"{ex.GetType().Name}: {ex.Message}", pollIntervalSeconds);
    Console.WriteLine($"[{DateTimeOffset.Now:t}] {health.Summary}");

    try
    {
        await notifier.NotifyHealthAsync(health, CancellationToken.None);
    }
    catch (Exception reportEx)
    {
        Console.WriteLine($"[{DateTimeOffset.Now:t}] Could not report startup failure: {reportEx.Message}");
    }
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
