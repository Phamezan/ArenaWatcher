using System.Text.Json;
using DiscordBot.Serialization;

namespace DiscordBot.Configuration;

public static class AppConfigLoader
{
    public static AppConfig Load(string? configPath = null)
    {
        var path = configPath ?? Environment.GetEnvironmentVariable("ARENA_BOT_CONFIG") ?? "appsettings.json";
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Missing config file '{path}'. Copy appsettings.example.json to appsettings.json and fill in your keys.");
        }

        var config = JsonSerializer.Deserialize<AppConfig>(
            File.ReadAllText(path),
            JsonOptions.Default) ?? throw new InvalidOperationException("Config file is empty.");

        var riotApiKey = Environment.GetEnvironmentVariable("RIOT_API_KEY") ?? config.RiotApiKey;
        var discordWebhookUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL") ?? config.DiscordWebhookUrl;
        var arenaTrackerWebhookUrl = Environment.GetEnvironmentVariable("ARENA_TRACKER_WEBHOOK_URL") ?? config.ArenaTrackerWebhookUrl;
        var arenaTrackerSyncKey = Environment.GetEnvironmentVariable("ARENA_TRACKER_SYNC_KEY") ?? config.ArenaTrackerSyncKey;

        if (string.IsNullOrWhiteSpace(riotApiKey) || riotApiKey.Contains("replace-me", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Set RiotApiKey in appsettings.json or RIOT_API_KEY.");
        }

        if (string.IsNullOrWhiteSpace(discordWebhookUrl) || discordWebhookUrl.Contains("replace-me", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Set DiscordWebhookUrl in appsettings.json or DISCORD_WEBHOOK_URL.");
        }

        if (config.TrackedPlayers is null || config.TrackedPlayers.Count == 0)
        {
            throw new InvalidOperationException("Add at least one player to TrackedPlayers.");
        }

        var duplicatePlayers = config.TrackedPlayers
            .GroupBy(player => $"{player.GameName.Trim()}#{player.TagLine.Trim()}", StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicatePlayers.Length > 0)
        {
            throw new InvalidOperationException($"Remove duplicate tracked player(s): {string.Join(", ", duplicatePlayers)}.");
        }

        foreach (var player in config.TrackedPlayers)
        {
            if (string.IsNullOrWhiteSpace(player.GameName) || string.IsNullOrWhiteSpace(player.TagLine))
            {
                throw new InvalidOperationException("Every tracked player must have both GameName and TagLine.");
            }
        }

        return config with
        {
            RiotApiKey = riotApiKey,
            DiscordWebhookUrl = discordWebhookUrl,
            PollIntervalSeconds = Math.Max(config.PollIntervalSeconds, 60),
            ArenaTrackerWebhookUrl = arenaTrackerWebhookUrl,
            ArenaTrackerSyncKey = arenaTrackerSyncKey
        };
    }
}
