// MyWhoosh's OpenBikeControl discovery, without Bonjour.
//
// OpenBikeControl is how BikeControl (and a KICKR BIKE PRO, and others) send
// virtual shifts to the game over Wi-Fi.  Read out of the game's IL, the part
// of it in WindowsConnectivity.dll is discovery and nothing else:
//
//   OBC_Initialize(cb)   new OpenBikeManager; OBM_Initialize; store cb in
//                        DelegateCallbacks.OpenBikeDataCallback
//   OBC_StartScan        OBM_StartScan: Bonjour Browse("_openbikecontrol._tcp.")
//   ServiceFound         Resolve(...)
//   ServiceResolved      Dns.GetHostAddressesAsync(hostname), first IPv4 one,
//                        FOpenBikeDataStruct{ipAddress, hostName, port},
//                        OpenBikeDataCallback.Invoke(struct)
//   ServiceLost          nothing
//
// The TCP connection and the OpenBikeControl protocol itself live in the
// game's engine (UOpenBikeController::ConnectToOpenBike, OnOpenBikeMessage-
// Received, ...), over Winsock, which works under Wine as it is.  So the one
// thing missing is that callback -- every step before it is Bonjour COM, which
// the Bonjour gate keeps the game out of (../CLAUDE.md), and with good reason:
// wine-mono cannot do ComAwareEventInfo.
//
// So: ../../exportshim points OBC_StartScan and OBC_StopScan here (Loader.cs
// wires it), blehelper.py browses with avahi-daemon on the Linux side, and
// each service it resolves is handed to the game through the game's own
// delegate, filled exactly as ServiceResolved fills it.  OBC_Initialize is
// left alone: with the gate shut it does nothing but store that delegate,
// which is what we need it to do.
//
// One departure from the original, on purpose: the engine's "connect?" popup
// times out within seconds, and the first offer arrives while the game is
// still starting and stacking popups of its own -- so it is easy to miss, and
// on Windows nothing would ever ask again.  While the game has no TCP
// connection to a service, it is offered again every MYWHOOSH_OBC_REOFFER
// seconds (default 30; 0 = never), at most MaxReoffers times in a row.  The
// helper reads the connection out of /proc/net/tcp, which sees the engine's
// Winsock socket as the Linux socket it is.
//
// The callback goes out on a thread of our own, not the backend's dispatcher:
// the original runs on Bonjour's event thread, never the game's, and what the
// engine does inside it is its business -- it must not be able to hold up the
// trainer's notifications behind it.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

namespace MyWhoosh.Ble
{
    static class OpenBike
    {
        const string GameAssembly = "WindowsConnectivity";
        const string StructType = "ConnectivityConstants.FOpenBikeDataStruct";
        const string CallbacksType = "ConnectivityConstants.DelegateCallbacks";
        const string CallbackField = "OpenBikeDataCallback";
        public const string ServiceType = "_openbikecontrol._tcp";

        const int MaxReoffers = 20;

        static readonly object Gate = new object();
        static bool scanning;
        static bool rebrowse;               // a repeated OBC_StartScan wants every service again
        static DateTime lastStart;
        static int browsedOn = -1;          // Backend.Generation the browse lives on, -1 = none
        static bool subscribed;
        static Thread worker;

        sealed class Offer
        {
            public BackendEvent Ev;
            public string Where;            // "ip:port" it was offered at
            public DateTime Last;           // last offered, or last seen connected
            public int Count;               // re-offers since it was last connected
            public bool Connected;          // the game had a TCP connection to it at the last look
        }

        // Services offered to the game since the current scan began, by name,
        // so it hears of each once -- as with Bonjour, which reports a service
        // again only after losing it, or on a fresh Browse -- plus re-offers.
        static readonly Dictionary<string, Offer> Offers = new Dictionary<string, Offer>();
        static readonly List<BackendEvent> Outbox = new List<BackendEvent>();
        // Every service the helper has reported and not since lost, scanning
        // or not: what a tap on the OpenBikeControl icon is answered from, at
        // once -- the helper may be busy, and the popup has to come now.
        static readonly Dictionary<string, BackendEvent> Known = new Dictionary<string, BackendEvent>();
        static bool saidNoCallback;
        static bool dirty;                  // something for the worker since it last looked
        static readonly int ReofferSeconds = ReadReoffer();

