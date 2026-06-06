namespace DiscordBot.Models;

public sealed record ParticipantInspection(
    string RiotId,
    string ChampionName,
    int? Placement,
    int? SubteamId,
    int DamageDealtToChampions,
    int DamageTaken,
    int HealingDone);
