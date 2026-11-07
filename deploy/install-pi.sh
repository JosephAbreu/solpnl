#!/usr/bin/env bash
# One-time SolPnl installation on a Raspberry Pi (Raspberry Pi OS / Debian, systemd).
# Run with sudo, passing the account that will deploy updates (default: the user who ran sudo):
#
#   sudo bash install-pi.sh [deploy-user]
#
# It is safe to run again. It:
#   - creates the 'solpnl' system user that runs the service,
#   - creates /opt/solpnl/releases (owned by the deploy user, so deploys need no sudo),
#   - installs the systemd unit (solpnl.service, next to this script),
#   - lets the deploy user start/stop/restart/status the service without a password (and nothing else).
set -euo pipefail

if [[ $EUID -ne 0 ]]; then
    echo "Run with sudo: sudo bash $0 [deploy-user]" >&2
    exit 1
fi

DEPLOY_USER="${1:-${SUDO_USER:-}}"
if [[ -z "$DEPLOY_USER" ]] || ! id "$DEPLOY_USER" >/dev/null 2>&1; then
    echo "Unknown deploy user '$DEPLOY_USER'. Pass it explicitly: sudo bash $0 <user>" >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UNIT_SOURCE="$SCRIPT_DIR/solpnl.service"
[[ -f "$UNIT_SOURCE" ]] || { echo "Missing $UNIT_SOURCE" >&2; exit 1; }

echo "==> Service user"
if ! id solpnl >/dev/null 2>&1; then
    useradd --system --home-dir /var/lib/solpnl --no-create-home --shell /usr/sbin/nologin solpnl
fi
# Let the deploy user read the data directory (e.g. to back up solpnl.db) via the solpnl group.
usermod -aG solpnl "$DEPLOY_USER"

echo "==> Directories"
install -d -m 0755 -o "$DEPLOY_USER" -g solpnl /opt/solpnl /opt/solpnl/releases
install -d -m 0750 -o solpnl -g solpnl /var/lib/solpnl

# Optional seed settings (device plugins) uploaded by deploy.ps1; never overwrites existing settings.
SEED="$SCRIPT_DIR/solpnl-seed.json"
SETTINGS=/var/lib/solpnl/appsettings.Local.json
if [[ -f "$SEED" && ! -f "$SETTINGS" ]]; then
    echo "==> Seeding device plugin settings"
    install -m 0600 -o solpnl -g solpnl "$SEED" "$SETTINGS"
fi

echo "==> systemd unit"
install -m 0644 "$UNIT_SOURCE" /etc/systemd/system/solpnl.service
systemctl daemon-reload
systemctl enable solpnl.service >/dev/null

echo "==> Passwordless service control for $DEPLOY_USER"
SUDOERS=/etc/sudoers.d/solpnl
TMP="$(mktemp)"
cat >"$TMP" <<EOF
# Installed by SolPnl install-pi.sh: lets $DEPLOY_USER manage only the solpnl service without a password.
$DEPLOY_USER ALL=(root) NOPASSWD: /usr/bin/systemctl start solpnl, /usr/bin/systemctl stop solpnl, /usr/bin/systemctl restart solpnl, /usr/bin/systemctl status solpnl
EOF
if visudo -cf "$TMP" >/dev/null; then
    install -m 0440 "$TMP" "$SUDOERS"
else
    echo "Generated sudoers file failed validation; not installed." >&2
fi
rm -f "$TMP"

echo
echo "Installed. Deploy a release from your PC with deploy/deploy.ps1."
if [[ -e /opt/solpnl/current ]]; then
    systemctl restart solpnl
    echo "Restarted the existing release."
fi
