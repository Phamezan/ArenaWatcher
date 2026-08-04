using DiscordBot.Models;

namespace DiscordBot.Infrastructure.ArenaTracker;

public interface IArenaTrackerNotifier
{
    Task NotifyWinAsync(ArenaWinEvent win, CancellationToken cancellationToken);

    /// <summary>Posts a full season snapshot (overwrites the player's data file).</summary>
    Task NotifySnapshotAsync(object snapshot, CancellationToken cancellationToken);
}
