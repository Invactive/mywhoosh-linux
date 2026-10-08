# tools — reading what the game actually does

Everything in this repository is built against MyWhoosh's own
`WindowsConnectivity.dll`: what it calls, in what order, with what signatures.
These are the three small programs that answer those questions, so decisions
come from the game's bytecode rather than from documentation or guesswork.

They all run **inside a Wine prefix, under wine-mono**, not under the host's
Mono. Two reasons: the host lacks `System.ServiceProcess`, which several of the
game's types reference (resolving a method fails before any code is read), and
type sizes have to come from the runtime the game will actually use.

## ILDump — what a method does

A minimal IL disassembler. Point it at a type and method:

```sh
./ildump.sh BluetoothManager.BluetoothProgram Advertisement_Received
./ildump.sh BluetoothManager.BluetoothProgram          # every method of a type
./ildump.sh FunctionsManager.MyWhoosh WD_GetDirconServiceAvailability
```

This is how the non-obvious behaviours were found — for example that the game
discards any Bluetooth advertisement carrying no service UUIDs, which otherwise
looks exactly like a scan that found nothing.

## SigDump / AllSigs — what a signature says about marshalling

Exact parameter attributes, `[MarshalAs]` (mono synthesises it from
`FieldMarshal`, so reflection sees it) and native struct layout:

```sh
mcs -platform:x64 -out:SigDump.exe SigDump.cs
mcs -platform:x64 -out:AllSigs.exe AllSigs.cs
WINEPREFIX=<prefix> wine SigDump.exe WindowsConnectivity.dll   # the four device-list exports
WINEPREFIX=<prefix> wine AllSigs.exe WindowsConnectivity.dll   # all 98 exports, odd ones flagged
```

`AllSigs` is what established that exactly four of the game's 98 exports take an
array by reference — the shape wine-mono cannot marshal, and the reason
`../exportshim/` exists. `SigDump` measured the struct those four return: 64
bytes under wine-mono.

## Building them, and which Wine runs them

`ildump.sh` runs in the game's prefix with **the prefix's own Wine**, found by
`wine.sh` from `<prefix>/version` (Proton and umu write the runner's name
there). Any other Wine rewrites the prefix as it starts (`wineboot -u`); set
`WINE=` if the right one is somewhere `wine.sh` does not look.

With no Mono on the host, `mcs.sh` supplies an `mcs` that runs wine-mono's
`mcs.exe` from an installed Proton, in a scratch prefix of its own
(`~/.cache/mywhoosh-linux/mcs-prefix`) — never the game's. The build scripts
source it; so can a shell (`. tools/mcs.sh`). Pass relative paths: wine-mono
reads `/home/...` as `C:\home\...`.

ILDump resolves the game's references from its own directory (and
`ILDUMP_PROBE`, `:`-separated — so a `Z:\...` path cannot go there). Methods
that touch WinRT types, like `BluetoothSensor`'s, need `Windows.dll` and
`System.Runtime.WindowsRuntime.dll` from `../bleshim/build/` copied beside it.

## Types — finding the class to look at

`Types.cs` is the same idea, smaller: it lists every type in an assembly, which
is how you find the name to point `ildump.sh` at.

```sh
mcs -out:Types.exe Types.cs
WINEPREFIX=<prefix> wine Types.exe WindowsConnectivity.dll
```
