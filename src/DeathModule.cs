using System;
using System.Text;
using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace DerethWatch
{
    // Deaths. Every death: DIED line in the shared log (where + the server's death message), the Stats
    // deaths counter, and any unstick attempt is cancelled. With "Stop VT on death" (this module's
    // switch, default on) the VTank macro is stopped too, so a respawned character stands at the
    // lifestone instead of running its route from there (and stuck detection, which only watches a
    // running macro, stays quiet until VTank is started again). Turn it off for metas that handle
    // death themselves (rebuff, walk back).
    // Sources (de-duplicated, whichever arrives first):
    //   - PrivateUpdatePropertyInt 0x02CD key 43 NumDeaths: sent by ACE Player.Die() on EVERY death
    //   - Decal CharacterFilter.Death / VictimNotification 0x01AC: carries the death message, but ACE
    //     sends it from OnDeath only, which @die (suicide) skips - so it can't be the only signal.
    internal class DeathModule : Module
    {
        VirindiViewService.Controls.HudCheckBox chkOn;
        const int PROP_NUM_DEATHS = 43;          // PropertyInt.NumDeaths
        DateTime lastDeath = DateTime.MinValue;
        int numDeaths = -1;                      // last NumDeaths seen (-1 = not yet)
        int deaths;

        public DeathModule(PluginCore p) : base(p) { }
        public override string Name { get { return "Stop VT on death"; } }
        public override string Key { get { return "death"; } }

        public override void Startup()
        {
            if (P.View != null)
            {
                chkOn = (VirindiViewService.Controls.HudCheckBox)P.View["chkDeathStop"];
                chkOn.Checked = Enabled;
                chkOn.Change += (s, e) => { Enabled = chkOn.Checked; Settings.Save(P); };
            }
            CoreManager.Current.CharacterFilter.Death += OnDeathEvent;
            CoreManager.Current.EchoFilter.ServerDispatch += OnServer;
        }
        public override void Shutdown()
        {
            try { CoreManager.Current.CharacterFilter.Death -= OnDeathEvent; } catch { }
            try { CoreManager.Current.EchoFilter.ServerDispatch -= OnServer; } catch { }
        }
        public override void SetEnabled(bool on)
        {
            Enabled = on;
            if (chkOn != null && chkOn.Checked != on) chkOn.Checked = on;
        }
        public override void OnLogin() { deaths = 0; lastDeath = DateTime.MinValue; numDeaths = -1; }

        void OnDeathEvent(object sender, DeathEventArgs e)
        {
            Guard("death", () => Died(e == null ? "" : e.Text));
        }

        void OnServer(object sender, NetworkMessageEventArgs e)
        {
            Guard("network", () =>
            {
                if (e.Message == null) return;
                int type = e.Message.Type;
                if (type != 0x02CD && type != unchecked((int)0xF7B0)) return;
                byte[] raw = e.Message.RawData;
                if (raw == null || raw.Length < 12) return;
                int off = BitConverter.ToInt32(raw, 0) == type ? 4 : 0;
                if (type == 0x02CD)
                {
                    // [sequence][uint key][int value] - read from the end so the sequence size doesn't matter
                    if (BitConverter.ToInt32(raw, raw.Length - 8) != PROP_NUM_DEATHS) return;
                    // ACE only sends this update from Die(), so any NumDeaths update in game = a death
                    // (the login value arrives inside PlayerDescription, not as an update)
                    int n2 = BitConverter.ToInt32(raw, raw.Length - 4);
                    if (numDeaths < 0 || n2 > numDeaths) { numDeaths = n2; Died(""); }
                    return;
                }
                if (raw.Length < 16) return;
                if (raw.Length - off < 14 || BitConverter.ToInt32(raw, off + 8) != 0x01AC) return;
                // body = String16L: [ushort length][bytes]
                int p = off + 12, n = BitConverter.ToUInt16(raw, p);
                string text = raw.Length >= p + 2 + n ? Encoding.Default.GetString(raw, p + 2, n) : "";
                Died(text);
            });
        }

        // Runs once per death, whichever source arrives first.
        void Died(string text)
        {
            if (Failed || !P.InGame) return;
            DateTime now = DateTime.Now;
            if ((now - lastDeath).TotalSeconds < 10) return;
            lastDeath = now;
            deaths++;
            text = (text ?? "").Trim();
            bool? macro = VTank.MacroEnabled;
            bool stop = Enabled && macro == true;
            if (stop) Util.Command("/vt stop");
            if (P.Stuck != null) P.Stuck.OnDeath();
            if (P.Stats != null) P.Stats.Died();
            string what = stop ? "VTank macro STOPPED (start it again when ready)"
                : macro == true ? "VTank macro left running (Stop VT on death is off)"
                : "VTank macro was not running";
            Util.Log(P.CharName, P.ServerName, "DIED", (text == "" ? "" : "'" + text + "' ") + "at " + Position.Now()
                + ", route " + (VTank.Nav ?? "?") + "; " + what);
            P.Chat("Died" + (text == "" ? "" : " (" + text + ")") + ". " + what + ".");
        }

        public override void Status()
        {
            P.Chat("Stop VT on death " + (Active ? "ON" : Failed ? "STOPPED (error)" : "OFF") + ": " + deaths + " death(s) this session.");
        }
    }
}
