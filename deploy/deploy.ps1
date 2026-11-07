<#
.SYNOPSIS
    Builds SolPnl for a Raspberry Pi and deploys it over SSH.

.DESCRIPTION
    Publishes a self-contained linux-arm64 build (no .NET needed on the Pi), uploads it to
    /opt/solpnl/releases/<timestamp>, points /opt/solpnl/current at it, restarts the service and checks health.
    Keeps the newest releases for rollback. Requires SSH key login and a one-time `install-pi.sh` on the Pi
    (use -Install to upload and run it; you'll be asked for your sudo password once).

.EXAMPLE
    ./deploy/deploy.ps1 -Install            # first time: installs the service, then deploys
.EXAMPLE
    ./deploy/deploy.ps1                     # update
.EXAMPLE
    ./deploy/deploy.ps1 -Rollback           # switch back to the previous release
#>
[CmdletBinding()]
param(
    [string]$Target = "administrator@server.local",
    [string]$Runtime = "linux-arm64",
    [int]$Port = 5255,
    [int]$KeepReleases = 3,
    [switch]$Install,
    [switch]$Rollback,
    # With -Install: copy only the "Plugins" section from this file to the Pi's settings (if the Pi has none yet),
    # so device plugins are configured from the start. Storage is chosen in the setup wizard.
    [string]$PluginSettingsFrom = ""
)
if (-not $PluginSettingsFrom) { $PluginSettingsFrom = Join-Path (Split-Path -Parent $PSScriptRoot) "src/SolPnl.Api/appsettings.Local.json" }

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$hostName = ($Target -split "@")[-1]

function Invoke-Remote([string]$Command) {
    # This script may be checked out with CRLF line endings (Windows); bash must never see '\r'
    # ('set -e\r' is an invalid option and aborts the whole command).
    $Command = $Command -replace "`r", ""
    ssh -o BatchMode=yes $Target $Command
    if ($LASTEXITCODE -ne 0) { throw "Remote command failed ($LASTEXITCODE): $Command" }
}

function Wait-Healthy {
    Write-Host "==> Waiting for SolPnl at http://${hostName}:$Port"
    for ($i = 0; $i -lt 60; $i++) {
        try {
            $health = Invoke-RestMethod -Uri "http://${hostName}:$Port/api/v1/health" -TimeoutSec 3
            if ($health.status -eq "ok") {
                if ($health.setupRequired) {
                    Write-Host "SolPnl is running and needs first-run setup:" -ForegroundColor Yellow
                    Write-Host "  Open http://${hostName}:$Port/setup"
                    Write-Host "  Setup code: ssh $Target `"journalctl -u solpnl -n 50 --no-pager | grep -o 'setup code [A-Z0-9-]*'`""
                } else {
                    Write-Host "SolPnl is running: http://${hostName}:$Port" -ForegroundColor Green
                }
                return
            }
        } catch { }
        Start-Sleep -Seconds 2
    }
    throw "SolPnl didn't become healthy. Check: ssh $Target `"journalctl -u solpnl -n 100 --no-pager`""
}

# Fail early if key-based SSH isn't working (deploys can't type passwords).
ssh -o BatchMode=yes -o ConnectTimeout=8 $Target "true"
if ($LASTEXITCODE -ne 0) { throw "Can't log in to $Target with an SSH key. See README (Deploying to a Raspberry Pi)." }

if ($Rollback) {
    Write-Host "==> Rolling back to the previous release"
    Invoke-Remote @'
set -e
cd /opt/solpnl/releases
current=$(basename "$(readlink -f /opt/solpnl/current)")
previous=$(ls -1 | sort | grep -v -x "$current" | awk -v c="$current" '$0 < c' | tail -1)
[ -n "$previous" ] || { echo "No earlier release to roll back to." >&2; exit 1; }
ln -sfn "/opt/solpnl/releases/$previous" /opt/solpnl/current.tmp && mv -T /opt/solpnl/current.tmp /opt/solpnl/current
echo "current -> $previous"
sudo -n systemctl restart solpnl
'@
    Wait-Healthy
    return
}

if ($Install) {
    Write-Host "==> Installing the service on $Target (asks for your sudo password)"
    $files = @("$PSScriptRoot/install-pi.sh", "$PSScriptRoot/solpnl.service")

    if (Test-Path $PluginSettingsFrom) {
        $plugins = (Get-Content $PluginSettingsFrom -Raw | ConvertFrom-Json).Plugins
        if ($plugins) {
            $seed = Join-Path ([System.IO.Path]::GetTempPath()) "solpnl-seed.json"
            # LF line endings and no BOM: the file is read on Linux.
            $json = (@{ Plugins = $plugins } | ConvertTo-Json -Depth 10) -replace "`r`n", "`n"
            [System.IO.File]::WriteAllText($seed, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
            $files += $seed
            Write-Host "    Including device plugin settings from $PluginSettingsFrom (Plugins section only)"
        }
    }

    scp -q @files "${Target}:/tmp/"
    if ($LASTEXITCODE -ne 0) { throw "Upload of install files failed." }
    if ($seed) { Remove-Item $seed -Force }
    ssh -t $Target "sudo bash /tmp/install-pi.sh `$USER; rc=`$?; rm -f /tmp/install-pi.sh /tmp/solpnl.service /tmp/solpnl-seed.json; exit `$rc"
    if ($LASTEXITCODE -ne 0) { throw "install-pi.sh failed." }
}

$release = Get-Date -Format "yyyyMMdd-HHmmss"
$publishDir = Join-Path $repo "artifacts/publish/$Runtime"
$archive = Join-Path $repo "artifacts/solpnl-$release-$Runtime.tar.gz"

Write-Host "==> Publishing $Runtime (self-contained)"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish (Join-Path $repo "src/SolPnl.Api/SolPnl.Api.csproj") -c Release -r $Runtime --self-contained true -o $publishDir -p:DebugType=none -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# Never ship machine-specific settings or local databases from the dev machine.
Get-ChildItem $publishDir -Include "appsettings.Local.json", "*.db", "*.db-shm", "*.db-wal", "appsettings.Development.json" -Recurse |
    Remove-Item -Force

Write-Host "==> Packaging"
tar -czf $archive -C $publishDir .
if ($LASTEXITCODE -ne 0) { throw "tar failed." }
Write-Host ("    {0:N1} MB" -f ((Get-Item $archive).Length / 1MB))

Write-Host "==> Uploading release $release"
scp -q $archive "${Target}:/tmp/solpnl-$release.tar.gz"
if ($LASTEXITCODE -ne 0) { throw "Upload failed." }

Write-Host "==> Activating"
Invoke-Remote @"
set -e
test -d /opt/solpnl/releases || { echo 'SolPnl is not installed on this machine. Run deploy.ps1 -Install.' >&2; exit 1; }
dir=/opt/solpnl/releases/$release
mkdir -p "`$dir"
tar -xzf /tmp/solpnl-$release.tar.gz -C "`$dir"
rm -f /tmp/solpnl-$release.tar.gz
chmod +x "`$dir/SolPnl.Api"
ln -sfn "`$dir" /opt/solpnl/current.tmp && mv -T /opt/solpnl/current.tmp /opt/solpnl/current
sudo -n systemctl restart solpnl
cd /opt/solpnl/releases && ls -1 | sort | head -n -$KeepReleases | xargs -r rm -rf
"@

Remove-Item $archive -Force
Wait-Healthy