        static int ReadReoffer()
        {
            int s;
            string env = Environment.GetEnvironmentVariable("MYWHOOSH_OBC_REOFFER");
            return !string.IsNullOrEmpty(env) && int.TryParse(env, out s) && s >= 0 ? s : 30;
        }

        /// The game's OBC_StartScan.  Runs on the game's thread, through a
        /// native-to-managed thunk: quick, and never throws.
        public static void StartScan()
        {
            try
            {
                lock (Gate)
                {
                    if (scanning)
                    {
                        // OBM_StartScan Browses afresh on every call, and a new
                        // browse reports every service again -- which is how the
                        // OpenBikeControl icon on the connection screen brings
                        // the popup back.  Debounced, in case anything calls it
                        // per frame.
                        if (DateTime.UtcNow - lastStart < TimeSpan.FromSeconds(2)) return;
                        lastStart = DateTime.UtcNow;
                        Backend.Log("obc: game called OBC_StartScan again -- offering every service again");
                        OfferKnown();
                        rebrowse = true;
                        Wake();
                        return;
                    }
                    Backend.Log("obc: game called OBC_StartScan -- browsing " + ServiceType);
                    scanning = true;
                    lastStart = DateTime.UtcNow;
                    OfferKnown();
                    Wake();
                }
            }
            catch (Exception e) { Backend.Log("obc: StartScan: " + e); }
        }

        /// The game's OBC_StopScan.  Same rules.
        public static void StopScan()
        {
            try
            {
                lock (Gate)
                {
                    if (!scanning) return;
                    Backend.Log("obc: game called OBC_StopScan");
                    scanning = false;
                    Outbox.Clear();
                    Offers.Clear();
                    Wake();
                }
            }
            catch (Exception e) { Backend.Log("obc: StopScan: " + e); }
        }

        static int availabilityAsked;

        /// The game's WD_GetDirconServiceAvailability: "is Apple Bonjour
        /// installed?" (WahooProgram::GetBonjourService -- a service of that
        /// name, in any state).  The connection screen asks it when the
        /// OpenBikeControl icon is tapped, and on "no" offers to install
        /// Bonjour instead of scanning.  Bonjour's job is done here without
        /// Bonjour, so: yes.  Nothing managed trusts this answer -- every
        /// Bonjour COM path checks isBonjourEnabled, read once at startup --
        /// so it cannot lead the game into COM.
        public static bool ServiceAvailable()
        {
            if (Interlocked.Increment(ref availabilityAsked) == 1)
                Backend.Log("obc: game asked WD_GetDirconServiceAvailability -- answering yes,"
                            + " discovery is served without Bonjour");
            return true;
        }

        /// The game's WD_InstallDirconServiceAsync: turn the Windows firewall
        /// off (`netsh advfirewall set allprofile state off`) and run the
        /// bundled bonjoursdksetup.exe.  A Bonjour service in the prefix opens
        /// the Bonjour gate and crashes the game at startup, so: never.
        public static void RefuseInstall(string path)
        {
            Backend.Log("obc: game asked to install Bonjour (" + (path ?? "") + "bonjoursdksetup.exe)"
                        + " -- refused: under Wine it opens the Bonjour gate and the game crashes at"
                        + " startup; OpenBikeControl is served without it");
        }

        /// Called once the two exports point here.  If the game had already
        /// called OBC_Initialize by then, it may also have called OBC_StartScan
        /// into the original no-op, and there would be no second call to wait
        /// for -- so browse as if it had, until the game says to stop.
        public static void Hooked()
        {
            try
            {
                if (Callback() == null)
                {
                    Backend.Log("obc: hooked before OBC_Initialize; waiting for the game's OBC_StartScan");
                    return;
                }
                Backend.Log("obc: hooked after OBC_Initialize, so an OBC_StartScan may have gone"
                            + " to the original; browsing until the game's OBC_StopScan");
                StartScan();
            }
            catch (Exception e) { Backend.Log("obc: Hooked: " + e); }
        }

