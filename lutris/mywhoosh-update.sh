#!/usr/bin/env bash
# Update MyWhoosh itself to the Microsoft Store's latest version.
#
#   mywhoosh-update.sh --check     say whether there is a newer version
#   mywhoosh-update.sh             download it and install it over the old one
#
# The installer copies this next to blehelper.py in $GAMEDIR/bleshim, so by
# default it updates the install it sits in; GAMEDIR= points it elsewhere.
#
# There is no Store under Wine to update the game, so this does what the Lutris
# installer did the first time: fetch the Store's package with Dj0ulo's
# ms_store_download.py, extract it over drive_c/MyWhoosh, and rename the
# engine's executable to the name the launcher stub starts.  Nothing of ours is
# in the game directory -- the Bluetooth/OpenBikeControl shim lives in bleshim/
# and in memory -- so an update leaves it in place, and the game's files stay
# exactly Microsoft's, which is what its own hash check wants.
#
# A new game version can change WindowsConnectivity.dll.  The shim checks every
# signature it touches and steps aside rather than guess, so the worst case is
# Bluetooth or OpenBikeControl being off until the shim catches up -- the game
# itself still runs.  This says when that DLL changed; the first launch's
# session.log says whether everything still hooked.
set -euo pipefail

PRODUCT=9ndh0f2vhzx2
DOWNLOADER_URL=https://raw.githubusercontent.com/Dj0ulo/ms-store-download/main/ms_store_download.py

GAMEDIR="${GAMEDIR:-$(cd "$(dirname "$0")/.." && pwd)}"
APP="$GAMEDIR/drive_c/MyWhoosh"
WIN64="$APP/MyWhoosh/Binaries/Win64"
CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/mywhoosh-linux/store"

say() { echo "[mywhoosh-update] $*"; }

[ -f "$APP/AppxManifest.xml" ] || { say "no MyWhoosh in $APP (set GAMEDIR=)"; exit 1; }

installed() {
    grep -o '<Identity [^>]*' "$APP/AppxManifest.xml" | grep -o 'Version="[^"]*"' | head -1 | cut -d'"' -f2
}

# The downloader is fetched fresh, as the installer does, into a directory of
# its own and run isolated: it is code from the network, not from here.
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
curl -fsSL -o "$work/ms_store_download.py" "$DOWNLOADER_URL" \
    || { say "cannot fetch $DOWNLOADER_URL"; exit 1; }

latest="$(cd "$work" && python3 -I ms_store_download.py "$PRODUCT" --architecture x64 2>/dev/null \
          | sed -n 's/^FileName: *[^_]*_\([0-9.]*\)_x64\..*/\1/p' | head -1)"
[ -n "$latest" ] || { say "the Store did not answer with a package; try again later"; exit 1; }
have="$(installed)"
say "installed $have, Store has $latest"

newer() {   # is $1 a higher version than $2?
    [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -1)" = "$1" ]
}
if ! newer "$latest" "$have"; then
    say "up to date"
    exit 0
fi
[ "${1:-}" = "--check" ] && { say "an update is available: run this without --check"; exit 0; }

if pgrep -f 'MyWhoosh-Win64-Shipping|MyWhoosh\.exe' >/dev/null; then
    say "MyWhoosh is running -- quit it first"
    exit 1
fi

before="$(sha256sum "$WIN64/WindowsConnectivity.dll" 2>/dev/null | cut -d' ' -f1 || true)"

mkdir -p "$CACHE"
say "downloading and extracting $latest (several GB) ..."
(cd "$work" && python3 -I ms_store_download.py "$PRODUCT" --architecture x64 \
    --download "$CACHE" --extract "$APP" --extract-flat)
# The package ships the engine as Binaries/Win64/MyWhoosh.exe; the launcher
# stub at the top starts MyWhoosh-Win64-Shipping.exe.  As the installer does.
if [ -f "$WIN64/MyWhoosh.exe" ]; then
    mv -f "$WIN64/MyWhoosh.exe" "$WIN64/MyWhoosh-Win64-Shipping.exe"
fi
rm -f "$CACHE"/*.msix "$CACHE"/*.appx 2>/dev/null || true

say "now $(installed)"
after="$(sha256sum "$WIN64/WindowsConnectivity.dll" | cut -d' ' -f1)"
if [ "$before" != "$after" ]; then
    say "WindowsConnectivity.dll changed with this version.  After the first launch,"
    say "check $GAMEDIR/bleshim/session.log for 'hooked 4/4 exports' and the obc: lines;"
    say "anything missing means the shim needs an update for this game version."
else
    say "WindowsConnectivity.dll is unchanged: the shim fits as before"
fi
