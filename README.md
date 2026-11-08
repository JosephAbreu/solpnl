# SolPnl
A self-hosted web application for solar equipment monitoring
<hr/>

SolPnl runs entirely on your own hardware (e.g. a Raspberry Pi). It reads equipment locally, keeps all logic
and alerting on the server, and depends on outside services only when a plugin you enable requires them.

## Layout

| Path | Purpose |
|---|---|
| `src/SolPnl.Contracts` | Shared models and plugin interfaces (`IDevicePluginType`, `IDevicePlugin`, `INotificationPlugin`) |
| `src/SolPnl.Core` | Storage, device registry, polling, summaries and retention |
| `src/SolPnl.Api` | ASP.NET Core REST API (`/api/v1`); also hosts the web app |
| `src/SolPnl.Ui` | Shared UI (Fluent UI Blazor): layout, pages, components and SVG charts; reusable by a future MAUI Blazor Hybrid app |
| `src/SolPnl.Web` | Blazor WebAssembly PWA host for `SolPnl.Ui` |
| `src/plugins/SolPnl.Plugin.SolarmanSrne` | Solarman V5 logger + SRNE-family inverters (e.g. Sungoldpower 10kW split phase) |
| `src/plugins/SolPnl.Plugin.OpenMeteo` | Weather service plugin: conditions, forecasts, solar irradiance and place search (open-meteo.com) |
| `src/plugins/SolPnl.Plugin.NwsAlerts` | Weather alert service plugin: official US watches and warnings (api.weather.gov) |
| `src/plugins/SolPnl.Plugin.Simulator` | Simulated inverter for development (registered only in the Development environment) |
| `src/plugins/SolPnl.Plugin.Notify.Smtp` | Email notification plugin (any SMTP server; MailKit) |
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
  **Add device** picks a device type and builds its settings form from the plugin, with **Test connection**.
- **Device page** – Essentials, current values and charts (Overview tab), and every reported metric with a chart
  for any of them (Metrics tab). **Remove** stops polling and forgets the settings; history is kept unless you choose
  to delete it too. Devices with history only can have it deleted.
- **Alerts** – what's going on now (offline devices, active weather alerts, solar warnings) and the history of every
  alert with its delivery result (sent, failed and retrying, waiting for a channel, or not sent); filter by category.
- **Weather** – sun times, current conditions, today's forecast vs actual temperatures, solar insights, weather charts
  (temperature, sunlight, cloud cover) and a daily history (forecast vs actual, sunlight, solar/load/grid energy).
  The dashboard shows the same "today" section and any active weather alerts.
- **Settings** – the location (search for a place, or enter coordinates), temperature unit (°F/°C), which events send
  notifications (with a test button), and a card for each enabled plugin that has its own settings (e.g. Email).
- **Plugins** – installed plugins with their device counts. **Disable** pauses all of a plugin's devices (settings and
  history are kept); **Enable** resumes them.
- **About** (ⓘ in the top bar) – web app and server versions (with a reload prompt when the browser has an older web
  app cached), the commit a release was built from, database, runtime and uptime, project links and data credits.

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
writes the `Db` section:
```json
{
  "Db": { "DriverName": "sqlite", "ConnectionString": "Data Source=/var/lib/solpnl/solpnl.db" }
}
```
Devices and plugin on/off states are stored in the database and managed in the web UI. (An older
`Plugins:SolarmanSrne` section, from before devices were managed in the UI, is imported once as a device named
"Inverter" and then ignored.)
`Db:DriverName` is `sqlite`, `mysql` (MySQL or MariaDB) or `mssql`. On Linux the file is written owner-only
because it can contain a database password. To change storage later, edit or remove the `Db` section and restart
(removing it brings the setup wizard back).

The schema is created and updated at startup from numbered SQL scripts in
`src/SolPnl.Core/Data/Migrations/<driver>/`. Every schema change needs a new script for **each** driver.

## Location, weather and insights
Set the location in **Settings**. Then:
- **Sun times** (sunrise, sunset, solar noon, daylight) and **clear-sky sunlight** are calculated on the machine (NOAA
  solar position formulas), with no network access.
- **Weather** comes from a weather service plugin (Open-Meteo by default; free, no API key). Current conditions are
  logged every 15 minutes as readings of the reserved device `site-weather`, so they get history, summaries and charts
  like device data.
- **Daily summaries** keep each day's forecast as it stood before the day began, next to what actually happened:
  high/low from the logged weather, and solar/load/grid energy from device readings.
- **Insights**: tomorrow's solar outlook (kWh, once SolPnl has learned your system from 3 sunny days), the best
  window for heavy loads, and a check that solar output matches the forecast (flags two low sunny days in a row).
