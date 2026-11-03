using System;
using System.Globalization;
using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace DerethWatch
{
    // Stuck detection + unstick (Phase 2). Logs STUCK when the VTank macro is running a route,
    // the waypoint has not advanced and the character has moved less than MOVE_M for WINDOW_S
    // seconds. Each line carries the busy / combat / monster signals.
    // Unstick ladder (manual: /dw unstick; automatic only with "Fix automatically" ticked, and
    // only when VTank is not busy and no monster is near):
    //   attempt 1 back off  : /vt stop, turn 180, run ~2 s, stop, /vt start
    //   attempt 2 jump out  : the same plus /vt jump
    //   attempt 3 re-pick   : the same plus reload the route so VTank re-picks a nearby waypoint
    //   still stuck         : /vt stop, STUCK-GAVE-UP naming the unreachable waypoint (no more tries until
    //                         the macro is started again), then per character: recall to lifestone
    //                         (default) and optionally log out - a stopped character in a dungeon is a target
    // Decal gives a plugin only heading + autorun for movement (no strafe / back-step).
    internal class StuckModule : Module
    {
        const double WINDOW_S = 12;      // still for this long = stuck
        const double MOVE_M = 1.5;       // less movement than this counts as "still"
        const double MONSTER_M = 25;     // monsters closer than this are counted as "nearby"
        const double RETRY_S = 90;       // stuck again within this long after a fix = next attempt
        const int MAX_ATTEMPTS = 3;
        const double FREE_M = 10;        // a fix only counts if the waypoint advanced or we got this far from the stuck spot

        VirindiViewService.Controls.HudCheckBox chkOn, chkAuto, chkGiveLs, chkGiveLogout;
        bool autoFix;                    // per character, saved; default off
        bool giveUpLs = true;            // per character, saved: after giving up, recall to lifestone (default)
        bool giveUpLogout;               // per character, saved: ... and log out ~25 s later
        DateTime recallAt = DateTime.MinValue, logoutAt = DateTime.MinValue;
        const string LS_COMMAND = "/lifestone";   // same lifestone recall the Memory guard uses
        string anchorPos;
        int anchorWp = -1;
        DateTime anchorTime = DateTime.MinValue, stuckSince = DateTime.MinValue;
        int episodes;

        // unstick ladder state
        int attempt;                     // attempts in the current episode
        int fixStep = -1;                // -1 = not fixing
        DateTime fixStart, fixEnd = DateTime.MinValue;
        bool awaitingResult, gaveUp;
        string stuckPos = ""; int stuckWp = -1;   // where the current episode got stuck
        string route = "";

        public StuckModule(PluginCore p) : base(p) { }
        public override string Name { get { return "Stuck detection"; } }
        public override string Key { get { return "stuck"; } }

        public override void Startup()
        {
            if (P.View == null) return;
            chkOn = (VirindiViewService.Controls.HudCheckBox)P.View["chkStuck"];
            chkOn.Checked = Enabled;
            chkOn.Change += (s, e) => { Enabled = chkOn.Checked; Settings.Save(P); if (!Enabled) Reset(); };
            chkAuto = (VirindiViewService.Controls.HudCheckBox)P.View["chkStuckAuto"];
            chkAuto.Checked = autoFix;
            chkAuto.Change += (s, e) => { autoFix = chkAuto.Checked; Settings.Save(P); if (autoFix) FixNowIfStuck(); };
            chkGiveLs = (VirindiViewService.Controls.HudCheckBox)P.View["chkStuckLs"];
            chkGiveLs.Checked = giveUpLs;
            chkGiveLs.Change += (s, e) => { giveUpLs = chkGiveLs.Checked; Settings.Save(P); };
            chkGiveLogout = (VirindiViewService.Controls.HudCheckBox)P.View["chkStuckLogout"];
            chkGiveLogout.Checked = giveUpLogout;
            chkGiveLogout.Change += (s, e) => { giveUpLogout = chkGiveLogout.Checked; Settings.Save(P); };
        }

        // Auto-fix switched on while already stuck: start now instead of waiting for a new episode.
        void FixNowIfStuck()
        {
            if (!P.InGame || fixStep >= 0 || stuckSince == DateTime.MinValue || gaveUp) return;
            bool quiet; Signals(out quiet);
            if (!quiet) { P.Chat("Stuck, but VTank is busy or monsters are near - not fixing yet."); return; }
            if (attempt >= MAX_ATTEMPTS) GiveUp(); else StartFix("auto (just switched on)");
        }
        public override void SetEnabled(bool on)
        {
            Enabled = on;
            if (chkOn != null && chkOn.Checked != on) chkOn.Checked = on;
            if (!on) Reset();
        }
        internal void LoadSettings(System.Collections.Generic.Dictionary<string, string> s)
        {
            autoFix = Util.Get(s, "StuckAutoFix") == "1";
            giveUpLs = Util.Get(s, "StuckGiveUpLs") != "0";
            giveUpLogout = Util.Get(s, "StuckGiveUpLogout") == "1";
            if (chkAuto != null) chkAuto.Checked = autoFix;
            if (chkGiveLs != null) chkGiveLs.Checked = giveUpLs;
            if (chkGiveLogout != null) chkGiveLogout.Checked = giveUpLogout;
        }
        internal void SaveSettings(System.Collections.Generic.Dictionary<string, string> d)
        {
            d["StuckAutoFix"] = autoFix ? "1" : "0";
            d["StuckGiveUpLs"] = giveUpLs ? "1" : "0";
            d["StuckGiveUpLogout"] = giveUpLogout ? "1" : "0";
        }
        public override void OnLogin() { Reset(); episodes = 0; attempt = 0; gaveUp = false; recallAt = logoutAt = DateTime.MinValue; }
        public override void OnLogoff() { Reset(); recallAt = logoutAt = DateTime.MinValue; }

        void Reset()
        {
            anchorPos = null; anchorWp = -1; anchorTime = DateTime.MinValue;
            stuckSince = DateTime.MinValue;
            if (fixStep >= 0) { fixStep = -1; try { CoreManager.Current.Actions.SetAutorun(false); } catch { } }
            awaitingResult = false;
        }

        public override void Tick()
        {
            if (!P.InGame) return;
            DateTime now = DateTime.Now;
            // after a give-up: lifestone recall, then (optional) logout
            if (recallAt != DateTime.MinValue && now >= recallAt)
            {
                recallAt = DateTime.MinValue;
                Util.Command(LS_COMMAND);
                Util.Log(P.CharName, P.ServerName, "STUCK-RECALL", "lifestone recall (" + LS_COMMAND + ")" + (giveUpLogout ? ", logout in 25s" : ""));
                if (giveUpLogout) logoutAt = now.AddSeconds(25);
            }
            if (logoutAt != DateTime.MinValue && now >= logoutAt)
            {
                logoutAt = DateTime.MinValue;
                Util.Log(P.CharName, P.ServerName, "STUCK-LOGOUT", "logging out after giving up");
                try { CoreManager.Current.Actions.Logout(); } catch { }
            }
            if (fixStep >= 0) { FixTick(now); return; }   // our own /vt stop must not count as "macro stopped"

            bool? macro = VTank.MacroEnabled;
            int points = ToInt(VTank.Get("NavNumPoints"));
            string navType = Convert.ToString(VTank.Get("NavType"), CultureInfo.InvariantCulture) ?? "";
            // Only a running route can be "stuck": no macro, no route, or following someone = not watched.
            if (macro != true || points <= 0 || navType.IndexOf("Follow", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (stuckSince != DateTime.MinValue) Cleared(now, "macro stopped / route unloaded");
                anchorPos = null; anchorWp = -1; anchorTime = DateTime.MinValue; awaitingResult = false;
                return;
            }
            if (gaveUp) { gaveUp = false; attempt = 0; }  // macro started again after a give-up: fresh start
            if (attempt > 0 && !awaitingResult && stuckSince == DateTime.MinValue && (now - fixEnd).TotalSeconds > RETRY_S) attempt = 0;

            string pos = Position.Now();
            int wp = ToInt(VTank.Get("NavCurrent"));
            // VTank busy (buffing, looting, fighting) = standing still on purpose: restart the window.
            object busy = VTank.Get("Decision_GlobalBusyFlag");
            if (busy is bool && (bool)busy) { anchorPos = pos; anchorWp = wp; anchorTime = now; return; }
            if (anchorPos == null) { anchorPos = pos; anchorWp = wp; anchorTime = now; return; }

            // After a fix: success only when VTank moved FORWARD past the stuck waypoint (the back-off
            // can make it turn back to an earlier one, and the back-off itself moves a few metres -
            // neither is freedom). Forward = up to half the route ahead, so circular routes wrap.
            int fwd = (stuckWp >= 0 && points > 0) ? ((wp - stuckWp) % points + points) % points : 0;
            if (awaitingResult && fwd >= 1 && fwd <= Math.Max(1, points / 2))
            {
                awaitingResult = false;
                string won = "waypoint " + stuckWp + " -> " + wp + string.Format(CultureInfo.InvariantCulture, ", {0:0} m from the stuck spot", Position.Distance(stuckPos, pos));
                Util.Log(P.CharName, P.ServerName, "STUCK-FIXED", "attempt " + attempt + " worked: " + won);
                P.Chat("Unstuck (attempt " + attempt + "): " + won + ".");
                anchorPos = pos; anchorWp = wp; anchorTime = now;
                return;
            }

            double moved = Position.Distance(anchorPos, pos);
            if (moved > MOVE_M || wp != anchorWp)
            {
                string how = wp != anchorWp ? "waypoint " + anchorWp + " -> " + wp : string.Format(CultureInfo.InvariantCulture, "moved {0:0.0} m", moved);
                if (!awaitingResult && stuckSince != DateTime.MinValue) Cleared(now, how);
                anchorPos = pos; anchorWp = wp; anchorTime = now;
                return;
            }

            if (stuckSince == DateTime.MinValue && (now - anchorTime).TotalSeconds >= WINDOW_S)
            {
                stuckSince = anchorTime;
                if (!awaitingResult) { stuckPos = pos; stuckWp = wp; }
                episodes++;
                bool quiet;
                string signals = Signals(out quiet);
                string detail = string.Format(CultureInfo.InvariantCulture,
                    "still {0:0}s at {1}, route {2}; signals: {3}", (now - anchorTime).TotalSeconds, pos, VTank.Nav ?? "?", signals);
                if (awaitingResult) detail = "attempt " + attempt + " did not help; " + detail;
                awaitingResult = false;
                string action = !autoFix ? "[auto-fix off, no action]"
                    : !quiet ? "[not fixing: VTank busy or monsters near]"
                    : attempt >= MAX_ATTEMPTS ? "[giving up]"
                    : "[fixing: attempt " + (attempt + 1) + "]";
                Util.Log(P.CharName, P.ServerName, "STUCK", detail + " " + action);
                P.Chat("Stuck? " + detail + " " + action);
                if (autoFix && quiet)
                {
                    if (attempt >= MAX_ATTEMPTS) GiveUp();
                    else StartFix("auto");
                }
            }
        }

        void Cleared(DateTime now, string how)
        {
            double secs = (now - stuckSince).TotalSeconds;
            Util.Log(P.CharName, P.ServerName, "STUCK-CLEARED", string.Format(CultureInfo.InvariantCulture, "after {0:0}s: {1}", secs, how));
            stuckSince = DateTime.MinValue;
        }

        void StartFix(string why)
        {
            attempt = Math.Min(attempt + 1, MAX_ATTEMPTS);
            route = VTank.Nav ?? "";
            int sp = route.IndexOf(" wp ", StringComparison.Ordinal);
            if (sp > 0) route = route.Substring(0, sp);
            fixStart = DateTime.Now;
            fixStep = 0;
            Util.Log(P.CharName, P.ServerName, "STUCK-FIX", "attempt " + attempt + " (" + why + "): "
                + (attempt == 1 ? "back off" : attempt == 2 ? "back off + jump" : "back off + jump + reload route " + route));
        }

        // One attempt, paced by the 1 s module tick.
        void FixTick(DateTime now)
        {
            var a = CoreManager.Current.Actions;
            double t = (now - fixStart).TotalSeconds;
            if (fixStep == 0) { Util.Command("/vt stop"); a.SetAutorun(false); fixStep = 1; return; }
            if (fixStep == 1 && t >= 1) { a.FaceHeading((a.Heading + 180) % 360, true); fixStep = 2; return; }
            if (fixStep == 2 && t >= 2) { a.SetAutorun(true); fixStep = 3; return; }
            if (fixStep == 3 && t >= 3) { if (attempt >= 2) Util.Command("/vt jump"); fixStep = 4; return; }
            if (fixStep == 4 && t >= 4) { a.SetAutorun(false); fixStep = 5; return; }
            if (fixStep == 5 && t >= 5)
            {
                if (attempt >= 3 && route != "" && route != "(none)")
                    Util.Command("/vt nav load " + (route.EndsWith(".nav", StringComparison.OrdinalIgnoreCase) ? route.Substring(0, route.Length - 4) : route));
                fixStep = 6; return;
            }
            if (fixStep == 6 && t >= 6)
            {
                Util.Command("/vt start");
                fixStep = -1;
                fixEnd = now;
                stuckSince = DateTime.MinValue;
                anchorPos = null;              // fresh 12 s window from here
                awaitingResult = true;
            }
        }

        void GiveUp()
        {
            Util.Command("/vt stop");
            gaveUp = true;
            awaitingResult = false;
            string rt = VTank.Nav ?? "?";
            int sp = rt.IndexOf(" wp ", StringComparison.Ordinal);
            string file = sp > 0 ? rt.Substring(0, sp) : rt;
            Util.Log(P.CharName, P.ServerName, "STUCK-GAVE-UP", "still stuck after " + MAX_ATTEMPTS + " attempts; macro stopped. "
                + file + " waypoint " + stuckWp + " looks unreachable from " + stuckPos + " - fix the route");
            P.Chat("Still stuck after " + MAX_ATTEMPTS + " attempts - macro STOPPED. " + file + " waypoint " + stuckWp
                + " looks unreachable - fix the route, then start VTank."
                + (giveUpLs ? " Recalling to lifestone" + (giveUpLogout ? ", then logging out." : ".") : ""));
            if (giveUpLs) recallAt = DateTime.Now.AddSeconds(3);   // let the macro stop first
        }

        // Everything that might separate "stuck" from "busy": VTank's own busy flag and current
        // rule, client combat mode / busy state, and monsters nearby. quiet = safe to act.
        static string Signals(out bool quiet)
        {
            object busy = VTank.Get("Decision_GlobalBusyFlag");
            int nearby = 0;
            string s = "vtBusy=" + busy + "/" + VTank.Get("Decision_GlobalBusyCount")
                + " vtRulePri=" + VTank.Get("CurrentRulePriority")
                + " meta='" + (VTank.MetaState ?? "?") + "'";
            try
            {
                var a = CoreManager.Current.Actions;
                s += " combat=" + a.CombatMode + " clientBusy=" + a.BusyState;
            }
            catch { }
            try
            {
                int me = CoreManager.Current.CharacterFilter.Id;
                double nearest = double.MaxValue;
                foreach (WorldObject o in CoreManager.Current.WorldFilter.GetByObjectClass(ObjectClass.Monster))
                {
                    double d = CoreManager.Current.WorldFilter.Distance(me, o.Id) * 240.0;
                    if (d < MONSTER_M) nearby++;
                    if (d < nearest) nearest = d;
                }
                s += string.Format(CultureInfo.InvariantCulture, " monsters<{0:0}m={1} nearest={2}", MONSTER_M, nearby,
                    nearest == double.MaxValue ? "none" : nearest.ToString("0", CultureInfo.InvariantCulture) + "m");
            }
            catch { }
            quiet = !(busy is bool && (bool)busy) && nearby == 0;
            return s;
        }

        static int ToInt(object o)
        {
            try { return o == null ? -1 : Convert.ToInt32(o, CultureInfo.InvariantCulture); } catch { return -1; }
        }

        public override bool Command(string cmd, string arg)
        {
            if (cmd == "unstick")
            {
                if (!P.InGame) return true;
                if (fixStep >= 0) { P.Chat("Already trying to unstick."); return true; }
                if (attempt >= MAX_ATTEMPTS) attempt = 0;
                if (!awaitingResult && stuckSince == DateTime.MinValue) { stuckPos = Position.Now(); stuckWp = ToInt(VTank.Get("NavCurrent")); }
                P.Chat("Unstick attempt " + (attempt + 1) + " (manual).");
                StartFix("manual /dw unstick");
                return true;
            }
            if (cmd == "stuckfix" && (arg == "on" || arg == "off"))
            {
                autoFix = arg == "on";
                if (chkAuto != null) chkAuto.Checked = autoFix;
                Settings.Save(P);
                P.Chat("Fix stuck automatically: " + (autoFix ? "ON" : "OFF") + " for " + P.CharName + ".");
                if (autoFix) FixNowIfStuck();
                return true;
            }
            if (cmd == "stuckgiveup" && (arg == "stay" || arg == "ls" || arg == "lslogout"))
            {
                giveUpLs = arg != "stay"; giveUpLogout = arg == "lslogout";
                if (chkGiveLs != null) chkGiveLs.Checked = giveUpLs;
                if (chkGiveLogout != null) chkGiveLogout.Checked = giveUpLogout;
                Settings.Save(P);
                P.Chat("When stuck for good: " + (arg == "stay" ? "stay put" : arg == "ls" ? "recall to lifestone" : "recall to lifestone, then log out") + ".");
                return true;
            }
            return false;
        }

        public override void Status()
        {
            P.Chat(string.Format("Stuck detection {0}, auto-fix {1}: {2} episode(s) this session{3}{4}.",
                Active ? "ON" : Failed ? "STOPPED (error)" : "OFF", autoFix ? "ON" : "OFF", episodes,
                stuckSince != DateTime.MinValue ? ", STUCK now for " + (DateTime.Now - stuckSince).TotalSeconds.ToString("0") + "s" : "",
                attempt > 0 ? ", attempts this episode " + attempt : ""));
        }
    }
}
