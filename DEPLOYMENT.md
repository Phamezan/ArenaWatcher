# VPS Deployment

This project runs as a long-lived `systemd` service on a Linux VPS, built
directly from a git checkout on the box — no local publish/rsync step.
Runs as your regular login user under `~/arena-watcher` (no dedicated
service user, no root needed — the bot only makes outbound HTTP calls).

`systemctl`/`journalctl` themselves still need `sudo` since they manage
system-wide services, but the app itself runs unprivileged.

## Layout

```
~/arena-watcher/src/          git checkout, source of truth
~/arena-watcher/current/      dotnet publish output, what systemd runs
~/arena-watcher/config/       appsettings.json (read by the app itself)
~/arena-watcher/data/         seen-matches.json (runtime state)
/etc/arena-watcher/           arena-watcher.env (secrets, read by systemd)
```

`current/` gets replaced on every deploy — never put config or state there.

`arena-watcher.env` specifically has to live under `/etc`, not the home
directory: on SELinux-enforcing distros (AlmaLinux included) systemd's own
`EnvironmentFile=` loader is denied access to home-directory files
regardless of Unix permissions — `restorecon` on the home dir does not fix
this, it's a distinct policy for files read directly by systemd at service
start. `appsettings.json` doesn't have this problem since the .NET app
reads it itself at runtime, not systemd.

The systemd unit (`deployment/arena-watcher.service`) hardcodes both the
`User=` and the absolute paths under that user's home directory — edit it
if your login user isn't `michaelsik12`.

## First-time Setup

Install the .NET SDK (needed to build on the box; `dotnet-sdk-8.0` or
whatever matches this project's target framework), then as your regular
user (no sudo needed for these steps):

```bash
mkdir -p ~/arena-watcher/config ~/arena-watcher/data
git clone https://github.com/Phamezan/ArenaWatcher.git ~/arena-watcher/src
cd ~/arena-watcher/src
dotnet publish -c Release -o ~/arena-watcher/current
```

Copy `appsettings.example.json` to `~/arena-watcher/config/appsettings.json`
and edit tracked players. Set `SeenMatchesPath` in it to
`/home/<you>/arena-watcher/data/seen-matches.json`. Keep secrets out of
this file (either leave `RiotApiKey`/`DiscordWebhookUrl`/etc as
`"replace-me"` and set them via the env file below, or just fill real
values in directly — both work, see AppConfigLoader.cs).

Create `/etc/arena-watcher/arena-watcher.env` (needs root):

```bash
sudo mkdir -p /etc/arena-watcher
```

```bash
ARENA_BOT_CONFIG=/home/<you>/arena-watcher/config/appsettings.json
RIOT_API_KEY=RGAPI-your-personal-key
DISCORD_WEBHOOK_URL=https://discord.com/api/webhooks/...

# Optional: syncs each detected Arena win to the arena-tracker dashboard
# (https://github.com/Phamezan/arena-tracker). Omit both to skip dashboard
# sync entirely — Discord posting still works either way.
ARENA_TRACKER_WEBHOOK_URL=https://arena-tracker-sync.<you>.workers.dev
ARENA_TRACKER_SYNC_KEY=<same SYNC_KEY set on the Worker>
```

```bash
sudo chown root:<you> /etc/arena-watcher/arena-watcher.env
sudo chmod 640 /etc/arena-watcher/arena-watcher.env
chmod 600 ~/arena-watcher/config/appsettings.json
```

## Install Service

```bash
sudo cp ~/arena-watcher/src/deployment/arena-watcher.service /etc/systemd/system/arena-watcher.service
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
cd ~/arena-watcher/src
git pull
dotnet publish -c Release -o ~/arena-watcher/current
sudo systemctl restart arena-watcher
```

## Updating Tracked Players / Config

```bash
nano ~/arena-watcher/config/appsettings.json
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
