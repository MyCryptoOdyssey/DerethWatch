using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Decal.Adapter;

namespace DerethWatch
{
    // Session stats (since this login or the Reset button): XP, luminance earned, killing blows, deaths, rares (all tiers).
    // Each client writes its numbers to state\<server>__<char>.stats.txt every 5 s so one
    // client's Watch strip can show the whole fleet.
    // Sources (checked in ACE source 2026-11-02):
    //   XP    - CharacterFilter.TotalXP (Int64)
    //   Lum   - no Decal property: login PlayerDescription (event 0x0013) Int64 table key 6
    //           AvailableLuminance for the baseline, then PrivateUpdatePropertyInt64 0x02CF key 6.
    //           Only increases count, so spending lum doesn't lower "earned".
    //   Kills - KillerNotification (event 0x01AD), sent to the player who landed the killing blow
    //   Deaths - counted by DeathModule (Decal Death event / VictimNotification 0x01AC)
    //   Rares - system chat "<name> has discovered the <item>!" (Corpse.cs); tier from rares.tsv,
    //           built from ACE LootGenerationFactory_Rare.cs tier lists + world DB names.
    internal class StatsModule : Module
    {
        const string SUFFIX = ".stats.txt";
        internal const string RARES_LOG = @"C:\Games\ACRecovery\rares.log";
        const int PROP_LUM = 6;                        // PropertyInt64.AvailableLuminance

        VirindiViewService.Controls.HudCheckBox chkOn;
        VirindiViewService.Controls.HudStaticText lblSession, lblXp, lblLum, lblKills, lblDeaths, lblRares, lblLastRare;
        int tick;

        DateTime start = DateTime.MinValue;
        long xp0 = -1;
        long lumNow = -1, lumEarned;                   // lumNow = last known AvailableLuminance (-1 = not seen yet)
        string lumSource = "";
        int kills, deaths, rareCount;                          // every rare counts, tier 1 included (a drop that doesn't move the counter confuses)
        string lastRare = "";

        static Dictionary<string, int> rareTier;
        static readonly Regex RareRx = new Regex(@"^(.+?) has discovered the (.+?)!\s*$", RegexOptions.Compiled);

        public StatsModule(PluginCore p) : base(p) { }
        public override string Name { get { return "Stats"; } }
        public override string Key { get { return "stats"; } }

        public override void Startup()
        {
            LoadRareTable();
            if (P.View != null)
            {
                chkOn = (VirindiViewService.Controls.HudCheckBox)P.View["chkStats"];
                chkOn.Checked = Enabled;
                chkOn.Change += (s, e) => { Enabled = chkOn.Checked; Settings.Save(P); UpdateLabels(); };
                lblSession = (VirindiViewService.Controls.HudStaticText)P.View["lblSession"];
                lblXp = (VirindiViewService.Controls.HudStaticText)P.View["lblXp"];
                lblLum = (VirindiViewService.Controls.HudStaticText)P.View["lblLum"];
                lblKills = (VirindiViewService.Controls.HudStaticText)P.View["lblKills"];
                lblDeaths = (VirindiViewService.Controls.HudStaticText)P.View["lblDeaths"];
                lblRares = (VirindiViewService.Controls.HudStaticText)P.View["lblRares"];
                lblLastRare = (VirindiViewService.Controls.HudStaticText)P.View["lblLastRare"];
                var btn = (VirindiViewService.Controls.HudButton)P.View["btnStatsReset"];
                btn.Hit += (s, e) => Guard("reset", () => { ResetSession(); UpdateLabels(); P.Chat("Stats reset for " + P.CharName + "."); });
            }
            // Hooked at startup so the login PlayerDescription (lum baseline) isn't missed.
            CoreManager.Current.EchoFilter.ServerDispatch += OnServer;
            CoreManager.Current.ChatBoxMessage += OnChat;
        }
        public override void Shutdown()
        {
            try { CoreManager.Current.EchoFilter.ServerDispatch -= OnServer; } catch { }
            try { CoreManager.Current.ChatBoxMessage -= OnChat; } catch { }
        }
        public override void SetEnabled(bool on)
        {
            Enabled = on;
            if (chkOn != null && chkOn.Checked != on) chkOn.Checked = on;
        }

        public override void OnLogin() { ResetSession(); }
        public override void OnLogoff() { if (Active) WriteFile(); }

        void ResetSession()
        {
            start = DateTime.Now;
            try { xp0 = CoreManager.Current.CharacterFilter.TotalXP; } catch { xp0 = -1; }
            lumEarned = 0; kills = 0; deaths = 0; rareCount = 0; lastRare = "";
        }

        public override void Tick()
        {
            if (!P.InGame || start == DateTime.MinValue) return;
            if (++tick % 5 != 0) return;
            UpdateLabels();
            WriteFile();
        }

