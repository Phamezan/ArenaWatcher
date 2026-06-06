using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using DiscordBot.Models;
using DiscordBot.Serialization;

namespace DiscordBot.Infrastructure.Riot;

public sealed class RiotClient(HttpClient httpClient, string apiKey, string regionalRoute) : IRiotClient
{
    private const int MaxAttempts = 3;

    public async Task<RiotAccount> GetAccountByRiotIdAsync(
        string gameName,
        string tagLine,
        CancellationToken cancellationToken)
    {
        var urlGameName = Uri.EscapeDataString(gameName);
        var urlTagLine = Uri.EscapeDataString(tagLine);
        var url = $"https://{regionalRoute}.api.riotgames.com/riot/account/v1/accounts/by-riot-id/{urlGameName}/{urlTagLine}";

        return await GetJsonAsync<RiotAccount>(url, cancellationToken);
    }

    public async Task<List<string>> GetRecentMatchIdsAsync(
        string puuid,
        int count,
        CancellationToken cancellationToken)
    {
        var url = $"https://{regionalRoute}.api.riotgames.com/lol/match/v5/matches/by-puuid/{puuid}/ids?start=0&count={count}";
        return await GetJsonAsync<List<string>>(url, cancellationToken);
    }

    public async Task<JsonDocument> GetMatchAsync(string matchId, CancellationToken cancellationToken)
    {
        var url = $"https://{regionalRoute}.api.riotgames.com/lol/match/v5/matches/{matchId}";
        return await GetJsonDocumentAsync(url, cancellationToken);
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var request = BuildRequest(url);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (await ShouldRetryAsync(response, attempt, cancellationToken))
            {
                continue;
            }

            await EnsureSuccessAsync(response, cancellationToken);

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions.Default, cancellationToken)
                ?? throw new InvalidOperationException($"Riot returned an empty response for {url}.");
        }

        throw new HttpRequestException($"Riot API request failed after {MaxAttempts} attempts: {url}");
    }

    private async Task<JsonDocument> GetJsonDocumentAsync(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var request = BuildRequest(url);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (await ShouldRetryAsync(response, attempt, cancellationToken))
            {
                continue;
            }

            await EnsureSuccessAsync(response, cancellationToken);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }

        throw new HttpRequestException($"Riot API request failed after {MaxAttempts} attempts: {url}");
    }

    private HttpRequestMessage BuildRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Riot-Token", apiKey);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"Riot API returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
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

        Console.WriteLine($"[{DateTimeOffset.Now:t}] Riot API returned {(int)response.StatusCode}; retrying in {delay.TotalSeconds:0.#}s.");
        await Task.Delay(delay, cancellationToken);
        return true;
    }
}
