# Docker Deployment on Ubuntu Server

This guide explains how to deploy **ArenaWatcher** in a Docker container on an Ubuntu Server using Docker Compose.

---

## 1. Directory Layout

On your Ubuntu server, we recommend keeping application configuration, runtime persistent state, and environment secrets outside the container so they persist across updates:

```
~/arena-watcher/
├── config/
│   └── appsettings.json        # Bot configuration (TrackedPlayers, RosterUrl, etc.)
├── data/
│   ├── seen-matches.json       # Persisted match de-duplication cache
│   └── seen-matches.json.season# Season backfill tracking marker
├── .env                        # Production secrets (RIOT_API_KEY, DISCORD_WEBHOOK_URL)
└── docker-compose.yml          # Docker Compose specification
```

In the container:
- Configuration is mounted to `/app/config/appsettings.json`
- Persistent state is mounted to `/app/data/`

---

## 2. Prerequisites

Install Docker Engine and the Docker Compose plugin on Ubuntu:

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl gnupg

# Add Docker's official GPG key & repository
sudo install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
sudo chmod a+r /etc/apt/keyrings/docker.gpg

echo \
  "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu \
  $(. /etc/os-release && echo "$UBUNTU_CODENAME") stable" | \
  sudo tee /etc/apt/sources.list.d/docker.list > /dev/null

sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

# Optional: Allow your user to run Docker without sudo
sudo usermod -aG docker $USER
newgrp docker
```

---

## 3. Deployment Steps

### Step 1: Clone Repository & Create Layout

```bash
git clone https://github.com/Phamezan/ArenaWatcher.git ~/arena-watcher
cd ~/arena-watcher
mkdir -p config data
```

### Step 2: Configure Environment Secrets

Copy the example `.env` file and populate your keys:

```bash
cp deployment/.env.example .env
nano .env
```

Set your values:

```env
RIOT_API_KEY=RGAPI-your-actual-api-key
DISCORD_WEBHOOK_URL=https://discord.com/api/webhooks/...

# Optional arena-tracker integration:
ARENA_TRACKER_WEBHOOK_URL=https://arena-tracker-sync.yourdomain.workers.dev
ARENA_TRACKER_SYNC_KEY=your-sync-key
```

### Step 3: Configure `appsettings.json`

Copy `appsettings.example.json` to `config/appsettings.json`:

```bash
cp appsettings.example.json config/appsettings.json
nano config/appsettings.json
```

Set `SeenMatchesPath` to point to `/app/data/seen-matches.json`:

```json
{
  "RegionalRoute": "europe",
  "PollIntervalSeconds": 60,
  "SeenMatchesPath": "/app/data/seen-matches.json",
  "RosterUrl": "https://raw.githubusercontent.com/<owner>/<repo>/main/data/players.json",
  "TrackedPlayers": [
    {
      "GameName": "YourName",
      "TagLine": "EUW"
    }
  ]
}
```

> **Note:** `RiotApiKey` and `DiscordWebhookUrl` can remain omitted or set to `"replace-me"` in `appsettings.json` since the `.env` file environment variables take precedence automatically.

---

## 4. Migrating State from Existing VPS Setup

If migrating from an existing `systemd` installation on a VPS:

1. Stop the existing service on your old VPS:
   ```bash
   sudo systemctl stop arena-watcher
   ```
2. Copy your existing `seen-matches.json` file to `~/arena-watcher/data/seen-matches.json` on the new Ubuntu server:
   ```bash
   scp user@old-vps:~/arena-watcher/data/seen-matches.json ~/arena-watcher/data/seen-matches.json
   ```
   *(If present, also copy `seen-matches.json.season` so season backfill state is preserved).*

---

## 5. Running the Application

### Start Container in Background

```bash
docker compose up -d --build
```

### View Logs

```bash
docker compose logs -f
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

## 6. Executing One-Off Commands & Flags

You can run CLI subcommands inside a temporary container attached to the same volumes and environment:

- **Post latest matches for all tracked players:**
  ```bash
  docker compose run --rm arena-watcher --post-latest
  ```

- **Post latest match for a specific player:**
  ```bash
  docker compose run --rm arena-watcher --post-latest-for "GameName#TagLine"
  ```

- **Force a full season backfill sync:**
  ```bash
  docker compose run --rm arena-watcher --backfill-season
  ```

- **Calibrate season start date against Riot API:**
  ```bash
  docker compose run --rm arena-watcher --calibrate-season "GameName#TagLine" --since "2026-01-01"
  ```

- **Render a layout test card to disk:**
  ```bash
  docker compose run --rm arena-watcher --render-layout-test
  ```

---

## 7. Updating to New Releases

To pull the latest code updates and redeploy:

```bash
cd ~/arena-watcher
git pull
docker compose up -d --build
```
