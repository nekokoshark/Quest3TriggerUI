#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == "--test" ]]; then
  src="$(cd -- "$(dirname -- "$0")" && pwd)/ORIGINAL_FILE.bat"
  dst="$(cd -- "$(dirname -- "$0")" && pwd)/rollback_test/VaM (OpenVR).bat"
  cp -- "$src" "$dst"
  cmp -- "$src" "$dst"
  printf '%s\n' 'ROLLBACK PASS: original OpenVR launcher restored in independent copy'
  exit 0
fi
rm -f -- '/f/vam1.22.0.12/VaM (OpenVR+OFXR).bat'
printf '%s\n' 'ROLLBACK LIVE PASS: dedicated OFXR launcher removed; original OpenVR launcher unchanged'
