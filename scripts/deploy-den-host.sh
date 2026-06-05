#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_FILE="${PROJECT_FILE:-$REPO_ROOT/src/DenHost/DenHost.csproj}"
PUBLISH_DIR="${PUBLISH_DIR:-}"
BUILD_ARTIFACTS_DIR="${BUILD_ARTIFACTS_DIR:-}"
DEPLOY_MODE="${DEPLOY_MODE:-auto}"
SSH_TARGET="${SSH_TARGET:-den-k8}"
BINARY_DIR="${BINARY_DIR:-/usr/local/bin}"
SERVICE_NAME="${SERVICE_NAME:-den-host.service}"
SERVICE_USER="${SERVICE_USER:-agent}"
SERVICE_GROUP="${SERVICE_GROUP:-agents}"
CONFIG_PATH="${CONFIG_PATH:-$REPO_ROOT/den-host.json}"
RUNTIME_DIR="${RUNTIME_DIR:-/var/lib/den-host}"
REMOTE_STAGE_DIR="${REMOTE_STAGE_DIR:-/tmp/den-host-live-publish}"
SKIP_RESTART=0
SKIP_SMOKE=0
DRY_RUN=0
BUILD_ONLY=0
WITH_FLEETOPS=0
INSTALL_FROM=""
TEMP_PUBLISH_DIR_CREATED=0
TEMP_BUILD_ARTIFACTS_DIR_CREATED=0

usage() {
  cat <<'EOF_USAGE'
Usage: scripts/deploy-den-host.sh [options]

Build and publish den-host (CLI/service binary) and install it locally or
on a remote host. The binary is published as a self-contained single-file
executable so .NET SDK is not required on the target machine.

Modes:
  local        Install on the current machine. Creates runtime dirs and
               registers a systemd system service.
  remote       Build locally and upload to SSH_TARGET, then install remotely.
  build-only   Build only, no install. Prints the install plan for a
               privileged agent or user to run. No sudo required.

DEPLOY_MODE defaults to auto. Auto selects local when running from /home/dev
on den-k8, otherwise remote.

Two-phase deploy workflow (agent-friendly):
  Phase 1 (any agent):  scripts/deploy-den-host.sh --build-only
  Phase 2 (sysadmin):   scripts/deploy-den-host.sh --install-from /tmp/den-host-live-publish.XXXXXX

This split lets a normal agent build the binary (no sudo needed) and then
pass the publish directory to a privileged agent for system install.

Do not run this script itself with sudo. In local mode it uses non-interactive
sudo internally for install steps. In remote mode it uses SSH plus remote sudo.

Options:
  --local                 Force local deployment mode
  --remote                Force remote SSH deployment mode
  --build-only            Build and publish only; skip install. Prints install plan.
  --with-fleetops         Also install and enable the FleetOps HTTP API service
                          (den-host-fleetops.service) alongside the background service.
  --install-from <dir>    Skip build; install from a pre-built publish directory.
  --skip-restart          Install binary but do not restart/enable systemd service
  --skip-smoke            Do not run den-host smoke checks after deploy
  --dry-run               Print resolved config and validate; no build/upload/install
  -h, --help              Show this help

Environment overrides:
  DEPLOY_MODE, SSH_TARGET, SERVICE_NAME, SERVICE_USER, SERVICE_GROUP,
  PROJECT_FILE, PUBLISH_DIR, BUILD_ARTIFACTS_DIR,
  BINARY_DIR, CONFIG_PATH, RUNTIME_DIR
EOF_USAGE
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --local)        DEPLOY_MODE=local ;;
      --remote)       DEPLOY_MODE=remote ;;
      --build-only)   BUILD_ONLY=1 ;;
      --with-fleetops) WITH_FLEETOPS=1 ;;
      --install-from) INSTALL_FROM="$2"; shift ;;
      --skip-restart) SKIP_RESTART=1 ;;
      --skip-smoke)   SKIP_SMOKE=1 ;;
      --dry-run)      DRY_RUN=1 ;;
      -h|--help)      usage; exit 0 ;;
      *) echo "Unknown argument: $1" >&2; usage >&2; exit 1 ;;
    esac
    shift
  done
}