- **Weather alerts** come from the NWS plugin (US only). Disable it on the Plugins page outside the US.

Weather and alert plugins are *service* plugins and can be disabled on the Plugins page; sensors on your network
would be *device* plugins.

## Notifications
Enable and set up a notification plugin on **Settings** (the **Email (SMTP)** card: server, port, encryption, login,
from and to; **Send test email** checks it). SolPnl then sends, once per event:
- severe weather alerts for your location (NWS),
- a device that stops reporting for N minutes (default 10), and again when it's back,
- solar output below the forecast on two sunny days in a row.

Choose which on the **Notifications** card. For Gmail, Outlook.com and iCloud use an app password.

Every alert is recorded on the **Alerts** page, even when no channel is set up yet; alerts from the last 24 hours are
sent as soon as one is. Failed sends are retried every 15 minutes for up to 24 hours. History is kept for a year.

## Plugin settings
Plugins can declare their own settings (`IConfigurablePlugin`: text, number, secret and choice fields, plus an optional
test action). The Settings page shows a card for each enabled one. Secret values (passwords, API keys) are stored in
the database and never returned by the API: the card shows them as "Saved", with Change and Remove.

## API
| Endpoint | Description |
|---|---|
| `GET /api/v1/about` | Server version, commit, runtime, OS, database and start time |
| `GET /api/v1/devices` | Devices with stored data |
| `DELETE /api/v1/devices/{deviceId}` | Delete the history of a device that is no longer configured |
| `GET /api/v1/config/devices` | Configured devices (secret settings omitted) |
| `POST /api/v1/config/devices` | Add a device: `{ name, pluginId, settings }` |
| `POST /api/v1/config/devices/test` | Try a device's settings (connects and reads once, saves nothing) |
| `DELETE /api/v1/config/devices/{id}[?deleteHistory=true]` | Remove a device |
| `GET /api/v1/plugins` | Installed plugins (device and service), with settings schema and device counts |
| `PUT /api/v1/plugins/{id}` | Enable or disable: `{ enabled }` |
| `GET/PUT /api/v1/plugins/{id}/settings` | A plugin's own settings (secrets never returned; null keeps, "" clears) |
| `POST /api/v1/plugins/{id}/settings/test` | Run the plugin's test with unsaved values (e.g. send a test email) |
| `GET/PUT /api/v1/notifications/preferences` | Which events notify, offline delay |
| `POST /api/v1/notifications/test` | Send a test notification through every configured channel |
| `GET /api/v1/alerts[?category=&limit=&before=]` | Ongoing conditions and alert history, newest first (page with `before` = last id) |
| `GET /api/v1/settings` | Location and temperature unit |
| `PUT /api/v1/settings/location` | `{ name, latitude, longitude, elevationMeters, timeZoneId }` |
| `GET /api/v1/settings/location/search?q=` | Find places (needs the Open-Meteo plugin) |
| `PUT /api/v1/settings/units` | `{ temperatureUnit: "F" \| "C" }` |
| `GET /api/v1/site/overview` | Sun times, weather now, today's summary, tomorrow's forecast, insights, alerts |
| `GET /api/v1/site/days[?from=&to=]` | Daily summaries (local dates, default last 30 days) |
| `GET /api/v1/readings/latest[?device=]` | Latest value of every metric |
| `GET /api/v1/readings/history?device=&metric=[&from=&to=&resolution=]` | Values over a range (default: last 24 h) |

`resolution` is `raw`, `minute`, `hour` or `auto` (default). Auto picks raw for up to 2 hours, minute for up to
3 days and hour beyond that, falling back to a coarser resolution when the finer one is no longer retained.
Summary points carry the bucket's mean as `value`, plus `min` and `max`.

The API has no authentication yet: anyone who can reach SolPnl on your network can change devices and plugins.

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
last 3 releases). Each release is versioned `{year}.{month}.{commit count}` and records its commit (marked
`+modified` if built with uncommitted changes); the About page shows both. **Roll back:** `./deploy/deploy.ps1 -Rollback`. Options: `-Target user@host`, `-Port`.

**On the Pi:** `systemctl status solpnl`, `journalctl -u solpnl -f`. The port is set in
`/etc/systemd/system/solpnl.service` (`ASPNETCORE_URLS`, default 5255).

## Probing a Solarman WiFi logger
You need the logger's IP address (from your router) and its serial number (sticker on the stick).
```sh
dotnet run --project tools/SolPnl.Probe -- --host 192.168.1.50 --serial 2712345678
dotnet run --project tools/SolPnl.Probe -- --host 192.168.1.50 --serial 2712345678 --range 0x0100:32 --verbose
```
Run with `--help` for all options.
