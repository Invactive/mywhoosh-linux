#!/usr/bin/env bash
# Run ILDump.exe under wine-mono inside the MyWhoosh prefix.
#
# Host Mono lacks System.ServiceProcess, which WindowsConnectivity references;
# resolving a method's local-variable signature then fails before any IL is read.
# wine-mono ships the full 4.5 BCL, so we disassemble there instead.
#
#   ./ildump.sh <Type.Name> [MethodName]
set -e
cd "$(dirname "$0")"

GAME_LIBS="${GAME_LIBS:-$HOME/Games/mywhoosh/drive_c/MyWhoosh/MyWhoosh/Binaries/Win64}"
# The Wine that made the prefix: any other one rewrites it on start (wine.sh).
. ./wine.sh
if [ -z "$WINE" ]; then
    echo "no Wine matching $WINEPREFIX/version found; set WINE to the one that runs the game" >&2
    exit 1
fi

[ -f ILDump.exe ] || { . ./mcs.sh; mcs -out:ILDump.exe ILDump.cs; }
cp -f "$GAME_LIBS/WindowsConnectivity.dll" .

WINEDEBUG=-all "$WINE" ILDump.exe WindowsConnectivity.dll "$@" 2>/dev/null
