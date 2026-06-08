#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_DIR="$ROOT_DIR/tools/obsbot-control"
DEFAULT_SDK_PATH="$PROJECT_DIR/libdev_v2.1.0_8"
LEGACY_SDK_PATH="/Users/ezreal/Downloads/libdev_v2.1.0_8"
SDK_PATH="${OBSBOT_SDK_PATH:-$DEFAULT_SDK_PATH}"
if [[ ! -d "$SDK_PATH" && "$SDK_PATH" == "$DEFAULT_SDK_PATH" && -d "$LEGACY_SDK_PATH" ]]; then
  SDK_PATH="$LEGACY_SDK_PATH"
fi
BUILD_DIR="$PROJECT_DIR/build"
BIN_DIR="$PROJECT_DIR/bin"

if [[ ! -d "$SDK_PATH" ]]; then
  echo "OBSBOT SDK not found: $SDK_PATH" >&2
  echo "Put the SDK at $DEFAULT_SDK_PATH or set OBSBOT_SDK_PATH=/path/to/libdev_v2.1.0_8" >&2
  exit 1
fi

ARCH="$(uname -m)"
if [[ "$ARCH" == "arm64" ]]; then
  SDK_LIB_DIR="$SDK_PATH/macos/arm64-release"
else
  SDK_LIB_DIR="$SDK_PATH/macos/x86_64-release"
fi

mkdir -p "$BIN_DIR"
if command -v cmake >/dev/null 2>&1; then
  cmake -S "$PROJECT_DIR" -B "$BUILD_DIR" \
    -DOBSBOT_SDK_PATH="$SDK_PATH" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_ARCHITECTURES="$ARCH"
  cmake --build "$BUILD_DIR" --config Release
  cp "$BUILD_DIR/obsbot-control" "$BIN_DIR/obsbot-control"
else
  clang++ -std=c++11 -O2 \
    -I"$SDK_PATH/include" \
    "$PROJECT_DIR/src/obsbot_control.cpp" \
    -L"$SDK_LIB_DIR" \
    -ldev \
    -Wl,-rpath,@executable_path \
    -o "$BIN_DIR/obsbot-control"
fi

cp "$SDK_LIB_DIR/libdev.dylib" "$BIN_DIR/libdev.dylib"
chmod +x "$BIN_DIR/obsbot-control"

if command -v xattr >/dev/null 2>&1; then
  xattr -dr com.apple.quarantine "$BIN_DIR" 2>/dev/null || true
fi

if command -v codesign >/dev/null 2>&1; then
  codesign --force --deep --sign - "$BIN_DIR/libdev.dylib" >/dev/null 2>&1 || true
  codesign --force --sign - "$BIN_DIR/obsbot-control" >/dev/null 2>&1 || true
fi

echo "Built $BIN_DIR/obsbot-control"
echo "Try: $BIN_DIR/obsbot-control status"
