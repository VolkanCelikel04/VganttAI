#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

export HOME="$ROOT_DIR"
export XDG_CONFIG_HOME="$ROOT_DIR/.config"
export PUB_CACHE="$ROOT_DIR/.pub-cache"

if [[ ! -f "pubspec.yaml" && -f "$ROOT_DIR/mobile/pubspec.yaml" ]]; then
  cd "$ROOT_DIR/mobile"
fi

exec "$ROOT_DIR/tools/flutter/bin/flutter" "$@"