        /// A fresh browse: forget what was offered, and offer everything known
        /// now.  The browse that follows confirms it, or reports what moved.
        /// Caller holds Gate.
        static void OfferKnown()
        {
            Offers.Clear();
            Outbox.Clear();
            foreach (BackendEvent ev in Known.Values)
            {
                Offers[ev.Name] = new Offer { Ev = ev, Where = ev.Ip + ":" + ev.Port, Last = DateTime.UtcNow };
                Outbox.Add(ev);
            }
        }

        /// Start the worker if need be and let it look at the state.
        /// Caller holds Gate.
        static void Wake()
        {
            if (!subscribed)
            {
                subscribed = true;
                Backend.Received += OnBackendEvent;
            }
            if (worker == null)
            {
                worker = new Thread(Work) { IsBackground = true, Name = "bleshim-obc" };
                worker.Start();
            }
            dirty = true;
            Monitor.PulseAll(Gate);
        }

        // ------------------------------------------------------------ events

        static void OnBackendEvent(BackendEvent ev)
        {
            if (ev.ServiceType != ServiceType) return;
            lock (Gate)
            {
                if (ev.Kind == "mdns_lost")
                {
                    Known.Remove(ev.Name);
                    if (Offers.Remove(ev.Name))
                        Backend.Log("obc: " + ev.Name + " is gone (the game is not told: ServiceLost is empty)");
                    return;
                }
                if (ev.Kind != "mdns") return;
                Known[ev.Name] = ev;
                if (!scanning) return;

                // The same service at a new address or port -- BikeControl moves
                // to the next port when its own is taken -- is news; the same
                // one again is not.
                string where = ev.Ip + ":" + ev.Port;
                Offer had;
                if (Offers.TryGetValue(ev.Name, out had) && had.Where == where) return;
                Offers[ev.Name] = new Offer { Ev = ev, Where = where, Last = DateTime.UtcNow };
                Outbox.Add(ev);
                dirty = true;
                Monitor.PulseAll(Gate);
            }
        }

        // ------------------------------------------------------------ worker

        static void Work()
        {
            while (true)
            {
                try { Step(); }
                catch (Exception e) { Backend.Log("obc: " + e); }
            }
        }

        static void Step()
        {
            bool want, again;
            int have;
            List<BackendEvent> due = null;
            lock (Gate)
            {
                // Every 5 s at the latest: a helper started after the game, or
                // restarted under it, needs the browse asked for again.
                if (!dirty) Monitor.Wait(Gate, 5000);
                dirty = false;
                want = scanning;
                again = rebrowse;
                rebrowse = false;
                have = browsedOn;
                if (Outbox.Count > 0 && Callback() != null)
                {
                    due = new List<BackendEvent>(Outbox);
                    Outbox.Clear();
                }
                else if (Outbox.Count > 0 && !saidNoCallback)
                {
                    saidNoCallback = true;
                    Backend.Log("obc: found a service but the game has not called OBC_Initialize yet; holding it");
                }
            }

            // Deliveries first: the helper may be slow to answer, the popup may not.
            if (due != null)
                foreach (BackendEvent ev in due) Deliver(ev);

            if (want && (again || have < 0 || have != Backend.Generation))
            {
                if (Backend.Call("op", "mdns_browse", "type", ServiceType, "enable", true) != null)
                {
                    lock (Gate) browsedOn = Backend.Generation;
                    Backend.Log("obc: helper is browsing " + ServiceType);
                }
            }
            else if (!want && have >= 0)
            {
                if (have == Backend.Generation)
                    Backend.Call("op", "mdns_browse", "type", ServiceType, "enable", false);
                lock (Gate) browsedOn = -1;
            }

            if (want && ReofferSeconds > 0) Reoffer();
        }