resolve_deploy_mode() {
  case "$DEPLOY_MODE" in
    local|remote) ;;
    auto)
      if [[ "$(hostname)" == den-k8 || -d /home/dev/den-host ]]; then
        DEPLOY_MODE=local
      else
        DEPLOY_MODE=remote
      fi
      ;;
    *) echo "Invalid DEPLOY_MODE: $DEPLOY_MODE (expected auto, local, or remote)" >&2; exit 1 ;;
  esac
  echo "Deploy mode: $DEPLOY_MODE"
}

print_config() {
  cat <<EOF_CONFIG
Resolved deploy configuration:
  REPO_ROOT=$REPO_ROOT
  PROJECT_FILE=$PROJECT_FILE
  BUILD_ARTIFACTS_DIR=${BUILD_ARTIFACTS_DIR:-<temporary>}
  PUBLISH_DIR=${PUBLISH_DIR:-<temporary>}
  DEPLOY_MODE=$DEPLOY_MODE
  SSH_TARGET=$SSH_TARGET
  BINARY_DIR=$BINARY_DIR
  SERVICE_NAME=$SERVICE_NAME
  SERVICE_USER=$SERVICE_USER
  SERVICE_GROUP=$SERVICE_GROUP
  CONFIG_PATH=$CONFIG_PATH
  RUNTIME_DIR=$RUNTIME_DIR
  BUILD_ONLY=$BUILD_ONLY
  INSTALL_FROM=${INSTALL_FROM:-<none>}
  SKIP_RESTART=$SKIP_RESTART
  SKIP_SMOKE=$SKIP_SMOKE
  WITH_FLEETOPS=$WITH_FLEETOPS
EOF_CONFIG
}

preflight_tools() {
  command -v dotnet >/dev/null || { echo "dotnet is required" >&2; exit 1; }
  command -v rsync >/dev/null || { echo "rsync is required" >&2; exit 1; }
  [[ -f "$PROJECT_FILE" ]] || { echo "Project file not found: $PROJECT_FILE" >&2; exit 1; }
  if [[ "$DEPLOY_MODE" == "remote" ]]; then
    command -v ssh >/dev/null || { echo "ssh is required for remote mode" >&2; exit 1; }
  fi
}

shell_quote() {
  printf '%q' "$1"
}

require_non_root() {
  if [[ ${EUID:-$(id -u)} -eq 0 ]]; then
    echo "Run this script as your normal user, not with sudo." >&2
    echo "The script performs privileged install steps internally." >&2
    exit 1
  fi
}

preflight_privilege() {
  # Build-only and dry-run modes don't need sudo.
  if [[ "$BUILD_ONLY" -eq 1 || "$DRY_RUN" -eq 1 ]]; then
    return 0
  fi

  if [[ "$DEPLOY_MODE" == "local" ]]; then
    if ! sudo -n true 2>/dev/null; then
      cat >&2 <<EOF
Deploy preflight failed: local mode requires non-interactive sudo for
installing the binary to $BINARY_DIR and setting up runtime dirs under
$RUNTIME_DIR.

You have two options:
  1) Run with --build-only to build the binary, then ask a sysadmin agent
     to run: scripts/deploy-den-host.sh --install-from <publish-dir>
  2) Set BINARY_DIR and RUNTIME_DIR to paths your user owns.
EOF
      exit 1
    fi
  else
    if ! ssh "$SSH_TARGET" 'sudo -n true' 2>/dev/null; then
      cat >&2 <<EOF
Deploy preflight failed: remote mode requires SSH to $SSH_TARGET and
non-interactive sudo on the remote host.
EOF
      exit 1
    fi
  fi
}

initialize_publish_dir() {
  if [[ -n "$INSTALL_FROM" ]]; then
    PUBLISH_DIR="$INSTALL_FROM"
    echo "Using pre-built publish directory: $PUBLISH_DIR"
    return
  fi
  if [[ -n "$PUBLISH_DIR" ]]; then
    rm -rf "$PUBLISH_DIR"
    mkdir -p "$PUBLISH_DIR"
    return
  fi
  PUBLISH_DIR="$(mktemp -d /tmp/den-host-live-publish.XXXXXX)"
  TEMP_PUBLISH_DIR_CREATED=1
}

