#!/usr/bin/env python3
"""
Serve Linux's Bluetooth to MyWhoosh's own BLE stack, running under Wine.

The game's BT_* path is written against WinRT, which Wine does not have and
mono cannot project.  ./src/Windows.cs answers those WinRT calls from inside the
prefix instead -- but it cannot reach BlueZ itself, because BlueZ is on D-Bus,
D-Bus is a unix socket, and Wine's winsock has no AF_UNIX.  So this does the
BlueZ half on the Linux side and the two halves talk over loopback TCP.

The protocol is newline-delimited JSON, deliberately dumb: it is a GATT client
API and nothing more, so that the day Wine's own Bluetooth stack grows
characteristic writes, notifications and scanning, the shim can drop this
process without anything above it changing.

    ->  {"id":1,"op":"scan","enable":true}
    <-  {"id":1,"ok":true}
    <-  {"ev":"advert","addr":"AA:BB:...","name":"Smart Trainer","uuids":[...],"rssi":-61}
    ->  {"id":2,"op":"connect","addr":"AA:BB:..."}
    <-  {"id":2,"ok":true,"name":"Smart Trainer"}
    ->  {"id":3,"op":"services","addr":"AA:BB:..."}
    <-  {"id":3,"ok":true,"services":[{"uuid":"00001826-..."}]}
    ->  {"id":4,"op":"notify","addr":"AA:BB:...","char":"00002ad2-...","enable":true}
    <-  {"id":4,"ok":true}
    <-  {"ev":"value","addr":"AA:BB:...","char":"00002ad2-...","value":"44024a00..."}

Ops: radios, scan, connect, disconnect, services, chars, read, write, notify,
mdns_browse, tcp_peer.  Events: advert, value, connection, mdns, mdns_lost.

mdns_browse is not Bluetooth: it is how the game's OpenBikeControl discovery
(BikeControl and friends, over Wi-Fi) reaches avahi-daemon -- see class Avahi.

    ->  {"id":5,"op":"mdns_browse","type":"_openbikecontrol._tcp","enable":true}
    <-  {"id":5,"ok":true}
    <-  {"ev":"mdns","type":"_openbikecontrol._tcp","name":"BikeControl",
         "host":"BikeControl.local","ip":"192.168.1.19","port":36870,...}

Usage:
    ./blehelper.py                  # serve on 127.0.0.1:27019
    ./blehelper.py --list           # scan, print what is on the air, exit
    ./blehelper.py --mdns           # browse _openbikecontrol._tcp, print, exit
    ./blehelper.py --port 27019 --adapter hci0

Start it before the game; nothing has to be configured per trainer, because the
game does its own scanning, pairing and slot filling through it.  This connects
to nothing by itself: it holds no device the game has not asked for, and several
sensors are simply several independent connections.
"""

import argparse
import errno
import json
import socket
import struct
import sys
import time

import dbus
import dbus.mainloop.glib
from gi.repository import GLib

BLUEZ = "org.bluez"
OM_IFACE = "org.freedesktop.DBus.ObjectManager"
PROPS_IFACE = "org.freedesktop.DBus.Properties"
ADAPTER_IFACE = "org.bluez.Adapter1"
DEVICE_IFACE = "org.bluez.Device1"
SERVICE_IFACE = "org.bluez.GattService1"
CHAR_IFACE = "org.bluez.GattCharacteristic1"

AVAHI = "org.freedesktop.Avahi"
AVAHI_SERVER_IFACE = "org.freedesktop.Avahi.Server"
AVAHI_BROWSER_IFACE = "org.freedesktop.Avahi.ServiceBrowser"
AVAHI_RESOLVER_IFACE = "org.freedesktop.Avahi.ServiceResolver"
AVAHI_IF_UNSPEC = -1
AVAHI_PROTO_INET = 0
OBC_SERVICE = "_openbikecontrol._tcp"

CONNECT_TIMEOUT = 25          # seconds waiting for a link and its GATT tree
ADVERT_TIMEOUT = 20           # seconds waiting for a named device to be on the air

verbose = False
DEFERRED = object()           # dispatch's "the reply comes later"


def log(fmt, *a):
    sys.stderr.write("[%s blehelper] %s\n"
                     % (time.strftime("%H:%M:%S"), fmt % a if a else fmt))
    sys.stderr.flush()


def trace(fmt, *a):
    if verbose:
        log(fmt, *a)


