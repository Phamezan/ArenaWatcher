using System.Net.Http.Json;
using DiscordBot.Configuration;
using DiscordBot.Infrastructure.Http;
using DiscordBot.Serialization;

namespace DiscordBot.Infrastructure.ArenaTracker;

/// <summary>
/// Loads the tracked-player roster from a shared URL (the arena-tracker
/// repo's data/players.json, a plain JSON array of "Name#Tag" Riot IDs), so
/// the friend list lives in one place for both the watcher and the tracker
/// worker. Falls back to TrackedPlayers from appsettings when the roster
/// can't be fetched.
/// </summary>
public static class RosterClient
{
    // The roster is fetched once at startup and a failure is fatal when
    // appsettings has no TrackedPlayers fallback, so this retries for longer
    // than a mid-run API call would: a boot during a DNS outage should wait
    // it out rather than crash-loop the container.
    private const int MaxAttempts = 5;

    public static async Task<AppConfig> ApplyRosterAsync(
        HttpClient httpClient,
        AppConfig config,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.RosterUrl))
        {
            return config with { TrackedPlayers = config.TrackedPlayers ?? [] };
        }

        try
        {
            var riotIds = await FetchRosterAsync(httpClient, config.RosterUrl, cancellationToken);
            var players = (riotIds ?? [])
                .Select(id => id.Split('#', 2))
                .Where(parts => parts.Length == 2
                    && !string.IsNullOrWhiteSpace(parts[0])
                    && !string.IsNullOrWhiteSpace(parts[1]))
                .Select(parts => new PlayerConfig(parts[0].Trim(), parts[1].Trim()))
                .ToList();

            if (players.Count == 0)
            {
                throw new InvalidOperationException("Roster contained no valid \"Name#Tag\" entries.");
            }

            Console.WriteLine($"Loaded {players.Count} tracked player(s) from roster: {config.RosterUrl}");
            return config with { TrackedPlayers = players };
        }
        catch (Exception ex)
        {
            if (config.TrackedPlayers is not null && config.TrackedPlayers.Count > 0)
            {
                Console.WriteLine($"Could not load roster ({ex.Message}); falling back to TrackedPlayers from appsettings.");
                return config;
            }

            throw new InvalidOperationException(
                $"Could not load roster from {config.RosterUrl} and TrackedPlayers is empty: {ex.Message}", ex);
        }
    }

    private static async Task<List<string>?> FetchRosterAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await httpClient.GetFromJsonAsync<List<string>>(url, JsonOptions.Default, cancellationToken);
            }
            catch (Exception ex) when (attempt < MaxAttempts && TransientHttpFailure.Matches(ex, cancellationToken))
            {
                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                Console.WriteLine($"[{DateTimeOffset.Now:t}] Could not fetch roster ({ex.GetType().Name}: {ex.Message}); retrying in {backoff.TotalSeconds:0.#}s.");
                await Task.Delay(backoff, cancellationToken);
            }
        }

        // Unreachable: the final attempt rethrows rather than matching the filter.
        throw new HttpRequestException($"Roster fetch failed after {MaxAttempts} attempts: {url}");
    }
}
