using System.Text.Json.Serialization;

namespace DiscordBot.Models;

public sealed record RiotAccount(
    [property: JsonPropertyName("puuid")] string Puuid,
    [property: JsonPropertyName("gameName")] string GameName,
    [property: JsonPropertyName("tagLine")] string TagLine);
