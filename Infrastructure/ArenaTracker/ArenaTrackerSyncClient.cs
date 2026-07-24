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
        await PostAsync(new { summoner, championName }, cancellationToken);
    }

    public async Task NotifySnapshotAsync(object snapshot, CancellationToken cancellationToken)
    {
        await PostAsync(snapshot, cancellationToken);
    }

    private async Task PostAsync(object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions.Default);

        using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
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
