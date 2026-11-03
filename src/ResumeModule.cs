using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace DerethWatch
{
    // Crash resume (was LeakWatch v2). Every 5 s saves whether the VTank macro runs, its meta
    // state and route waypoint. After an UNCLEAN exit (crash / kill / hang) and ThwargLauncher's
    // relaunch, runs /vt start (+ /vt setmetastate) 5 s after login. A normal logout = no resume.
    // Optional planned restart every N hours of client uptime: stop macro, log out, close the
    // client; Thwarg relaunches it and the macro resumes.
    internal class ResumeModule : Module
    {
        const int RESUME_DELAY_S = 5;                  // VTank has its profile loaded by then (VT is usually started on entry)
        const string SUFFIX = ".state.txt";

        VirindiViewService.Controls.HudCheckBox chkOn, chkSamePlace, chkRestoreMeta;
        bool samePlaceOnly = true;                     // per character, saved: skip resume if the character moved (died?)
        // Per character, saved, default OFF: jump back to the saved meta state after a crash. Off = the
        // meta starts from Default, because setvar values live only in VTank's memory and a crash wipes
        // them (multi-state metas are fiddly enough without a half-restored one).
        bool restoreMeta = false;
        string resumePos = "";
        VirindiViewService.Controls.HudStaticText lblRestart;
        double restartHours = 0;                       // per character, saved; 0 = off
        int tick;
        DateTime resumeAt = DateTime.MinValue, metaAt = DateTime.MinValue, verifyAt = DateTime.MinValue;
        string resumeMeta = "", resumeReason = "", resumeNav = "";
        bool restarting;                               // planned restart in progress: freeze state file
        internal bool Restarting { get { return restarting; } }
        DateTime restartLogoutAt = DateTime.MinValue;
        const int CLOSE_DELAY_S = 20;                  // logout -> character select takes ~5-10 s

        public ResumeModule(PluginCore p) : base(p) { }
        public override string Name { get { return "Crash resume"; } }
        public override string Key { get { return "resume"; } }

        public override void Startup()
        {
            if (P.View == null) return;
            chkOn = (VirindiViewService.Controls.HudCheckBox)P.View["chkResume"];
            chkOn.Checked = Enabled;
            chkOn.Change += (s, e) => { Enabled = chkOn.Checked; Settings.Save(P); };
            chkSamePlace = (VirindiViewService.Controls.HudCheckBox)P.View["chkSamePlace"];
            chkSamePlace.Checked = samePlaceOnly;
            chkSamePlace.Change += (s, e) => { samePlaceOnly = chkSamePlace.Checked; Settings.Save(P); };
            chkRestoreMeta = (VirindiViewService.Controls.HudCheckBox)P.View["chkRestoreMeta"];
            chkRestoreMeta.Checked = restoreMeta;
            chkRestoreMeta.Change += (s, e) => { restoreMeta = chkRestoreMeta.Checked; Settings.Save(P); };
            lblRestart = (VirindiViewService.Controls.HudStaticText)P.View["lblRestart"];
        }
        public override void SetEnabled(bool on)
        {
            Enabled = on;
            if (chkOn != null && chkOn.Checked != on) chkOn.Checked = on;
        }

        internal void LoadSettings(Dictionary<string, string> s)
        {
            // LeakWatch's per-character "Resume=0" carries over when there is no module setting yet
            if (Util.Get(s, "Module.resume") == "" && Util.Get(s, "Resume") == "0") SetEnabled(false);
            double h;
            restartHours = double.TryParse(Util.Get(s, "RestartHours"), NumberStyles.Float, CultureInfo.InvariantCulture, out h) ? h : 0;
            samePlaceOnly = Util.Get(s, "SamePlaceOnly") != "0";
            if (chkSamePlace != null) chkSamePlace.Checked = samePlaceOnly;
            restoreMeta = Util.Get(s, "RestoreMeta") == "1";
            if (chkRestoreMeta != null) chkRestoreMeta.Checked = restoreMeta;
            UpdateRestartLabel();
        }
        internal void SaveSettings(Dictionary<string, string> d)
        {
            d["RestartHours"] = restartHours.ToString(CultureInfo.InvariantCulture);
            d["SamePlaceOnly"] = samePlaceOnly ? "1" : "0";
            d["RestoreMeta"] = restoreMeta ? "1" : "0";
        }

        string Key2 { get { return Util.CharKey(P.ServerName, P.CharName); } }

        public override void OnLogin()
        {
            restarting = false;
            resumeAt = metaAt = verifyAt = DateTime.MinValue;

            // How did the previous session for this character end?
            var st = Util.ReadCharFile(Key2, SUFFIX);
            bool clean = Util.Get(st, "Clean") == "1";
            bool planned = Util.Get(st, "Planned") == "1";
            string macro = Util.Get(st, "Macro");      // 1 / 0 / ? (VTank unreadable)
            resumeMeta = Util.Get(st, "Meta");
            resumeNav = Util.Get(st, "Nav");
            resumePos = Util.Get(st, "Pos");

            if (st.Count > 0 && (planned || !clean) && macro != "0")
            {
                resumeReason = planned ? "planned restart" : "unclean exit (crash or kill)";
                if (Active)
                {
                    resumeAt = DateTime.Now.AddSeconds(RESUME_DELAY_S);
                    P.Chat("Previous session ended by " + resumeReason + " with the macro running - resuming VTank in " + RESUME_DELAY_S + "s.");
                }
                else Util.Log(P.CharName, P.ServerName, "RESUME-SKIPPED", "Crash resume is off for this character (" + resumeReason + ")");
            }
            SaveState(false, false);                   // this session is now "live" (unclean until logoff)
            UpdateRestartLabel();
        }

        public override void OnLogoff()
        {
            if (!P.InGame) return;
            if (restarting) return;                    // keep Planned=1 for the next login
            SaveState(true, false);                    // deliberate logout: no resume next time
            resumeAt = metaAt = verifyAt = DateTime.MinValue;
        }

        public override void Tick()
        {
            ResumeTick();
            RestartTick();
            if (++tick % 5 != 0) return;               // state every 5 s
            if (P.InGame && !restarting && resumeAt == DateTime.MinValue) SaveState(false, false);
            if (P.InGame && restartHours > 0 && !restarting && UptimeHours() >= restartHours)
                BeginPlannedRestart("uptime " + UptimeHours().ToString("0.0") + "h >= " + restartHours + "h");
        }

        void ResumeTick()
        {
            DateTime now = DateTime.Now;
            if (resumeAt != DateTime.MinValue && now >= resumeAt)
            {
                resumeAt = DateTime.MinValue;
                // Died (respawned at the lifestone) or moved while the client was down?
                // Then a route/meta resume would start from the wrong place: stay idle instead.
                string nowPos = Position.Now();
                double dist = Position.Distance(resumePos, nowPos);
                if (samePlaceOnly && !string.IsNullOrEmpty(resumePos) && dist > Position.SAME_PLACE_M)
                {
                    string why = "position changed (died or moved?): was " + resumePos + ", now " + nowPos
                        + (dist < double.MaxValue ? string.Format(" ({0:0} m away)", dist) : " (different area)") + ", after " + resumeReason;
                    Util.Log(P.CharName, P.ServerName, "RESUME-SKIPPED", why);
                    P.Chat("NOT resuming the macro - " + why + ". Character left idle.");
                    return;
                }
                Util.Command("/vt start");
                // Saved state not Default: either restore it, or say Default explicitly so the meta
                // starts from the top whatever VTank came back with.
                bool hasMeta = !string.IsNullOrEmpty(resumeMeta) && !resumeMeta.Equals("Default", StringComparison.OrdinalIgnoreCase);
                if (hasMeta) metaAt = now.AddSeconds(2);
                verifyAt = now.AddSeconds(hasMeta ? 6 : 4);
            }
            if (metaAt != DateTime.MinValue && now >= metaAt)
            {
                metaAt = DateTime.MinValue;
                Util.Command("/vt setmetastate " + (restoreMeta ? resumeMeta : "Default"));
            }
            if (verifyAt != DateTime.MinValue && now >= verifyAt)
            {
                verifyAt = DateTime.MinValue;
                bool? on = VTank.MacroEnabled;
                string meta = VTank.MetaState;
                bool hadMeta = !string.IsNullOrEmpty(resumeMeta) && !resumeMeta.Equals("Default", StringComparison.OrdinalIgnoreCase);
                string detail = "macro " + (on == null ? "state unreadable" : on.Value ? "ON" : "still OFF")
                    + ", meta state '" + (meta ?? "?") + "'"
                    + (!hadMeta ? (string.IsNullOrEmpty(resumeMeta) ? "" : " (saved '" + resumeMeta + "')")
                       : restoreMeta ? " (restored saved '" + resumeMeta + "')" : " (started from Default; was '" + resumeMeta + "' before exit)")
                    + ", route " + (VTank.Nav ?? "?") + (string.IsNullOrEmpty(resumeNav) ? "" : " (before exit: " + resumeNav + ")")
                    + ", after " + resumeReason;
                Util.Log(P.CharName, P.ServerName, on == false ? "RESUME-FAILED" : "RESUMED", detail);
                P.Chat((on == false ? "Resume FAILED: " : "Resumed: ") + detail);
            }
        }

        void BeginPlannedRestart(string why)
        {
            restarting = true;
            bool? on = VTank.MacroEnabled;
            string meta = VTank.MetaState;
            SaveStateRaw(false, true, on, meta);
            Util.Log(P.CharName, P.ServerName, "PLANNED-RESTART", why + "; macro " + (on == null ? "?" : on.Value ? "ON" : "OFF") + ", meta state '" + (meta ?? "?") + "'");
            P.Chat("Planned restart (" + why + "): stopping macro, logging out, client will close and ThwargLauncher relaunches it.");
            Util.Command("/vt stop");
            restartLogoutAt = DateTime.Now.AddSeconds(8);
        }

        void RestartTick()
        {
            if (!restarting) return;
            DateTime now = DateTime.Now;
            if (restartLogoutAt != DateTime.MinValue && now >= restartLogoutAt)
            {
                restartLogoutAt = DateTime.MinValue;
                // Decal unloads plugins at logout, so no timer of ours survives to close the client
                // (witnessed 2026-11-02: client sat at character select). Hand the close to a
                // detached cmd that outlives the plugin: wait CLOSE_DELAY_S, then close this pid.
                // Plain taskkill = WM_CLOSE (the window's X): the client says goodbye (Disconnect flag),
                // ACE drops the session at once (NetworkSession.cs:319). A forced kill sends nothing, the
                // account stays logged in at character select and Thwarg's first relaunch is refused
                // ('Account In Use', seen 2026-11-02). /F only if the client is still there 15 s later.
                int pid = Process.GetCurrentProcess().Id;
                try
                {
                    var psi = new ProcessStartInfo("cmd.exe", "/c ping -n " + (CLOSE_DELAY_S + 1) + " 127.0.0.1 >nul & taskkill /PID " + pid
                        + " & ping -n 16 127.0.0.1 >nul & taskkill /F /PID " + pid);
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                    Process.Start(psi);
                    Util.Log(P.CharName, P.ServerName, "CLIENT-CLOSE", "logging out; client (pid " + pid + ") will be closed in " + CLOSE_DELAY_S + "s for planned restart");
                }
                catch (Exception ex) { Util.Log(P.CharName, P.ServerName, "CLIENT-CLOSE", "could not schedule close (" + ex.Message + "); Thwarg closes clients left at character select"); }
                try { Decal.Adapter.CoreManager.Current.Actions.Logout(); } catch { }
            }
        }

        static double UptimeHours()
        {
            try { return (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalHours; } catch { return 0; }
        }

        public override bool Command(string cmd, string arg)
        {
            if (cmd == "resume" && (arg == "on" || arg == "off"))
            {
                SetEnabled(arg == "on");
                Settings.Save(P);
                P.Chat("Crash resume: " + (Enabled ? "ON" : "OFF") + " for " + P.CharName + ".");
                return true;
            }
            if (cmd == "resume" && (arg == "sameplace" || arg == "anywhere"))
            {
                samePlaceOnly = arg == "sameplace";
                if (chkSamePlace != null) chkSamePlace.Checked = samePlaceOnly;
                Settings.Save(P);
                P.Chat("Crash resume for " + P.CharName + ": " + (samePlaceOnly ? "only if still in the same place" : "anywhere") + ".");
                return true;
            }
            if (cmd == "resume" && (arg == "meta on" || arg == "meta off"))
            {
                restoreMeta = arg == "meta on";
                if (chkRestoreMeta != null) chkRestoreMeta.Checked = restoreMeta;
                Settings.Save(P);
                P.Chat("Crash resume for " + P.CharName + ": " + (restoreMeta ? "restore the saved meta state" : "start the meta from Default") + ".");
                return true;
            }
            if (cmd == "restart" && arg == "now")
            {
                if (P.InGame && !restarting) BeginPlannedRestart("manual /dw restart now");
                return true;
            }
            if (cmd == "restart" && arg != "")
            {
                double h;
                if (arg == "off") h = 0;
                else if (!double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out h) || h < 1) { P.Chat("Usage: /dw restart <hours, 1 or more> | off | now"); return true; }
                restartHours = h;
                Settings.Save(P);
                UpdateRestartLabel();
                P.Chat("Planned restart: " + (h > 0 ? "every " + h + "h of client uptime" : "OFF") + " for " + P.CharName + ".");
                return true;
            }
            return false;
        }

        public override void Status()
        {
            bool? on = VTank.MacroEnabled;
            P.Chat(string.Format("Crash resume {0}: {1} on {2}, uptime {3:0.0}h, VTank macro {4}, meta state '{5}', route {6}, planned restart {7}.",
                Active ? "ON" : Failed ? "STOPPED (error)" : "OFF", P.CharName, P.ServerName, UptimeHours(),
                on == null ? "UNREADABLE" : on.Value ? "ON" : "OFF", VTank.MetaState ?? "?", VTank.Nav ?? "?",
                restartHours > 0 ? "every " + restartHours + "h" : "OFF"));
            P.Chat("Resume " + (samePlaceOnly ? "only if same place" : "anywhere") + ", meta " + (restoreMeta ? "restored" : "from Default") + "; position now " + Position.Now() + ".");
            if (on == null) P.Chat("VTank unreadable: " + VTank.Error);
        }

        void UpdateRestartLabel()
        {
            if (lblRestart != null)
                lblRestart.Text = restartHours > 0 ? "Planned restart: every " + restartHours + "h" : "Planned restart: off";
        }

        void SaveState(bool clean, bool planned) { SaveStateRaw(clean, planned, VTank.MacroEnabled, VTank.MetaState); }
        void SaveStateRaw(bool clean, bool planned, bool? macro, string meta)
        {
            if (P.CharName == "") return;
            Util.WriteCharFile(Key2, SUFFIX, new Dictionary<string, string> {
                { "Character", P.CharName }, { "Server", P.ServerName },
                { "Clean", clean ? "1" : "0" }, { "Planned", planned ? "1" : "0" },
                { "Macro", macro == null ? "?" : macro.Value ? "1" : "0" }, { "Meta", meta ?? "" },
                { "VtError", macro == null ? VTank.Error : "" },
                { "Nav", VTank.Nav ?? "" },
                { "Pos", Position.Now() },
                { "Pid", Process.GetCurrentProcess().Id.ToString() },
                { "SavedLocal", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") } });
        }
    }
}
