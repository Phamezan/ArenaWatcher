using System.Text.Json;
using DiscordBot.Models;

namespace DiscordBot.Services;

public static class ArenaMatchParser
{
    private static readonly HashSet<int> ArenaQueueIds = [1700, 1710, 1740, 1750];
    private static readonly HashSet<int> HiddenItemIds = [3348];

    public static bool IsArenaQueue(int queueId) => ArenaQueueIds.Contains(queueId);

    public static bool TryGetQueueId(JsonDocument match, out int queueId)
    {
        queueId = 0;
        return match.RootElement.TryGetProperty("info", out var info)
            && info.TryGetProperty("queueId", out var queue)
            && queue.ValueKind == JsonValueKind.Number
            && queue.TryGetInt32(out queueId);
    }

    public static IReadOnlyList<JsonElement> GetParticipants(JsonDocument match)
    {
        if (!match.RootElement.TryGetProperty("info", out var info)
            || !info.TryGetProperty("participants", out var participants)
            || participants.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return participants.EnumerateArray().ToArray();
    }

    public static int? GetPlacement(JsonElement participant)
    {
        if (participant.TryGetProperty("subteamPlacement", out var placement)
            && placement.ValueKind == JsonValueKind.Number
            && placement.TryGetInt32(out var value)
            && value > 0)
        {
            return value;
        }

        return null;
    }

    public static JsonElement? FindParticipant(JsonDocument match, string puuid)
    {
        foreach (var participant in GetParticipants(match))
        {
            if (participant.TryGetProperty("puuid", out var participantPuuid)
                && participantPuuid.GetString() == puuid)
            {
                return participant;
            }
        }

        return null;
    }

    public static bool ShouldPostArenaResult(JsonElement participant)
    {
        return HasArenaPlacement(participant) || IsArenaWin(participant);
    }

    public static bool IsArenaWin(JsonElement participant)
    {
        if (participant.TryGetProperty("subteamPlacement", out var placement)
            && placement.ValueKind == JsonValueKind.Number)
        {
            return placement.GetInt32() == 1;
        }

        return participant.TryGetProperty("win", out var win)
            && win.ValueKind is JsonValueKind.True;
    }

    private static bool HasArenaPlacement(JsonElement participant)
    {
        return participant.TryGetProperty("subteamPlacement", out var placement)
            && placement.ValueKind == JsonValueKind.Number
            && placement.GetInt32() > 0;
    }

    public static MatchSummary CreateSummary(TrackedPlayer player, JsonElement participant, string matchId)
    {
        return CreateSummary(player.DisplayName, participant, matchId);
    }

    public static MatchSummary CreateSummary(string playerName, JsonElement participant, string matchId)
    {
        var items = Enumerable.Range(0, 7)
            .Select(index => ReadInt(participant, $"item{index}"))
            .Where(itemId => itemId > 0 && !HiddenItemIds.Contains(itemId))
            .ToArray();

        var augments = Enumerable.Range(1, 6)
            .Select(index => ReadInt(participant, $"playerAugment{index}"))
            .Where(augmentId => augmentId > 0)
            .ToArray();

        var championName = ReadString(participant, "championName");
        var stats = new MatchStats(
            ReadInt(participant, "totalDamageDealtToChampions"),
            ReadInt(participant, "totalDamageTaken"),
            ReadInt(participant, "totalHeal"),
            ReadInt(participant, "damageSelfMitigated"));

        return new MatchSummary(playerName, championName, items, augments, matchId, GetPlacement(participant), stats);
    }

    public static string ReadRiotId(JsonElement participant)
    {
        var gameName = ReadString(participant, "riotIdGameName", ReadString(participant, "summonerName", "Unknown"));
        var tagLine = ReadString(participant, "riotIdTagline");
        return string.IsNullOrWhiteSpace(tagLine) ? gameName : $"{gameName}#{tagLine}";
    }

    public static ParticipantInspection CreateParticipantInspection(JsonElement participant)
    {
        return new ParticipantInspection(
            ReadRiotId(participant),
            ReadString(participant, "championName"),
            GetPlacement(participant),
            ReadNullableInt(participant, "subteamId"),
            ReadInt(participant, "totalDamageDealtToChampions"),
            ReadInt(participant, "totalDamageTaken"),
            ReadInt(participant, "totalHeal"));
    }

    private static string ReadString(JsonElement element, string propertyName, string fallback = "Unknown")
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static int? ReadNullableInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
            ? value
            : null;
    }
}