class Bluez:
    """BlueZ, in the few verbs the shim needs of it."""

    def __init__(self, bus, adapter):
        self.bus = bus
        self.adapter_path = "/org/bluez/%s" % adapter
        self.om = dbus.Interface(bus.get_object(BLUEZ, "/"), OM_IFACE)
        self.scanning = False
        self.wanted = False                 # the client asked for a scan
        self.waiters = 0                    # connects waiting for an advertisement

    # --------------------------------------------------------------- lookup

    def objects(self):
        return self.om.GetManagedObjects()

    def device_path(self, addr):
        return "%s/dev_%s" % (self.adapter_path, addr.upper().replace(":", "_"))

    def device_props(self, addr):
        return self.objects().get(self.device_path(addr), {}).get(DEVICE_IFACE, {})

    def on_air(self, addr):
        """Is this device actually advertising, or merely remembered?

        BlueZ keeps an object for every device it has ever seen, and connecting
        to one that is not there costs a 30s timeout in Connect().  RSSI is set
        from an advertisement received during the current discovery and cleared
        when discovery stops, which is exactly the distinction worth making --
        a trainer that has been asleep since yesterday looks identical without
        it.
        """
        d = self.device_props(addr)
        return "RSSI" in d or bool(d.get("Connected"))

    def adapter(self):
        return dbus.Interface(self.bus.get_object(BLUEZ, self.adapter_path), ADAPTER_IFACE)

    def radios(self):
        """Every adapter BlueZ has, and whether it is powered.

        The game asks this first and shows the user "Bluetooth is off" for an
        empty or unpowered answer, so it comes from Adapter1.Powered -- the one
        source that cannot disagree with what a scan will then do.  (sysfs looks
        like the easier route from inside the prefix and is not: current kernels
        have no /sys/class/bluetooth/hciN/flags to read at all.)
        """
        out = []
        for path, ifaces in sorted(self.objects().items()):
            a = ifaces.get(ADAPTER_IFACE)
            if not a:
                continue
            out.append({"name": path.rsplit("/", 1)[-1],
                        "address": str(a.get("Address", "")),
                        "powered": bool(a.get("Powered", False))})
        return out

    # ------------------------------------------------------------ discovery

    def start_scan(self):
        if self.scanning:
            return
        try:
            self.adapter().SetDiscoveryFilter({"Transport": "le", "DuplicateData": dbus.Boolean(True)})
        except dbus.DBusException as e:
            log("discovery filter refused (%s), scanning anyway", e.get_dbus_name())
        try:
            self.adapter().StartDiscovery()
        except dbus.DBusException as e:
            if e.get_dbus_name() != "org.bluez.Error.InProgress":
                raise
        self.scanning = True
        log("scanning")

    def stop_scan(self):
        if not self.scanning:
            return
        self.scanning = False
        try:
            self.adapter().StopDiscovery()
        except dbus.DBusException as e:
            trace("StopDiscovery: %s", e.get_dbus_name())
        log("scan stopped")

    def wait_for_advert(self, addr, seconds, then):
        """Scan until this device is on the air, whatever the client asked for,
        then call then(seen).

        The game connects to a device it saw in a scan it has since stopped, and
        a trainer drops its radio the moment the cranks stop turning.  Waiting
        here turns "connect failed" into "connect took a moment", and the scan
        state the client asked for is restored afterwards.

        Asynchronous, like everything a connect does: a trainer that is asleep
        costs ADVERT_TIMEOUT seconds, the game retries every ~25 s while it
        sleeps, and the helper must go on answering everything else meanwhile
        -- the radio check, the scan, OpenBikeControl's browse.
        """
        if self.on_air(addr):
            then(True)
            return
        if self.waiters == 0:
            self.start_scan()
        self.waiters += 1
        state = {"done": False}

        def finish(seen):
            if state["done"]:
                return False
            state["done"] = True
            self.waiters -= 1
            if self.waiters == 0 and not self.wanted:
                self.stop_scan()
            then(seen)
            return False

        def poll():
            if state["done"]:
                return False
            if not self.on_air(addr):
                return True
            return finish(True)

        GLib.timeout_add(300, poll)
        GLib.timeout_add_seconds(seconds, lambda: finish(False))

    # -------------------------------------------------------------- connect

    def connect(self, addr, done):
        """Connect and wait for the GATT tree, the way WinRT's
        FromBluetoothAddressAsync leaves a device ready to be walked; then
        done(name) or done(None, error).  Returns at once."""
        path = self.device_path(addr)

        def fail(why):
            done(None, why)

        def guarded(step):
            # Each step runs from the main loop; nothing may escape it.
            def run(*a):
                try:
                    step(*a)
                except dbus.DBusException as e:
                    fail(e.get_dbus_message() or e.get_dbus_name())
                except BleError as e:
                    fail(str(e))
                return False
            return run

        def props():
            return dbus.Interface(self.bus.get_object(BLUEZ, path), PROPS_IFACE)

        @guarded
        def start():
            if path not in self.objects():
                self.wait_for_advert(addr, ADVERT_TIMEOUT, guarded(after_known))
            else:
                after_known(True)

        def after_known(seen):
            if not seen:
                raise BleError("%s is not on the air (asleep, or out of range)" % addr)
            if props().Get(DEVICE_IFACE, "Connected"):
                connected()
            elif self.on_air(addr):
                link()
            else:
                self.wait_for_advert(addr, ADVERT_TIMEOUT, guarded(after_air))

        def after_air(seen):
            if not seen:
                raise BleError("%s is not on the air (asleep, or out of range)" % addr)
            link()

        def link():
            log("connecting to %s", addr)
            dbus.Interface(self.bus.get_object(BLUEZ, path), DEVICE_IFACE).Connect(
                reply_handler=guarded(connected),
                error_handler=lambda e: fail("Connect() failed: %s" % e.get_dbus_message()),
                timeout=CONNECT_TIMEOUT + 10)

        def connected():
            try:
                # An untrusted device is one BlueZ will not let reconnect on
                # its own, and a trainer that sleeps between intervals needs to.
                if not props().Get(DEVICE_IFACE, "Trusted"):
                    props().Set(DEVICE_IFACE, "Trusted", dbus.Boolean(True))
            except dbus.DBusException:
                pass
            deadline = time.monotonic() + CONNECT_TIMEOUT

            @guarded
            def resolved():
                if props().Get(DEVICE_IFACE, "ServicesResolved"):
                    name = str(self.device_props(addr).get("Alias", "") or "")
                    log("%s connected as %r", addr, name)
                    done(name)
                elif time.monotonic() > deadline:
                    fail("connected to %s but its services never resolved" % addr)
                else:
                    GLib.timeout_add(300, resolved)

            resolved()

        start()

    def disconnect(self, addr):
        path = self.device_path(addr)
        if path not in self.objects():
            return
        try:
            dbus.Interface(self.bus.get_object(BLUEZ, path), DEVICE_IFACE).Disconnect()
            log("%s disconnected", addr)
        except dbus.DBusException as e:
            trace("Disconnect(%s): %s", addr, e.get_dbus_name())

    # ----------------------------------------------------------------- gatt

    def services(self, addr):
        prefix = self.device_path(addr) + "/"
        out = []
        for path, ifaces in sorted(self.objects().items()):
            svc = ifaces.get(SERVICE_IFACE)
            if svc and path.startswith(prefix):
                out.append({"uuid": str(svc["UUID"]).lower()})
        return out

    def characteristics(self, addr, service_uuid):
        objs = self.objects()
        prefix = self.device_path(addr) + "/"
        wanted = {path for path, ifaces in objs.items()
                  if path.startswith(prefix)
                  and SERVICE_IFACE in ifaces
                  and str(ifaces[SERVICE_IFACE]["UUID"]).lower() == service_uuid.lower()}
        out = []
        for path, ifaces in sorted(objs.items()):
            ch = ifaces.get(CHAR_IFACE)
            if not ch or str(ch["Service"]) not in wanted:
                continue
            out.append({"uuid": str(ch["UUID"]).lower(),
                        "flags": [str(f) for f in ch.get("Flags", [])]})
        return out

    def char_path(self, addr, char_uuid):
        prefix = self.device_path(addr) + "/"
        for path, ifaces in sorted(self.objects().items()):
            ch = ifaces.get(CHAR_IFACE)
            if ch and path.startswith(prefix) and str(ch["UUID"]).lower() == char_uuid.lower():
                return path
        raise BleError("%s has no characteristic %s" % (addr, char_uuid))

    def char(self, addr, char_uuid):
        return dbus.Interface(self.bus.get_object(BLUEZ, self.char_path(addr, char_uuid)), CHAR_IFACE)

    def read(self, addr, char_uuid):
        try:
            value = self.char(addr, char_uuid).ReadValue({})
        except dbus.DBusException as e:
            raise BleError("ReadValue failed: %s" % e.get_dbus_message())
        return bytes(bytearray(value))

    def write(self, addr, char_uuid, value, with_response):
        options = {"type": "request" if with_response else "command"}
        try:
            self.char(addr, char_uuid).WriteValue(dbus.Array(value, signature="y"), options)
        except dbus.DBusException as e:
            raise BleError("WriteValue failed: %s" % e.get_dbus_message())

    def notify(self, addr, char_uuid, enable):
        char = self.char(addr, char_uuid)
        try:
            if enable:
                char.StartNotify()
            else:
                char.StopNotify()
        except dbus.DBusException as e:
            # Subscribing twice is the game re-pairing a sensor it already has,
            # which is not a failure worth reporting up.
            if e.get_dbus_name() == "org.bluez.Error.InProgress" and enable:
                trace("%s %s already notifying", addr, char_uuid)
                return
            raise BleError("%s failed: %s" % ("StartNotify" if enable else "StopNotify",
                                              e.get_dbus_message()))


