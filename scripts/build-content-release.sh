#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

UNITY_PATH="${UNITY_PATH:-/Applications/Unity/Hub/Editor/6000.3.12f1/Unity.app/Contents/MacOS/Unity}"
PROFILE_PATH="${1:-${CONTENT_PROFILE_PATH:-}}"
VERSION_OVERRIDE="${2:-${CONTENT_VERSION:-}}"
OUTPUT_OVERRIDE="${3:-${CONTENT_OUTPUT:-}}"
LOG_PATH="${LOG_PATH:-/tmp/unity-build-content.log}"

if [[ -z "${PROFILE_PATH}" ]]; then
  echo "Usage:"
  echo "  ${0} <AssetDatabaseProfilePath> [version] [output-directory]"
  echo
  echo "Example:"
  echo "  ${0} Assets/ARStickerBooth/Editor/ContentProfiles/BoothContentBuildProfile.asset 1.2.0 Builds/Content"
  exit 1
fi

CMD=(
  "${UNITY_PATH}"
  -batchmode
  -nographics
  -projectPath "${PROJECT_ROOT}"
  -executeMethod PhotoBooth.Booth.Editor.Content.BoothContentBuilder.BuildFromCommandLine
  -boothContentProfile "${PROFILE_PATH}"
  -logFile "${LOG_PATH}"
  -quit
)

if [[ -n "${VERSION_OVERRIDE}" ]]; then
  CMD+=(-boothContentVersion "${VERSION_OVERRIDE}")
fi

if [[ -n "${OUTPUT_OVERRIDE}" ]]; then
  CMD+=(-boothContentOutput "${OUTPUT_OVERRIDE}")
fi

echo "Building content release..."
echo "Unity:   ${UNITY_PATH}"
echo "Project: ${PROJECT_ROOT}"
echo "Profile: ${PROFILE_PATH}"
echo "Log:     ${LOG_PATH}"

"${CMD[@]}"