initialize_build_artifacts_dir() {
  if [[ -n "$INSTALL_FROM" ]]; then
    return
  fi
  if [[ -n "$BUILD_ARTIFACTS_DIR" ]]; then
    rm -rf "$BUILD_ARTIFACTS_DIR"
    mkdir -p "$BUILD_ARTIFACTS_DIR"
    return
  fi
  BUILD_ARTIFACTS_DIR="$(mktemp -d /tmp/den-host-live-artifacts.XXXXXX)"
  TEMP_BUILD_ARTIFACTS_DIR_CREATED=1
}

cleanup() {
  if [[ "$TEMP_PUBLISH_DIR_CREATED" -eq 1 && -n "$PUBLISH_DIR" ]]; then
    rm -rf "$PUBLISH_DIR"
  fi
  if [[ "$TEMP_BUILD_ARTIFACTS_DIR_CREATED" -eq 1 && -n "$BUILD_ARTIFACTS_DIR" ]]; then
    rm -rf "$BUILD_ARTIFACTS_DIR"
  fi
}

publish_binary() {
  echo "Publishing den-host as self-contained single-file binary ..."
  env \
    GIT_CONFIG_COUNT="${GIT_CONFIG_COUNT:-1}" \
    GIT_CONFIG_KEY_0="${GIT_CONFIG_KEY_0:-safe.directory}" \
    GIT_CONFIG_VALUE_0="${GIT_CONFIG_VALUE_0:-$REPO_ROOT}" \
    dotnet publish "$PROJECT_FILE" \
      -c Release \
      -r linux-x64 \
      --self-contained \
      --artifacts-path "$BUILD_ARTIFACTS_DIR" \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:DebugType=embedded \
      -o "$PUBLISH_DIR/"

  [[ -x "$PUBLISH_DIR/den-host" ]] || { echo "Publish output missing den-host executable" >&2; exit 1; }
  echo "Published: $(file "$PUBLISH_DIR/den-host")"
}

sudo_local() {
  sudo -n "$@"
}