class BleError(Exception):
    """Something the client asked for that the hardware would not do."""


class Avahi:
    """mDNS service discovery, through the host's avahi-daemon.

    MyWhoosh finds OpenBikeControl devices (BikeControl, KICKR BIKE PRO ...)
    by browsing `_openbikecontrol._tcp` with Apple Bonjour's COM objects, and
    those are exactly what the Bonjour gate keeps it away from -- see
    ../bleshim/CLAUDE.md.  So the browse happens here instead.  Not inside Wine:
    the host's avahi-daemon already owns UDP 5353, and a second responder in
    the prefix would fight it for every answer.  Avahi is on the system bus,
    which this process is already on for BlueZ.

    One report per service, however many interfaces it is seen on, and only
    IPv4: OpenBikeManager::ServiceResolved keeps the first IPv4 address and
    nothing else.

    Each service is followed by an avahi ServiceResolver, which stays alive and
    reports again whenever the answer changes -- the phone's DHCP lease moving
    it to a new address, or the app coming back on a different port (BikeControl
    walks to the next free one).  A changed answer goes out as a new `mdns`
    event, and the shim offers the game the new address.
    """

    def __init__(self, bus, emit):
        self.bus = bus
        self.emit = emit                    # called with each event dict
        self.browsers = {}                  # service type -> browser object path
        self.seen = {}                      # (type, name) -> interface indexes listing it
        self.resolvers = {}                 # resolver object path -> ((type, name), interface)
        self.resolved = {}                  # (type, name) -> the event last reported
        self.hooked = False

    def server(self):
        return dbus.Interface(self.bus.get_object(AVAHI, "/"), AVAHI_SERVER_IFACE)

    def _hook(self):
        # Subscribed by interface rather than by object path, and before any
        # object exists: avahi starts work the moment ServiceBrowserNew or
        # ServiceResolverNew returns, and a path-matched receiver added after
        # that can miss the first signal.  The handlers filter by path instead.
        if self.hooked:
            return
        for iface, member, fn in ((AVAHI_BROWSER_IFACE, "ItemNew", self._item_new),
                                  (AVAHI_BROWSER_IFACE, "ItemRemove", self._item_remove),
                                  (AVAHI_BROWSER_IFACE, "Failure", self._failure),
                                  (AVAHI_RESOLVER_IFACE, "Found", self._found),
                                  (AVAHI_RESOLVER_IFACE, "Failure", self._resolve_failed)):
            self.bus.add_signal_receiver(fn, dbus_interface=iface,
                                         signal_name=member, path_keyword="path")
        self.hooked = True

    def browse(self, stype):
        if stype in self.browsers:
            # A fresh browse is what Bonjour does on every OBC_StartScan, and it
            # reports every service again; so does this.
            for key, ev in list(self.resolved.items()):
                if key[0] == stype:
                    self.emit(ev)
            return
        self._hook()
        try:
            path = self.server().ServiceBrowserNew(dbus.Int32(AVAHI_IF_UNSPEC),
                                                   dbus.Int32(AVAHI_PROTO_INET),
                                                   stype, "local", dbus.UInt32(0))
        except dbus.DBusException as e:
            raise BleError("avahi-daemon is not reachable (%s) -- is it running?"
                           % e.get_dbus_name())
        self.browsers[stype] = str(path)
        log("browsing %s", stype)

    def _free(self, path, iface):
        try:
            dbus.Interface(self.bus.get_object(AVAHI, path), iface).Free()
        except dbus.DBusException as e:
            trace("%s.Free: %s", iface.rsplit(".", 1)[-1], e.get_dbus_name())

    def stop(self, stype):
        path = self.browsers.pop(stype, None)
        for rpath, (key, _) in list(self.resolvers.items()):
            if key[0] == stype:
                del self.resolvers[rpath]
                self._free(rpath, AVAHI_RESOLVER_IFACE)
        for key in [k for k in self.seen if k[0] == stype]:
            del self.seen[key]
        for key in [k for k in self.resolved if k[0] == stype]:
            del self.resolved[key]
        if path is None:
            return
        self._free(path, AVAHI_BROWSER_IFACE)
        log("stopped browsing %s", stype)

    def stop_all(self):
        for stype in list(self.browsers):
            self.stop(stype)

    def _type_of(self, path):
        for stype, p in self.browsers.items():
            if p == path:
                return stype
        return None

    def _item_new(self, interface, protocol, name, stype, domain, flags, path=None):
        mine = self._type_of(path)
        if mine is None:
            return
        key = (mine, str(name))
        self.seen.setdefault(key, set()).add(int(interface))
        if any(v == (key, int(interface)) for v in self.resolvers.values()):
            return
        trace("mdns: %s on interface %d, resolving", name, interface)
        try:
            rpath = self.server().ServiceResolverNew(
                interface, protocol, name, stype, domain,
                dbus.Int32(AVAHI_PROTO_INET), dbus.UInt32(0))
        except dbus.DBusException as e:
            log("mdns: cannot resolve %s (%s)", name, e.get_dbus_message())
            return
        self.resolvers[str(rpath)] = (key, int(interface))

    def _found(self, iface, protocol, name, stype, domain, host, aprotocol,
               address, port, txt, flags, path=None):
        entry = self.resolvers.get(path)
        if entry is None:
            return
        key = entry[0]
        try:
            ifname = socket.if_indextoname(int(iface))
        except OSError:
            ifname = str(int(iface))
        fields = {}
        for item in txt:
            k, _, v = bytes(bytearray(item)).decode("utf-8", "replace").partition("=")
            fields[k] = v
        ev = {"ev": "mdns", "type": key[0], "name": str(name), "host": str(host),
              "ip": str(address), "port": int(port), "iface": ifname, "txt": fields}
        before = self.resolved.get(key)
        if before and (before["ip"], before["port"], before["host"]) == (ev["ip"], ev["port"], ev["host"]):
            self.resolved[key] = ev         # a TXT refresh, or another interface: no news
            return
        self.resolved[key] = ev
        if before:
            log("mdns: %s moved from %s:%d to %s:%d (%s, %s)", name, before["ip"], before["port"],
                address, port, host, ifname)
        else:
            log("mdns: %s at %s:%d (%s, %s)", name, address, port, host, ifname)
        self.emit(ev)

    def _resolve_failed(self, error, path=None):
        entry = self.resolvers.get(path)
        if entry is not None:
            # Not final: the resolver keeps watching, and answers if the
            # records come back.
            log("mdns: cannot resolve %s yet (%s)", entry[0][1], error)

    def _item_remove(self, interface, protocol, name, stype, domain, flags, path=None):
        mine = self._type_of(path)
        if mine is None:
            return
        key = (mine, str(name))
        for rpath, v in list(self.resolvers.items()):
            if v == (key, int(interface)):
                del self.resolvers[rpath]
                self._free(rpath, AVAHI_RESOLVER_IFACE)
        ifaces = self.seen.get(key)
        if ifaces is None:
            return
        ifaces.discard(int(interface))
        if ifaces:
            return
        del self.seen[key]
        if self.resolved.pop(key, None) is not None:
            log("mdns: %s gone", name)
            self.emit({"ev": "mdns_lost", "type": key[0], "name": str(name)})

    def _failure(self, error, path=None):
        if self._type_of(path) is not None:
            log("mdns browse failed: %s", error)


