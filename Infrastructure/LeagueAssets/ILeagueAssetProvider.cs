using DiscordBot.Models;

namespace DiscordBot.Infrastructure.LeagueAssets;

public interface ILeagueAssetProvider
{
    Task<MatchCardData> BuildCardDataAsync(MatchSummary summary, CancellationToken cancellationToken);
}
