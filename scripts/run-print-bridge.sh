#!/usr/bin/env bash

set -euo pipefail

TEAM_PRINT_BRIDGE_ROOT="/Users/ezreal/Downloads/Furryways2_Claude"
TEAM_PRINT_BRIDGE_ENV="${TEAM_PRINT_BRIDGE_ROOT}/print-bridge.env"
TEAM_PRINT_BRIDGE_DIR="${TEAM_PRINT_BRIDGE_ROOT}/Build/backend/print-bridge"
DEFAULT_ENV_FILE="${HOME}/.photo-booth/print-bridge.env"

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

if [[ -n "${PHOTO_BOOTH_PRINT_BRIDGE_ENV:-}" ]]; then
  ENV_FILE="${PHOTO_BOOTH_PRINT_BRIDGE_ENV}"
elif [[ -f "${TEAM_PRINT_BRIDGE_ENV}" ]]; then
  ENV_FILE="${TEAM_PRINT_BRIDGE_ENV}"
else
  ENV_FILE="${DEFAULT_ENV_FILE}"
fi

if [[ -f "${ENV_FILE}" ]]; then
  load_env_file "${ENV_FILE}"
fi

REPO_PRINT_BRIDGE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../backend/print-bridge" && pwd)"
if [[ -z "${PRINT_BRIDGE_DIR:-}" && -d "${TEAM_PRINT_BRIDGE_DIR}" ]]; then
  PRINT_BRIDGE_DIR="${TEAM_PRINT_BRIDGE_DIR}"
fi
PRINT_BRIDGE_DIR="${PRINT_BRIDGE_DIR:-${REPO_PRINT_BRIDGE_DIR}}"
NODE_BIN="${NODE_BIN:-}"

if [[ -z "${NODE_BIN}" ]]; then
  for candidate in /opt/homebrew/bin/node /usr/local/bin/node /usr/bin/node; do
    if [[ -x "${candidate}" ]]; then
      NODE_BIN="${candidate}"
      break
    fi
  done
fi

if [[ -z "${NODE_BIN}" && -d "${HOME}/.nvm/versions/node" ]]; then
  while IFS= read -r candidate; do
    if [[ -x "${candidate}" ]]; then
      NODE_BIN="${candidate}"
      break
    fi
  done < <(find "${HOME}/.nvm/versions/node" -path "*/bin/node" -type f | sort -Vr)
fi

if [[ -z "${NODE_BIN}" || ! -x "${NODE_BIN}" ]]; then
  echo "Node.js was not found. Set NODE_BIN in ${ENV_FILE}." >&2
  exit 127
fi

export PORT="${PORT:-18080}"
export DEFAULT_PRINTER_NAME="${DEFAULT_PRINTER_NAME:-}"
export ALLOWED_PRINTER_NAMES="${ALLOWED_PRINTER_NAMES:-${DEFAULT_PRINTER_NAME}}"
export OVERRIDE_REQUESTED_PRINTER="${OVERRIDE_REQUESTED_PRINTER:-true}"
export PRINT_COMMAND="${PRINT_COMMAND:-lp}"
export PRINT_OPTIONS="${PRINT_OPTIONS:-PageSize=w4h6,orientation-requested=3,fit-to-page,MediaMethod=Normal,PaperType=LabelGaps,GapsHeight=3,PostAction=TearOff,Occurrence=Every,Brightness=0,HalftoneType=Stucki,Origin=Default,MirrorImage=False,NegativeImage=False,PrintSpeed=2,Darkness=13}"

if [[ -z "${DEFAULT_PRINTER_NAME}" ]]; then
  echo "DEFAULT_PRINTER_NAME is required. Set it in ${ENV_FILE}." >&2
  exit 2
fi

cd "${PRINT_BRIDGE_DIR}"
exec "${NODE_BIN}" src/server.js
