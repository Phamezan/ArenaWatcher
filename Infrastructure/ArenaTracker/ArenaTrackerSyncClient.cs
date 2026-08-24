using System.Text;
using System.Text.Json;
using DiscordBot.Infrastructure.Http;
using DiscordBot.Models;
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
    private const int MaxAttempts = 3;

    public async Task NotifyWinAsync(ArenaWinEvent win, CancellationToken cancellationToken)
    {
        await PostAsync(win, cancellationToken);
    }

    public async Task NotifySnapshotAsync(object snapshot, CancellationToken cancellationToken)
    {
        await PostAsync(snapshot, cancellationToken);
    }

    public async Task NotifyHealthAsync(WatcherHealth health, CancellationToken cancellationToken)
    {
        await PostAsync(health, cancellationToken, HeartbeatUrl());
    }

    /// <summary>The Worker serves heartbeats on /heartbeat, next to the sync endpoint.</summary>
    private string HeartbeatUrl() => new UriBuilder(webhookUrl) { Path = "/heartbeat", Query = string.Empty }.Uri.ToString();

    private async Task PostAsync(object payload, CancellationToken cancellationToken, string? url = null)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions.Default);

        var target = url ?? webhookUrl;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var lastAttempt = attempt == MaxAttempts;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, target)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Add("X-Sync-Key", syncKey);

                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var failure = new HttpRequestException(
                    $"Arena tracker sync returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");

                // 4xx means this payload will never be accepted; only 5xx is worth another try.
                if (lastAttempt || (int)response.StatusCode < 500)
                {
                    throw failure;
                }

                Console.WriteLine($"[{DateTimeOffset.Now:t}] Arena tracker sync returned {(int)response.StatusCode}; retry {attempt}/{MaxAttempts - 1}.");
            }
            catch (Exception ex) when (!lastAttempt && TransientHttpFailure.Matches(ex, cancellationToken))
            {
                Console.WriteLine($"[{DateTimeOffset.Now:t}] Arena tracker sync failed ({ex.GetType().Name}: {ex.Message}); retry {attempt}/{MaxAttempts - 1}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
        }
    }
}