remote_install_script() {
  cat <<'EOF_REMOTE'
set -euo pipefail
: "${BINARY_DIR:?}"
: "${SERVICE_NAME:?}"
: "${SERVICE_USER:?}"
: "${SERVICE_GROUP:?}"
: "${CONFIG_PATH:?}"
: "${RUNTIME_DIR:?}"
: "${SKIP_RESTART:?}"

publish_stage="$REMOTE_STAGE_DIR/publish"

if [[ ! -f "$publish_stage/den-host" ]]; then
  echo "Remote stage is missing den-host: $publish_stage" >&2
  exit 1
fi

echo "Installing den-host binary to $BINARY_DIR ..."
sudo -n install -d -m 0755 "$BINARY_DIR"
sudo -n cp "$publish_stage/den-host" "$BINARY_DIR/den-host"
sudo -n chmod 755 "$BINARY_DIR/den-host"

echo "Setting up runtime directories under $RUNTIME_DIR ..."
sudo -n install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$RUNTIME_DIR"
sudo -n install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$RUNTIME_DIR/run"
sudo -n install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$RUNTIME_DIR/state"
sudo -n install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$RUNTIME_DIR/log"
sudo -n install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$RUNTIME_DIR/quarantine"

echo "Installing config to $(dirname "$CONFIG_PATH") ..."
sudo -n install -d -m 0755 "$(dirname "$CONFIG_PATH")"
if [[ -f "$CONFIG_PATH" ]]; then
  echo "Config already exists at $CONFIG_PATH; preserving existing file."
else
  sudo -n cp "$publish_stage/den-host.example.json" "$CONFIG_PATH"
  sudo -n chmod 644 "$CONFIG_PATH"
fi

  # Warn if the API key environment file is missing.
  if [[ ! -f /etc/den-host.env ]]; then
    echo "WARNING: /etc/den-host.env not found. Create it with DEN_HOST_CORE_API_KEY and DEN_HOST_CHANNELS_API_KEY."
  fi

# Install the logrotate config so the per-run log files do not
# accumulate without bound. Substitute the runtime dir and service
# user into the template at install time.
if [[ -f "$publish_stage/den-host.logrotate" ]]; then
  echo "Installing logrotate config to /etc/logrotate.d/den-host ..."
  tmp_logrotate="$(mktemp /tmp/den-host.logrotate.XXXXXX)"
  sed \
    -e "s|@RUNTIME_DIR@|$RUNTIME_DIR|g" \
    -e "s|@SERVICE_USER@|$SERVICE_USER|g" \
    -e "s|@SERVICE_GROUP@|$SERVICE_GROUP|g" \
    "$publish_stage/den-host.logrotate" > "$tmp_logrotate"
  sudo -n install -d -m 0755 /etc/logrotate.d
  sudo -n cp "$tmp_logrotate" /etc/logrotate.d/den-host
  sudo -n chmod 644 /etc/logrotate.d/den-host
  rm -f "$tmp_logrotate"
fi

# Install systemd system service unit.
local_service_dir="/etc/systemd/system"
sudo -n install -d -m 0755 "$local_service_dir"
sudo -n cp "$publish_stage/den-host.service" "$local_service_dir/$SERVICE_NAME"
sudo -n chmod 644 "$local_service_dir/$SERVICE_NAME"
sudo -n systemctl daemon-reload

# Optionally install the FleetOps HTTP API service unit.
: "${WITH_FLEETOPS:=0}"
if [[ "$WITH_FLEETOPS" -eq 1 ]]; then
  echo "Installing FleetOps service unit ..."
  sudo -n cp "$publish_stage/den-host-fleetops.service" "$local_service_dir/den-host-fleetops.service"
  sudo -n chmod 644 "$local_service_dir/den-host-fleetops.service"
  sudo -n systemctl daemon-reload
fi

if [[ "$SKIP_RESTART" -eq 1 ]]; then
  echo "Installed den-host binary + service unit; skipping restart."
  if [[ "$WITH_FLEETOPS" -eq 1 ]]; then
    echo "FleetOps service unit installed but not restarted (--skip-restart)."
  fi
  sudo -n rm -rf "$REMOTE_STAGE_DIR"
  exit 0
fi

# Optionally enable and restart FleetOps service.
if [[ "$WITH_FLEETOPS" -eq 1 ]]; then
  echo "Enabling and restarting den-host-fleetops.service ..."
  sudo -n systemctl enable den-host-fleetops.service 2>/dev/null || true
  sudo -n systemctl restart den-host-fleetops.service || \
    echo "FleetOps service restart failed; check status manually." >&2
fi

echo "Enabling and restarting $SERVICE_NAME ..."
sudo -n systemctl enable "$SERVICE_NAME" 2>/dev/null || true
if sudo -n systemctl restart "$SERVICE_NAME"; then
  sudo -n systemctl --no-pager --full status "$SERVICE_NAME" --lines=15
  echo "Service $SERVICE_NAME restarted successfully."
  sudo -n rm -rf "$REMOTE_STAGE_DIR"
  exit 0
fi

echo "Service restart failed; binary is installed but service not running." >&2
sudo -n systemctl --no-pager --full status "$SERVICE_NAME" --lines=40 || true
exit 1
EOF_REMOTE
}

