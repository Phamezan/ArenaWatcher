using System.Net.Http.Json;
using System.Text.Json;
using DiscordBot.Configuration;
using DiscordBot.Infrastructure.Http;
using DiscordBot.Serialization;

namespace DiscordBot.Infrastructure.ArenaTracker;

/// <summary>
/// Loads the tracked-player roster from a shared URL (the arena-tracker
/// repo's data/players.json, a plain JSON array of "Name#Tag" Riot IDs), so
/// the friend list lives in one place for both the watcher and the tracker
/// worker.
///
/// Every successful fetch is cached next to the seen-matches file. When the
/// fetch fails the cache is used instead, so an outage cannot stop the watcher
/// and the roster still only has to be maintained in one place. Falls back to
/// TrackedPlayers from appsettings only if there is no cache either.
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

        var cachePath = CachePath(config.SeenMatchesPath);
        List<string>? riotIds;
        var source = config.RosterUrl;

        try
        {
            riotIds = await FetchRosterAsync(httpClient, config.RosterUrl, cancellationToken);
            await SaveCacheAsync(cachePath, riotIds, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            riotIds = await ReadCacheAsync(cachePath, cancellationToken);
            if (riotIds is null)
            {
                return FallBackToAppSettings(config, ex);
            }

            source = $"cache {cachePath}";
            Console.WriteLine($"Could not fetch roster ({ex.Message}); using the cached roster from {cachePath}.");
        }

        try
        {
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

            Console.WriteLine($"Loaded {players.Count} tracked player(s) from roster: {source}");
            return config with { TrackedPlayers = players };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return FallBackToAppSettings(config, ex);
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

    /// <summary>Cache lives beside seen-matches.json, which is already a persisted volume.</summary>
    private static string CachePath(string seenMatchesPath) =>
        Path.Combine(Path.GetDirectoryName(seenMatchesPath) ?? ".", "roster-cache.json");

    private static async Task SaveCacheAsync(string path, List<string>? riotIds, CancellationToken cancellationToken)
    {
        if (riotIds is null || riotIds.Count == 0)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(riotIds, JsonOptions.Default), cancellationToken);
        }
        catch (Exception ex)
        {
            // A cache we cannot write is a lost safety net, not a reason to fail startup.
            Console.WriteLine($"Could not write the roster cache to {path}: {ex.Message}");
        }
    }

    private static async Task<List<string>?> ReadCacheAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var cached = JsonSerializer.Deserialize<List<string>>(
                await File.ReadAllTextAsync(path, cancellationToken), JsonOptions.Default);

            return cached is { Count: > 0 } ? cached : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not read the roster cache at {path}: {ex.Message}");
            return null;
        }
    }

    private static AppConfig FallBackToAppSettings(AppConfig config, Exception ex)
    {
        if (config.TrackedPlayers is { Count: > 0 })
        {
            Console.WriteLine($"Could not load roster ({ex.Message}); falling back to TrackedPlayers from appsettings.");
            return config;
        }

        throw new InvalidOperationException(
            $"Could not load roster from {config.RosterUrl}, no cached roster, and TrackedPlayers is empty: {ex.Message}", ex);
    }
}
