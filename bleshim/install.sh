#!/usr/bin/env bash
# Put the shim where mono will find it, inside one Wine prefix.
#
#   WINEPREFIX=<prefix> ./install.sh
#   WINEPREFIX=<prefix> ./install.sh --verify     # also: SHIM_DIR=<dir>
#   WINEPREFIX=<prefix> ./install.sh --restore
#
# The assemblies go in the prefix's own wine-mono tree, never in the game
# directory: MyWhoosh hashes WindowsConnectivity.dll and silently declines to
# load it when a byte near it changed, and nothing here needs a game file
# touched.  mono probes c:\windows\mono\mono-2.0\lib for a referenced assembly
# by simple name, which is this directory, and `Windows` is exactly the name the
# game's metadata asks for.
#
# --restore puts ../winmd's inert stubs back rather than deleting anything: with
# no assembly of that name the game does not start at all.
#
# Both install and --verify report the Bonjour gate described below, because a
# prefix with a Bonjour service in it is the one thing that stops this working.
#
# --verify also looks where the Lutris installer puts the shim instead --
# $GAMEDIR/bleshim, found through MONO_PATH, because current runners (Proton,
# GE-Proton) keep no wine-mono tree in the prefix at all -- and reports the
# OpenBikeControl path (BikeControl over Wi-Fi): hooks, helper, avahi-daemon,
# and what is answering on the network right now.
set -e
cd "$(dirname "$0")"

WINEPREFIX="${WINEPREFIX:?set WINEPREFIX to the prefix to install into}"
# Exported explicitly: the gate check below shells out to wine, which reads it
# from the environment rather than from this shell.
export WINEPREFIX
TARGET="$WINEPREFIX/drive_c/windows/mono/mono-2.0/lib"
SHIM="Windows.dll System.Runtime.WindowsRuntime.dll"

[ -d "$WINEPREFIX/drive_c" ] || { echo "no prefix at $WINEPREFIX" >&2; exit 1; }
if [ "${1:-}" != "--verify" ] && [ ! -d "$TARGET" ]; then
    echo "no wine-mono in $WINEPREFIX (expected $TARGET)" >&2
    echo "a Proton prefix has none: install with the Lutris installer, which uses MONO_PATH" >&2
    exit 1
fi

# ------------------------------------------------------------ the Bonjour gate
# MyWhoosh only ever reaches Apple Bonjour's COM objects -- and, through them,
# the System.Runtime.InteropServices.ComAwareEventInfo that wine-mono leaves as
# NotImplementedException stubs -- when the SCM reports a service named exactly
# "Bonjour Service" in state Running.  Read out of the game's own IL:
# OpenBikeManager::GetNetworkState and WahooProgram::GetNetworkState are that
# test, each constructor stores the answer in isBonjourEnabled, and both call
# sites branch over their initialiser when it is false --
#
#   OpenBikeManager::OBM_Initialize   IL_0001 ldfld isBonjourEnabled
#                                     IL_0006 brfalse IL_0100 (the ret)
#   WahooProgram::.ctor               IL_0063 ldfld isBonjourEnabled
#                                     IL_0068 brfalse.s IL_0070 (past WFTNP_Init)
#
# Those two are the whole of it: nothing else in WindowsConnectivity.dll calls
# Marshal::GetTypeFromCLSID or constructs a ComAwareEventInfo.  So with no such
# service the shim needs no COM server and no patched runtime, and with one the
# game needs both -- Apple's COM objects must exist, or OBM_Initialize dies with
# a COMException before Bluetooth is ever reached.  A prefix that has had
# Bonjour installed into it (by Apple's installer, by iTunes, or by hand) is
# therefore worth knowing about, and this only reports it: the service is not
# ours and is never touched.
#
# The registry file answers the common case without starting Wine at all: a
# service that is not installed cannot be running.  Only when it is installed
# is Wine asked -- and only the prefix's own Wine, since any other one rewrites
# the prefix on start (../tools/wine.sh).
. ../tools/wine.sh