class Server:
    """One TCP client -- the game -- and the BlueZ state it asked for."""

    def __init__(self, bluez, port, bus):
        self.bluez = bluez
        self.avahi = Avahi(bus, self.send)
        self.port = port
        self.client = None
        self.buffer = b""
        self.subscribed = set()             # (addr, char uuid) the client wants
        self.silent = set()                 # devices reported as having no service UUIDs
        self.char_addr = {}                 # char object path -> (addr, uuid)
        self.connected = {}                 # addr -> last reported Connected

    # ------------------------------------------------------------ transport

    def listen(self):
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            s.bind(("127.0.0.1", self.port))
        except OSError as e:
            if e.errno == errno.EADDRINUSE:
                raise SystemExit("port %d is already in use -- another blehelper?" % self.port)
            raise
        s.listen(1)
        s.setblocking(False)
        GLib.io_add_watch(s, GLib.IO_IN, self.on_accept)
        log("listening on 127.0.0.1:%d", self.port)

    def on_accept(self, sock, condition):
        try:
            conn, _ = sock.accept()
        except OSError:
            return True
        if self.client is not None:
            # The shim opens one connection and keeps it; a second means the
            # game was restarted and the old socket is a corpse.
            log("replacing the previous client")
            self.drop_client()
        conn.setblocking(False)
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.client = conn
        self.buffer = b""
        GLib.io_add_watch(conn, GLib.IO_IN | GLib.IO_HUP | GLib.IO_ERR, self.on_data)
        log("shim connected")
        return True

    def drop_client(self):
        """Let go of everything the client had asked for.

        The game going away must not leave a trainer subscribed and a scan
        running: the next process to use the adapter would inherit both.
        """
        if self.client is not None:
            try:
                self.client.close()
            except OSError:
                pass
            self.client = None
        for addr, uuid in list(self.subscribed):
            try:
                self.bluez.notify(addr, uuid, False)
            except (BleError, dbus.DBusException):
                pass
        self.subscribed.clear()
        self.char_addr.clear()
        self.silent.clear()
        self.bluez.wanted = False
        self.bluez.stop_scan()
        self.avahi.stop_all()

    def on_data(self, conn, condition):
        if condition & (GLib.IO_HUP | GLib.IO_ERR):
            log("shim disconnected")
            self.drop_client()
            return False
        try:
            chunk = conn.recv(65536)
        except OSError:
            chunk = b""
        if not chunk:
            log("shim disconnected")
            self.drop_client()
            return False

        self.buffer += chunk
        while b"\n" in self.buffer:
            line, self.buffer = self.buffer.split(b"\n", 1)
            line = line.strip()
            if line:
                self.handle(line)
        return True

    def send(self, obj):
        if self.client is None:
            return
        try:
            self.client.sendall((json.dumps(obj) + "\n").encode("utf-8"))
        except OSError as e:
            log("send failed (%s); dropping the client", e)
            self.drop_client()

    # ------------------------------------------------------------- requests

    def handle(self, line):
        try:
            req = json.loads(line.decode("utf-8"))
        except (ValueError, UnicodeDecodeError) as e:
            log("unparsable request (%s): %r", e, line[:80])
            return

        rid = req.get("id")
        op = req.get("op")
        try:
            reply = self.dispatch(op, req)
        except BleError as e:
            trace("%s: %s", op, e)
            self.send({"id": rid, "ok": False, "error": str(e)})
            return
        except dbus.DBusException as e:
            log("%s: %s", op, e.get_dbus_message())
            self.send({"id": rid, "ok": False, "error": e.get_dbus_message()})
            return
        if reply is DEFERRED:
            return
        reply = dict(reply or {})
        reply.update({"id": rid, "ok": True})
        self.send(reply)

    def dispatch(self, op, req):
        addr = (req.get("addr") or "").upper()
        char = (req.get("char") or "").lower()

        if op == "radios":
            return {"radios": self.bluez.radios()}

        if op == "scan":
            self.bluez.wanted = bool(req.get("enable"))
            if self.bluez.wanted:
                self.bluez.start_scan()
                self.announce_known()
            elif self.bluez.waiters == 0:
                self.bluez.stop_scan()      # else the last waiting connect stops it
            return {}

        if op == "connect":
            # Answered later, by the connect itself; meanwhile the helper goes
            # on serving the client's other requests.
            client, rid = self.client, req.get("id")

            def done(name, error=None):
                if self.client is not client:
                    return                  # that client is gone
                if error:
                    log("connect: %s", error)
                    self.send({"id": rid, "ok": False, "error": error})
                    return
                self.connected[addr] = True
                self.send({"id": rid, "ok": True, "name": name})

            self.bluez.connect(addr, done)
            return DEFERRED

        if op == "disconnect":
            for key in [k for k in self.subscribed if k[0] == addr]:
                self.subscribed.discard(key)
            self.bluez.disconnect(addr)
            return {}

        if op == "services":
            return {"services": self.bluez.services(addr)}

        if op == "chars":
            chars = self.bluez.characteristics(addr, (req.get("service") or "").lower())
            for c in chars:
                try:
                    self.char_addr[self.bluez.char_path(addr, c["uuid"])] = (addr, c["uuid"])
                except BleError:
                    pass
            return {"chars": chars}

        if op == "read":
            return {"value": self.bluez.read(addr, char).hex()}

        if op == "write":
            value = bytes.fromhex(req.get("value") or "")
            self.bluez.write(addr, char, value, bool(req.get("response", True)))
            return {}

        if op == "notify":
            enable = bool(req.get("enable"))
            self.bluez.notify(addr, char, enable)
            self.char_addr[self.bluez.char_path(addr, char)] = (addr, char)
            if enable:
                self.subscribed.add((addr, char))
            else:
                self.subscribed.discard((addr, char))
            return {}

        if op == "mdns_browse":
            stype = (req.get("type") or OBC_SERVICE).rstrip(".")
            if req.get("enable"):
                self.avahi.browse(stype)
            else:
                self.avahi.stop(stype)
            return {}

        if op == "tcp_peer":
            try:
                return {"connected": tcp_established(req.get("ip") or "", int(req.get("port") or 0))}
            except (OSError, ValueError) as e:
                raise BleError("tcp_peer: %s" % e)

        raise BleError("unknown op %r" % op)

    # --------------------------------------------------------------- events

    def announce_known(self):
        """Report the devices already on the air when a scan starts.

        BlueZ only signals what changes, so a device that advertised a second
        before the game opened its pairing screen would otherwise stay invisible
        until it happened to move.
        """
        sent = 0
        for path, ifaces in self.bluez.objects().items():
            d = ifaces.get(DEVICE_IFACE)
            if not d or not path.startswith(self.bluez.adapter_path + "/"):
                continue
            if "RSSI" not in d and not d.get("Connected"):
                continue
            self.advert(d)
            sent += 1
        trace("announced %d device(s) already on the air", sent)

    def advert(self, props):
        addr = str(props.get("Address", "")).upper()
        if not addr:
            return
        # The game drops any advertisement with no service UUIDs
        # (BluetoothProgram::Advertisement_Received), so this is where a sensor
        # silently fails to appear -- worth a line when tracing.
        uuids = [str(u).lower() for u in props.get("UUIDs", [])]
        if not uuids and addr not in self.silent:
            self.silent.add(addr)
            trace("%s advertises no service UUIDs; the game will ignore it", addr)
        self.send({"ev": "advert",
                   "addr": addr,
                   "name": str(props.get("Alias", "") or ""),
                   "uuids": uuids,
                   "rssi": int(props.get("RSSI", 0))})

    def on_properties_changed(self, iface, changed, invalidated, path=None):
        if iface == CHAR_IFACE and "Value" in changed:
            known = self.char_addr.get(path)
            if not known:
                return
            addr, uuid = known
            if (addr, uuid) not in self.subscribed:
                return
            self.send({"ev": "value", "addr": addr, "char": uuid,
                       "value": bytes(bytearray(changed["Value"])).hex()})
            return

        if iface != DEVICE_IFACE:
            return
        props = self.bluez.objects().get(path, {}).get(DEVICE_IFACE, {})
        addr = str(props.get("Address", "")).upper()
        if not addr:
            return
        if "Connected" in changed:
            now = bool(changed["Connected"])
            if self.connected.get(addr) != now:
                self.connected[addr] = now
                self.send({"ev": "connection", "addr": addr, "connected": now})
        if self.bluez.scanning and ("RSSI" in changed or "UUIDs" in changed or "Alias" in changed):
            self.advert(props)

    def on_interfaces_added(self, path, ifaces):
        d = ifaces.get(DEVICE_IFACE)
        if d and self.bluez.scanning:
            self.advert(d)

    def on_interfaces_removed(self, path, ifaces):
        # BlueZ removes a non-bonded device's object when it stops advertising,
        # which is what a sleeping trainer looks like.  The game hears about it
        # as a disconnection, and reconnecting is its decision, not ours.
        if DEVICE_IFACE not in ifaces:
            return
        for addr, was in list(self.connected.items()):
            if self.bluez.device_path(addr) == path and was:
                self.connected[addr] = False
                self.send({"ev": "connection", "addr": addr, "connected": False})


