using System.Text.Json.Serialization;

namespace DiscordBot.Models;

/// <summary>
/// Win event posted to the arena-tracker sync worker. gameEnd is the match's
/// real end time (epoch ms, match-v5 info.gameEndTimestamp) so wins delivered
/// late (e.g. after a connectivity outage) keep their true date on the
/// dashboard's recent-wins banner; matchId lets the worker dedupe backfills.
/// </summary>
public sealed record ArenaWinEvent(
    [property: JsonPropertyName("summoner")] string Summoner,
    [property: JsonPropertyName("championName")] string ChampionName,
    [property: JsonPropertyName("matchId")] string MatchId,
    [property: JsonPropertyName("gameEnd")] long GameEnd,
    [property: JsonPropertyName("kills")] int Kills,
    [property: JsonPropertyName("deaths")] int Deaths,
    [property: JsonPropertyName("assists")] int Assists,
    [property: JsonPropertyName("items")] IReadOnlyList<int> Items);
