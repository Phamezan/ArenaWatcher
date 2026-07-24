using System.Text.Json;
using DiscordBot.Models;

namespace DiscordBot.Infrastructure.Riot;

public interface IRiotClient
{
    Task<RiotAccount> GetAccountByRiotIdAsync(string gameName, string tagLine, CancellationToken cancellationToken);

    Task<List<string>> GetRecentMatchIdsAsync(string puuid, int count, CancellationToken cancellationToken);

    /// <summary>All match ids for one queue since the given epoch seconds (paged).</summary>
    Task<List<string>> GetMatchIdsAsync(string puuid, int queueId, long startTimeSeconds, CancellationToken cancellationToken);

    Task<JsonDocument> GetMatchAsync(string matchId, CancellationToken cancellationToken);
}
