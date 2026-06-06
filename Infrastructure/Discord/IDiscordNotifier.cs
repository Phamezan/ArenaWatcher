using DiscordBot.Models;

namespace DiscordBot.Infrastructure.Discord;

public interface IDiscordNotifier
{
    Task PostArenaResultAsync(MatchSummary summary, byte[]? resultCard, CancellationToken cancellationToken);
}
