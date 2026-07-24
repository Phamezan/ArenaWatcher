# Docker Deployment on Ubuntu Server

This guide explains how to deploy **ArenaWatcher** in a Docker container on an Ubuntu Server using Docker Compose.

---

## 1. Directory & File Structure

On your Ubuntu server, clone the repository directly. Configuration, runtime data state, and environment secrets reside in local files mounted into the container:

```
~/arena-watcher/               # Project root (git checkout)
├── config/
│   └── appsettings.json       # App config (RosterUrl or TrackedPlayers, route, path)
├── data/
│   └── seen-matches.json      # De-duplication match cache (auto-created on startup)
├── .env                       # Active environment secrets (copied from deployment/arena-watcher.env.example)
├── docker-compose.yml         # Container orchestration specification
└── Dockerfile                 # Multi-stage .NET 8 build with ImageSharp font support
```

Inside the container:
- Configuration is read from `/app/config/appsettings.json` (set via `ARENA_BOT_CONFIG` environment variable in `Dockerfile`).
- Persistent match state is stored in `/app/data/`.

---

## 2. Deployment Setup

### Step 1: Clone Repository & Create Directories

```bash
git clone https://github.com/Phamezan/ArenaWatcher.git ~/arena-watcher
cd ~/arena-watcher
mkdir -p config data
```

### Step 2: Configure Environment Secrets (`.env`)

Docker Compose automatically loads environment variables from a file named `.env` located in the project root directory.

Copy `deployment/arena-watcher.env.example` to `.env` in the root of the repository:

```bash
cp deployment/arena-watcher.env.example .env
nano .env
```

Fill in your actual API keys:

```env
RIOT_API_KEY=RGAPI-your-actual-api-key
DISCORD_WEBHOOK_URL=https://discord.com/api/webhooks/...

# Optional arena-tracker dashboard integration:
ARENA_TRACKER_WEBHOOK_URL=https://arena-tracker-sync.yourdomain.workers.dev
ARENA_TRACKER_SYNC_KEY=your-sync-key
```

### Step 3: Configure `appsettings.json`

Copy `appsettings.example.json` to `config/appsettings.json`:

```bash
cp appsettings.example.json config/appsettings.json
nano config/appsettings.json
```

Ensure `SeenMatchesPath` points to `/app/data/seen-matches.json` (the in-container path).

#### Option A: Using `RosterUrl` (Shared player list from GitHub)
If using `RosterUrl`, the bot dynamically fetches player names from your repository (`players.json`), so `TrackedPlayers`, `RiotApiKey`, and `DiscordWebhookUrl` do not need to be in `appsettings.json`:

```json
{
  "RegionalRoute": "europe",
  "PollIntervalSeconds": 60,
  "SeenMatchesPath": "/app/data/seen-matches.json",
  "RosterUrl": "https://raw.githubusercontent.com/<owner>/<repo>/main/data/players.json"
}
```

#### Option B: Manual `TrackedPlayers` List
If you are not using a shared `RosterUrl`, define your tracked players directly in `TrackedPlayers`:

```json
{
  "RegionalRoute": "europe",
  "PollIntervalSeconds": 60,
  "SeenMatchesPath": "/app/data/seen-matches.json",
  "TrackedPlayers": [
    {
      "GameName": "Phamezan",
      "TagLine": "EUW"
    }
  ]
}
```

> **Note:** API secrets (`RIOT_API_KEY`, `DISCORD_WEBHOOK_URL`, `ARENA_TRACKER_WEBHOOK_URL`, `ARENA_TRACKER_SYNC_KEY`) are managed cleanly via `.env` and do not need to be in `appsettings.json`.

---

## 3. Running the Bot

### Start the Service

```bash
docker compose up -d --build
```

> **Automatic Match Priming:** On startup, the bot automatically runs `PrimeSeenMatchesAsync`, fetching the 20 most recent match IDs for all tracked players and marking them as seen. You do not need to copy any old state files when deploying to a new server; old matches will never be re-posted to Discord.

### View Live Logs

```bash
docker compose logs -f arena-watcher
```

### Check Container Status

```bash
docker compose ps
```

### Restart Container

```bash
docker compose restart
```

---

## 4. Running Manual Commands & CLI Flags

You can run any of the application's CLI subcommands using temporary containers attached to the same `.env` and persistent `./data` volume:

- **Post latest matches for all tracked players:**
  ```bash
  docker compose run --rm arena-watcher --post-latest
  ```

- **Post latest match for a specific player (e.g. after downtime):**
  ```bash
  docker compose run --rm arena-watcher --post-latest-for "GameName#TagLine"
  ```

- **Inspect latest match participant breakdown (no posting):**
  ```bash
  docker compose run --rm arena-watcher --inspect-latest
  ```

- **Test group match result posting:**
  ```bash
  docker compose run --rm arena-watcher --post-latest-group-test
  ```

- **Force full season backfill sync to arena-tracker:**
  ```bash
  docker compose run --rm arena-watcher --backfill-season
  ```

- **Calibrate season start date against Riot API:**
  ```bash
  docker compose run --rm arena-watcher --calibrate-season "GameName#TagLine" --since "2026-01-01"
  ```

- **Render a sample card layout test image:**
  ```bash
  docker compose run --rm arena-watcher --render-layout-test
  ```

---

## 5. Updating & Maintenance

To update ArenaWatcher when new code is pushed:

```bash
cd ~/arena-watcher
git pull
docker compose up -d --build
```
