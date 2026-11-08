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
- **Dashboard** – current solar output, load, grid voltage and temperature, money saved (today, month, year, in total,
  and payback progress), plus solar/load and per-string charts.
- **Devices** – every device with status and last reading; filterable (also from the header search).
  **Add device** picks a device type and builds its settings form from the plugin, with **Test connection**.
- **Device page** – Essentials, current values and charts (Overview tab), and every reported metric with a chart
  for any of them (Metrics tab). **Remove** stops polling and forgets the settings; history is kept unless you choose
  to delete it too. Devices with history only can have it deleted.
- **Alerts** – what's going on now (offline devices, grid outages, active weather alerts, solar warnings), grid power
  over the last 30 days (outages and voltage problems), and the history of every
  alert with its delivery result (sent, failed and retrying, waiting for a channel, or not sent); filter by category.
- **Weather** – sun times, current conditions, today's forecast vs actual temperatures, solar insights, weather charts
  (temperature, sunlight, cloud cover) and a daily history (forecast vs actual, sunlight, solar/load/grid energy).
  The dashboard shows the same "today" section and any active weather alerts.
- **Settings** – the location (search for a place, or enter coordinates), units (US or metric), the electricity price
  (see [Money saved](#money-saved)), which events send
  notifications (with a test button), and a card for each enabled plugin that has its own settings (e.g. Email).
- **Backups** – status, the backups on the server (download, restore, delete), the daily schedule, and restoring
  from an uploaded file. See [Backups](#backups).
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
- **Weather** comes from a weather service plugin (Open-Meteo by default; free, no API key). Current conditions
  (temperature, humidity, cloud, wind, rain, sunlight and sea-level barometric pressure) are logged every 15 minutes as readings of the reserved device `site-weather`, so they get history, summaries and charts
  like device data.
- **Daily summaries** keep each day's forecast as it stood before the day began, next to what actually happened:
  high/low from the logged weather, and solar/load/grid energy from device readings.
- **Insights**: tomorrow's solar outlook (kWh, once SolPnl has learned your system from 3 sunny days), the best
  window for heavy loads, and a check that solar output matches the forecast (flags two low sunny days in a row).
- **Barometric pressure** shows with its 3-hour tendency: rising or falling when it changes by at least 1 hPa
  (0.03 inHg), "rapidly" from 3 hPa (0.09 inHg). A falling barometer often means a storm is coming.
- **Units**: US (°F, inHg, mph, ft) or metric (°C, hPa, km/h, m), chosen in Settings. Values are stored in metric;
  the location's elevation is entered in feet or meters to match.
- **Weather alerts** come from the NWS plugin (US only). Disable it on the Plugins page outside the US.

Weather and alert plugins are *service* plugins and can be disabled on the Plugins page; sensors on your network
would be *device* plugins.

## Notifications
Enable and set up a notification plugin on **Settings** (the **Email (SMTP)** card: server, port, encryption, login,
from and to; **Send test email** checks it). SolPnl then sends, once per event:
- severe weather alerts for your location (NWS),
- a device that stops reporting for N minutes (default 10), and again when it's back,
- solar output below the forecast on two sunny days in a row,
- the grid going out (and coming back, with how long it was out),
- grid voltage outside the normal range for a minute (automatic: 106–127 V per leg for 120 V service, 207–253 V for
  230 V; or set your own),
- a scheduled backup that fails.

Choose which on the **Notifications** card. For Gmail, Outlook.com and iCloud use an app password.

Every alert is recorded on the **Alerts** page, even when no channel is set up yet; alerts from the last 24 hours are
sent as soon as one is. Failed sends are retried every 15 minutes for up to 24 hours. History is kept for a year.

## Grid outages
SolPnl watches the grid voltage the inverter reports on every poll (no outside service needed, and quicker than a
utility's outage map):
- **Outage:** every leg under 30 V for 20 seconds, or under it and then the inverter goes quiet. An inverter with no
  battery shuts down with the grid when there isn't enough sun, so its "offline" alert is held back during an outage.
- **Low / high voltage:** any leg outside the limits for a minute.

Each event is recorded with its start and end, and the lowest (or highest) voltage, and shown on the **Alerts** page.
SolPnl can't see time when it is itself without power, and it can't know about planned outages in advance.

## Money saved
Enter the price you pay per kWh on **Settings → Electricity price** (the energy charge plus per-kWh fuel or rider
charges; fixed monthly charges don't change with solar). An optional summer price applies June–September. When the
rate changes, add the new price with the date it applies from; earlier days keep the old price.

Savings for each day = that day's solar energy × the price that day. This assumes all solar is used at home (no
export). The Dashboard shows today, this month, this year and the total; with the system cost entered, also payback
progress. The Weather page's daily history gets a **Saved** column.

## Backups
SolPnl backs up its database every day (02:30 in your location's time zone by default) and keeps the newest 14,
in `backups` in the data directory (`/var/lib/solpnl/backups` on the Pi). Change the time, how many to keep and the
folder on the **Backups** page, or select **Back up now**.

- **Format:** a zip with `manifest.json` and one JSON-lines file per table. Rows go through SolPnl's data model, not the
  database's own dump tool, so a backup restores into any supported database: move from SQLite to MariaDB by setting
  up the new storage and restoring a backup taken on the old one.
- **Passwords are never in backups** (e.g. the email password), so files are safe to copy around. A restore keeps the
  passwords already set on the server; on a fresh install, enter them again.
- **Restore** a backup on the server or an uploaded file. SolPnl restarts, backs up the current data ("before a restore"),
  then replaces it in one transaction: if anything fails, nothing changes. The page reconnects when SolPnl is back
  (a few minutes; longer with a lot of history).
- **Off the device:** a failed SD card takes local backups with it. Download one now and then, or point the folder at a
  network share. On the Pi the service may only write to `/var/lib/solpnl`; for another folder add
  `ReadWritePaths=/path/to/folder` to `/etc/systemd/system/solpnl.service`, then
  `sudo systemctl daemon-reload && sudo systemctl restart solpnl`.
- A scheduled backup that fails raises a **System** alert (and a notification, if enabled in Settings).

## Plugin settings
Plugins can declare their own settings (`IConfigurablePlugin`: text, number, secret and choice fields, plus an optional
test action). The Settings page shows a card for each enabled one. Secret values (passwords, API keys) are stored in
the database and never returned by the API: the card shows them as "Saved", with Change and Remove.

## API
| Endpoint | Description |
|---|---|
| `GET /api/v1/grid/events[?days=30]` | Grid outages and low/high voltage, newest first, with outage totals |
| `GET /api/v1/site/savings` | Money saved: today, month, year, total, payback, per month |
| `GET/PUT /api/v1/settings/savings` | `{ currency, rates: [{ from, pricePerKwh, summerPricePerKwh }], systemCost }` |
| `GET /api/v1/backups` | Backup status, schedule and the backups on the server |
| `PUT /api/v1/backups/settings` | `{ enabled, time: "HH:mm", keep, folder }` (folder null = default) |
| `POST /api/v1/backups` | Start a backup (202; poll `GET /api/v1/backups`) |
| `GET/DELETE /api/v1/backups/{name}` | Download or delete a backup |
| `POST /api/v1/backups/{name}/restore` | Restore a backup on the server (SolPnl restarts) |
| `POST /api/v1/backups/restore` | Restore an uploaded backup (body: the zip; SolPnl restarts) |
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