        /// Offer again what the game has not connected to, now and then.
        static void Reoffer()
        {
            List<Offer> stale = new List<Offer>();
            lock (Gate)
            {
                foreach (Offer o in Offers.Values)
                    if (DateTime.UtcNow - o.Last >= TimeSpan.FromSeconds(ReofferSeconds)) stale.Add(o);
            }
            foreach (Offer o in stale)
            {
                var reply = Backend.Call("op", "tcp_peer", "ip", o.Ev.Ip, "port", o.Ev.Port);
                if (reply == null) continue;            // an older helper: no re-offers
                bool up = Json.Bool(reply, "connected");
                lock (Gate)
                {
                    if (!scanning || !Offers.ContainsKey(o.Ev.Name) || Offers[o.Ev.Name] != o) continue;
                    o.Last = DateTime.UtcNow;
                    if (up)
                    {
                        if (!o.Connected) Backend.Log("obc: the game is connected to " + o.Ev.Name + " at " + o.Where);
                        o.Connected = true;
                        o.Count = 0;
                        continue;
                    }
                    if (o.Connected) Backend.Log("obc: the game's connection to " + o.Ev.Name + " is gone");
                    o.Connected = false;
                    if (o.Count >= MaxReoffers)
                    {
                        if (o.Count++ == MaxReoffers)
                            Backend.Log("obc: " + o.Ev.Name + " offered " + MaxReoffers + " more times without a"
                                        + " connection; stopping (the OpenBikeControl icon on the connection"
                                        + " screen offers it again)");
                        continue;
                    }
                    o.Count++;
                    Backend.Log("obc: re-offering " + o.Ev.Name + " (" + o.Count + "/" + MaxReoffers
                                + ") -- the game has not connected to it");
                }
                Deliver(o.Ev);
            }
        }

        // ---------------------------------------------------------- delivery

        static Type gameStruct;
        static FieldInfo gameCallback;

        static bool FindGame()
        {
            if (gameStruct != null && gameCallback != null) return true;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != GameAssembly) continue;
                Type s = a.GetType(StructType, false);
                Type c = a.GetType(CallbacksType, false);
                FieldInfo f = c == null ? null
                    : c.GetField(CallbackField, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (s == null || f == null) continue;
                gameStruct = s;
                gameCallback = f;
                return true;
            }
            return false;
        }

        /// The delegate the game passed to OBC_Initialize, or null.
        static Delegate Callback()
        {
            if (!FindGame()) return null;
            return gameCallback.GetValue(null) as Delegate;
        }

        /// What OpenBikeManager::ServiceResolved does once it has an address.
        static void Deliver(BackendEvent ev)
        {
            try
            {
                // ServiceResolved keeps the first InterNetwork address whose
                // text is at least five characters long, and drops the service
                // otherwise.
                IPAddress ip;
                if (!IPAddress.TryParse(ev.Ip, out ip) || ip.AddressFamily != AddressFamily.InterNetwork
                    || ip.ToString().Length < 5)
                {
                    Backend.Log("obc: " + ev.Name + " has no usable IPv4 address (" + ev.Ip + "); skipped");
                    return;
                }

                Delegate cb = Callback();
                if (cb == null) { Backend.Log("obc: the game's callback went away; " + ev.Name + " dropped"); return; }

                // Bonjour hands ServiceResolved the target host as a fully
                // qualified name, trailing dot and all; avahi leaves it off.
                string host = ev.Host ?? "";
                if (host.Length > 0 && !host.EndsWith(".")) host += ".";

                // Boxed, so Initialize and the field stores act on one copy.
                object data = Activator.CreateInstance(gameStruct);
                MethodInfo init = gameStruct.GetMethod("Initialize", BindingFlags.Public | BindingFlags.NonPublic
                                                                      | BindingFlags.Instance);
                if (init != null) init.Invoke(data, null);
                gameStruct.GetField("ipAddress").SetValue(data, ip.ToString());
                gameStruct.GetField("hostName").SetValue(data, host);
                gameStruct.GetField("port").SetValue(data, ev.Port);

                Backend.Log("obc: handing " + ev.Name + " to the game: " + ip + ":" + ev.Port + " (" + host + ")");
                // The game's own delegate, so mono marshals it through the same
                // wrapper OpenBikeDataCallback.Invoke would.
                cb.DynamicInvoke(data);
                Backend.Log("obc: the game took " + ev.Name);
            }
            catch (TargetInvocationException e)
            {
                Backend.Log("obc: the game's callback threw: " + (e.InnerException ?? e));
            }
            catch (Exception e)
            {
                Backend.Log("obc: delivering " + ev.Name + " failed: " + e);
            }
        }
    }
}
