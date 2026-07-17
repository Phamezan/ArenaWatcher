# VPS Deployment

This project runs as a long-lived `systemd` service on a Linux VPS, built
directly from a git checkout on the box — no local publish/rsync step.
Runs as root under `~/arena-watcher` (`/root/arena-watcher`); no dedicated
service user.

## Layout

```
~/arena-watcher/src/       git checkout, source of truth
~/arena-watcher/current/   dotnet publish output, what systemd runs
~/arena-watcher/config/    appsettings.json + arena-watcher.env
~/arena-watcher/data/      seen-matches.json (runtime state)
```

`current/` gets replaced on every deploy — never put config or state there.

## First-time Setup

Install the .NET SDK (needed to build on the box; `dotnet-sdk-8.0` or
whatever matches this project's target framework), then as root:

```bash
mkdir -p ~/arena-watcher/config ~/arena-watcher/data
git clone https://github.com/Phamezan/ArenaWatcher.git ~/arena-watcher/src
cd ~/arena-watcher/src
dotnet publish -c Release -o ~/arena-watcher/current
```

Copy `appsettings.example.json` to `~/arena-watcher/config/appsettings.json`
and edit tracked players. Set `SeenMatchesPath` in it to
`/root/arena-watcher/data/seen-matches.json`. Keep secrets out of this file.

Create `~/arena-watcher/config/arena-watcher.env`:

```bash
ARENA_BOT_CONFIG=/root/arena-watcher/config/appsettings.json
RIOT_API_KEY=RGAPI-your-personal-key
DISCORD_WEBHOOK_URL=https://discord.com/api/webhooks/...

# Optional: syncs each detected Arena win to the arena-tracker dashboard
# (https://github.com/Phamezan/arena-tracker). Omit both to skip dashboard
# sync entirely — Discord posting still works either way.
ARENA_TRACKER_WEBHOOK_URL=https://arena-tracker-sync.<you>.workers.dev
ARENA_TRACKER_SYNC_KEY=<same SYNC_KEY set on the Worker>
```

```bash
chmod 600 ~/arena-watcher/config/arena-watcher.env ~/arena-watcher/config/appsettings.json
```

## Install Service

```bash
cp ~/arena-watcher/src/deployment/arena-watcher.service /etc/systemd/system/arena-watcher.service
systemctl daemon-reload
systemctl enable arena-watcher
systemctl start arena-watcher
```

Check status and logs:

```bash
systemctl status arena-watcher
journalctl -u arena-watcher -f
```

## Redeploying (new code)

```bash
cd ~/arena-watcher/src
git pull
dotnet publish -c Release -o ~/arena-watcher/current
systemctl restart arena-watcher
```

## Updating Tracked Players / Config

```bash
nano ~/arena-watcher/config/appsettings.json
systemctl restart arena-watcher
```

## Useful Commands

Stop:

```bash
systemctl stop arena-watcher
```

Restart:

```bash
systemctl restart arena-watcher
```

View recent logs:

```bash
journalctl -u arena-watcher -n 100
```
