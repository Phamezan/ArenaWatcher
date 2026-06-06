using DiscordBot.Models;

namespace DiscordBot.Rendering;

public interface IMatchCardRenderer
{
    Task<byte[]> RenderAsync(MatchCardData cardData, CancellationToken cancellationToken);

    Task<byte[]> RenderGroupAsync(IReadOnlyList<MatchCardData> cards, CancellationToken cancellationToken);
}
