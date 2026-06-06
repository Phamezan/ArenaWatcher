namespace DiscordBot.Models;

public sealed record MatchSummary(
    string PlayerName,
    string ChampionName,
    IReadOnlyList<int> ItemIds,
    IReadOnlyList<int> AugmentIds,
    string MatchId,
    int? Placement,
    MatchStats Stats);
