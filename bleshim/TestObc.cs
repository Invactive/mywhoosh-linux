// Drive the OpenBikeControl path the way the game does, without the game.
//
// Three stages, each one a thing the game depends on:
//
//   1. discovery -- OpenBike.cs asks ./blehelper.py to browse
//      _openbikecontrol._tcp, exactly as the hooked OBC_StartScan does;
//   2. delivery -- each service found is handed to the game's own
//      OpenBikeDataDelegate type, wrapping a *native* function pointer as the
//      game's is, so mono marshals FOpenBikeDataStruct out to native memory.
//      The receiving end reads that memory raw, at the offsets the engine
//      would (LPWStr @0, int @8, LPWStr @16), so a layout mistake shows here
//      and not as a popup that never appears;
//   3. a TCP session -- from inside Wine to the address that was delivered,
//      decoding what the device sends with the OpenBikeControl message format
//      (button state 0x01, device status 0x02).  This is the game engine's job
//      in a real run; doing it here proves the network path it will take.
//
// Built from the shim's own sources rather than against Windows.dll, because
// OpenBike and Backend are internal.  Run it under wine-mono in the prefix --
// stage 2 is about wine-mono's marshaller and means nothing on the host -- with
// MyWhoosh closed: BikeControl serves one client at a time.
//
//   ./blehelper.py --port 27020 &
//   cp <game>/WindowsConnectivity.dll build/       # a copy; never touch the game's
//   mcs -platform:x64 -out:build/TestObc.exe TestObc.cs src/OpenBike.cs src/Backend.cs src/Json.cs
//   MYWHOOSH_BLE_PORT=27020 wine build/TestObc.exe [--seconds 60] [--appinfo] [--no-tcp]
//   MYWHOOSH_BLE_PORT=27020 MYWHOOSH_OBC_REOFFER=5 wine build/TestObc.exe --watch 40
//
// --watch N keeps the scan open for N seconds instead, to exercise re-offers:
// no connection for the first half, so the service should be offered again
// every MYWHOOSH_OBC_REOFFER seconds; then a connection held for the second
// half, as the game's would be, so the offers should stop.
//
// --appinfo sends the optional App Information message (0x04) after
// connecting.  It is off by default because BikeControl remembers the button
// list an app sends against whichever trainer app is selected in it -- a test
// run would overwrite what MyWhoosh sent.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MyWhoosh.Ble;