SVC='Bonjour Service'

service_installed() {
    # system.reg spells each backslash of a key name twice.
    grep -qF -e '[System\\ControlSet001\\Services\\'"$SVC]" \
             -e '[System\\CurrentControlSet\\Services\\'"$SVC]" "$WINEPREFIX/system.reg" 2>/dev/null
}

report_gate() {
    if ! service_installed; then
        echo "  gate     shut -- no '$SVC' installed, so the game skips Bonjour entirely"
        return
    fi
    if [ -z "$WINE" ]; then
        echo "  gate     ?    -- '$SVC' is installed; whether it runs needs the prefix's"
        echo "                  own Wine (set WINE=); if it does, the game takes the"
        echo "                  Bonjour path and crashes in OBM_Initialize"
        return
    fi
    if WINEDEBUG=-all "$WINE" sc query "$SVC" 2>/dev/null | tr -d '\r' | grep -q RUNNING; then
        echo "  gate     OPEN -- '$SVC' is RUNNING in this prefix"
        echo "           the game will take the Bonjour path and then need Apple's"
        echo "           COM objects, which wine-mono cannot drive (ComAwareEventInfo);"
        echo "           OpenBikeControl does not need it here.  Stop it:"
        echo "               WINEPREFIX=$WINEPREFIX \"$WINE\" net stop \"$SVC\""
    else
        echo "  gate     shut -- '$SVC' is installed but not running"
    fi
}

# Where the game finds the shim: MONO_PATH (as Lutris sets it, a Z: path),
# SHIM_DIR, or the Lutris layout's $GAMEDIR/bleshim, whose GAMEDIR is the prefix.
shim_dirs() {
    local d
    [ -d "$TARGET" ] && echo "$TARGET"
    if [ -n "${MONO_PATH:-}" ]; then
        local IFS=';'
        for d in $MONO_PATH; do
            d="${d#Z:}"; d="${d#z:}"
            [ -d "$d" ] && echo "$d"
        done
    fi
    [ -n "${SHIM_DIR:-}" ] && [ -d "$SHIM_DIR" ] && echo "$SHIM_DIR"
    [ -d "$WINEPREFIX/bleshim" ] && echo "$WINEPREFIX/bleshim"
}

# A .NET assembly keeps member names in its #Strings heap as UTF-8, and string
# literals in #US as UTF-16; either spelling is evidence enough.
has_name() {
    python3 -c 'import sys; b = open(sys.argv[1], "rb").read(); n = sys.argv[2]
sys.exit(0 if n.encode() in b or n.encode("utf-16-le") in b else 1)' "$1" "$2" 2>/dev/null
}

verify_dir() {
    local dir="$1" dll what
    echo "  in $dir"
    for dll in $SHIM MyWhooshShim.dll; do
        if [ ! -f "$dir/$dll" ]; then
            echo "    MISSING  $dll"
            continue
        fi
        if [ "$dll" = MyWhooshShim.dll ]; then what=../exportshim/build/$dll; else what=build/$dll; fi
        if [ -f "$what" ] && cmp -s "$what" "$dir/$dll"; then
            echo "    ok       $dll (this build)"
        elif [ -f "../winmd/build/$dll" ] && cmp -s "../winmd/build/$dll" "$dir/$dll"; then
            echo "    stub     $dll (../winmd -- the game will find no devices)"
        else
            echo "    other    $dll (not this build)"
        fi
    done
    [ -f "$dir/blehelper.py" ] && echo "    helper   $dir/blehelper.py"
}

