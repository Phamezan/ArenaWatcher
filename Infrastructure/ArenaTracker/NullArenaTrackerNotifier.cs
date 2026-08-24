using DiscordBot.Models;

namespace DiscordBot.Infrastructure.ArenaTracker;

/// <summary>Used when ArenaTrackerWebhookUrl isn't configured — dashboard sync is optional.</summary>
public sealed class NullArenaTrackerNotifier : IArenaTrackerNotifier
{
    public Task NotifyWinAsync(ArenaWinEvent win, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task NotifySnapshotAsync(object snapshot, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task NotifyHealthAsync(WatcherHealth health, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
