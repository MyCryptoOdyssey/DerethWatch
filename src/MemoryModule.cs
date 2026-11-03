using System;
using System.Diagnostics;
using System.Drawing;
using Decal.Adapter;

namespace DerethWatch
{
    // Memory guard (was LeakWatch v1). The 32-bit client (LAA) has a hard 4GB address-space
    // ceiling and leaks toward it as you play. Shows live usage color-coded on the panel,
    // warns in chat at 70% / 85%, and (optional) recalls and/or logs out at 90%.
    internal class MemoryModule : Module
    {
        // Effective ceiling: the practical wall. Fragmentation degrades the
        // engine well before the hard 4GB address-space limit, so 100% here
        // means "you are done", not "theoretical maximum".
        const double CEILING = 3.8 * 1073741824.0;    // 3.8 GB effective
        const double YELLOW = 0.70, RED = 0.85, PANIC = 0.90;

        VirindiViewService.Controls.HudStaticText lbl;
        VirindiViewService.Controls.HudCheckBox chkOn, chkRecall, chkLogout;
        int tick;
        int warned = 0;                                // 0 none, 1 yellow, 2 red
        DateTime lastRecall = DateTime.MinValue;
        DateTime pendingLogout = DateTime.MinValue;

        public MemoryModule(PluginCore p) : base(p) { }
        public override string Name { get { return "Memory guard"; } }
        public override string Key { get { return "memory"; } }

        public override void Startup()
        {
            if (P.View == null) return;
            lbl = (VirindiViewService.Controls.HudStaticText)P.View["lblMem"];
            chkOn = (VirindiViewService.Controls.HudCheckBox)P.View["chkMemory"];
            chkOn.Checked = Enabled;
            chkOn.Change += (s, e) => { Enabled = chkOn.Checked; Settings.Save(P); Refresh(); };
            chkRecall = (VirindiViewService.Controls.HudCheckBox)P.View["chkRecall"];
            chkRecall.Checked = true;
            chkLogout = (VirindiViewService.Controls.HudCheckBox)P.View["chkLogout"];
            chkLogout.Checked = false;   // "Recall & Logout" - implies recall first
        }

        public override void SetEnabled(bool on)
        {
            Enabled = on;
            if (chkOn != null && chkOn.Checked != on) chkOn.Checked = on;
            Refresh();
        }
        void Refresh()
        {
            if (lbl != null && !Enabled) { lbl.Text = "Memory guard off"; lbl.TextColor = Color.FromArgb(150, 150, 150); }
        }

        public override void Tick()
        {
            if (++tick % 5 != 0) return;               // every 5 s
            double used = Process.GetCurrentProcess().VirtualMemorySize64;
            double pct = used / CEILING;

            if (lbl != null)
            {
                lbl.Text = string.Format("Memory {0:0}%", Math.Min(pct, 1.0) * 100);
                if (pct < YELLOW) lbl.TextColor = Color.FromArgb(120, 220, 120);
                else if (pct < RED) lbl.TextColor = Color.FromArgb(235, 210, 90);
                else lbl.TextColor = Color.FromArgb(240, 90, 90);
            }

            // chat warnings, latched per band so they fire once per crossing
            if (pct >= RED && warned < 2)
            {
                warned = 2;
                P.Chat(string.Format("DANGER: memory {0:0}% - relog soon to avoid lag death.", pct * 100));
            }
            else if (pct >= YELLOW && warned < 1)
            {
                warned = 1;
                P.Chat(string.Format("Warning: memory {0:0}% - plan a relog.", pct * 100));
            }
            else if (pct < YELLOW && warned != 0)
            {
                warned = 0;                            // fresh session / dropped back
            }

            // panic actions (once per 3 minutes max):
            // Auto-Recall = park at lifestone, stay logged in.
            // Recall & Logout = park, wait for the cast to finish, then log out
            // (logout is what actually frees the leaked memory).
            if (pct >= PANIC && (DateTime.Now - lastRecall).TotalSeconds > 180)
                DoPanic();
            if (pendingLogout != DateTime.MinValue && DateTime.Now >= pendingLogout)
            {
                pendingLogout = DateTime.MinValue;
                P.Chat("Logging out to free memory.");
                try { CoreManager.Current.Actions.Logout(); } catch { try { CoreManager.Current.Actions.InvokeChatParser("/logout"); } catch { } }
            }
        }

        void DoPanic()
        {
            bool doRecall = chkRecall != null && chkRecall.Checked;
            bool doLogout = chkLogout != null && chkLogout.Checked;
            if (!doRecall && !doLogout) return;
            lastRecall = DateTime.Now;
            try { CoreManager.Current.Actions.InvokeChatParser("/vt opt set EnableCombat false"); } catch { }
            if (doRecall)
            {
                P.Chat("CRITICAL: recalling to lifestone.");
                try { CoreManager.Current.Actions.InvokeChatParser("/lifestone"); } catch { }
            }
            if (doLogout)
            {
                int delay = doRecall ? 25 : 3;   // after the cast, or (nearly) in place
                P.Chat("CRITICAL: logout in " + delay + "s.");
                pendingLogout = DateTime.Now.AddSeconds(delay);
            }
        }

        public override void Status()
        {
            double pct = Process.GetCurrentProcess().VirtualMemorySize64 / CEILING;
            P.Chat(string.Format("Memory guard {0}: memory {1:0}%, auto-recall {2}, auto-logout {3}.",
                Active ? "ON" : Failed ? "STOPPED (error)" : "OFF", pct * 100,
                chkRecall != null && chkRecall.Checked ? "on" : "off", chkLogout != null && chkLogout.Checked ? "on" : "off"));
        }
    }
}
