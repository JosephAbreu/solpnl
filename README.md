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
| `src/SolPnl.Ui` | Razor Class Library with shared UI components (reusable by a future MAUI Blazor Hybrid app) |
| `src/SolPnl.Web` | Blazor WebAssembly PWA |
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

## Configuration
Machine-specific settings go in `src/SolPnl.Api/appsettings.Local.json` (git-ignored):
```json
{
  "Plugins": {
    "SolarmanSrne": { "Host": "192.168.1.50", "LoggerSerial": 2712345678 }
  }
}
```

### Storage
| `Db:DriverName` | Database | Notes |
|---|---|---|
| `sqlite` (default) | SQLite | No setup; `Data Source=solpnl.db` |
| `mysql` | MySQL or MariaDB | Via `MySql.EntityFrameworkCore` |
| `mssql` | SQL Server | Runs on another machine (no Raspberry Pi build) |

```json
"Db": { "DriverName": "mysql", "ConnectionString": "Server=localhost;Database=solpnl;User=solpnl;Password=...;" }
```
The schema is created and updated at startup from numbered SQL scripts in
`src/SolPnl.Core/Data/Migrations/<driver>/`. Every schema change needs a new script for **each** driver.

## API
| Endpoint | Description |
|---|---|
| `GET /api/v1/devices` | Known devices |
| `GET /api/v1/readings/latest[?device=]` | Latest value of every metric |
| `GET /api/v1/readings/history?device=&metric=[&from=&to=]` | Raw values (default: last 24 h) |

## Probing a Solarman WiFi logger
You need the logger's IP address (from your router) and its serial number (sticker on the stick).
```sh
dotnet run --project tools/SolPnl.Probe -- --host 192.168.1.50 --serial 2712345678
dotnet run --project tools/SolPnl.Probe -- --host 192.168.1.50 --serial 2712345678 --range 0x0100:32 --verbose
```
Run with `--help` for all options.
