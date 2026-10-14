#!/usr/bin/env bash
# Deploy a built .tlx into TuneLab's extension directory (file-level install; close TuneLab first).
# Project root = parent of this script's folder (tools/).
#
# Environment overrides (all optional):
#   SVFS_TLX          explicit .tlx path     (default: newest in SVFS_PACKAGE_DIR / <root>/packages)
#   SVFS_PACKAGE_DIR  .tlx search directory   (default: <project root>/packages)
#   SVFS_EXT_DIR      TuneLab Extensions root (default: per-OS user config, see below)
#   SVFS_EXT_NAME     extension folder name   (default: SynthesizerVFormatSupport)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NAME="${SVFS_EXT_NAME:-SynthesizerVFormatSupport}"
PKG_DIR="${SVFS_PACKAGE_DIR:-$ROOT/packages}"

if [[ -n "${SVFS_TLX:-}" ]]; then
  TLX="$SVFS_TLX"
else
  TLX="$(ls -1t "$PKG_DIR"/*.tlx 2>/dev/null | head -n1 || true)"
fi
[[ -n "${TLX:-}" && -f "$TLX" ]] || { echo "no .tlx found - run packtlx.sh first (or set SVFS_TLX)" >&2; exit 1; }

# Extensions root, matching TuneLab's per-OS application-data location.
if [[ "$(uname)" == "Darwin" ]]; then
  BASE="${SVFS_EXT_DIR:-$HOME/Library/Application Support/TuneLab/Extensions}"
else
  CFG="${XDG_CONFIG_HOME:-$HOME/.config}"
  BASE="${SVFS_EXT_DIR:-$CFG/TuneLab/Extensions}"
fi

if pgrep -x TuneLab >/dev/null 2>&1; then
  echo "TuneLab is running - close it before deploying" >&2
  exit 1
fi
command -v unzip >/dev/null 2>&1 || { echo "unzip is required but not found" >&2; exit 1; }

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
unzip -q "$TLX" -d "$STAGE"
[[ -f "$STAGE/manifest.json" ]] || { echo "archive has no manifest.json at root - not a valid .tlx" >&2; exit 1; }

TARGET="$BASE/$NAME"
mkdir -p "$BASE"
if [[ -d "$TARGET" ]]; then
  # Rollback copies must live OUTSIDE the Extensions scan tree (TuneLab treats
  # any first-level folder with manifest.json as a package -> duplicate ext).
  RB="${SVFS_ROLLBACK_DIR:-$(dirname "$BASE")/Extensions-rollback}"
  mkdir -p "$RB"
  OLD="$RB/$NAME.old-$(date +%Y%m%d-%H%M%S)"
  mv "$TARGET" "$OLD"
  echo "previous install moved to: $OLD"
fi
mv "$STAGE" "$TARGET"
echo "deployed: $TARGET  (from $TLX)"