        // ---- network: luminance + kills ----
        void OnServer(object sender, NetworkMessageEventArgs e)
        {
            Guard("network", () =>
            {
                if (!Active || e.Message == null) return;
                int type = e.Message.Type;
                if (type != 0x02CF && type != unchecked((int)0xF7B0)) return;
                byte[] raw = e.Message.RawData;
                if (raw == null || raw.Length < 8) return;
                int off = BitConverter.ToInt32(raw, 0) == type ? 4 : 0;   // RawData may or may not start with the type

                if (type == 0x02CF)
                {
                    // [sequence][uint key][long value] - read from the end so the sequence size doesn't matter
                    if (raw.Length - off < 12) return;
                    int key = BitConverter.ToInt32(raw, raw.Length - 12);
                    if (key != PROP_LUM) return;
                    LumSeen(BitConverter.ToInt64(raw, raw.Length - 8), "update");
                    return;
                }
                // 0xF7B0 game event: [guid][sequence][event type][body]
                if (raw.Length - off < 12) return;
                int evt = BitConverter.ToInt32(raw, off + 8);
                if (evt == 0x01AD) { kills++; return; }
                if (evt == 0x0013) ParseDescription(raw, off + 12);
            });
        }

        void LumSeen(long value, string source)
        {
            if (lumNow >= 0 && value > lumNow) lumEarned += value - lumNow;
            if (lumNow < 0) lumSource = source;        // which way the baseline arrived (login packet or first update)
            lumNow = value;
        }

        // PlayerDescription body: [uint flags][uint weenieType] then, if flags & 1, the Int32 table,
        // then if flags & 0x80 the Int64 table; each table = [ushort count][ushort buckets] + entries.
        void ParseDescription(byte[] raw, int p)
        {
            if (raw.Length < p + 8) return;
            uint flags = BitConverter.ToUInt32(raw, p); p += 8;
            if ((flags & 0x1) != 0)
            {
                if (raw.Length < p + 4) return;
                int n = BitConverter.ToUInt16(raw, p); p += 4 + n * 8;
            }
            if ((flags & 0x80) == 0 || raw.Length < p + 4) return;
            int count = BitConverter.ToUInt16(raw, p); p += 4;
            for (int i = 0; i < count && raw.Length >= p + 12; i++, p += 12)
                if (BitConverter.ToInt32(raw, p) == PROP_LUM) { LumSeen(BitConverter.ToInt64(raw, p + 4), "login"); return; }
        }

        internal void Died() { deaths++; UpdateLabels(); }

        // ---- chat: rares ----
        void OnChat(object sender, ChatTextInterceptEventArgs e)
        {
            Guard("chat", () =>
            {
                if (!Active || !P.InGame || e.Text == null) return;
                Match m = RareRx.Match(e.Text.Trim());
                if (!m.Success || m.Groups[1].Value != P.CharName) return;   // each client reports only its own rares
                RareFound(m.Groups[2].Value);
            });
        }

        int TierOf(string item)
        {
            int tier;
            return rareTier != null && rareTier.TryGetValue(item, out tier) ? tier : 0;   // 0 = not in the table (shown as T?)
        }

        void RareFound(string item)
        {
            int tier = TierOf(item);
            string t = tier == 0 ? "?" : tier.ToString(CultureInfo.InvariantCulture);
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + P.CharName + " | " + P.ServerName + " | RARE | tier " + t + " | " + item;
            try { File.AppendAllText(RARES_LOG, line + Environment.NewLine); } catch (Exception ex) { Debug.WriteLine(ex); }
            rareCount++;
            lastRare = item + " (T" + t + ")";
            Util.Log(P.CharName, P.ServerName, "RARE", "tier " + (tier == 0 ? "? (not in table)" : t) + ": " + item);
            P.Chat("RARE found: " + lastRare + ". Logged in " + RARES_LOG);
            UpdateLabels();
        }

        static void LoadRareTable()
        {
            if (rareTier != null) return;
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (Stream s = typeof(StatsModule).Assembly.GetManifestResourceStream("DerethWatch.rares.tsv"))
            {
                if (s != null)
                    using (var r = new StreamReader(s))
                    {
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            int tab = line.IndexOf('\t');
                            int t;
                            if (tab > 0 && int.TryParse(line.Substring(0, tab), out t)) d[line.Substring(tab + 1).Trim()] = t;
                        }
                    }
            }
            rareTier = d;
        }

        // ---- output ----
        double Hours { get { return start == DateTime.MinValue ? 0 : (DateTime.Now - start).TotalHours; } }
        long XpSession
        {
            get
            {
                if (xp0 < 0) return 0;
                try { return Math.Max(0, CoreManager.Current.CharacterFilter.TotalXP - xp0); } catch { return 0; }
            }
        }
        string PerHour(double v) { return Hours < 1.0 / 60 ? "--" : Short(v / Hours); }

