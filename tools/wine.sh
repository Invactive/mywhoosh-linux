# Sourced by scripts that run Wine in an existing prefix: set WINE to the Wine
# that prefix belongs to.
#
# Any other Wine runs `wineboot -u` over the prefix the moment it starts --
# rewriting its registry and system files for a different version -- so "the
# first Wine on PATH" is not a safe default when the game was installed with a
# Proton.  Proton and umu record the runner's name in <prefix>/version
# ("GE-Proton11-7"), and Steam's and Lutris' runner directories are named after
# it.  When nothing matches, WINE is left empty and the caller decides whether
# a Wine is worth the risk.
#
# Env overrides: WINE (used as is), WINEPREFIX (default ~/Games/mywhoosh).

WINEPREFIX="${WINEPREFIX:-$HOME/Games/mywhoosh}"
export WINEPREFIX
if [ -z "${WINE:-}" ]; then
    runner="$(head -c 64 "$WINEPREFIX/version" 2>/dev/null | tr -d '\n')"
    if [ -n "$runner" ]; then
        for w in "$HOME"/.local/share/Steam/compatibilitytools.d/"$runner"*/files/bin/wine \
                 "$HOME"/.steam/root/compatibilitytools.d/"$runner"*/files/bin/wine \
                 "$HOME"/.local/share/lutris/runners/wine/"$runner"*/bin/wine \
                 "$HOME"/.local/share/lutris/runners/proton/"$runner"*/files/bin/wine; do
            [ -x "$w" ] && { WINE="$w"; break; }
        done
    fi
    unset runner
fi
