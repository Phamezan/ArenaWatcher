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

    /// <summary>The process died before it ever polled (e.g. the roster fetch failed).</summary>
    public const string StartupFailed = "startup-failed";

    public static WatcherHealth Startup(string error, int pollIntervalSeconds) =>
        new(StartupFailed, error, PlayersChecked: 0, PlayersFailed: 0, pollIntervalSeconds);

    /// <summary>Single-line summary for the container log.</summary>
    [JsonIgnore]
    public string Summary => Status switch
    {
        Ok => $"health ok: {PlayersChecked} player(s) checked",
        StartupFailed => $"health startup-failed: {Error}",
        _ => $"health {Status}: {PlayersFailed}/{PlayersChecked} player(s) failed: {Error}",
    };

    public static WatcherHealth From(int checkedCount, int failedCount, string? error, int pollIntervalSeconds)
    {
        var status = failedCount == 0 ? Ok
            : failedCount >= checkedCount ? Down
            : Degraded;

        return new WatcherHealth(status, error, checkedCount, failedCount, pollIntervalSeconds);
    }
}
