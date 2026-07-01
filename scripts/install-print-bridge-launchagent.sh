#!/usr/bin/env bash

set -euo pipefail

LABEL="com.readyverse.photobooth.printbridge"
PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEAM_PRINT_BRIDGE_ROOT="/Users/ezreal/Downloads/Furryways2_Claude"
TEAM_PRINT_BRIDGE_ENV="${TEAM_PRINT_BRIDGE_ROOT}/print-bridge.env"
TEAM_PRINT_BRIDGE_DIR="${TEAM_PRINT_BRIDGE_ROOT}/Build/backend/print-bridge"
DEFAULT_PRINT_BRIDGE_DIR="${PROJECT_ROOT}/backend/print-bridge"

load_env_file() {
  local file_path="$1"
  local line name value
  while IFS= read -r line || [[ -n "${line}" ]]; do
    line="${line%$'\r'}"
    [[ -z "${line}" || "${line}" == \#* || "${line}" != *=* ]] && continue
    name="${line%%=*}"
    value="${line#*=}"
    name="$(printf '%s' "${name}" | LC_ALL=C sed 's/^\xEF\xBB\xBF//; s/^[[:space:]]*//; s/[[:space:]]*$//')"
    value="$(printf '%s' "${value}" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')"
    value="${value%\"}"
    value="${value#\"}"
    [[ -z "${name}" ]] && continue
    export "${name}=${value}"
  done < "${file_path}"
}

if [[ -f "${TEAM_PRINT_BRIDGE_ENV}" ]]; then
  load_env_file "${TEAM_PRINT_BRIDGE_ENV}"
fi

if [[ -z "${PRINT_BRIDGE_DIR:-}" && -d "${TEAM_PRINT_BRIDGE_DIR}" ]]; then
  PRINT_BRIDGE_DIR="${TEAM_PRINT_BRIDGE_DIR}"
fi
PRINT_BRIDGE_DIR="${PRINT_BRIDGE_DIR:-${DEFAULT_PRINT_BRIDGE_DIR}}"
PORT="${PORT:-18080}"
DEFAULT_PRINTER_NAME="${DEFAULT_PRINTER_NAME:-DS-RX1 4x6 Cut}"
ALLOWED_PRINTER_NAMES="${ALLOWED_PRINTER_NAMES:-${DEFAULT_PRINTER_NAME}}"
OVERRIDE_REQUESTED_PRINTER="${OVERRIDE_REQUESTED_PRINTER:-true}"
PRINT_COMMAND="${PRINT_COMMAND:-lp}"
PRINT_OPTIONS="${PRINT_OPTIONS:-PageSize=w4h6,orientation-requested=3,fit-to-page,MediaMethod=Normal,PaperType=LabelGaps,GapsHeight=3,PostAction=TearOff,Occurrence=Every,Brightness=0,HalftoneType=Stucki,Origin=Default,MirrorImage=False,NegativeImage=False,PrintSpeed=2,Darkness=13}"
NODE_BIN="${NODE_BIN:-}"

if [[ -z "${NODE_BIN}" ]]; then
  for candidate in /opt/homebrew/bin/node /usr/local/bin/node /usr/bin/node; do
    if [[ -x "${candidate}" ]]; then
      NODE_BIN="${candidate}"
      break
    fi
  done
fi

if [[ -z "${NODE_BIN}" || ! -x "${NODE_BIN}" ]]; then
  echo "Node.js was not found. Install Node.js or pass NODE_BIN=/absolute/path/to/node." >&2
  exit 127
fi

if ! lpstat -p "${DEFAULT_PRINTER_NAME}" >/dev/null 2>&1; then
  echo "Printer '${DEFAULT_PRINTER_NAME}' was not found by lpstat." >&2
  echo "Available printers:" >&2
  lpstat -p >&2 || true
  exit 2
fi

CONFIG_DIR="${HOME}/.photo-booth"
LOG_DIR="${HOME}/Library/Logs/PhotoBooth"
PLIST_PATH="${HOME}/Library/LaunchAgents/${LABEL}.plist"
ENV_FILE="${CONFIG_DIR}/print-bridge.env"
RUNNER_PATH="${PROJECT_ROOT}/scripts/run-print-bridge.sh"

mkdir -p "${CONFIG_DIR}" "${LOG_DIR}" "$(dirname "${PLIST_PATH}")"

cat > "${ENV_FILE}" <<EOF
PRINT_BRIDGE_DIR="${PRINT_BRIDGE_DIR}"
NODE_BIN="${NODE_BIN}"
PORT="${PORT}"
DEFAULT_PRINTER_NAME="${DEFAULT_PRINTER_NAME}"
ALLOWED_PRINTER_NAMES="${ALLOWED_PRINTER_NAMES}"
OVERRIDE_REQUESTED_PRINTER="${OVERRIDE_REQUESTED_PRINTER}"
PRINT_COMMAND="${PRINT_COMMAND}"
PRINT_OPTIONS="${PRINT_OPTIONS}"
EOF

cat > "${PLIST_PATH}" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>${LABEL}</string>
  <key>ProgramArguments</key>
  <array>
    <string>/bin/bash</string>
    <string>${RUNNER_PATH}</string>
  </array>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <true/>
  <key>StandardOutPath</key>
  <string>${LOG_DIR}/print-bridge.out.log</string>
  <key>StandardErrorPath</key>
  <string>${LOG_DIR}/print-bridge.err.log</string>
  <key>WorkingDirectory</key>
  <string>${PRINT_BRIDGE_DIR}</string>
</dict>
</plist>
EOF

launchctl bootout "gui/$(id -u)" "${PLIST_PATH}" >/dev/null 2>&1 || true
launchctl bootstrap "gui/$(id -u)" "${PLIST_PATH}"
launchctl kickstart -k "gui/$(id -u)/${LABEL}"

echo "Installed ${LABEL}"
echo "Config: ${ENV_FILE}"
echo "Plist:  ${PLIST_PATH}"
echo "Logs:   ${LOG_DIR}/print-bridge.out.log"
echo "Health: curl http://127.0.0.1:${PORT}/healthz"
