namespace DiscordBot.Infrastructure.ArenaTracker;

public interface IArenaTrackerNotifier
{
    Task NotifyWinAsync(string summoner, string championName, CancellationToken cancellationToken);
}
