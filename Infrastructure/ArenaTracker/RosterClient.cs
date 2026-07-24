using System.Net.Http.Json;
using DiscordBot.Configuration;
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
    public static async Task<AppConfig> ApplyRosterAsync(HttpClient httpClient, AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.RosterUrl))
        {
            return config with { TrackedPlayers = config.TrackedPlayers ?? [] };
        }

        try
        {
            var riotIds = await httpClient.GetFromJsonAsync<List<string>>(config.RosterUrl, JsonOptions.Default);
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
}