# The OpenBikeControl path, piece by piece: each line names what is missing.
verify_obc() {
    local dir="$1" helper="" ok=1
    echo "  OpenBikeControl (BikeControl over Wi-Fi)"
    if [ -f "$dir/Windows.dll" ] && has_name "$dir/Windows.dll" "OBC_StartScan"; then
        echo "    ok       Windows.dll discovers OpenBikeControl devices"
    else
        echo "    MISSING  Windows.dll has no OpenBikeControl support (an older build)"; ok=0
    fi
    if [ -f "$dir/MyWhooshShim.dll" ] && has_name "$dir/MyWhooshShim.dll" "HookVoid"; then
        echo "    ok       MyWhooshShim.dll can hook OBC_StartScan/OBC_StopScan"
    else
        echo "    MISSING  MyWhooshShim.dll cannot hook the OBC exports (an older build)"; ok=0
    fi
    if [ -f "$dir/MyWhooshShim.dll" ] && has_name "$dir/MyWhooshShim.dll" "HookBool" \
       && has_name "$dir/Windows.dll" "WD_InstallDirconServiceAsync"; then
        echo "    ok       the connection screen's OpenBikeControl icon scans, and its"
        echo "             'install Dircon/Bonjour' button is refused"
    else
        echo "    MISSING  the OpenBikeControl icon offers to install Bonjour (an older build)"; ok=0
    fi
    for helper in "$dir/blehelper.py" ./blehelper.py; do [ -f "$helper" ] && break; done
    if grep -q '"mdns_browse"' "$helper" 2>/dev/null; then
        echo "    ok       $helper serves mdns_browse"
    else
        echo "    MISSING  $helper has no mdns_browse (an older helper)"; ok=0; helper=./blehelper.py
    fi
    if python3 -c 'import dbus; dbus.Interface(dbus.SystemBus().get_object("org.freedesktop.Avahi", "/"), "org.freedesktop.Avahi.Server").GetVersionString()' 2>/dev/null; then
        echo "    ok       avahi-daemon reachable on the system bus"
    else
        echo "    MISSING  avahi-daemon (sudo apt install avahi-daemon); nothing will be found"; ok=0
    fi
    if [ "${MYWHOOSH_OBC:-}" = 0 ]; then
        echo "    off      MYWHOOSH_OBC=0 is set in this environment"
    fi
    if [ $ok = 1 ]; then
        echo "    on the network now (5 s browse):"
        python3 "$helper" --mdns 2>/dev/null | sed 's/^/             /'
    fi
}

case "${1:-}" in
--verify)
    first=""
    for dir in $(shim_dirs | awk '!seen[$0]++'); do
        verify_dir "$dir"
        [ -n "$first" ] || first="$dir"
    done
    if [ -z "$first" ]; then
        echo "  MISSING  the shim: not in $TARGET, MONO_PATH, SHIM_DIR or $WINEPREFIX/bleshim"
        first=.
    fi
    report_gate
    verify_obc "$first"
    exit 0
    ;;
--restore)
    [ -d ../winmd/build ] || { echo "../winmd is not built; run ../winmd/build.sh" >&2; exit 1; }
    for dll in $SHIM; do
        cp -f "../winmd/build/$dll" "$TARGET/$dll"
        echo "  restored $TARGET/$dll (inert stub)"
    done
    echo "done -- the game starts and reports no Bluetooth devices"
    exit 0
    ;;
esac

for dll in $SHIM; do
    [ -f "build/$dll" ] && continue
    # A release bundle ships build/ prepopulated and has no build.sh at all.
    [ -x ./build.sh ] || { echo "missing build/$dll and no build.sh to make it" >&2; exit 1; }
    ./build.sh
    break
done

for dll in $SHIM; do
    cp -f "build/$dll" "$TARGET/$dll"
    echo "  installed $TARGET/$dll"
done

report_gate

cat <<'EOF'
done.  Two things this prefix still needs:

  * ./blehelper.py running before the game, or it sees no Bluetooth at all
  * ../exportshim installed, or the game's device list crashes on its first poll

and WindowsConnectivity.dll must be left unpatched, or the game will not load it.
EOF
