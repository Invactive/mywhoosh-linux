// Start ../../exportshim from inside the game.
//
// Four of WindowsConnectivity.dll's exports pass their device list by
// reference, wine-mono compiles a throw into the wrapper for that shape, and
// two of the four -- BT_GetScannedDevicesList and BT_GetConnectedDevicesList --
// are what the game's own UI polls.  ../../exportshim/ serves them itself, but
// something has to call its Install() inside the game's process, before the
// first poll and with WindowsConnectivity.dll already loaded.
//
// Nothing here is native code, so there is no loader to hook and nothing to
// reach through libmono's embedding API.  What does the job is smaller: this
// assembly *is* managed code the game loads.  Mono runs a static constructor
// before the first use of its type, and the game's very first act on the BLE
// path is to ask for the radio state (BluetoothProgram::CheckRadioState), so a
// kick from the static constructors of the few types the game touches first
// runs inside the game, on the game's thread, after WindowsConnectivity.dll is
// loaded -- which is every condition Install() needs.
//
// Kicking from several types rather than one is deliberate: which of them the
// game reaches first is its business, not ours, and Kick() costs a bool read
// once it has run.

using System;
using System.IO;
using System.Reflection;

namespace MyWhoosh.Ble
{
    static class Loader
    {
        const string ShimAssembly = "MyWhooshShim.dll";
        const string ShimType = "MyWhoosh.ExportShim";
        const string GameAssembly = "WindowsConnectivity";

        static readonly object Gate = new object();
        static bool done;
        static bool saidNoGame;

        /// Load the export shim and hook the exports, once, if we are running
        /// inside the game.  Never throws: every caller is a static constructor,
        /// and a type initializer that throws takes the type -- and with it the
        /// whole BLE path -- down with it.
        public static void Kick()
        {
            if (done) return;
            lock (Gate)
            {
                if (done) return;
                try { KickCore(); }
                catch (Exception e)
                {
                    done = true;
                    Backend.Log("export shim: not started (" + e.Message + ")");
                }
            }
        }

        static void KickCore()
        {
            if (!GameIsLoaded())
            {
                // TestBle.exe and anything else that is not MyWhoosh: there are
                // no exports to hook, so this is not a failure.  Left un-done on
                // purpose -- inside the game the first kick can arrive before
                // the game's own assembly does, and a later one has to retry.
                if (!saidNoGame)
                {
                    saidNoGame = true;
                    Backend.Log("export shim: no " + GameAssembly + " in this process, nothing to hook");
                }
                return;
            }

            string path = Environment.GetEnvironmentVariable("MYWHOOSH_SHIM_DLL");
            if (path != null && path.Length == 0)
            {
                done = true;
                Backend.Log("export shim disabled (MYWHOOSH_SHIM_DLL is empty)");
                return;
            }
            if (string.IsNullOrEmpty(path))
            {
                // Next to ourselves -- $GAMEDIR/bleshim through MONO_PATH, or
                // the prefix's mono tree from install.sh: the shim is installed
                // beside this assembly and neither knows where that is.
                string here = Path.GetDirectoryName(typeof(Loader).Assembly.Location);
                path = Path.Combine(here ?? ".", ShimAssembly);
            }

            done = true;
            if (!File.Exists(path))
            {
                Backend.Log("export shim: " + path + " is not installed -- the game's device"
                            + " list will throw on its first poll (see ../exportshim)");
                return;
            }

            Type shim = Assembly.LoadFrom(path).GetType(ShimType, false);
            if (shim == null)
            {
                Backend.Log("export shim: no " + ShimType + " in " + path);
                return;
            }
            MethodInfo install = shim.GetMethod("Install", BindingFlags.Public | BindingFlags.Static);
            if (install == null)
            {
                Backend.Log("export shim: " + ShimType + " has no Install()");
                return;
            }

            Backend.Log("export shim: invoking Install from " + path);
            install.Invoke(null, null);

            HookOpenBike(shim);
        }

        /// Point OBC_StartScan / OBC_StopScan at OpenBike.cs, which discovers
        /// OpenBikeControl devices without Bonjour, and answer the two exports
        /// around them that assume Bonjour.  Its own catch: a failure here must
        /// not read as the export shim having failed.
        static void HookOpenBike(Type shim)
        {
            try
            {
                // Whatever else is on or off: the game's "install Bonjour"
                // button must not put a Bonjour service into the prefix.
                MethodInfo hookString = shim.GetMethod("HookVoidString", BindingFlags.Public | BindingFlags.Static);
                if (hookString != null)
                    hookString.Invoke(null, new object[] { "WD_InstallDirconServiceAsync",
                                                           (Action<string>)OpenBike.RefuseInstall });

                if (Environment.GetEnvironmentVariable("MYWHOOSH_OBC") == "0")
                {
                    Backend.Log("obc: disabled (MYWHOOSH_OBC=0)");
                    return;
                }
                MethodInfo hook = shim.GetMethod("HookVoid", BindingFlags.Public | BindingFlags.Static);
                if (hook == null)
                {
                    Backend.Log("obc: " + ShimType + " has no HookVoid (an older " + ShimAssembly
                                + "?); OpenBikeControl stays off");
                    return;
                }
                // Stop first: a scan the game cannot stop would be worse than
                // none at all.
                if (!(bool)hook.Invoke(null, new object[] { "OBC_StopScan", (Action)OpenBike.StopScan })
                    || !(bool)hook.Invoke(null, new object[] { "OBC_StartScan", (Action)OpenBike.StartScan }))
                {
                    Backend.Log("obc: exports not hooked; OpenBikeControl stays off");
                    return;
                }
                OpenBike.Hooked();

                // The OpenBikeControl icon on the connection screen checks for
                // Bonjour first, and offers to install it rather than scan.
                MethodInfo hookBool = shim.GetMethod("HookBool", BindingFlags.Public | BindingFlags.Static);
                if (hookBool != null)
                    hookBool.Invoke(null, new object[] { "WD_GetDirconServiceAvailability",
                                                         (Func<bool>)OpenBike.ServiceAvailable });
            }
            catch (Exception e)
            {
                Backend.Log("obc: not started (" + e.Message + ")");
            }
        }

        static bool GameIsLoaded()
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == GameAssembly) return true;
            return false;
        }
    }
}
