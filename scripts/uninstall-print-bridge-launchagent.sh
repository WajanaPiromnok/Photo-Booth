#!/usr/bin/env bash

set -euo pipefail

LABEL="com.readyverse.photobooth.printbridge"
PLIST_PATH="${HOME}/Library/LaunchAgents/${LABEL}.plist"

launchctl bootout "gui/$(id -u)" "${PLIST_PATH}" >/dev/null 2>&1 || true
rm -f "${PLIST_PATH}"

echo "Uninstalled ${LABEL}"
echo "Config left in place: ${HOME}/.photo-booth/print-bridge.env"
echo "Logs left in place:   ${HOME}/Library/Logs/PhotoBooth"
