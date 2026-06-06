namespace DiscordBot.Models;

public sealed record MatchStats(
    int DamageDealtToChampions,
    int DamageTaken,
    int HealingDone,
    int DamageMitigated);
