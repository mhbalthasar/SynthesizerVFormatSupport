#!/usr/bin/env bash
# Pack SynthesizerVFormatSupport into a .tlx (zip with manifest.json at archive root).
# Project root = parent of this script's folder (tools/).
#
# Self-contained: no Python, nothing beyond the standard coreutils (find/grep/dd/tr/
# sed/cut) plus `zip`. It builds the plugin, copies bin/package into the archive
# (dropping .pdb + dev cruft), strips absolute build paths out of the managed DLL's
# CodeView debug record (privacy hardening), and writes a forward-slash zip.
# tools/packtlx.ps1 does the same on Windows.
#
# Environment overrides (all optional):
#   SVFS_DOTNET             dotnet CLI                  (default: dotnet on PATH)
#   SVFS_PACKAGE_DIR        where the .tlx is written   (default: <root>/packages)
#   SVFS_TLX_NAME           output file name            (default: SynthesizerVFormatSupport.tlx)
#   SVFS_NO_BUILD=1         skip the dotnet build step
#   SVFS_TLX_NO_HARDEN=1    disable the CodeView path redaction (debugging only)
set -euo pipefail
export LC_ALL=C

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${SVFS_DOTNET:-dotnet}"

if [ "${SVFS_NO_BUILD:-0}" != "1" ]; then
    "$DOTNET" build "$ROOT/SynthesizerVFormatSupport.csproj" -c Release --nologo
fi

BIN="$ROOT/bin/package"
[ -f "$BIN/manifest.json" ] || { echo "build output not found: $BIN" >&2; exit 1; }
command -v zip >/dev/null 2>&1 || { echo "zip is required but not found" >&2; exit 1; }

OUT_DIR="${SVFS_PACKAGE_DIR:-$ROOT/packages}"
mkdir -p "$OUT_DIR"
TLX="$OUT_DIR/${SVFS_TLX_NAME:-SynthesizerVFormatSupport.tlx}"
HARDEN=1; [ "${SVFS_TLX_NO_HARDEN:-0}" = "1" ] && HARDEN=0

STAGE="$(mktemp -d)"
LIST="$(mktemp)"
# Cleanup via `find -delete` rather than `rm`: some CI/sandbox environments shadow
# `rm` with a wrapper script that then fails, which would abort the trap. `find -delete`
# is supported by both GNU findutils and BSD/macOS find.
cleanup() {
    find "$STAGE" -delete 2>/dev/null || true
    find "$LIST" -delete 2>/dev/null || true
    if [ -n "${TMP_TLX:-}" ] && [ -e "$TMP_TLX" ]; then find "$TMP_TLX" -delete 2>/dev/null || true; fi
}
trap cleanup EXIT

# ---- copy the keep-list into a staging dir (mirror tools/packtlx.ps1) ----
skipped=0
find "$BIN" -type f | sort > "$LIST"
while IFS= read -r f; do
    rel="${f#"$BIN"/}"
    name="${rel##*/}"
    low="$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]')"
    case "$low" in *.pdb) continue ;; esac
    skip=0
    for m in .bak -bak .prev .trace .old -old .orig .backup .stale .tmp .swp; do
        case "$low" in *"$m"*) skip=1; break ;; esac
    done
    if [ "$skip" = 1 ]; then skipped=$((skipped + 1)); continue; fi
    d="${rel%/*}"; if [ "$d" = "$rel" ]; then d="."; fi
    mkdir -p "$STAGE/$d"
    cp -f "$f" "$STAGE/$rel"
done < "$LIST"

[ -f "$STAGE/manifest.json" ] || { echo "manifest.json is not at the archive root" >&2; exit 1; }

# ---- privacy hardening ----------------------------------------------------
# Every PE debug record has a CodeView (RSDS) entry whose tail is a NUL-terminated
# PDB path. Rewrite an absolute one to '/_/<basename>', NUL-padded (length preserved)
# so the PE stays valid. Locating RSDS by byte offset avoids a full PE parser.
rsds_offsets() { LC_ALL=C grep -abo 'RSDS' "$1" 2>/dev/null | cut -d: -f1 || true; }
read_cv_path() {  # $1 = file, $2 = offset of the RSDS magic
    dd if="$1" bs=1 skip=$(($2 + 24)) count=1024 2>/dev/null | tr '\000' '\n' | head -n 1
}
harden_pe() {
    f="$1"
    for off in $(rsds_offsets "$f"); do
        p="$(read_cv_path "$f" "$off")"
        [ -n "$p" ] || continue
        case "$p" in
            [A-Za-z]:[\\/]*|/*) ;;
            *) continue ;;
        esac
        case "$p" in /_/*) continue ;; esac
        base="$(printf '%s' "$p" | tr '\\' '/' | sed 's#.*/##')"
        new="/_/$base"
        nlen=${#new}; olen=${#p}
        if [ "$nlen" -gt "$olen" ]; then new="$base"; nlen=${#new}; fi
        [ "$nlen" -le "$olen" ] || continue
        start=$(($off + 24))
        printf '%s' "$new" | dd of="$f" bs=1 seek="$start" conv=notrunc 2>/dev/null
        if [ "$nlen" -lt "$olen" ]; then
            dd if=/dev/zero of="$f" bs=1 seek=$(($start + nlen)) count=$(($olen - nlen)) conv=notrunc 2>/dev/null
        fi
        echo "               - ${f##*/}"
        echo "                   $p"
        echo "                -> $new"
    done
}

if [ "$HARDEN" = 1 ]; then
    find "$STAGE" -type f | sort > "$LIST"
    while IFS= read -r f; do
        case "$f" in *.dll|*.exe) harden_pe "$f" ;; esac
    done < "$LIST"
fi

# ---- zip (forward-slash entry names; -D = no directory entries, -X = strip extra attrs) ----
# Build into a unique temp name and `mv` it into place, so no `rm` of the target is needed.
TMP_TLX="${TLX}.tmp.$$"
( cd "$STAGE" && zip -qrXD "$TMP_TLX" . ) || { echo "zip failed (exit $?)" >&2; exit 1; }
[ -s "$TMP_TLX" ] || { echo "zip produced no archive" >&2; exit 1; }
mv -f "$TMP_TLX" "$TLX"

# ---- verify: no absolute CodeView path survives ----
leaks=0
find "$STAGE" -type f | sort > "$LIST"
while IFS= read -r f; do
    case "$f" in *.dll|*.exe) ;; *) continue ;; esac
    for off in $(rsds_offsets "$f"); do
        p="$(read_cv_path "$f" "$off")"
        case "$p" in
            [A-Za-z]:[\\/]*|/*) case "$p" in /_/*) ;; *) leaks=$((leaks + 1)); echo "               ! ${f#"$STAGE"/}: $p" ;; esac ;;
        esac
    done
done < "$LIST"

echo "packed       : $TLX"
echo "entries      : $(cd "$STAGE" && find . -type f | wc -l | tr -d ' ')"
if [ "$skipped" -gt 0 ]; then echo "skipped      : $skipped file(s) (.pdb / stale artifacts)"; fi
echo "root manifest: True"
if [ "$HARDEN" = 1 ]; then echo "hardened     : see redactions above (or 0 if none)"; else echo "hardened     : DISABLED (SVFS_TLX_NO_HARDEN=1)"; fi
echo "abs-path leak: $leaks   (must be 0)"
echo "tlx bytes    : $(wc -c < "$TLX" | tr -d ' ')"
[ "$leaks" -eq 0 ] || exit 1
