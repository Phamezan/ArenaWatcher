using System.Text;
using System.Text.Json;
using System.Net;
using DiscordBot.Models;
using DiscordBot.Serialization;

namespace DiscordBot.Infrastructure.Discord;

public sealed class DiscordWebhookClient(HttpClient httpClient, string webhookUrl) : IDiscordNotifier
{
    private const int MaxAttempts = 3;

    public async Task PostArenaResultAsync(MatchSummary summary, byte[]? resultCard, CancellationToken cancellationToken)
    {
        var content = resultCard is null
            ? $"{summary.PlayerName} finished an Arena game on {summary.ChampionName}."
            : string.Empty;

        var payload = JsonSerializer.Serialize(new
        {
            username = "Arena Watcher",
            content,
            attachments = resultCard is null
                ? []
                : new[]
                {
                    new
                    {
                        id = 0,
                        filename = "arena-result.png"
                    }
                }
        }, JsonOptions.Default);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var form = CreatePayload(payload, resultCard);
            using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
            {
                Content = form
            };

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            if (await ShouldRetryAsync(response, attempt, cancellationToken))
            {
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Discord webhook returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        throw new HttpRequestException($"Discord webhook failed after {MaxAttempts} attempts.");
    }

    private static MultipartFormDataContent CreatePayload(string payload, byte[]? resultCard)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json" }
        };

        if (resultCard is not null)
        {
            form.Add(new ByteArrayContent(resultCard), "files[0]", "arena-result.png");
        }

        return form;
    }

    private static async Task<bool> ShouldRetryAsync(
        HttpResponseMessage response,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (attempt >= MaxAttempts)
        {
            return false;
        }

        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout))
        {
            return false;
        }

        var delay = response.Headers.RetryAfter?.Delta
            ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));

        Console.WriteLine($"[{DateTimeOffset.Now:t}] Discord webhook returned {(int)response.StatusCode}; retrying in {delay.TotalSeconds:0.#}s.");
        await Task.Delay(delay, cancellationToken);
        return true;
    }
}