def list_devices(bluez):
    """Print what is on the air, so the Linux half can be checked on its own."""
    bluez.start_scan()
    loop = GLib.MainLoop()
    GLib.timeout_add_seconds(8, lambda: (loop.quit(), False)[1])
    loop.run()
    bluez.stop_scan()

    rows = []
    for path, ifaces in sorted(bluez.objects().items()):
        d = ifaces.get(DEVICE_IFACE)
        if not d or not path.startswith(bluez.adapter_path + "/"):
            continue
        if "RSSI" not in d:
            continue
        rows.append((str(d.get("Address", "?")),
                     str(d.get("Alias", "") or ""),
                     int(d.get("RSSI", 0)),
                     [str(u).lower() for u in d.get("UUIDs", [])]))
    if not rows:
        print("nothing advertising -- wake the sensor up (pedal, or touch the strap)")
        return
    for addr, name, rssi, uuids in sorted(rows, key=lambda r: -r[2]):
        print("%s  %-28s %4d dBm  %s" % (addr, name or "(no name)", rssi,
                                         " ".join(u[4:8] for u in uuids) or "no service UUIDs"))


def tcp_established(ip, port):
    """Does anything on this machine hold an established TCP connection to
    ip:port?  The game's engine connects to an OpenBikeControl device itself,
    over Winsock -- which under Wine is a plain Linux socket, so it is in
    /proc/net/tcp like any other.  How the shim knows the game took an offer."""
    v4 = "%08X:%04X" % (struct.unpack("<I", socket.inet_aton(ip))[0], port)
    want = {"/proc/net/tcp": v4,
            "/proc/net/tcp6": "0000000000000000FFFF0000" + v4}   # v4-mapped
    for path, remote in want.items():
        try:
            with open(path) as f:
                next(f, None)
                for line in f:
                    parts = line.split()
                    if len(parts) > 3 and parts[2] == remote and parts[3] == "01":
                        return True
        except OSError:
            pass
    return False


