# SolPnl
A self-hosted web application for solar equipment monitoring
<hr/>

SolPnl runs entirely on your own hardware (e.g. a Raspberry Pi). It reads equipment locally, keeps all logic
and alerting on the server, and depends on outside services only when a plugin you enable requires them.

## Layout

| Path | Purpose |
|---|---|
| `src/SolPnl.Contracts` | Shared models and plugin interfaces (`IDevicePlugin`, `INotificationPlugin`) |
| `src/SolPnl.Core` | Plugin loading, polling, storage, alerting |
| `src/SolPnl.Api` | ASP.NET Core REST API (`/api/v1`); also hosts the web app |
| `src/SolPnl.Ui` | Shared UI (Fluent UI Blazor): layout, pages, components and SVG charts; reusable by a future MAUI Blazor Hybrid app |
| `src/SolPnl.Web` | Blazor WebAssembly PWA host for `SolPnl.Ui` |
| `src/plugins/SolPnl.Plugin.SolarmanSrne` | Solarman V5 logger + SRNE-family inverters (e.g. Sungoldpower 10kW split phase) |
| `src/plugins/SolPnl.Plugin.Notify.Smtp` | Email notifications |
| `tools/SolPnl.Probe` | CLI to verify local access to a Solarman logger and dump raw registers |

## Requirements
- .NET 10 SDK

## Build & run
```sh
dotnet build SolPnl.slnx
dotnet test SolPnl.slnx --filter "Category!=Containers"   # fast tests (SQLite only)
dotnet test SolPnl.slnx                                   # all tests; MySQL/MariaDB/SQL Server need Docker
dotnet run --project src/SolPnl.Api
```

Then open http://localhost:5255 (the URL printed at startup) for the dashboard.

## Web UI
A portal-style UI built with [Fluent UI Blazor](https://www.fluentui-blazor.net) and served by `SolPnl.Api`:
- **Dashboard** – current solar output, load, grid voltage and temperature, plus solar/load and per-string charts.
- **Devices** – every device with status and last reading; filterable (also from the header search).
- **Device page** – Essentials, current values and charts (Overview tab), and every reported metric with a chart
  for any of them (Metrics tab).

Charts are dependency-free SVG with a hover/keyboard crosshair tooltip, a table view, and light/dark themes. All assets
are bundled locally (no CDNs).

## First-run setup
On first start SolPnl has no storage configured and runs in **setup mode**: it serves only the setup wizard and
writes a one-time setup code to its log:

```
SolPnl needs to be set up. Open http://localhost:5255/setup in a browser and enter setup code MP2X-WJMN.
```

Open `/setup` (from another device, use this machine's address), enter the code, choose storage, test the connection
and select **Save and start**. SolPnl creates the database schema, saves the choice and restarts into normal mode.

| Storage | Notes |
|---|---|
| SQLite (recommended) | A file on this machine; nothing else to install |
| MySQL or MariaDB | Database must exist; the user needs permission to create tables |
| SQL Server | Runs on another machine (no Raspberry Pi build) |

## Configuration
Machine-specific settings live in `appsettings.Local.json` (git-ignored) in the **data directory**: `SOLPNL_DATA_DIR`
if set (e.g. `/var/lib/solpnl`), otherwise the content root (`src/SolPnl.Api` during development). The setup wizard
writes the `Db` section; device plugins are configured there too:
```json
{
  "Db": { "DriverName": "sqlite", "ConnectionString": "Data Source=/var/lib/solpnl/solpnl.db" },
  "Plugins": {
    "SolarmanSrne": { "Host": "192.168.1.50", "LoggerSerial": 2712345678 }
  }
}
```
`Db:DriverName` is `sqlite`, `mysql` (MySQL or MariaDB) or `mssql`. On Linux the file is written owner-only
because it can contain a database password. To change storage later, edit or remove the `Db` section and restart
(removing it brings the setup wizard back).

The schema is created and updated at startup from numbered SQL scripts in
`src/SolPnl.Core/Data/Migrations/<driver>/`. Every schema change needs a new script for **each** driver.

## API
| Endpoint | Description |
|---|---|
| `GET /api/v1/devices` | Known devices |
| `GET /api/v1/readings/latest[?device=]` | Latest value of every metric |
| `GET /api/v1/readings/history?device=&metric=[&from=&to=&resolution=]` | Values over a range (default: last 24 h) |

`resolution` is `raw`, `minute`, `hour` or `auto` (default). Auto picks raw for up to 2 hours, minute for up to
3 days and hour beyond that, falling back to a coarser resolution when the finer one is no longer retained.
Summary points carry the bucket's mean as `value`, plus `min` and `max`.

### Retention
Raw readings are summarised per minute, and minutes per hour, by a background job (`Retention:MaintenanceIntervalSeconds`).
Defaults: raw 7 days, minute summaries 90 days, hourly summaries forever (`Retention:RawDays`, `MinuteDays`,
`HourDays`; 0 = keep forever). Data is only deleted after it has been summarised.

## Deploying to a Raspberry Pi
SolPnl ships as a self-contained `linux-arm64` build (no .NET needed on the Pi) and runs as a systemd service.
Tested on a Raspberry Pi 4 with 64-bit Raspberry Pi OS (Debian 13).

**Once:** set up SSH key login from your PC (deploys can't type passwords):
```powershell
ssh-keygen -t ed25519
type $env:USERPROFILE\.ssh\id_ed25519.pub | ssh administrator@server.local "mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys"
```

**First deploy** installs the service (asks for your sudo password once), then deploys:
```powershell
./deploy/deploy.ps1 -Install
```
This creates a `solpnl` service user, `/opt/solpnl/releases/` (owned by your user) and `/var/lib/solpnl/` (data:
`appsettings.Local.json`, `solpnl.db`), installs `solpnl.service`, and allows your user to start/stop/restart the
service without a password. SolPnl then starts in setup mode; open `http://server.local:5255/setup` and get the setup
code with `ssh administrator@server.local "journalctl -u solpnl -n 50 --no-pager | grep -o 'setup code [A-Z0-9-]*'"`.

**Updates:** `./deploy/deploy.ps1` (builds, uploads a new release, switches to it, restarts, checks health; keeps the
last 3 releases). **Roll back:** `./deploy/deploy.ps1 -Rollback`. Options: `-Target user@host`, `-Port`.

**On the Pi:** `systemctl status solpnl`, `journalctl -u solpnl -f`. The port is set in
`/etc/systemd/system/solpnl.service` (`ASPNETCORE_URLS`, default 5255).

## Probing a Solarman WiFi logger
You need the logger's IP address (from your router) and its serial number (sticker on the stick).
```sh
dotnet run --project tools/SolPnl.Probe -- --host 192.168.1.50 --serial 2712345678
dotnet run --project tools/SolPnl.Probe -- --host 192.168.1.50 --serial 2712345678 --range 0x0100:32 --verbose
```
Run with `--help` for all options.
