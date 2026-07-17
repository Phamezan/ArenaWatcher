# VPS Deployment

This project runs as a long-lived `systemd` service on a Linux VPS, built
directly from a git checkout on the box — no local publish/rsync step.

## Layout

```
/opt/arena-watcher/src/       git checkout, source of truth
/opt/arena-watcher/current/   dotnet publish output, what systemd runs
/etc/arena-watcher/           appsettings.json + secrets env file
/var/lib/arena-watcher/       seen-matches.json (runtime state)
```

`current/` and everything under it gets replaced on every deploy — never
put config or state there.

## First-time Setup

Install the .NET SDK (needed to build on the box; `dotnet-sdk-8.0` or
whatever matches this project's target framework), then:

```bash
sudo useradd --system --home /opt/arena-watcher --shell /usr/sbin/nologin arena-watcher
sudo mkdir -p /opt/arena-watcher/src /etc/arena-watcher /var/lib/arena-watcher
sudo chown -R arena-watcher:arena-watcher /opt/arena-watcher /var/lib/arena-watcher
sudo chmod 750 /etc/arena-watcher

sudo -u arena-watcher git clone https://github.com/Phamezan/ArenaWatcher.git /opt/arena-watcher/src
cd /opt/arena-watcher/src
sudo -u arena-watcher dotnet publish -c Release -o /opt/arena-watcher/current
```

Copy `appsettings.example.json` to `/etc/arena-watcher/appsettings.json` and
edit tracked players. Keep secrets out of this file.

Create `/etc/arena-watcher/arena-watcher.env`:

```bash
ARENA_BOT_CONFIG=/etc/arena-watcher/appsettings.json
RIOT_API_KEY=RGAPI-your-personal-key
DISCORD_WEBHOOK_URL=https://discord.com/api/webhooks/...

# Optional: syncs each detected Arena win to the arena-tracker dashboard
# (https://github.com/Phamezan/arena-tracker). Omit both to skip dashboard
# sync entirely — Discord posting still works either way.
ARENA_TRACKER_WEBHOOK_URL=https://arena-tracker-sync.<you>.workers.dev
ARENA_TRACKER_SYNC_KEY=<same SYNC_KEY set on the Worker>
```

Protect the config and env files:

```bash
sudo chown root:arena-watcher /etc/arena-watcher/arena-watcher.env /etc/arena-watcher/appsettings.json
sudo chmod 640 /etc/arena-watcher/arena-watcher.env /etc/arena-watcher/appsettings.json
```

## Install Service

```bash
sudo cp /opt/arena-watcher/src/deployment/arena-watcher.service /etc/systemd/system/arena-watcher.service
sudo systemctl daemon-reload
sudo systemctl enable arena-watcher
sudo systemctl start arena-watcher
```

Check status and logs:

```bash
sudo systemctl status arena-watcher
sudo journalctl -u arena-watcher -f
```

## Redeploying (new code)

```bash
cd /opt/arena-watcher/src
sudo -u arena-watcher git pull
sudo -u arena-watcher dotnet publish -c Release -o /opt/arena-watcher/current
sudo systemctl restart arena-watcher
```

## Updating Tracked Players / Config

```bash
sudo nano /etc/arena-watcher/appsettings.json
sudo systemctl restart arena-watcher
```

## Useful Commands

Stop:

```bash
sudo systemctl stop arena-watcher
```

Restart:

```bash
sudo systemctl restart arena-watcher
```

View recent logs:

```bash
sudo journalctl -u arena-watcher -n 100
```