def list_mdns(bus, stype):
    """Print the services avahi finds, so the network half can be checked
    on its own -- the same browse the game's OBC_StartScan triggers."""
    found = []
    avahi = Avahi(bus, found.append)
    try:
        avahi.browse(stype)
    except BleError as e:
        raise SystemExit(str(e))
    loop = GLib.MainLoop()
    GLib.timeout_add_seconds(5, lambda: (loop.quit(), False)[1])
    loop.run()
    avahi.stop_all()
    found = [ev for ev in found if ev["ev"] == "mdns"]
    if not found:
        print("no %s service found -- is the app running, on the same network,"
              " with its network (mDNS) connection enabled?" % stype)
        return
    for ev in found:
        print("%-24s %s:%d  host %s  on %s  %s" % (
            ev["name"], ev["ip"], ev["port"], ev["host"], ev["iface"],
            " ".join("%s=%s" % kv for kv in sorted(ev["txt"].items()))))


def main():
    global verbose
    ap = argparse.ArgumentParser(description=__doc__.strip().splitlines()[0])
    ap.add_argument("--port", type=int, default=27019, help="loopback port to serve on")
    ap.add_argument("--adapter", default="hci0", help="BlueZ adapter (default hci0)")
    ap.add_argument("--list", action="store_true", help="scan, print what is on the air, exit")
    ap.add_argument("--mdns", nargs="?", const=OBC_SERVICE, metavar="TYPE",
                    help="browse mDNS (default %s), print what answers, exit" % OBC_SERVICE)
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()
    verbose = args.verbose

    dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
    bus = dbus.SystemBus()
    if args.mdns:
        list_mdns(bus, args.mdns.rstrip("."))
        return
    bluez = Bluez(bus, args.adapter)
    try:
        bluez.objects()
    except dbus.DBusException as e:
        raise SystemExit("cannot talk to BlueZ (%s) -- is bluetoothd running?" % e.get_dbus_name())
    if bluez.adapter_path not in bluez.objects():
        raise SystemExit("no adapter at %s -- try --adapter" % bluez.adapter_path)

    if args.list:
        list_devices(bluez)
        return

    server = Server(bluez, args.port, bus)
    bus.add_signal_receiver(server.on_properties_changed,
                            dbus_interface=PROPS_IFACE,
                            signal_name="PropertiesChanged",
                            path_keyword="path")
    bus.add_signal_receiver(server.on_interfaces_added,
                            dbus_interface=OM_IFACE,
                            signal_name="InterfacesAdded")
    bus.add_signal_receiver(server.on_interfaces_removed,
                            dbus_interface=OM_IFACE,
                            signal_name="InterfacesRemoved")
    server.listen()

    try:
        GLib.MainLoop().run()
    except KeyboardInterrupt:
        pass
    finally:
        server.drop_client()
        log("stopped")


if __name__ == "__main__":
    main()
