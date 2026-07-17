namespace DiscordBot.Infrastructure.ArenaTracker;

/// <summary>Used when ArenaTrackerWebhookUrl isn't configured — dashboard sync is optional.</summary>
public sealed class NullArenaTrackerNotifier : IArenaTrackerNotifier
{
    public Task NotifyWinAsync(string summoner, string championName, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