generate_service_unit() {
  local unit_path="$1"
  cat > "$unit_path" <<EOF_SERVICE
# den-host systemd service unit.
# Den Host is the harness-agnostic machine-local Den agent/runtime host.
# This unit runs background services (binding heartbeat, Channels shadow
# reader, reconciliation) as a Generic Host via \`den-host run\`.
#
# For the FleetOps HTTP API surface, deploy den-host-fleetops.service
# instead (or in addition), which runs \`den-host serve\` to expose the
# FleetOps REST API via Kestrel.
#
# To deploy: run scripts/deploy-den-host.sh or copy this unit to
# /etc/systemd/system/den-host.service and adapt paths as needed.
#
# After install:
#   sudo systemctl daemon-reload
#   sudo systemctl enable den-host.service
#   sudo systemctl start den-host.service
#   sudo systemctl status den-host.service
#
# View logs: journalctl -fu den-host.service

[Unit]
Description=Den Host – harness-agnostic local agent runtime
Documentation=https://github.com/FuzzySlipper/den-host
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=${BINARY_DIR}/den-host run
Restart=on-failure
RestartSec=10
StartLimitBurst=5

Environment="DEN_HOST_CONFIG=${CONFIG_PATH}"

# Set up runtime dirs one level up from the actual dirs so den-host
# creates its own subdirs under RUNTIME_DIR with correct permissions.
# Override in EnvironmentFile if needed.
Environment="DEN_HOST_RUN_DIR=${RUNTIME_DIR}/run"
Environment="DEN_HOST_STATE_DIR=${RUNTIME_DIR}/state"
Environment="DEN_HOST_LOG_DIR=${RUNTIME_DIR}/log"
Environment="DEN_HOST_QUARANTINE_DIR=${RUNTIME_DIR}/quarantine"

# Secrets should be set via environment or EnvironmentFile:
# Environment="DEN_HOST_CORE_API_KEY=..."
# Environment="DEN_HOST_CHANNELS_API_KEY=..."
# Do NOT embed secrets in the unit file.

EnvironmentFile=/etc/den-host.env

# RuntimeDirectories are created by systemd before ExecStart.
RuntimeDirectory=den-host
RuntimeDirectoryMode=0750

User=${SERVICE_USER}
Group=${SERVICE_GROUP}

# Pin the working directory to the runtime dir. den-host writes its
# per-run log files under \$DEN_HOST_LOG_DIR (a subdir of RUNTIME_DIR),
# so an unset cwd would land us in / and confuse the relative-path log
# tee inside HermesHarnessModule.WakeAsync.
WorkingDirectory=${RUNTIME_DIR}

# Hardening
ProtectSystem=full
PrivateTmp=yes
NoNewPrivileges=yes

[Install]
WantedBy=multi-user.target
EOF_SERVICE
}

generate_fleetops_service_unit() {
  local unit_path="$1"
  cat > "$unit_path" <<EOF_FLEETOPS
# den-host FleetOps HTTP API systemd service unit.
# Den Host FleetOps exposes a bounded machine-local Hermes fleet
# management REST API via Kestrel. This unit runs \`den-host serve\`,
# which binds to the FleetOps:ListenAddress configured in den-host.json.
#
# This unit is independent of den-host.service (Generic Host background
# services) and can be enabled alongside it or in place of it, depending
# on deployment needs.
#
# To deploy: run scripts/deploy-den-host.sh or copy this unit to
# /etc/systemd/system/den-host-fleetops.service and adapt paths as needed.
#
# After install:
#   sudo systemctl daemon-reload
#   sudo systemctl enable den-host-fleetops.service
#   sudo systemctl start den-host-fleetops.service
#   sudo systemctl status den-host-fleetops.service
#
# Health check: curl http://<listen-address>/api/host/health
# Overview:     curl http://<listen-address>/api/host/fleet-ops
# View logs:    journalctl -fu den-host-fleetops.service

[Unit]
Description=Den Host FleetOps – Hermes fleet management HTTP API
Documentation=https://github.com/FuzzySlipper/den-host
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=${BINARY_DIR}/den-host serve
Restart=on-failure
RestartSec=10
StartLimitBurst=5

Environment="DEN_HOST_CONFIG=${CONFIG_PATH}"

# Secrets should be set via environment or EnvironmentFile:
# Environment="DEN_HOST_CORE_API_KEY=..."
# Environment="DEN_HOST_CHANNELS_API_KEY=..."
# Do NOT embed secrets in the unit file.

EnvironmentFile=/etc/den-host.env

User=${SERVICE_USER}
Group=${SERVICE_GROUP}

WorkingDirectory=${RUNTIME_DIR}

# Hardening
ProtectSystem=full
PrivateTmp=yes
NoNewPrivileges=yes

[Install]
WantedBy=multi-user.target
EOF_FLEETOPS
}

