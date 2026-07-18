using DiscordBot.Configuration;
using DiscordBot.Infrastructure.ArenaTracker;
using DiscordBot.Infrastructure.Discord;
using DiscordBot.Infrastructure.LeagueAssets;
using DiscordBot.Infrastructure.Riot;
using DiscordBot.Models;
using DiscordBot.Persistence;
using DiscordBot.Rendering;

namespace DiscordBot.Services;

public sealed class ArenaWatcherService(
    IRiotClient riotClient,
    IDiscordNotifier discordNotifier,
    IArenaTrackerNotifier arenaTrackerNotifier,
    ILeagueAssetProvider leagueAssetProvider,
    IMatchCardRenderer matchCardRenderer,
    SeenMatchStore seenMatchStore,
    AppConfig config)
{
    public async Task PostLatestMatchForTrackedPlayersAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Posting latest match for {config.TrackedPlayers.Count} tracked player(s).");

        var trackedPlayers = await ResolvePlayersAsync(cancellationToken);

        foreach (var player in trackedPlayers)
        {
            try
            {
                await PostLatestMatchAsync(player, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.DisplayName}: {ex.Message}");
            }
        }
    }

    public async Task PostLatestMatchForPlayerAsync(string riotId, CancellationToken cancellationToken = default)
    {
        var parts = riotId.Split('#', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            Console.WriteLine($"Invalid Riot ID '{riotId}'. Expected format: GameName#TagLine.");
            return;
        }

        var gameName = parts[0];
        var tagLine = parts[1];

        TrackedPlayer player;
        try
        {
            var account = await riotClient.GetAccountByRiotIdAsync(gameName, tagLine, cancellationToken);
            player = new TrackedPlayer(gameName, tagLine, account.Puuid);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not resolve {riotId}: {ex.Message}");
            return;
        }

        Console.WriteLine($"Posting latest match for {player.DisplayName}.");
        await PostLatestMatchAsync(player, cancellationToken, syncWinToArenaTracker: true);
    }

    public async Task InspectLatestMatchForTrackedPlayersAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Inspecting latest match for {config.TrackedPlayers.Count} tracked player(s).");

        var trackedPlayers = await ResolvePlayersAsync(cancellationToken);

        foreach (var player in trackedPlayers)
        {
            try
            {
                await InspectLatestMatchAsync(player, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.DisplayName}: {ex.Message}");
            }
        }
    }

    public async Task PostLatestGroupTestForTrackedPlayersAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Posting latest group test for {config.TrackedPlayers.Count} tracked player(s).");

        var trackedPlayers = await ResolvePlayersAsync(cancellationToken);

        foreach (var player in trackedPlayers)
        {
            try
            {
                await PostLatestGroupTestAsync(player, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.DisplayName}: {ex.Message}");
            }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"Arena watcher started for {config.TrackedPlayers.Count} player(s).");
        Console.WriteLine($"Polling every {config.PollIntervalSeconds} seconds. Press Ctrl+C to stop.");

        var trackedPlayers = await ResolvePlayersAsync(cancellationToken);
        await PrimeSeenMatchesAsync(trackedPlayers, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:t}] Polling Riot for recent matches...");
            await CheckTrackedPlayersAsync(trackedPlayers, cancellationToken);

            await seenMatchStore.SaveAsync();
            await Task.Delay(TimeSpan.FromSeconds(config.PollIntervalSeconds), cancellationToken);
        }

        await seenMatchStore.SaveAsync();
    }

    private async Task<List<TrackedPlayer>> ResolvePlayersAsync(CancellationToken cancellationToken)
    {
        var resolved = new List<TrackedPlayer>();

        foreach (var player in config.TrackedPlayers)
        {
            try
            {
                var account = await riotClient.GetAccountByRiotIdAsync(player.GameName, player.TagLine, cancellationToken);
                resolved.Add(new TrackedPlayer(player.GameName, player.TagLine, account.Puuid));
                Console.WriteLine($"Tracking {player.GameName}#{player.TagLine}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:t}] Could not resolve {player.GameName}#{player.TagLine}: {ex.Message}");
            }
        }

        if (resolved.Count == 0)
        {
            throw new InvalidOperationException("No tracked players could be resolved through Riot Account API.");
        }

        return resolved;
    }

    private async Task PrimeSeenMatchesAsync(
        IReadOnlyList<TrackedPlayer> trackedPlayers,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"[{DateTimeOffset.Now:t}] Priming current recent matches so startup does not backfill old games...");

        foreach (var player in trackedPlayers)
        {
            try
            {
                var matchIds = await riotClient.GetRecentMatchIdsAsync(player.Puuid, count: 20, cancellationToken);
                foreach (var matchId in matchIds)
                {
                    seenMatchStore.MarkSeen(CreateSeenKey(player.Puuid, matchId));
                }

                Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.DisplayName}: primed {matchIds.Count} match id(s).");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.DisplayName}: could not prime recent matches: {ex.Message}");
            }
        }

        await seenMatchStore.SaveAsync();
    }

    private async Task PostLatestMatchAsync(
        TrackedPlayer player,
        CancellationToken cancellationToken,
        bool syncWinToArenaTracker = false)
    {
        var matchIds = await riotClient.GetRecentMatchIdsAsync(player.Puuid, count: 1, cancellationToken);
        var matchId = matchIds.FirstOrDefault();
        if (matchId is null)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.DisplayName}: no recent matches found.");
            return;
        }

        using var match = await riotClient.GetMatchAsync(matchId, cancellationToken);
        if (!ArenaMatchParser.TryGetQueueId(match, out var queueId))
        {
            Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.DisplayName}: latest match {matchId} did not include a queue id.");
            return;
        }

        Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.DisplayName}: latest match {matchId}, queue {queueId}.");
        if (!ArenaMatchParser.IsArenaQueue(queueId))
        {
            Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.DisplayName}: latest match is not Arena, skipping test post.");
            return;
        }

        var participant = ArenaMatchParser.FindParticipant(match, player.Puuid);
        if (participant is null)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.DisplayName}: participant not found in latest match.");
            return;
        }

        var placement = ArenaMatchParser.GetPlacement(participant.Value);
        Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.DisplayName}: placement {(placement?.ToString() ?? "unknown")}.");

        var summary = ArenaMatchParser.CreateSummary(player, participant.Value, matchId);
        var card = await RenderCardAsync(summary, cancellationToken);
        await discordNotifier.PostArenaResultAsync(summary, card, cancellationToken);
        Console.WriteLine($"[{DateTimeOffset.Now:t}] Posted latest match for {player.DisplayName}: {matchId}");

        if (syncWinToArenaTracker && ArenaMatchParser.IsArenaWin(participant.Value))
        {
            await SyncWinsToArenaTrackerAsync([summary], matchId, cancellationToken);
        }
    }

    private async Task InspectLatestMatchAsync(TrackedPlayer player, CancellationToken cancellationToken)
    {
        var matchIds = await riotClient.GetRecentMatchIdsAsync(player.Puuid, count: 1, cancellationToken);
        var matchId = matchIds.FirstOrDefault();
        if (matchId is null)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.DisplayName}: no recent matches found.");
            return;
        }

        using var match = await riotClient.GetMatchAsync(matchId, cancellationToken);
        if (!ArenaMatchParser.TryGetQueueId(match, out var queueId))
        {
            Console.WriteLine($"Latest match for {player.DisplayName}: {matchId}");
            Console.WriteLine("Queue: unknown");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"Latest match for {player.DisplayName}: {matchId}");
        Console.WriteLine($"Queue: {queueId}");
        Console.WriteLine("Participants:");

        var participants = ArenaMatchParser.GetParticipants(match)
            .Select(ArenaMatchParser.CreateParticipantInspection)
            .OrderBy(p => p.Placement ?? int.MaxValue)
            .ThenBy(p => p.SubteamId ?? int.MaxValue);

        foreach (var participant in participants)
        {
            Console.WriteLine(
                $"  #{participant.Placement?.ToString() ?? "?"} | team {participant.SubteamId?.ToString() ?? "?"} | {participant.RiotId} | {participant.ChampionName} | damage {participant.DamageDealtToChampions:N0} | taken {participant.DamageTaken:N0} | healed {participant.HealingDone:N0}");
        }
    }

    private async Task PostLatestGroupTestAsync(TrackedPlayer player, CancellationToken cancellationToken)
    {
        var matchIds = await riotClient.GetRecentMatchIdsAsync(player.Puuid, count: 1, cancellationToken);
        var matchId = matchIds.FirstOrDefault();
        if (matchId is null)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.DisplayName}: no recent matches found.");
            return;
        }

        using var match = await riotClient.GetMatchAsync(matchId, cancellationToken);
        var playerParticipant = ArenaMatchParser.FindParticipant(match, player.Puuid);
        if (playerParticipant is null)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.DisplayName}: participant not found in latest match.");
            return;
        }

        var placement = ArenaMatchParser.GetPlacement(playerParticipant.Value);
        var groupParticipants = ArenaMatchParser.GetParticipants(match)
            .Where(participant => ArenaMatchParser.GetPlacement(participant) == placement)
            .ToArray();

        var summaries = groupParticipants
            .Select(participant => ArenaMatchParser.CreateSummary(ArenaMatchParser.ReadRiotId(participant), participant, matchId))
            .ToArray();

        var cardData = new List<MatchCardData>();
        foreach (var summary in summaries)
        {
            cardData.Add(await leagueAssetProvider.BuildCardDataAsync(summary, cancellationToken));
        }

        var card = await matchCardRenderer.RenderGroupAsync(cardData, cancellationToken);
        await discordNotifier.PostArenaResultAsync(summaries[0], card, cancellationToken);

        Console.WriteLine($"[{DateTimeOffset.Now:t}] Posted group test for placement {placement?.ToString() ?? "unknown"} in {matchId}.");
    }

    private async Task CheckTrackedPlayersAsync(
        IReadOnlyList<TrackedPlayer> trackedPlayers,
        CancellationToken cancellationToken)
    {
        var candidateMatches = new Dictionary<string, HashSet<string>>();

        foreach (var player in trackedPlayers)
        {
            try
            {
                var matchIds = await riotClient.GetRecentMatchIdsAsync(player.Puuid, count: 20, cancellationToken);
                Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.DisplayName}: found {matchIds.Count} recent match id(s).");

                foreach (var matchId in matchIds)
                {
                    if (seenMatchStore.HasSeen(CreateSeenKey(player.Puuid, matchId)))
                    {
                        continue;
                    }

                    if (!candidateMatches.TryGetValue(matchId, out var puuids))
                    {
                        puuids = [];
                        candidateMatches[matchId] = puuids;
                    }

                    puuids.Add(player.Puuid);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:t}] {player.DisplayName}: {ex.Message}");
            }
        }

        foreach (var matchId in candidateMatches.Keys.Order(StringComparer.Ordinal))
        {
            try
            {
                await CheckMatchAsync(matchId, trackedPlayers, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:t}] Could not process {matchId}: {ex.Message}");
            }
        }
    }

    private async Task CheckMatchAsync(
        string matchId,
        IReadOnlyList<TrackedPlayer> trackedPlayers,
        CancellationToken cancellationToken)
    {
        using var match = await riotClient.GetMatchAsync(matchId, cancellationToken);
        if (!ArenaMatchParser.TryGetQueueId(match, out var queueId))
        {
            Console.WriteLine($"[{DateTimeOffset.Now:t}] Skipped {matchId}, match did not include a queue id.");
            foreach (var player in trackedPlayers.Where(player => !seenMatchStore.HasSeen(CreateSeenKey(player.Puuid, matchId))))
            {
                seenMatchStore.MarkSeen(CreateSeenKey(player.Puuid, matchId));
            }

            return;
        }

        Console.WriteLine($"[{DateTimeOffset.Now:t}] Checking {matchId}, queue {queueId}.");

        var trackedParticipants = trackedPlayers
            .Select(player => (Player: player, Participant: ArenaMatchParser.FindParticipant(match, player.Puuid)))
            .Where(result => result.Participant is not null)
            .Select(result => new TrackedMatchParticipant(result.Player, result.Participant!.Value))
            .ToArray();

        if (!ArenaMatchParser.IsArenaQueue(queueId))
        {
            Console.WriteLine($"[{DateTimeOffset.Now:t}] Skipped {matchId}, not an Arena queue.");
            MarkSeenForParticipants(trackedParticipants, matchId);
            return;
        }

        var winners = trackedParticipants
            .Where(result => ArenaMatchParser.IsArenaWin(result.Participant))
            .Select(result => ArenaMatchParser.CreateSummary(result.Player, result.Participant, matchId))
            .ToArray();

        foreach (var result in trackedParticipants)
        {
            var placement = ArenaMatchParser.GetPlacement(result.Participant);
            Console.WriteLine($"[{DateTimeOffset.Now:t}] {result.Player.DisplayName}: Arena placement {(placement?.ToString() ?? "unknown")}.");
        }

        if (winners.Length == 0)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:t}] Skipped {matchId}, no tracked first-place players.");
            MarkSeenForParticipants(trackedParticipants, matchId);
            return;
        }

        await SyncWinsToArenaTrackerAsync(winners, matchId, cancellationToken);

        if (winners.Length == 1)
        {
            var card = await RenderCardAsync(winners[0], cancellationToken);
            await discordNotifier.PostArenaResultAsync(winners[0], card, cancellationToken);
            Console.WriteLine($"[{DateTimeOffset.Now:t}] Posted Arena win for {winners[0].PlayerName}: {matchId}");
        }
        else
        {
            var card = await RenderGroupCardAsync(winners, cancellationToken);
            await discordNotifier.PostArenaResultAsync(winners[0], card, cancellationToken);
            Console.WriteLine($"[{DateTimeOffset.Now:t}] Posted grouped Arena win for {winners.Length} tracked players: {matchId}");
        }

        MarkSeenForParticipants(trackedParticipants, matchId);
    }

    private async Task SyncWinsToArenaTrackerAsync(
        IReadOnlyList<MatchSummary> winners,
        string matchId,
        CancellationToken cancellationToken)
    {
        foreach (var winner in winners)
        {
            try
            {
                await arenaTrackerNotifier.NotifyWinAsync(winner.PlayerName, winner.ChampionName, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:t}] Could not sync {winner.PlayerName}'s win to arena-tracker for {matchId}: {ex.Message}");
            }
        }
    }

    private void MarkSeenForParticipants(
        IEnumerable<TrackedMatchParticipant> trackedParticipants,
        string matchId)
    {
        foreach (var result in trackedParticipants)
        {
            seenMatchStore.MarkSeen(CreateSeenKey(result.Player.Puuid, matchId));
        }
    }

    private async Task<byte[]?> RenderCardAsync(MatchSummary summary, CancellationToken cancellationToken)
    {
        try
        {
            var cardData = await leagueAssetProvider.BuildCardDataAsync(summary, cancellationToken);
            return await matchCardRenderer.RenderAsync(cardData, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:t}] Could not render result card: {ex.Message}");
            return null;
        }
    }

    private async Task<byte[]?> RenderGroupCardAsync(IReadOnlyList<MatchSummary> summaries, CancellationToken cancellationToken)
    {
        try
        {
            var cardData = new List<MatchCardData>();
            foreach (var summary in summaries)
            {
                cardData.Add(await leagueAssetProvider.BuildCardDataAsync(summary, cancellationToken));
            }

            return await matchCardRenderer.RenderGroupAsync(cardData, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:t}] Could not render group result card: {ex.Message}");
            return null;
        }
    }

    private static string CreateSeenKey(string puuid, string matchId) => $"{puuid}:{matchId}";

    private sealed record TrackedMatchParticipant(TrackedPlayer Player, System.Text.Json.JsonElement Participant);
}
