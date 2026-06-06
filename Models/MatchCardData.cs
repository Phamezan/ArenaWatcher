namespace DiscordBot.Models;

public sealed record MatchCardData(
    string PlayerName,
    string ChampionName,
    string ChampionIconUrl,
    IReadOnlyList<ItemAsset> Items,
    IReadOnlyList<AugmentAsset> Augments,
    string MatchId,
    int? Placement,
    MatchStats Stats);