preflight_config() {
  # Startup-time config validation precheck. Run \`den-host health --no-fail\`
  # before declaring the install successful. This catches obvious
  # misconfigurations (missing endpoints, bad URLs) at install time
  # rather than letting the service start and fail in journalctl. The
  # \`--no-fail\` flag is intentional: a degraded health report (e.g.,
  # Core unreachable in dev) is not an install failure; we just want
  # to surface it.
  if [[ "$DEPLOY_MODE" == "remote" ]]; then
    echo "Running remote startup precheck on $SSH_TARGET ..."
    ssh "$SSH_TARGET" "$(shell_quote "${BINARY_DIR}/den-host") health --no-fail" </dev/null || \
      echo "(startup precheck returned non-zero; review the output above before continuing.)"
  else
    echo "Running local startup precheck ..."
    "${BINARY_DIR}/den-host" health --no-fail || \
      echo "(startup precheck returned non-zero; review the output above before continuing.)"
  fi
}

smoke_binary() {
  if [[ "$SKIP_SMOKE" -eq 1 ]]; then
    echo "Skipping smoke checks."
    return
  fi

  local bin="${BINARY_DIR}/den-host"
  if [[ "$DEPLOY_MODE" == "remote" ]]; then
    echo "Running remote smoke checks on $SSH_TARGET ..."
    ssh "$SSH_TARGET" "$(shell_quote "$bin") version" </dev/null
    ssh "$SSH_TARGET" "$(shell_quote "$bin") health --no-fail" </dev/null || echo "(health status is informational; non-zero exit is expected if Core/Channels are unreachable)"
    ssh "$SSH_TARGET" "$(shell_quote "$bin") smoke" </dev/null || echo "(smoke status is informational; may be blocked if hermes not installed)"
    echo "Remote smoke checks completed."
  else
    echo "Running local smoke checks ..."
    "$bin" version
    "$bin" health --no-fail || echo "(health status is informational; non-zero exit is expected if Core/Channels are unreachable)"
    "$bin" smoke || echo "(smoke status is informational; may be blocked if hermes not installed)"
    echo "Local smoke checks completed."
  fi
}

