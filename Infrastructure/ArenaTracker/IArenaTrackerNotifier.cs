using DiscordBot.Models;

namespace DiscordBot.Infrastructure.ArenaTracker;

public interface IArenaTrackerNotifier
{
    Task NotifyWinAsync(ArenaWinEvent win, CancellationToken cancellationToken);

    /// <summary>Posts a full season snapshot (overwrites the player's data file).</summary>
    Task NotifySnapshotAsync(object snapshot, CancellationToken cancellationToken);

    /// <summary>Posts the latest poll result so the dashboard can warn when the watcher is stuck.</summary>
    Task NotifyHealthAsync(WatcherHealth health, CancellationToken cancellationToken);
}