        internal static string Short(double v)
        {
            if (v >= 1e9) return (v / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + "B";
            if (v >= 1e6) return (v / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            if (v >= 1e3) return (v / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + "k";
            return v.ToString("0", CultureInfo.InvariantCulture);
        }

        void UpdateLabels()
        {
            if (lblXp == null) return;
            bool on = Active && P.InGame && start != DateTime.MinValue;
            TimeSpan up = on ? DateTime.Now - start : TimeSpan.Zero;
            Set(lblSession, !on ? (Active ? "" : "Stats off") : string.Format(CultureInfo.InvariantCulture, "Session {0}h {1:00}m", (int)up.TotalHours, up.Minutes));
            Set(lblXp, !on ? "" : Short(XpSession) + "   (" + PerHour(XpSession) + "/h)");
            Set(lblLum, !on ? "" : lumNow < 0 ? "not seen yet" : Short(lumEarned) + "   (" + PerHour(lumEarned) + "/h)");
            Set(lblKills, !on ? "" : kills + "   (" + PerHour(kills) + "/h)");
            Set(lblDeaths, !on ? "" : deaths.ToString(CultureInfo.InvariantCulture));
            Set(lblRares, !on ? "" : rareCount.ToString(CultureInfo.InvariantCulture));
            bool had = lblLastRare != null && lblLastRare.Text != "";
            Set(lblLastRare, !on || lastRare == "" ? "" : "Last: " + lastRare);
            if (lblLastRare != null && had != (lblLastRare.Text != "")) P.FitTab();
        }
        internal bool HasLastRare { get { return lblLastRare != null && lblLastRare.Text != ""; } }
        static void Set(VirindiViewService.Controls.HudStaticText l, string s) { if (l != null && l.Text != s) l.Text = s; }

        void WriteFile()
        {
            if (P.CharName == "") return;
            int level = 0;
            try { level = CoreManager.Current.CharacterFilter.Level; } catch { }
            bool? macro = VTank.MacroEnabled;
            var d = new Dictionary<string, string>
            {
                { "Character", P.CharName }, { "Server", P.ServerName }, { "Level", level.ToString(CultureInfo.InvariantCulture) },
                { "Minutes", (Hours * 60).ToString("0", CultureInfo.InvariantCulture) },
                { "Xp", XpSession.ToString(CultureInfo.InvariantCulture) }, { "XpPerHour", PerHour(XpSession) },
                { "Lum", lumNow < 0 ? "?" : lumEarned.ToString(CultureInfo.InvariantCulture) }, { "LumPerHour", lumNow < 0 ? "?" : PerHour(lumEarned) },
                { "Kills", kills.ToString(CultureInfo.InvariantCulture) }, { "KillsPerHour", PerHour(kills) },
                { "Deaths", deaths.ToString(CultureInfo.InvariantCulture) },
                { "Rares", rareCount.ToString(CultureInfo.InvariantCulture) }, { "LastRare", lastRare },
                { "Macro", macro == null ? "?" : macro.Value ? "ON" : "OFF" }, { "Nav", VTank.Nav ?? "" },
                { "Pid", Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) },
                { "UpdatedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
            };
            Util.WriteKv(Path.Combine(Util.StateDir, Util.CharKey(P.ServerName, P.CharName) + SUFFIX), d);
        }

        public override bool Command(string cmd, string arg)
        {
            if (cmd == "stats" && arg == "reset") { ResetSession(); UpdateLabels(); P.Chat("Stats reset for " + P.CharName + "."); return true; }
            if (cmd == "stats" && arg == "") { Status(); return true; }
            if (cmd == "rares" && arg.StartsWith("test "))
            {
                // Dry run of the announcement filter: /dw rares test Lich's Pearl - nothing is logged or counted.
                string item = arg.Substring(5).Trim();
                int tier = TierOf(item);
                P.Chat("Rare test '" + item + "': " + (tier == 0 ? "not in the tier table (would show as T?)" : "tier " + tier) + ".");
                return true;
            }
            return false;
        }

        public override void Status()
        {
            P.Chat(string.Format(CultureInfo.InvariantCulture,
                "Stats {0}: {1:0} min this session, XP {2} ({3}/h), Lum {4}, killing blows {5} ({6}/h), deaths {9}, rares {7}{8}.",
                Active ? "ON" : Failed ? "STOPPED (error)" : "OFF", Hours * 60, Short(XpSession), PerHour(XpSession),
                lumNow < 0 ? "not seen yet" : Short(lumEarned) + " (" + PerHour(lumEarned) + "/h, now " + Short(lumNow) + ", baseline from " + lumSource + ")",
                kills, PerHour(kills), rareCount, lastRare == "" ? "" : "; last " + lastRare, deaths));
        }
    }
}
