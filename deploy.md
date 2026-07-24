# Docker Deployment on Ubuntu Server

This guide explains how to deploy **ArenaWatcher** in a Docker container on an Ubuntu Server using Docker Compose.

---

## 1. Directory & File Structure

On your Ubuntu server, clone the repository directly. Configuration, runtime data state, and environment secrets reside in local files mounted into the container:

```
~/arena-watcher/               # Project root (git checkout)
├── config/
│   └── appsettings.json       # Configured with TrackedPlayers, RosterUrl, etc.
├── data/
│   ├── seen-matches.json      # De-duplication match cache (persisted)
│   └── seen-matches.json.season # Season backfill state marker
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

Ensure `SeenMatchesPath` points to `/app/data/seen-matches.json` (the in-container path):

```json
{
  "RegionalRoute": "europe",
  "PollIntervalSeconds": 60,
  "SeenMatchesPath": "/app/data/seen-matches.json",
  "RosterUrl": "https://raw.githubusercontent.com/<owner>/<repo>/main/data/players.json",
  "TrackedPlayers": [
    {
      "GameName": "Phamezan",
      "TagLine": "EUW"
    }
  ]
}
```

> **Note:** `RiotApiKey` and `DiscordWebhookUrl` can be set to `null` or left as placeholder strings in `appsettings.json` because environment variables defined in `.env` automatically take priority.

---

## 3. Migrating State from VPS to Docker

If you are moving from an existing VPS installation to Docker:

1. **Stop the existing VPS service**:
   ```bash
   sudo systemctl stop arena-watcher
   ```
2. **Copy persistent match data** into the local `./data` folder on your Ubuntu server:
   ```bash
   scp user@old-vps:~/arena-watcher/data/seen-matches.json ~/arena-watcher/data/seen-matches.json
   ```
   *(If present, also copy `seen-matches.json.season` to preserve season backfill history).*

---

## 4. Running the Bot

### Start the Service

```bash
docker compose up -d --build
```

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

## 5. Running Manual Commands & CLI Flags

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

## 6. Updating & Maintenance

To update ArenaWatcher when new code is pushed:

```bash
cd ~/arena-watcher
git pull
docker compose up -d --build
```
