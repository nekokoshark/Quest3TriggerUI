#!/usr/bin/env bash
set -euo pipefail
export PATH="/usr/bin:/bin:$PATH"

default_archive="/c/Users/Administrator/.codex/visualizations/2026/08/27/01a040a0-585c-7313-b018-a54adc4f41f9/Quest3TriggerUI_cleanup_archive_20260908/items"
default_target="/f/vam1.22.0.12/BepInEx/plugins/Quest3TriggerUI"

restore_items() {
  local archive="$1"
  local target="$2"
  [[ -d "$archive" ]] || { printf 'Archive missing: %s\n' "$archive" >&2; return 1; }
  [[ -d "$target" ]] || { printf 'Target missing: %s\n' "$target" >&2; return 1; }
  local entry name count=0
  shopt -s nullglob
  for entry in "$archive"/*; do
    name="$(basename -- "$entry")"
    [[ ! -e "$target/$name" ]] || { printf 'Rollback destination exists: %s\n' "$target/$name" >&2; return 1; }
  done
  for entry in "$archive"/*; do
    mv -- "$entry" "$target/"
    count=$((count + 1))
  done
  printf 'ROLLBACK PASS: restored %s top-level intermediate items\n' "$count"
}

case "${1:-}" in
  --live)
    restore_items "$default_archive" "$default_target"
    ;;
  --test)
    [[ $# -eq 3 ]] || { printf '%s\n' 'Usage: ROLLBACK.sh --test ARCHIVE TARGET' >&2; exit 2; }
    restore_items "$2" "$3"
    ;;
  *)
    printf '%s\n' 'Use --live to restore the external archive, or --test ARCHIVE TARGET.'
    ;;
esac
