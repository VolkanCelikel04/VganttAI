#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [[ -f "$ROOT_DIR/api/env.local" ]]; then
  set -a
  # shellcheck disable=SC1091
  source "$ROOT_DIR/api/env.local"
  set +a
elif [[ -f "$ROOT_DIR/api/.env" ]]; then
  set -a
  # shellcheck disable=SC1091
  source "$ROOT_DIR/api/.env"
  set +a
fi

exec dotnet run --project "$ROOT_DIR/api/VganttAi.Api.csproj" "$@"