sync_binary_local() {
  echo "Installing den-host to $BINARY_DIR ..."
  sudo_local install -d -m 0755 "$BINARY_DIR"
  sudo_local cp "$PUBLISH_DIR/den-host" "$BINARY_DIR/den-host"
  sudo_local chmod 755 "$BINARY_DIR/den-host"

  echo "Setting up runtime directories under $RUNTIME_DIR ..."
  sudo_local install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$RUNTIME_DIR"
  sudo_local install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$RUNTIME_DIR/run"
  sudo_local install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$RUNTIME_DIR/state"
  sudo_local install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$RUNTIME_DIR/log"
  sudo_local install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$RUNTIME_DIR/quarantine"

  # Copy the example config for reference; never overwrite an existing live config.
  if [[ ! -f "$CONFIG_PATH" ]]; then
    echo "Installing example config to $CONFIG_PATH ..."
    sudo_local cp "$REPO_ROOT/config/den-host.example.json" "$CONFIG_PATH"
    sudo_local chmod 644 "$CONFIG_PATH"
  else
    # Warn if the API key environment file is missing.
    if [[ ! -f /etc/den-host.env ]]; then
      echo "WARNING: /etc/den-host.env not found. Create it with DEN_HOST_CORE_API_KEY and DEN_HOST_CHANNELS_API_KEY."
    fi
    echo "Config exists at $CONFIG_PATH; preserving. Example at $REPO_ROOT/config/den-host.example.json."
  fi

  # Install the logrotate config so per-run log files do not accumulate.
  if [[ -f "$REPO_ROOT/scripts/den-host.logrotate" ]]; then
    echo "Installing logrotate config to /etc/logrotate.d/den-host ..."
    tmp_logrotate="$(mktemp /tmp/den-host.logrotate.XXXXXX)"
    sed \
      -e "s|@RUNTIME_DIR@|$RUNTIME_DIR|g" \
      -e "s|@SERVICE_USER@|$SERVICE_USER|g" \
      -e "s|@SERVICE_GROUP@|$SERVICE_GROUP|g" \
      "$REPO_ROOT/scripts/den-host.logrotate" > "$tmp_logrotate"
    sudo_local install -d -m 0755 /etc/logrotate.d
    sudo_local cp "$tmp_logrotate" /etc/logrotate.d/den-host
    sudo_local chmod 644 /etc/logrotate.d/den-host
    rm -f "$tmp_logrotate"
  fi

  # Generate and install the systemd service unit.
  local tmp_unit
  tmp_unit="$(mktemp /tmp/den-host.service.XXXXXX)"
  generate_service_unit "$tmp_unit"
  sudo_local cp "$tmp_unit" "/etc/systemd/system/$SERVICE_NAME"
  sudo_local chmod 644 "/etc/systemd/system/$SERVICE_NAME"
  rm -f "$tmp_unit"
  sudo_local systemctl daemon-reload

  # Optionally install the FleetOps HTTP API service unit.
  if [[ "$WITH_FLEETOPS" -eq 1 ]]; then
    local fleetops_tmp_unit
    fleetops_tmp_unit="$(mktemp /tmp/den-host-fleetops.service.XXXXXX)"
    generate_fleetops_service_unit "$fleetops_tmp_unit"
    sudo_local cp "$fleetops_tmp_unit" "/etc/systemd/system/den-host-fleetops.service"
    sudo_local chmod 644 "/etc/systemd/system/den-host-fleetops.service"
    rm -f "$fleetops_tmp_unit"
    sudo_local systemctl daemon-reload
    echo "FleetOps service unit: /etc/systemd/system/den-host-fleetops.service"

    if [[ "$SKIP_RESTART" -eq 0 ]]; then
      echo "Enabling and restarting den-host-fleetops.service ..."
      sudo_local systemctl enable den-host-fleetops.service 2>/dev/null || true
      sudo_local systemctl restart den-host-fleetops.service || \
        echo "FleetOps service restart failed; binary installed but service not running." >&2
    fi
  fi

  echo "Binary installed at $BINARY_DIR/den-host"
  echo "Runtime dirs under $RUNTIME_DIR"
  echo "Service unit: /etc/systemd/system/$SERVICE_NAME"
}

sync_binary_remote() {
  echo "Uploading publish output to $SSH_TARGET: ..."
  # shellcheck disable=SC2029
  ssh "$SSH_TARGET" "rm -rf $(shell_quote "$REMOTE_STAGE_DIR") && mkdir -p $(shell_quote "$REMOTE_STAGE_DIR/publish")"
  rsync -a --delete "$PUBLISH_DIR/" "$SSH_TARGET:$REMOTE_STAGE_DIR/publish/"
  # Also upload the example config for remote install.
  rsync -a "$REPO_ROOT/config/den-host.example.json" "$SSH_TARGET:$REMOTE_STAGE_DIR/publish/"
  if [[ -f "$REPO_ROOT/scripts/den-host.logrotate" ]]; then
    rsync -a "$REPO_ROOT/scripts/den-host.logrotate" "$SSH_TARGET:$REMOTE_STAGE_DIR/publish/"
  fi

  local remote_env remote_install_path
  remote_install_path="$REMOTE_STAGE_DIR/install-den-host.sh"
  remote_env="BINARY_DIR=$(shell_quote "$BINARY_DIR")"
  remote_env+=" SERVICE_NAME=$(shell_quote "$SERVICE_NAME")"
  remote_env+=" SERVICE_USER=$(shell_quote "$SERVICE_USER")"
  remote_env+=" SERVICE_GROUP=$(shell_quote "$SERVICE_GROUP")"
  remote_env+=" CONFIG_PATH=$(shell_quote "$CONFIG_PATH")"
  remote_env+=" RUNTIME_DIR=$(shell_quote "$RUNTIME_DIR")"
  remote_env+=" SKIP_RESTART=$(shell_quote "$SKIP_RESTART")"
  remote_env+=" REMOTE_STAGE_DIR=$(shell_quote "$REMOTE_STAGE_DIR")"
  remote_env+=" WITH_FLEETOPS=$(shell_quote "$WITH_FLEETOPS")"

  # shellcheck disable=SC2029
  remote_install_script | ssh "$SSH_TARGET" "cat > $(shell_quote "$remote_install_path") && chmod 700 $(shell_quote "$remote_install_path")"
  # shellcheck disable=SC2029
  ssh "$SSH_TARGET" "$remote_env bash $(shell_quote "$remote_install_path")"
}

