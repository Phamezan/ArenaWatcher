using System.Net.Sockets;

namespace DiscordBot.Infrastructure.Http;

/// <summary>
/// Classifies the transport-level failures worth retrying: DNS hiccups,
/// dropped connections and request timeouts, as opposed to a real HTTP error
/// or a caller-requested cancellation.
/// </summary>
public static class TransientHttpFailure
{
    public static bool Matches(Exception ex, CancellationToken cancellationToken)
    {
        // A caller-requested cancellation is not a transport failure; never retry it.
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return ex is HttpRequestException or SocketException or IOException
            // HttpClient surfaces its own request timeout as TaskCanceledException.
            or TaskCanceledException;
    }
}
