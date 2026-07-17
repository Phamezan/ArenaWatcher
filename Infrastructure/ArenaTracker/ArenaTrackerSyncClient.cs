using System.Text;
using System.Text.Json;
using DiscordBot.Serialization;

namespace DiscordBot.Infrastructure.ArenaTracker;

/// <summary>
/// Posts win events to the arena-tracker Cloudflare Worker so the shared
/// dashboard updates automatically. See arena-tracker/worker/sync-worker.js
/// for the receiving end.
/// </summary>
public sealed class ArenaTrackerSyncClient(HttpClient httpClient, string webhookUrl, string syncKey)
    : IArenaTrackerNotifier
{
    public async Task NotifyWinAsync(string summoner, string championName, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { summoner, championName }, JsonOptions.Default);

        using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Sync-Key", syncKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Arena tracker sync returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }
    }
}
