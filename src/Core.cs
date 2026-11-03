// DerethWatch v2 - one Decal plugin for the multibox fleet (2026-11-02)
// Modules (each can be switched on/off per character; a module that throws is switched
// off and logged, the others keep running):
//   Stuck detection - logs STUCK with busy/combat/monster signals; /dw unstick or 'Fix automatically' runs
//                   back off -> jump -> reload route -> give up (stop macro)
//   Character Views - eye toggle: live DWM thumbnail strip of every client (UB video patch off while open)
//   Memory guard  - memory % on the panel, chat warnings, auto-recall / logout at 90% (was LeakWatch)
//   Stats         - XP, luminance, killing blows, rares (all tiers) this session; shared stats file per client
//   Crash resume  - remembers VTank macro / meta / route; after an unclean exit (crash, kill) restarts
//                   the macro once ThwargLauncher's Auto Relaunch has logged the character back in;
//                   optional planned restart every N hours (was LeakWatch v2)
// Outside the game: C:\Games\ACRecovery\ACRecovery.ps1 logs crashes / relaunches to the shared log.
// Commands: /dw help   (/lw ... still works)
// Build: build.bat   Registered in Decal as DerethWatch.PluginCore (C:\Games\Decal Plugins\DerethWatch)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace DerethWatch
{
    [FriendlyName("DerethWatch")]
    public class PluginCore : PluginBase
    {
        internal static PluginCore Instance;
        internal VirindiViewService.HudView View;
        internal readonly List<Module> Modules = new List<Module>();
        internal FleetModule Fleet;
        internal MemoryModule Memory;
        internal ResumeModule Resume;
        internal StuckModule Stuck;
        internal StatsModule Stats;

        internal bool InGame;
        internal string CharName = "", ServerName = "";
        System.Windows.Forms.Timer timer;
        VirindiViewService.Controls.HudStaticText lblStatus;

        protected override void Startup()
        {
            Instance = this;
            try
            {
                VirindiViewService.ViewProperties properties;
                VirindiViewService.ControlGroup controls;
                var parser = new VirindiViewService.XMLParsers.Decal3XMLParser();
                parser.ParseFromResource("DerethWatch.ViewXML.xml", out properties, out controls);
                View = new VirindiViewService.HudView(properties, controls);
                lblStatus = (VirindiViewService.Controls.HudStaticText)View["lblStatus"];
                // Window fits the open tab: Stats is short, Settings needs the full height.
                var nb = (VirindiViewService.Controls.HudTabView)View["nbMain"];
                nbMain = nb;
                nb.OpenTabChange += (s, e) => FitTab();
                FitTab();
            }
            catch (Exception ex) { Debug.WriteLine(ex); }

            Fleet = new FleetModule(this);
            Memory = new MemoryModule(this);
            Resume = new ResumeModule(this);
            Stuck = new StuckModule(this);
            Stats = new StatsModule(this);
            Modules.Add(Fleet); Modules.Add(Memory); Modules.Add(Resume); Modules.Add(Stuck); Modules.Add(Stats);
            foreach (var m in Modules) { Module mm = m; mm.Guard("startup", mm.Startup); }

            try
            {
                Core.CharacterFilter.LoginComplete += CharacterFilter_LoginComplete;
                Core.CharacterFilter.Logoff += CharacterFilter_Logoff;
                Core.CommandLineText += Core_CommandLineText;
            }
            catch (Exception ex) { Debug.WriteLine(ex); }

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += Timer_Tick;
            timer.Start();
        }

        protected override void Shutdown()
        {
            try
            {
                if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
                Core.CharacterFilter.LoginComplete -= CharacterFilter_LoginComplete;
                Core.CharacterFilter.Logoff -= CharacterFilter_Logoff;
                Core.CommandLineText -= Core_CommandLineText;
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
            foreach (var m in Modules) { Module mm = m; mm.Guard("shutdown", mm.Shutdown); }
            try { if (View != null) { View.Dispose(); View = null; } } catch { }
            Instance = null;
        }

        void Timer_Tick(object sender, EventArgs e)
        {
            foreach (var m in Modules)
            {
                Module mm = m;
                if (mm.Active) mm.Guard("tick", mm.Tick);
            }
            UpdateStatus();
        }

        void CharacterFilter_LoginComplete(object sender, EventArgs e)
        {
            try
            {
                CharName = Core.CharacterFilter.Name ?? "";
                ServerName = Core.CharacterFilter.Server ?? "";
                InGame = true;
                Settings.Load(this);
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
            foreach (var m in Modules) { Module mm = m; mm.Guard("login", mm.OnLogin); }
        }

        void CharacterFilter_Logoff(object sender, LogoffEventArgs e)
        {
            foreach (var m in Modules) { Module mm = m; mm.Guard("logoff", mm.OnLogoff); }
            if (!Resume.Restarting) InGame = false;
        }

        void Core_CommandLineText(object sender, ChatParserInterceptEventArgs e)
        {
            try
            {
                string t = (e.Text ?? "").Trim();
                string lower = t.ToLowerInvariant();
                if (!(lower.StartsWith("/dw") || lower.StartsWith("/lw"))) return;
                if (t.Length > 3 && t[3] != ' ') return;
                e.Eat = true;
                string[] a = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string cmd = a.Length > 1 ? a[1].ToLowerInvariant() : "help";
                string arg = a.Length > 2 ? string.Join(" ", a, 2, a.Length - 2).ToLowerInvariant() : "";   // "meta on" = two words
                if (cmd == "status")
                {
                    foreach (var m in Modules) { Module mm = m; mm.Guard("status", mm.Status); }
                    return;
                }
                if ((cmd == "on" || cmd == "off") && arg != "")
                {
                    if (arg == "views") arg = "fleet";   // panel name is "Character Views"; settings key stays "fleet"
                    foreach (var m in Modules)
                        if (m.Key == arg) { m.SetEnabled(cmd == "on"); Settings.Save(this); Chat(m.Name + ": " + (m.Enabled ? "ON" : "OFF") + " for " + CharName + "."); return; }
                    Chat("Unknown module '" + arg + "'. Modules: views (fleet), memory, resume, stuck, stats.");
                    return;
                }
                foreach (var m in Modules)
                {
                    Module mm = m; bool handled = false;
                    mm.Guard("command", () => { handled = mm.Command(cmd, arg); });
                    if (handled) return;
                }
                Chat("/dw status | /dw on|off views|memory|resume|stuck|stats | /dw stats [reset] | /dw strip reset | /dw rares test <item> | /dw resume on|off|sameplace|anywhere|meta on|meta off | /dw restart <hours>|off|now | /dw unstick | /dw stuckfix on|off | /dw stuckgiveup stay|ls|lslogout");
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        void UpdateStatus()
        {
            if (lblStatus == null) return;
            var failed = new List<string>();
            foreach (var m in Modules) if (m.Failed) failed.Add(m.Name);
            string s = failed.Count == 0 ? "" : "Stopped: " + string.Join(", ", failed.ToArray());
            if (lblStatus.Text != s) { lblStatus.Text = s; FitTab(); }
        }

        VirindiViewService.Controls.HudTabView nbMain;
        // Stats tab: ends under the Character Views group; grows only while the last-rare line or an
        // error notice has something to show (keeps the window tight).
        const int STATS_TAB_BASE = 248, SETTINGS_TAB_HEIGHT = 284;
        internal void FitTab()
        {
            if (View == null || nbMain == null) return;
            try
            {
                int h = SETTINGS_TAB_HEIGHT;
                if (nbMain.CurrentTab == 0)
                {
                    h = STATS_TAB_BASE;
                    if (Stats != null && Stats.HasLastRare) h += 18;
                    if (lblStatus != null && lblStatus.Text != "") h += 28;
                }
                if (View.ClientArea.Height != h) View.ClientArea = new System.Drawing.Size(View.ClientArea.Width, h);
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        internal IntPtr DecalHwnd { get { return (IntPtr)Host.Decal.Hwnd; } }

        internal void Chat(string msg)
        {
            try { Host.Actions.AddChatText("[DerethWatch] " + msg, 3); } catch { }
        }
    }

    // One feature. Everything it does runs through Guard(): an exception switches the
    // module off (Failed), writes MODULE-ERROR to the recovery log and tells the player.
    internal abstract class Module
    {
        protected readonly PluginCore P;
        protected Module(PluginCore p) { P = p; }
        public abstract string Name { get; }
        public abstract string Key { get; }               // used by /dw on|off <key> and settings
        public bool Enabled = true;
        public bool Failed;
        public bool Active { get { return Enabled && !Failed; } }

        public virtual void Startup() { }
        public virtual void Shutdown() { }
        public virtual void Tick() { }
        public virtual void OnLogin() { }
        public virtual void OnLogoff() { }
        public virtual void Status() { }
        public virtual bool Command(string cmd, string arg) { return false; }
        public virtual void SetEnabled(bool on) { Enabled = on; }

        public void Guard(string what, Action a)
        {
            if (Failed && what != "shutdown") return;
            try { a(); }
            catch (Exception ex)
            {
                Failed = true;
                string msg = Name + " stopped after an error in " + what + ": " + ex.GetType().Name + " " + ex.Message;
                Util.Log(P.CharName, P.ServerName, "MODULE-ERROR", msg);
                P.Chat(msg + " (other modules keep running; relog to retry)");
                Debug.WriteLine(ex);
            }
        }
    }
}
