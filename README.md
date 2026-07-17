# Arena Watcher

A Discord bot that watches a group of League of Legends players and posts a
result card to Discord every time one of them wins an Arena game — no
manual sharing, no screenshots, it just knows.

Polls the Riot API for each tracked player, detects Arena wins as they
happen, and posts an image card (champion, placement, items, augments,
damage/healing stats) to a Discord webhook. Optionally also syncs each win
to [arena-tracker](https://github.com/Phamezan/arena-tracker), a shared
dashboard tracking which champions the group has ever won an Arena game
with.

## Requirements

- A [Riot Developer API key](https://developer.riotgames.com/)
- A Discord webhook URL for the channel you want results posted to
- .NET 8 SDK (build/run) or just the runtime (if deploying prebuilt output)

## Quick start

```bash
git clone https://github.com/Phamezan/ArenaWatcher.git
cd ArenaWatcher
cp appsettings.example.json appsettings.json
# edit appsettings.json: RiotApiKey, DiscordWebhookUrl, TrackedPlayers
dotnet run
```

That starts the polling loop (`RunAsync`), checking tracked players every
`PollIntervalSeconds` for new Arena wins.

## Other run modes

```bash
dotnet run -- --post-latest              # post each tracked player's most recent match, regardless of result
dotnet run -- --inspect-latest           # print full participant breakdown for each player's most recent match, no posting
dotnet run -- --post-latest-group-test   # post a grouped result card for everyone who shares the latest match's placement
dotnet run -- --render-layout-test       # render a sample card to disk without hitting Discord, for layout iteration
```

## Configuration

`appsettings.json` (see `appsettings.example.json`):

| Field | Meaning |
|---|---|
| `RiotApiKey` | Your Riot API key. Can also be set via `RIOT_API_KEY` env var (takes priority). |
| `DiscordWebhookUrl` | Where result cards get posted. Can also be set via `DISCORD_WEBHOOK_URL` env var. |
| `RegionalRoute` | Riot regional routing value, e.g. `europe`, `americas`, `asia`. |
| `PollIntervalSeconds` | How often to check for new matches. Minimum enforced at 60. |
| `SeenMatchesPath` | Where the dedup cache (already-posted match ids) is stored. |
| `TrackedPlayers` | List of `{ GameName, TagLine }` to watch. |
| `ArenaTrackerWebhookUrl` / `ArenaTrackerSyncKey` | Optional — syncs wins to an [arena-tracker](https://github.com/Phamezan/arena-tracker) dashboard. Also settable via `ARENA_TRACKER_WEBHOOK_URL` / `ARENA_TRACKER_SYNC_KEY` env vars. Leave unset to skip; Discord posting works either way. |

Any field can instead be set via environment variable — useful for keeping
real secrets out of `appsettings.json` entirely (see `DEPLOYMENT.md`).

## Deploying

See [`DEPLOYMENT.md`](DEPLOYMENT.md) for running this as a long-lived
`systemd` service on a VPS.

## Notes

- Not affiliated with Riot Games.
- Arena queue ids recognized: `1700`, `1710`, `1750`. If Riot adds a new
  Arena queue id, add it in `Services/ArenaMatchParser.cs`.