static class TestObc
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate void Receiver(IntPtr data);

    [DllImport("kernel32", SetLastError = true)]
    static extern IntPtr VirtualAlloc(IntPtr addr, IntPtr size, uint type, uint prot);

    static readonly object Gate = new object();
    static readonly List<string[]> Delivered = new List<string[]>();   // ip, port, host
    static Receiver receiver;                                          // rooted

    static int Main(string[] args)
    {
        int seconds = 60;
        bool appInfo = false, tcp = true;
        int watch = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--seconds" && i + 1 < args.Length) seconds = int.Parse(args[++i]);
            else if (args[i] == "--appinfo") appInfo = true;
            else if (args[i] == "--no-tcp") tcp = false;
            else if (args[i] == "--watch" && i + 1 < args.Length) watch = int.Parse(args[++i]);
            else { Console.WriteLine("usage: TestObc.exe [--seconds N] [--appinfo] [--no-tcp] [--watch N]"); return 2; }
        }

        Console.WriteLine("== runtime " + Environment.Version + ", " + (IntPtr.Size * 8) + "-bit");
        if (!InstallCallback()) return 1;

        Console.WriteLine("== discovery: OBC_StartScan, as the hooked export calls it");
        OpenBike.StartScan();
        string[] first = null;
        DateTime until = DateTime.UtcNow.AddSeconds(15);
        lock (Gate)
        {
            while (Delivered.Count == 0 && DateTime.UtcNow < until) Monitor.Wait(Gate, 500);
            if (Delivered.Count > 0) first = Delivered[0];
        }
        if (first == null)
        {
            Console.WriteLine("   nothing delivered in 15 s -- is blehelper.py running (MYWHOOSH_BLE_PORT),"
                              + " and does `./blehelper.py --mdns` see the app?");
            OpenBike.StopScan();
            Thread.Sleep(500);
            return 1;
        }
        if (watch > 0) return Watch(first[0], int.Parse(first[1]), watch);
        Thread.Sleep(1000);         // let any second service arrive before stopping
        OpenBike.StopScan();
        Thread.Sleep(500);          // the worker sends the stop

        if (!tcp) return 0;
        return Session(first[0], int.Parse(first[1]), seconds, appInfo) ? 0 : 1;
    }

    // -------------------------------------------------------------- delivery

    /// Put a delegate of the game's own type, around a native function
    /// pointer, where OBC_Initialize would put the game's.
    static bool InstallCallback()
    {
        string here = AppDomain.CurrentDomain.BaseDirectory;
        string dll = Path.Combine(here, "WindowsConnectivity.dll");
        if (!File.Exists(dll))
        {
            Console.WriteLine("   no " + dll + " -- copy the game's WindowsConnectivity.dll next to this exe");
            return false;
        }
        Assembly game = Assembly.LoadFrom(dll);
        Type dtype = game.GetType("ConnectivityConstants.DelegateCallbacks+OpenBikeDataDelegate", true);
        Type stype = game.GetType("ConnectivityConstants.FOpenBikeDataStruct", true);
        FieldInfo slot = game.GetType("ConnectivityConstants.DelegateCallbacks", true)
                             .GetField("OpenBikeDataCallback", BindingFlags.Public | BindingFlags.Static);
        Console.WriteLine("== FOpenBikeDataStruct: " + Marshal.SizeOf(stype) + " bytes native, ipAddress @"
                          + Marshal.OffsetOf(stype, "ipAddress") + ", port @" + Marshal.OffsetOf(stype, "port")
                          + ", hostName @" + Marshal.OffsetOf(stype, "hostName"));

        // The receiving end, reached only through native code: a fresh
        // `mov rax, imm64; jmp rax` in front of it, so mono cannot recognise
        // the pointer as one of its own delegates and skip the marshalling.
        receiver = Receive;
        IntPtr target = Marshal.GetFunctionPointerForDelegate(receiver);
        IntPtr tramp = VirtualAlloc(IntPtr.Zero, (IntPtr)4096, 0x3000, 0x40);
        var code = new List<byte> { 0x48, 0xB8 };
        code.AddRange(BitConverter.GetBytes(target.ToInt64()));
        code.AddRange(new byte[] { 0xFF, 0xE0 });
        Marshal.Copy(code.ToArray(), 0, tramp, code.Count);

        slot.SetValue(null, Marshal.GetDelegateForFunctionPointer(tramp, dtype));
        Console.WriteLine("   DelegateCallbacks.OpenBikeDataCallback -> native 0x" + tramp.ToString("x"));
        return true;
    }

    /// What the engine's callback sees: one pointer to a caller-made copy of
    /// the 24-byte struct (x64 passes anything over 8 bytes that way).
    static void Receive(IntPtr data)
    {
        try
        {
            string ip = Marshal.PtrToStringUni(Marshal.ReadIntPtr(data, 0));
            int port = Marshal.ReadInt32(data, 8);
            string host = Marshal.PtrToStringUni(Marshal.ReadIntPtr(data, 16));
            Console.WriteLine("   delivered: ipAddress=\"" + ip + "\" port=" + port + " hostName=\"" + host + "\"");
            lock (Gate)
            {
                Delivered.Add(new[] { ip, port.ToString(), host });
                Monitor.PulseAll(Gate);
            }
        }
        catch (Exception e) { Console.WriteLine("   receiver threw: " + e); }
    }

    static int Watch(string ip, int port, int seconds)
    {
        Console.WriteLine("== watching " + seconds + " s: unconnected for half, then connected");
        Thread.Sleep(seconds * 500);
        var c = new TcpClient();
        c.Connect(ip, port);
        Thread.Sleep(1000);         // an offer already on its way is not a failure
        int before;
        lock (Gate) before = Delivered.Count;
        Console.WriteLine("   " + DateTime.Now.ToString("HH:mm:ss") + " connected, holding it (" + before + " offer(s) so far)");
        Thread.Sleep(seconds * 500 - 1000);
        int after;
        lock (Gate) after = Delivered.Count;
        c.Close();
        OpenBike.StopScan();
        Thread.Sleep(500);
        Console.WriteLine("   offers while unconnected: " + before + ", while connected: " + (after - before));
        return before > 1 && after == before ? 0 : 1;
    }

    // ---------------------------------------------------------------- TCP

    static readonly Dictionary<int, string> Buttons = new Dictionary<int, string>
    {
        { 0x01, "Shift Up" }, { 0x02, "Shift Down" }, { 0x03, "Gear Set" }, { 0x04, "Chainring Set" },
        { 0x05, "Cassette Set" }, { 0x10, "Up" }, { 0x11, "Down" }, { 0x12, "Left" }, { 0x13, "Right" },
        { 0x14, "Select" }, { 0x15, "Back" }, { 0x16, "Menu" }, { 0x17, "Home" }, { 0x18, "Steer Left" },
        { 0x19, "Steer Right" }, { 0x1A, "Brake" }, { 0x1B, "Steering Angle" }, { 0x20, "Emote" },
        { 0x21, "Push to Talk" }, { 0x24, "Screenshot" }, { 0x30, "Increase Difficulty" },
        { 0x31, "Decrease Difficulty" }, { 0x32, "Skip Interval" }, { 0x33, "Pause" }, { 0x34, "Resume" },
        { 0x35, "Lap" }, { 0x36, "Previous Interval" }, { 0x37, "U-Turn" }, { 0x38, "Change Mode" },
        { 0x39, "Take a break" }, { 0x3A, "Join another rider" }, { 0x3B, "Change route" },
        { 0x3C, "Cruise Control" }, { 0x40, "Camera View" }, { 0x44, "HUD Toggle" }, { 0x45, "Map Toggle" },
        { 0x46, "Spectate rider" }, { 0x50, "Power-up 1" }, { 0x51, "Power-up 2" }, { 0x52, "Power-up 3" },
    };

    static bool Session(string ip, int port, int seconds, bool appInfo)
    {
        Console.WriteLine("== TCP session to " + ip + ":" + port + " for " + seconds + " s"
                          + " -- press buttons in the app now");
        var c = new TcpClient();
        try { c.Connect(ip, port); }
        catch (SocketException e)
        {
            Console.WriteLine("   connect failed: " + e.SocketErrorCode + " -- firewall, or a different network?");
            return false;
        }
        c.NoDelay = true;
        NetworkStream s = c.GetStream();
        Console.WriteLine("   connected from " + c.Client.LocalEndPoint);

        if (appInfo)
        {
            // [04] [version 01] [len] "mywhoosh-linux-test" [len] "1" [count 0 = all buttons]
            byte[] id = Encoding.UTF8.GetBytes("mywhoosh-linux-test"), ver = Encoding.UTF8.GetBytes("1");
            var m = new List<byte> { 0x04, 0x01, (byte)id.Length };
            m.AddRange(id); m.Add((byte)ver.Length); m.AddRange(ver); m.Add(0x00);
            s.Write(m.ToArray(), 0, m.Count);
            Console.WriteLine("   sent App Information: " + Hex(m.ToArray(), m.Count));
        }

        // The protocol has no length prefix; like the reference client, take
        // what one read returns as one message, and say so when it is odd.
        var buf = new byte[256];
        int messages = 0;
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        c.ReceiveTimeout = 1000;
        while (DateTime.UtcNow < end)
        {
            int n;
            try { n = s.Read(buf, 0, buf.Length); }
            catch (IOException) { continue; }        // the 1 s timeout
            if (n == 0) { Console.WriteLine("   the device closed the connection"); break; }
            messages++;
            Console.WriteLine("   " + DateTime.Now.ToString("HH:mm:ss.fff") + "  " + Hex(buf, n) + "  " + Decode(buf, n));
        }
        c.Close();
        Console.WriteLine("   " + messages + " message(s); connection closed");
        return true;
    }

    static string Decode(byte[] b, int n)
    {
        if (b[0] == 0x01 && n % 2 == 1)
        {
            var parts = new List<string>();
            for (int i = 1; i + 1 < n; i += 2)
            {
                string name;
                if (!Buttons.TryGetValue(b[i], out name)) name = "0x" + b[i].ToString("X2");
                int v = b[i + 1];
                parts.Add(name + " " + (v == 0 ? "released" : v == 1 ? "pressed" : "value 0x" + v.ToString("X2")));
            }
            return "button state: " + string.Join(", ", parts.ToArray());
        }
        if (b[0] == 0x02 && n == 3)
            return "status: battery " + (b[1] == 0xFF ? "n/a" : b[1] + "%") + ", " + (b[2] == 1 ? "ready" : "not ready");
        return "(not one whole message -- coalesced or split by TCP)";
    }

    static string Hex(byte[] b, int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(i == 0 ? "" : " ").Append(b[i].ToString("x2"));
        return sb.ToString();
    }
}
