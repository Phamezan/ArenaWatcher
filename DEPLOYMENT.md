# VPS Deployment

This project can run as a long-lived `systemd` service on a Linux VPS.

## Publish

From your development machine:

```powershell
dotnet publish -c Release -o .\publish
```

Copy the `publish` folder contents to the VPS:

```bash
sudo mkdir -p /opt/arena-watcher
sudo rsync -av ./publish/ /opt/arena-watcher/
```

## Configure

Create a service user and data/config directories:

```bash
sudo useradd --system --home /opt/arena-watcher --shell /usr/sbin/nologin arena-watcher
sudo mkdir -p /etc/arena-watcher /var/lib/arena-watcher /opt/arena-watcher/data
sudo chown -R arena-watcher:arena-watcher /opt/arena-watcher /var/lib/arena-watcher
sudo chmod 750 /etc/arena-watcher
```

Copy `appsettings.example.json` to `/opt/arena-watcher/appsettings.json` and edit tracked players. Keep secrets out of this file.

Create `/etc/arena-watcher/arena-watcher.env`:

```bash
RIOT_API_KEY=RGAPI-your-personal-key
DISCORD_WEBHOOK_URL=https://discord.com/api/webhooks/...
```

Protect the env file:

```bash
sudo chown root:arena-watcher /etc/arena-watcher/arena-watcher.env
sudo chmod 640 /etc/arena-watcher/arena-watcher.env
```

## Install Service

Copy the unit file:

```bash
sudo cp deployment/arena-watcher.service /etc/systemd/system/arena-watcher.service
sudo systemctl daemon-reload
sudo systemctl enable arena-watcher
sudo systemctl start arena-watcher
```

Check status and logs:

```bash
sudo systemctl status arena-watcher
sudo journalctl -u arena-watcher -f
```

## Updating Tracked Players

Edit:

```bash
sudo nano /opt/arena-watcher/appsettings.json
```

Then restart:

```bash
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