sync_binary() {
  if [[ "$DEPLOY_MODE" == "local" ]]; then
    sync_binary_local
  else
    sync_binary_remote
  fi
}

print_install_plan() {
  echo ""
  echo "=== Build complete ==="
  echo "Publish directory: $PUBLISH_DIR"
  echo ""
  echo "The binary and support files are ready at:"
  echo "  binary:     $PUBLISH_DIR/den-host"
  echo "  unit file:  $PUBLISH_DIR/den-host.service"
  echo "  fleetops:   $PUBLISH_DIR/den-host-fleetops.service"

  if [[ -f "$PUBLISH_DIR/den-host.logrotate" ]]; then
    echo "  logrotate:  $PUBLISH_DIR/den-host.logrotate"
  fi
  echo "  example config: $REPO_ROOT/config/den-host.example.json"
  echo ""
  echo "============================== FIRST-TIME SETUP =============================="
  echo "If this is a first-time install, create /etc/den-host.env with:"
  echo "  DEN_HOST_CORE_API_KEY=<your-api-key>"
  echo "  DEN_HOST_CHANNELS_API_KEY=<your-api-key>"
  echo ""
  echo "To install on this machine, run:"
  echo ""
  echo "  scripts/deploy-den-host.sh --install-from $(shell_quote "$PUBLISH_DIR")"
  echo ""
  echo "To install with FleetOps HTTP API, add --with-fleetops:"
  echo "  scripts/deploy-den-host.sh --install-from $(shell_quote "$PUBLISH_DIR") --with-fleetops"
  echo ""
  echo "Or if handing off to a sysadmin agent, pass the publish directory path above."
  echo ""
  echo "=== End of build-only output ==="
}

prepare_publish_artifacts() {
  # Generate the systemd service units alongside the publish output so the
  # remote install script (or --install-from) can copy them.
  generate_service_unit "$PUBLISH_DIR/den-host.service"
  generate_fleetops_service_unit "$PUBLISH_DIR/den-host-fleetops.service"

  # Copy the logrotate template so it can be installed with substitutions.
  if [[ -f "$REPO_ROOT/scripts/den-host.logrotate" ]]; then
    cp "$REPO_ROOT/scripts/den-host.logrotate" "$PUBLISH_DIR/den-host.logrotate"
  fi
}

main() {
  require_non_root
  parse_args "$@"
  resolve_deploy_mode
  print_config
  preflight_tools

  if [[ "$DRY_RUN" -eq 1 ]]; then
    echo "Dry run requested; stopping before build/upload/install."
    exit 0
  fi

  preflight_privilege
  initialize_publish_dir
  initialize_build_artifacts_dir
  trap cleanup EXIT

  if [[ -z "$INSTALL_FROM" ]]; then
    # Full build phase: compile and stage artifacts.
    prepare_publish_artifacts
    publish_binary

    if [[ "$BUILD_ONLY" -eq 1 ]]; then
      # Suppress temp dir cleanup so the publish directory survives for
      # the --install-from phase. The caller is responsible for cleaning
      # it up after the install, or it will be reaped by /tmp cleanup.
      TEMP_PUBLISH_DIR_CREATED=0
      TEMP_BUILD_ARTIFACTS_DIR_CREATED=0
      print_install_plan
      exit 0
    fi
  else
    # Install-from mode: using a pre-built publish directory.
    if [[ ! -f "$PUBLISH_DIR/den-host" ]]; then
      echo "Install-from directory missing den-host binary: $PUBLISH_DIR" >&2
      exit 1
    fi
    echo "Using pre-built publish directory: $PUBLISH_DIR"
  fi

  sync_binary
  preflight_config
  smoke_binary
  echo "Deploy complete."
}

main "$@"
