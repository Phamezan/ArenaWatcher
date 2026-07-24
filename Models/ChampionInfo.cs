namespace DiscordBot.Models;

/// <summary>A champion as listed in Data Dragon (numeric id + display name).</summary>
public sealed record ChampionInfo(int Id, string Name);
