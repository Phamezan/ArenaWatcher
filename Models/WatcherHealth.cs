using System.Text.Json.Serialization;

namespace DiscordBot.Models;

/// <summary>
/// One poll cycle's outcome, posted to the arena-tracker Worker so the
/// dashboard can tell visitors when wins are no longer being recorded.
/// </summary>
/// <param name="Status">"ok", "degraded" (some players failed) or "down" (none succeeded).</param>
public sealed record WatcherHealth(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("playersChecked")] int PlayersChecked,
    [property: JsonPropertyName("playersFailed")] int PlayersFailed,
    [property: JsonPropertyName("pollIntervalSeconds")] int PollIntervalSeconds)
{
    public const string Ok = "ok";
    public const string Degraded = "degraded";
    public const string Down = "down";

    public static WatcherHealth From(int checkedCount, int failedCount, string? error, int pollIntervalSeconds)
    {
        var status = failedCount == 0 ? Ok
            : failedCount >= checkedCount ? Down
            : Degraded;

        return new WatcherHealth(status, error, checkedCount, failedCount, pollIntervalSeconds);
    }
}
