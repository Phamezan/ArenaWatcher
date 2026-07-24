using DiscordBot.Models;

namespace DiscordBot.Infrastructure.LeagueAssets;

public interface ILeagueAssetProvider
{
    Task<MatchCardData> BuildCardDataAsync(MatchSummary summary, CancellationToken cancellationToken);

    /// <summary>Every champion (numeric id + display name) from the latest Data Dragon.</summary>
    Task<IReadOnlyList<ChampionInfo>> GetChampionsAsync(CancellationToken cancellationToken);
}
