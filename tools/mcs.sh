# Sourced by the build scripts: make `mcs` work on a host with no Mono.
#
# The host's mcs is used when there is one.  Without it, wine-mono ships the
# same compiler as mcs.exe, and any Wine that carries a wine-mono can run it --
# Proton and GE-Proton keep theirs in files/share/wine/mono.  Arguments must
# then be relative paths: wine-mono reads /home/... as C:\home\..., and the
# build scripts cd to their own directory first for exactly this reason.
#
# It runs in a prefix of its own, never the game's.  A Wine other than the one
# that made a prefix runs `wineboot -u` over it the moment it starts, so
# compiling in the game's prefix with whichever Wine came first would quietly
# rewrite that prefix for a different runner.
#
# Env overrides: MCS_EXE (the mcs.exe to run), WINE, MCS_WINEPREFIX.

if ! command -v mcs >/dev/null 2>&1; then
    if [ -z "$WINE" ]; then
        for w in "$HOME"/.local/share/Steam/compatibilitytools.d/*/files/bin/wine \
                 "$HOME"/.local/share/lutris/runners/wine/*/bin/wine; do
            [ -x "$w" ] && [ -d "$(dirname "$w")/../share/wine/mono" ] && WINE="$w"
        done
        [ -n "$WINE" ] || WINE=wine
    fi
    if [ -z "$MCS_EXE" ]; then
        for m in "$(dirname "$WINE")"/../share/wine/mono/wine-mono-*/lib/mono/4.5/mcs.exe; do
            [ -f "$m" ] && MCS_EXE="$m"
        done
    fi
    if [ -z "$MCS_EXE" ]; then
        echo "no mcs: install Mono (mono-mcs), or set MCS_EXE to a wine-mono mcs.exe" >&2
        exit 1
    fi
    MCS_WINEPREFIX="${MCS_WINEPREFIX:-${XDG_CACHE_HOME:-$HOME/.cache}/mywhoosh-linux/mcs-prefix}"
    mkdir -p "$MCS_WINEPREFIX"
    echo "(no host mcs; using $MCS_EXE under $WINE, prefix $MCS_WINEPREFIX)" >&2
    mcs() {
        WINEPREFIX="$MCS_WINEPREFIX" WINEDEBUG=-all "$WINE" "$MCS_EXE" "$@" \
            2> >(grep -Ev '^(Fontconfig|wineserver: using)' >&2)
    }
fi
