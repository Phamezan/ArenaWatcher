namespace DiscordBot.Configuration;

public sealed record AppConfig(
    string RiotApiKey,
    string DiscordWebhookUrl,
    string RegionalRoute,
    int PollIntervalSeconds,
    string SeenMatchesPath,
    List<PlayerConfig> TrackedPlayers,
    string? ArenaTrackerWebhookUrl = null,
    string? ArenaTrackerSyncKey = null);
