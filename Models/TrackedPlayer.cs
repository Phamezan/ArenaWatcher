namespace DiscordBot.Models;

public sealed record TrackedPlayer(string GameName, string TagLine, string Puuid)
{
    public string DisplayName => $"{GameName}#{TagLine}";
}
