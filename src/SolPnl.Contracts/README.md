# SolPnl.Contracts

Plugin interfaces and models for [SolPnl](https://github.com/JosephAbreu/solpnl), a self-hosted web app for monitoring
a home solar system. Reference this package to build a SolPnl plugin.

## What a plugin can be
A plugin is a .NET 10 class library with public classes implementing one or more of:

| Interface | For |
|---|---|
| `IDevicePluginType` | A kind of device: inverters, meters, sensors. Creates an `IDevicePlugin` per configured device, which reads its values as `Reading`s (prefer the names in `Metrics`). Optionally also `IDeviceFaults` and `IDeviceState`. |
| `IWeatherProvider` | Current conditions and forecasts (optionally `ILocationSearch` for place search). |
| `IWeatherAlertProvider` | Official weather alerts for a location. |
| `INotificationPlugin` | A way to send notifications (email, push, ...). |

Any plugin can also implement `IConfigurablePlugin` to get a settings card in SolPnl. SolPnl creates each class through
dependency injection, so its constructor can take an `HttpClient`, an `ILogger<T>` or other SolPnl services.

## Making a package
1. Create a class library and reference this package. SolPnl always supplies its own copy of SolPnl.Contracts, so
   leave it out of the build output:
   ```xml
   <PropertyGroup>
     <TargetFramework>net10.0</TargetFramework>
     <EnableDynamicLoading>true</EnableDynamicLoading>
   </PropertyGroup>
   <ItemGroup>
     <PackageReference Include="SolPnl.Contracts" Version="0.1.0" ExcludeAssets="runtime" />
     <None Update="plugin.json" CopyToOutputDirectory="PreserveNewest" />
   </ItemGroup>
   ```
2. Add a `plugin.json`:
   ```json
   {
     "id": "acme.inverter",
     "name": "Acme inverters",
     "assembly": "Acme.SolPnl.Inverter.dll"
   }
   ```
3. `dotnet build -c Release`, then zip the contents of `bin/Release/net10.0`. Add it on SolPnl's **Plugins** page, or
   unzip it into its own folder in the `plugins` folder of SolPnl's data directory and restart SolPnl.

The built-in plugins in [src/plugins](https://github.com/JosephAbreu/solpnl/tree/main/src/plugins) are working examples.

## Versions
SolPnl won't load a plugin built against a newer SolPnl.Contracts than the one it ships; the Plugins page says so. While
the version is 0.x, a new minor version (0.1 → 0.2) may change the interfaces, so rebuild your plugin against it.
Patch versions (0.1.0 → 0.1.1) only add things.
