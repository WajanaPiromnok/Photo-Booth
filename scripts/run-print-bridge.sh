#!/usr/bin/env bash

set -euo pipefail

ENV_FILE="${PHOTO_BOOTH_PRINT_BRIDGE_ENV:-${HOME}/.photo-booth/print-bridge.env}"
if [[ -f "${ENV_FILE}" ]]; then
  # shellcheck disable=SC1090
  source "${ENV_FILE}"
fi

PRINT_BRIDGE_DIR="${PRINT_BRIDGE_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../backend/print-bridge" && pwd)}"
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
