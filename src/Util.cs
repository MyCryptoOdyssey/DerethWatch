using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Decal.Adapter;

namespace DerethWatch
{
    internal static class Util
    {
        // Shared with ACRecovery.ps1 (crash logger): one log for crash / relaunch / resume / module events.
        internal const string RECOVERY_LOG = @"C:\Games\ACRecovery\recovery.log";
        // LeakWatch v2 kept per-character state here; read once so nothing is lost in the switch.
        internal const string LEAKWATCH_STATE = @"C:\Games\Decal Plugins\LeakWatch\state";

        internal static string PluginDir { get { return Path.GetDirectoryName(typeof(Util).Assembly.Location); } }
        internal static string StateDir { get { return Path.Combine(PluginDir, "state"); } }

        internal static string CharKey(string server, string character)
        {
            string k = server + "__" + character;
            foreach (char c in Path.GetInvalidFileNameChars()) k = k.Replace(c, '_');
            return k;
        }

        // Read <state dir>\<key><suffix>, falling back to LeakWatch's copy (first run after the merge).
        internal static Dictionary<string, string> ReadCharFile(string key, string suffix)
        {
            string mine = Path.Combine(StateDir, key + suffix);
            if (File.Exists(mine)) return ReadKv(mine);
            return ReadKv(Path.Combine(LEAKWATCH_STATE, key + suffix));
        }
        internal static void WriteCharFile(string key, string suffix, Dictionary<string, string> d)
        {
            WriteKv(Path.Combine(StateDir, key + suffix), d);
        }

        internal static Dictionary<string, string> ReadKv(string path)
        {
            var d = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(path)) return d;
                foreach (string line in File.ReadAllLines(path))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
            return d;
        }
        internal static void WriteKv(string path, Dictionary<string, string> d)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = new List<string>();
                foreach (var kv in d) lines.Add(kv.Key + "=" + kv.Value);
                string tmp = path + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
        internal static string Get(Dictionary<string, string> d, string k) { string v; return d.TryGetValue(k, out v) ? v : ""; }

        // time | character | server | EVENT | details  (same format as ACRecovery.ps1)
        internal static void Log(string character, string server, string evt, string detail)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + character + " | " + server + " | " + evt + " | " + detail + Environment.NewLine;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(RECOVERY_LOG));
                    File.AppendAllText(RECOVERY_LOG, line);
                    return;
                }
                catch (IOException) { System.Threading.Thread.Sleep(50); }
                catch (Exception ex) { Debug.WriteLine(ex); return; }
            }
        }

        // Send a command as if typed. Decal's DispatchOnChatCommand lets other plugins
        // (VTank's /vt, UtilityBelt's /ub) handle it; InvokeChatParser alone skips them.
        [DllImport("Decal.dll")]
        static extern int DispatchOnChatCommand(ref IntPtr str, int target);
        internal static void Command(string cmd)
        {
            bool eaten = false;
            IntPtr bstr = Marshal.StringToBSTR(cmd);
            try { eaten = (DispatchOnChatCommand(ref bstr, 1) & 1) > 0; }
            catch (Exception ex) { Debug.WriteLine(ex); }
            finally { Marshal.FreeBSTR(bstr); }
            if (!eaten) try { CoreManager.Current.Actions.InvokeChatParser(cmd); } catch { }
        }
    }

    // Per-character settings: which modules are on, plus resume options.
    internal static class Settings
    {
        const string SUFFIX = ".settings.txt";
        internal static void Load(PluginCore p)
        {
            var s = Util.ReadCharFile(Util.CharKey(p.ServerName, p.CharName), SUFFIX);
            foreach (var m in p.Modules)
            {
                string v = Util.Get(s, "Module." + m.Key);
                m.SetEnabled(v != "0");
            }
            p.Resume.LoadSettings(s);
            p.Stuck.LoadSettings(s);
            p.Fleet.LoadSettings(s);
        }
        internal static void Save(PluginCore p)
        {
            if (p.CharName == "") return;
            var d = new Dictionary<string, string>();
            foreach (var m in p.Modules) d["Module." + m.Key] = m.Enabled ? "1" : "0";
            p.Resume.SaveSettings(d);
            p.Stuck.SaveSettings(d);
            p.Fleet.SaveSettings(d);
            Util.WriteCharFile(Util.CharKey(p.ServerName, p.CharName), SUFFIX, d);
        }
    }
}
