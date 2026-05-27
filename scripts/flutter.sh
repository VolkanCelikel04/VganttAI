#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
USER_HOME="${HOME}"
USER_GEM_HOME="${USER_HOME}/.gem/ruby/2.6.0"
USER_GEM_BIN="${USER_GEM_HOME}/bin"

export HOME="$ROOT_DIR"
export XDG_CONFIG_HOME="$ROOT_DIR/.config"
export PUB_CACHE="$ROOT_DIR/.pub-cache"
export GRADLE_USER_HOME="$ROOT_DIR/.gradle"
export DEVELOPER_DIR="/Applications/Xcode-26.4.1.app/Contents/Developer"
export JAVA_HOME="$ROOT_DIR/tools/jdk/Contents/Home"
export ANDROID_HOME="$ROOT_DIR/tools/android-sdk"
export ANDROID_SDK_ROOT="$ANDROID_HOME"
export GEM_HOME="$USER_GEM_HOME"
export GEM_PATH="$USER_GEM_HOME:/Library/Ruby/Gems/2.6.0:/System/Library/Frameworks/Ruby.framework/Versions/2.6/usr/lib/ruby/gems/2.6.0"
export RUBYOPT="-rlogger ${RUBYOPT:-}"
export PATH="$USER_GEM_BIN:$JAVA_HOME/bin:$ANDROID_HOME/cmdline-tools/latest/bin:$ANDROID_HOME/platform-tools:$PATH"

if [[ ! -f "pubspec.yaml" && -f "$ROOT_DIR/mobile/pubspec.yaml" ]]; then
  cd "$ROOT_DIR/mobile"
fi

exec "$ROOT_DIR/tools/flutter/bin/flutter" "$@"
