using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Decal.Adapter;

namespace DerethWatch
{
    // Character Views (was "Fleet view", the original DerethWatch). An eye toggle on the panel. Click: every AC client
    // un-patches (UB Video Patch off) and a live DWM thumbnail strip opens. Click again: strip
    // closes, fleet re-patches to near-zero cost. Below this class the code is unchanged from v1.
    internal class FleetModule : Module
    {
        static readonly string[] CMDS_ON  = { "/ub videopatch disable",
                                              "/ub opt set Plugin.VideoPatchFocus false" };
        static readonly string[] CMDS_OFF = { "/ub videopatch enable",
                                              "/ub opt set Plugin.VideoPatchFocus true" };
        internal static readonly uint WM_FE = Native.RegisterWindowMessage("DerethWatchToggleMsg");
        VirindiViewService.Controls.HudCheckBox chk;
        VirindiViewService.Controls.HudCheckBox chkClientOnly;
        VirindiViewService.Controls.HudCombo cboMode, cboLayout;
        // Per character, saved: what the strip shows - Text / Text + Client View / Client View
        // (Client View = the live picture of each client) - and whether it runs across (Horizontal) or down (Vertical).
        internal const int MODE_TEXT = 0, MODE_TEXT_PICS = 1, MODE_PICS = 2;
        static readonly string[] MODE_NAMES = { "Text", "Text + Client View", "Client View" };
        int mode = MODE_TEXT_PICS;
        bool vertical;
        StripForm strip;
        Thread stripThread;
        CtlWindow ctl;
        bool eyes = false;
        bool syncing = false;
        int myPid = Process.GetCurrentProcess().Id;
        IntPtr myClientHwnd = IntPtr.Zero;

        public FleetModule(PluginCore p) : base(p) { }
        public override string Name { get { return "Character Views"; } }
        public override string Key { get { return "fleet"; } }

        public override void Startup()
        {
            if (P.View == null) return;
            chk = (VirindiViewService.Controls.HudCheckBox)P.View["chkEye"];
            chk.Change += new EventHandler(Chk_Change);
            chkClientOnly = (VirindiViewService.Controls.HudCheckBox)P.View["chkClientOnly"];
            chkClientOnly.Checked = true;
            chkClientOnly.Change += new EventHandler(ChkClientOnly_Change);
            cboMode = (VirindiViewService.Controls.HudCombo)P.View["cboMode"];
            foreach (string m in MODE_NAMES) cboMode.AddItem(m, null);
            cboMode.Current = mode;
            cboMode.Change += (s, e) => Guard("show", () =>
            {
                if (syncing || cboMode.Current < 0) return;
                mode = cboMode.Current;
                Settings.Save(P);
                if (eyes && strip != null) { CloseStrip(); OpenStrip(); }   // swap the open strip's style now
            });
            cboLayout = (VirindiViewService.Controls.HudCombo)P.View["cboLayout"];
            cboLayout.AddItem("Horizontal", null); cboLayout.AddItem("Vertical", null);
            cboLayout.Current = vertical ? 1 : 0;
            cboLayout.Change += (s, e) => Guard("layout", () =>
            {
                if (syncing || cboLayout.Current < 0) return;
                vertical = cboLayout.Current == 1;
                Settings.Save(P);
                if (eyes && strip != null) { CloseStrip(); OpenStrip(); }
            });
            ctl = new CtlWindow(this);
            // cache OUR top-level client window once; used for chat delivery both ways
            foreach (var w in Native.AcWindows())
            {
                uint wpid; Native.GetWindowThreadProcessId(w.Key, out wpid);
                if (wpid == (uint)myPid) { myClientHwnd = w.Key; break; }
            }
            if (myClientHwnd == IntPtr.Zero) myClientHwnd = P.DecalHwnd;
        }
        public override void Shutdown()
        {
            CloseStrip();
            if (chk != null) chk.Change -= new EventHandler(Chk_Change);
            if (ctl != null) { ctl.DestroyHandle(); ctl = null; }
        }
        public override void SetEnabled(bool on)
        {
            Enabled = on;
            if (!on && eyes) OnRemote(false, myPid);   // switching the module off closes the strip
        }
        public override void Status()
        {
            P.Chat("Character Views " + (Active ? "ON" : Failed ? "STOPPED (error)" : "OFF") + ": strip " + (eyes ? "open" : "closed") + ".");
        }
        void ChkClientOnly_Change(object sender, EventArgs e)
        {
            var s = strip;
            if (s != null) s.ClientOnly = chkClientOnly.Checked;
        }
        void CloseStrip()
        {
            var s = strip;
            var t = stripThread;
            if (s == null) { stripThread = null; return; }
            try
            {
                if (s.IsHandleCreated && !s.IsDisposed)
                {
                    s.BeginInvoke(new MethodInvoker(() =>
                    {
                        try { s.ForceClose(); } catch { }
                        try { Application.ExitThread(); } catch { }
                    }));
                }
            }
            catch (Exception ex) { Native.Log("CloseStrip EX: " + ex.Message); }
            // Wait for the STA thread to actually exit before letting a new strip start
            try { if (t != null && !t.Join(1500)) Native.Log("stripThread did not exit in 1500ms"); } catch { }
            strip = null;
            stripThread = null;
        }
        void Chk_Change(object sender, EventArgs e)
        {
            FeLog.W("Chk_Change fired, syncing=" + syncing + " chk.Checked=" + chk.Checked);
            if (syncing) return;
            if (!Active) { syncing = true; chk.Checked = false; syncing = false; P.Chat("Character Views is off for this character (/dw on views)."); return; }
            bool want = chk.Checked;
            FeLog.W("  local OnRemote + broadcast, want=" + want);
            Guard("toggle", () => OnRemote(want, myPid));                      // guaranteed local
            Native.PostMessage((IntPtr)0xFFFF, WM_FE, (IntPtr)(want ? 1 : 0), (IntPtr)myPid);   // bonus
        }
        internal void Remote(bool state, int initiatorPid)
        {
            if (!Active) return;
            Guard("remote toggle", () => OnRemote(state, initiatorPid));
        }
        void OnRemote(bool state, int initiatorPid)
        {
            eyes = state;
            if (chk != null) { syncing = true; chk.Checked = eyes; syncing = false; }
            CloseStrip();
            // EVERY instance types the commands into its own client window -
            // identical path to manual typing, which works on every UB build
            {
                string[] cmds = eyes ? CMDS_ON : CMDS_OFF;
                Native.Log("OnRemote state=" + eyes + " initiator=" + initiatorPid + " cmds=" + cmds.Length);
                int i = 0;
                foreach (string c in cmds)
                {
                    Native.Log("fire[" + (++i) + "]: " + c);
                    try { Native.FireCommandLineText(c); Native.Log("  ok"); }
                    catch (Exception ex) { Native.Log("  EX: " + ex.Message); }
                }
            }
            if (initiatorPid != myPid) return;      // only the initiator hosts the strip
            if (eyes) OpenStrip();
        }

        internal void LoadSettings(Dictionary<string, string> s)
        {
            int m;
            if (int.TryParse(Util.Get(s, "StripMode"), out m) && m >= MODE_TEXT && m <= MODE_PICS) mode = m;
            else mode = Util.Get(s, "StripTextOnly") == "1" ? MODE_TEXT : MODE_TEXT_PICS;   // earlier build's checkbox
            vertical = Util.Get(s, "StripVertical") == "1";
            syncing = true;
            if (cboMode != null) cboMode.Current = mode;
            if (cboLayout != null) cboLayout.Current = vertical ? 1 : 0;
            syncing = false;
        }
        internal void SaveSettings(Dictionary<string, string> d) { d["StripMode"] = mode.ToString(); d["StripVertical"] = vertical ? "1" : "0"; }

        public override bool Command(string cmd, string arg)
        {
            if (cmd != "strip" || arg != "reset") return false;
            StripForm.ResetPositions();
            P.Chat("Character Views strip: saved positions cleared - back to bottom-centre.");
            if (eyes && strip != null) { CloseStrip(); OpenStrip(); }
            return true;
        }

        void OpenStrip()
        {
            {
                var wins = Native.AcWindows();
                IntPtr hostHwnd = P.DecalHwnd;
                bool clientOnly = (chkClientOnly == null) || chkClientOnly.Checked;
                int show = mode; bool vert = vertical;
                var stripReady = new System.Threading.ManualResetEventSlim(false);
                stripThread = new Thread(() =>
                {
                    try
                    {
                        strip = new StripForm(wins, hostHwnd, show, vert);
                        strip.ClientOnly = clientOnly;
                        strip.HandleCreated += (s, e) => stripReady.Set();
                        strip.Show();
                        Application.Run();               // pump this thread's messages
                        try { strip.Dispose(); } catch { }
                    }
                    catch (Exception ex) { Util.Log(P.CharName, P.ServerName, "MODULE-ERROR", "Character Views strip thread: " + ex.Message); }
                    strip = null;
                });
                stripThread.SetApartmentState(ApartmentState.STA);
                stripThread.IsBackground = true;
                stripThread.Start();
                stripReady.Wait(2000);
            }
        }
    }

    class CtlWindow : NativeWindow
    {
        FleetModule owner;
        public CtlWindow(FleetModule o) { owner = o; CreateHandle(new CreateParams()); }
        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if ((uint)m.Msg == FleetModule.WM_FE)
                owner.Remote(m.WParam != IntPtr.Zero, m.LParam.ToInt32());
            base.WndProc(ref m);
        }
    }

    static class Native
    {
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
        public delegate bool EnumProc(IntPtr hWnd, IntPtr lp);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        public static void FocusWindow(IntPtr target)
        {
            BringWindowToTop(target);
            SetForegroundWindow(target);
        }
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool r);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr h, int f, StringBuilder exe, ref int size);
        [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        public static void Log(string s) {
            try { Decal.Adapter.CoreManager.Current.Actions.AddChatText("[DW] " + s, 5); } catch { }
        }
        [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref ThumbProps p);
        [StructLayout(LayoutKind.Sequential)]
        public struct ThumbProps
        {
            public int dwFlags; public RECT rcDestination; public RECT rcSource;
            public byte opacity; public bool fVisible; public bool fSourceClientAreaOnly;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int L, T, R, B; }
        public const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_CHAR = 0x102;
        public const int VK_RETURN = 0x0D;

        public static List<KeyValuePair<IntPtr, string>> AcWindows()
        {
            var list = new List<KeyValuePair<IntPtr, string>>();
            EnumWindows((h, lp) =>
            {
                if (!IsWindowVisible(h)) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                IntPtr p = OpenProcess(0x1000, false, pid);
                if (p != IntPtr.Zero)
                {
                    var sb = new StringBuilder(1024); int n = 1024;
                    if (QueryFullProcessImageName(p, 0, sb, ref n) &&
                        sb.ToString().ToLower().EndsWith("acclient.exe"))
                    {
                        var t = new StringBuilder(256);
                        GetWindowText(h, t, 256);
                        string title = t.ToString();
                        RECT wr; GetWindowRect(h, out wr);
                        bool ours = title.StartsWith("DerethWatch");
                        bool tiny = (wr.R - wr.L) < 300 || (wr.B - wr.T) < 200;
                        if (!ours && !tiny)
                            list.Add(new KeyValuePair<IntPtr, string>(h, title));
                    }
                    CloseHandle(p);
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        static System.Reflection.MethodInfo _raiseCommandLineText;
        static object _coreManagerInstance;
        public static void FireCommandLineText(string text)
        {
            // Raise CoreManager.CommandLineText so plugin-registered handlers
            // (like UB's Core_CommandLineText) see the command. Uses reflection
            // to invoke the event's delegate field directly.
            try
            {
                if (_raiseCommandLineText == null)
                {
                    _coreManagerInstance = Decal.Adapter.CoreManager.Current;
                    var t = _coreManagerInstance.GetType();
                    // events are stored as private delegate fields with the same name
                    var fld = t.GetField("myChatParserIntercept", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (fld == null && t.BaseType != null)
                        fld = t.BaseType.GetField("myChatParserIntercept", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (fld == null)
                    {
                        foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                            if (f.FieldType.Name.Contains("EventHandler") && f.Name == "myChatParserIntercept") { fld = f; break; }
                    }
                    if (fld != null)
                    {
                        var del = fld.GetValue(_coreManagerInstance) as Delegate;
                        if (del != null) _raiseCommandLineText = del.GetType().GetMethod("Invoke");
                    }
                }
                var argsType = typeof(Decal.Adapter.ChatParserInterceptEventArgs);
                var ctor = argsType.GetConstructor(
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                    null, new[] { typeof(string), typeof(bool) }, null);
                var args = (Decal.Adapter.ChatParserInterceptEventArgs)ctor.Invoke(new object[] { text, false });
                var t2 = _coreManagerInstance.GetType();
                var f2 = t2.GetField("myChatParserIntercept", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (f2 == null && t2.BaseType != null)
                    f2 = t2.BaseType.GetField("myChatParserIntercept", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (f2 != null)
                {
                    var delegateNow = f2.GetValue(_coreManagerInstance) as Delegate;
                    if (delegateNow != null)
                        delegateNow.DynamicInvoke(_coreManagerInstance, args);
                }
            }
            catch (Exception ex) { Log("FireCommandLineText EX: " + ex.Message); }
        }
        public static void SendChat(IntPtr h, string text)
        {
            PostMessage(h, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
            PostMessage(h, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero);
            Thread.Sleep(140);
            foreach (char c in text)
            { PostMessage(h, WM_CHAR, (IntPtr)c, IntPtr.Zero); Thread.Sleep(5); }
            Thread.Sleep(60);
            PostMessage(h, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
            PostMessage(h, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero);
        }
    }

    static class FeLog
    {
        static readonly object lk = new object();
        static readonly string path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DerethWatch.log");
        public static void W(string msg)
        {
            try { lock (lk) System.IO.File.AppendAllText(path,
                DateTime.Now.ToString("HH:mm:ss.fff") + " pid=" + Process.GetCurrentProcess().Id + " " + msg + "\r\n"); } catch { }
        }
    }

    // ---------- the preview strip ----------
    public class StripForm : Form
    {
        List<IntPtr> thumbs = new List<IntPtr>();
        List<KeyValuePair<Rectangle, IntPtr>> slices = new List<KeyValuePair<Rectangle, IntPtr>>();
        List<KeyValuePair<Rectangle, IntPtr>> dests = new List<KeyValuePair<Rectangle, IntPtr>>();
        int active = -1;
        public bool ClientOnly = true;
        public void ForceClose()
        {
            Native.Log("ForceClose enter, thumbs=" + thumbs.Count);
            // 1) stop the timer so it never re-registers or re-invalidates
            try { if (watch != null) { watch.Stop(); watch.Dispose(); watch = null; Native.Log("  watch stopped"); } } catch (Exception ex) { Native.Log("  watch EX: " + ex.Message); }
            // 2) hide the host window FIRST so DWM stops compositing our thumbnails
            try { Hide(); Native.Log("  Hide() done"); } catch (Exception ex) { Native.Log("  Hide EX: " + ex.Message); }
            // 3) hide each thumbnail explicitly (fVisible=false) before unregistering
            try
            {
                foreach (var t in thumbs)
                {
                    var p = new Native.ThumbProps();
                    p.dwFlags = 0x8;   // TNP_VISIBLE only
                    p.fVisible = false;
                    try { Native.DwmUpdateThumbnailProperties(t, ref p); } catch { }
                    try { Native.DwmUnregisterThumbnail(t); } catch { }
                }
                thumbs.Clear();
                Native.Log("  thumbs hidden+freed");
            }
            catch (Exception ex) { Native.Log("  thumb EX: " + ex.Message); }
            // 4) close and dispose the host, ending its message pump
            try { Close(); Native.Log("  Close() done"); } catch (Exception ex) { Native.Log("  Close EX: " + ex.Message); }
            try { Dispose(); Native.Log("  Dispose() done"); } catch (Exception ex) { Native.Log("  Dispose EX: " + ex.Message); }
            try { Application.ExitThread(); Native.Log("  ExitThread called"); } catch (Exception ex) { Native.Log("  ExitThread EX: " + ex.Message); }
        }
        System.Collections.Generic.HashSet<IntPtr> clientSet = new System.Collections.Generic.HashSet<IntPtr>();
        IntPtr host;
        System.Windows.Forms.Timer watch;
        public StripForm(List<KeyValuePair<IntPtr, string>> wins, IntPtr hostHwnd, int mode, bool vertical)
        {
            host = hostHwnd;
            watch = new System.Windows.Forms.Timer(); watch.Interval = 150;
            watch.Tick += (s, e) =>
            {
                try
                {
                    IntPtr fg = Native.GetForegroundWindow();
                    bool show = !Native.IsIconic(host);
                    if (ClientOnly) show = show && clientSet.Contains(fg);
                    Visible = show;
                    int a = -1;
                    for (int i = 0; i < dests.Count; i++) if (dests[i].Value == fg) a = i;
                    if (a != active) { active = a; Invalidate(); }
                }
                catch { }
            };
            watch.Start();
            Text = "DerethWatchStrip";
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual; BackColor = Color.FromArgb(16, 16, 16);
            DoubleBuffered = true;
            var scr = Screen.PrimaryScreen.WorkingArea;
            int n = Math.Max(1, wins.Count);
            textMode = mode == FleetModule.MODE_TEXT;
            bool caps = mode != FleetModule.MODE_PICS;            // captions under the pictures (Text + Client View)
            posKey = (textMode ? "Text" : caps ? "Pic" : "PicOnly") + (vertical ? "V" : "H");
            int W, H, tw = 0, th = 0, cellH = 0;
            if (textMode)
            {
                if (vertical) { W = TEXT_NAME_W + TEXT_CAP_W + 28; H = 8 + n * TEXT_ROW_H; }
                else { W = 4 + n * (TEXT_CELL_W + 4); H = TEXT_CELL_H + 8; }
            }
            else
            {
                int chrome = caps ? 44 : 26;                      // name row + gaps (+ caption row)
                if (vertical)
                {
                    // one column; shrink the pictures if the stack would be taller than the screen
                    tw = 320; th = tw * 9 / 16;
                    int maxTh = (scr.Height - 40) / n - chrome;
                    if (th > maxTh) { th = Math.Max(60, maxTh); tw = th * 16 / 9; }
                    cellH = th + chrome;
                    W = tw + 16; H = cellH * n;
                }
                else
                {
                    tw = Math.Min(380, (scr.Width - 60 - 8 * (n - 1)) / n); th = tw * 9 / 16;
                    cellH = th + chrome;
                    W = tw * n + 8 * (n - 1) + 16; H = cellH;
                }
            }
            var home = vertical
                ? new Rectangle(scr.Right - W - 10, scr.Top + (scr.Height - H) / 2, W, H)      // vertical: right edge, centred
                : new Rectangle(scr.Left + (scr.Width - W) / 2, scr.Bottom - H - 10, W, H);   // horizontal: bottom-centre
            Bounds = SavedBounds(home);
            Load += (s, e) =>
            {
                foreach (var w in wins) clientSet.Add(w.Key);
                if (textMode) { BuildTextCells(wins, vertical); StartCaptions(); return; }
                for (int i = 0; i < wins.Count; i++)
                {
                    int x0 = vertical ? 8 : 8 + i * (tw + 8), y0 = vertical ? i * cellH : 0;
                    var lbl = new Label
                    {
                        Text = CharName(wins[i].Value), ForeColor = Color.FromArgb(185, 163, 90),
                        BackColor = BackColor, Font = new Font("Segoe UI", 8, FontStyle.Bold),
                        Bounds = new Rectangle(x0, y0 + 2, tw, 14), TextAlign = ContentAlignment.MiddleCenter
                    };
                    Controls.Add(lbl);
                    HookDrag(lbl, wins[i].Key);
                    IntPtr tid;
                    if (Native.DwmRegisterThumbnail(Handle, wins[i].Key, out tid) == 0)
                    {
                        var p = new Native.ThumbProps
                        {
                            dwFlags = 0x1 | 0x8 | 0x10,
                            rcDestination = new Native.RECT { L = x0, T = y0 + 18, R = x0 + tw, B = y0 + 18 + th },
                            fVisible = true, fSourceClientAreaOnly = true
                        };
                        Native.DwmUpdateThumbnailProperties(tid, ref p);
                        thumbs.Add(tid);
                        dests.Add(new KeyValuePair<Rectangle, IntPtr>(new Rectangle(x0, y0 + 18, tw, th), wins[i].Key));
                    }
                    slices.Add(new KeyValuePair<Rectangle, IntPtr>(new Rectangle(x0, y0, tw, cellH), wins[i].Key));
                    if (!caps) continue;

                    // caption under the picture, filled from that client's stats file (matched by process id)
                    var cap = new CaptionLabel
                    {
                        Text = "", ForeColor = CapNormal, BackColor = BackColor, Font = new Font("Segoe UI", 8),
                        Bounds = new Rectangle(x0, y0 + 18 + th + 3, tw, CAPTION_H), TextAlign = ContentAlignment.MiddleCenter
                    };
                    Controls.Add(cap);
                    uint wpid; Native.GetWindowThreadProcessId(wins[i].Key, out wpid);
                    captions.Add(new KeyValuePair<uint, Label>(wpid, cap));
                    HookDrag(cap, wins[i].Key);
                }
                if (caps) StartCaptions();
            };
            HookDrag(this, IntPtr.Zero);                 // the strip itself: the client is found from the click point
            FormClosed += (s, e) => { if (watch != null) watch.Stop(); if (capTimer != null) capTimer.Stop(); foreach (var t in thumbs) Native.DwmUnregisterThumbnail(t); };
        }

        // ---- Text: no live pictures. Vertical = one row per client ("NAME   Lv 64 | ... | VT");
        // horizontal = one cell per client side by side (name over caption). Text is inset TEXT_PAD so the
        // active client's gold frame (3 px pen on the cell edge) is never covered by a label.
        const int TEXT_ROW_H = 24, TEXT_PAD = 4, TEXT_NAME_W = 110, TEXT_CAP_W = 400;
        const int TEXT_CELL_W = 300, TEXT_CELL_H = 44;
        bool textMode;
        void BuildTextCells(List<KeyValuePair<IntPtr, string>> wins, bool vertical)
        {
            for (int i = 0; i < wins.Count; i++)
            {
                Rectangle cell, nameR, capR;
                ContentAlignment align;
                if (vertical)
                {
                    int y = 4 + i * TEXT_ROW_H;
                    cell = new Rectangle(4, y + 1, Width - 9, TEXT_ROW_H - 2);
                    nameR = new Rectangle(12, y + TEXT_PAD, TEXT_NAME_W, TEXT_ROW_H - 2 * TEXT_PAD);
                    capR = new Rectangle(12 + TEXT_NAME_W, y + TEXT_PAD, TEXT_CAP_W, TEXT_ROW_H - 2 * TEXT_PAD);
                    align = ContentAlignment.MiddleLeft;
                }
                else
                {
                    int x = 4 + i * (TEXT_CELL_W + 4);
                    cell = new Rectangle(x, 5, TEXT_CELL_W - 1, TEXT_CELL_H - 2);
                    nameR = new Rectangle(x + 8, 4 + TEXT_PAD + 2, TEXT_CELL_W - 16, 16);
                    capR = new Rectangle(x + 8, 4 + TEXT_PAD + 18, TEXT_CELL_W - 16, 16);
                    align = ContentAlignment.MiddleCenter;
                }
                var name = new Label
                {
                    Text = CharName(wins[i].Value), ForeColor = Color.FromArgb(185, 163, 90), BackColor = BackColor,
                    Font = new Font("Segoe UI", 8, FontStyle.Bold), Bounds = nameR, TextAlign = align
                };
                var cap = new CaptionLabel
                {
                    Text = "", ForeColor = CapNormal, BackColor = BackColor, Font = new Font("Segoe UI", 8),
                    Bounds = capR, TextAlign = align
                };
                Controls.Add(name); Controls.Add(cap);
                uint wpid; Native.GetWindowThreadProcessId(wins[i].Key, out wpid);
                captions.Add(new KeyValuePair<uint, Label>(wpid, cap));
                dests.Add(new KeyValuePair<Rectangle, IntPtr>(cell, wins[i].Key));    // gold frame on the active client
                slices.Add(new KeyValuePair<Rectangle, IntPtr>(cell, wins[i].Key));
                HookDrag(name, wins[i].Key); HookDrag(cap, wins[i].Key);
            }
        }

        // ---- move: CTRL + drag anywhere moves the whole strip, so it can't be moved by
        // accident; a plain click switches to that client. The spot is saved per show mode + layout in
        // state\strip.txt; an off-screen spot falls back to the home position (/dw strip reset clears them).
        const int DRAG_PX = 5;
        string posKey = "PicH";
        bool mouseDown, dragged, ctrlDrag;
        Point dragFrom, formFrom;
        IntPtr downTarget;
        static string PosFile { get { return System.IO.Path.Combine(Util.StateDir, "strip.txt"); } }

        void HookDrag(Control c, IntPtr target)
        {
            c.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                mouseDown = true; dragged = false;
                ctrlDrag = (Control.ModifierKeys & Keys.Control) != 0;
                dragFrom = Cursor.Position; formFrom = Location;
                downTarget = target;
                if (target == IntPtr.Zero)
                    foreach (var sl in slices) if (sl.Key.Contains(e.Location)) { downTarget = sl.Value; break; }
            };
            c.MouseMove += (s, e) =>
            {
                if (!mouseDown || !ctrlDrag) return;
                Point p = Cursor.Position;
                int dx = p.X - dragFrom.X, dy = p.Y - dragFrom.Y;
                if (!dragged && (Math.Abs(dx) > DRAG_PX || Math.Abs(dy) > DRAG_PX)) dragged = true;
                if (dragged) Location = new Point(formFrom.X + dx, formFrom.Y + dy);
            };
            c.MouseUp += (s, e) =>
            {
                if (!mouseDown) return;
                mouseDown = false;
                if (dragged) { SavePosition(); return; }
                if (downTarget == IntPtr.Zero) return;
                if (Native.IsIconic(downTarget)) Native.ShowWindow(downTarget, 9);  // SW_RESTORE
                Native.FocusWindow(downTarget);
            };
        }

        Rectangle SavedBounds(Rectangle dflt)
        {
            try
            {
                var d = Util.ReadKv(PosFile);
                int x, y;
                if (!int.TryParse(Util.Get(d, posKey + "X"), out x) || !int.TryParse(Util.Get(d, posKey + "Y"), out y)) return dflt;
                var r = new Rectangle(x, y, dflt.Width, dflt.Height);
                foreach (var s in Screen.AllScreens)
                {
                    var v = Rectangle.Intersect(s.WorkingArea, r);
                    if (v.Width >= 60 && v.Height >= 20) return r;   // enough of it on some monitor to grab again
                }
            }
            catch (Exception ex) { FeLog.W("SavedBounds EX: " + ex.Message); }
            return dflt;
        }
        void SavePosition()
        {
            try
            {
                var d = Util.ReadKv(PosFile);
                d[posKey + "X"] = Location.X.ToString();
                d[posKey + "Y"] = Location.Y.ToString();
                Util.WriteKv(PosFile, d);
            }
            catch (Exception ex) { FeLog.W("SavePosition EX: " + ex.Message); }
        }
        internal static void ResetPositions() { try { System.IO.File.Delete(PosFile); } catch { } }

        void StartCaptions()
        {
            RefreshCaptions();
            capTimer = new System.Windows.Forms.Timer(); capTimer.Interval = 3000;
            capTimer.Tick += (s2, e2) => RefreshCaptions();
            capTimer.Start();
        }

        // ---- captions: "Lv 64 | 1.2M xp/h | 38 kills/h | VT ON" from state\*.stats.txt ----
        const int CAPTION_H = 16;
        // gold like the names; VT ON green / VT OFF red; grey = no stats
        static readonly Color CapNormal = Color.FromArgb(185, 163, 90), CapDead = Color.FromArgb(120, 120, 120),
            VtOn = Color.FromArgb(90, 200, 90), VtOff = Color.FromArgb(225, 70, 55);
        List<KeyValuePair<uint, Label>> captions = new List<KeyValuePair<uint, Label>>();
        System.Windows.Forms.Timer capTimer;

        void RefreshCaptions()
        {
            try
            {
                var byPid = new Dictionary<string, Dictionary<string, string>>();
                if (System.IO.Directory.Exists(Util.StateDir))
                    foreach (string f in System.IO.Directory.GetFiles(Util.StateDir, "*.stats.txt"))
                    {
                        var d = Util.ReadKv(f);
                        string pid = Util.Get(d, "Pid");
                        if (pid != "") byPid[pid] = d;
                    }
                foreach (var c in captions)
                {
                    Dictionary<string, string> d;
                    var cap = (CaptionLabel)c.Value;
                    string main, tail = ""; Color col = CapNormal, tailCol = CapNormal;
                    DateTime upd;
                    if (!byPid.TryGetValue(c.Key.ToString(), out d)
                        || !DateTime.TryParse(Util.Get(d, "UpdatedUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out upd)
                        || (DateTime.UtcNow - upd.ToUniversalTime()).TotalSeconds > 30)
                    { main = "no stats (Stats off or not in game)"; col = CapDead; }
                    else
                    {
                        string macro = Util.Get(d, "Macro");
                        string rares = Util.Get(d, "Rares");
                        main = "Lv " + Util.Get(d, "Level") + "  |  " + Util.Get(d, "XpPerHour") + " xp/h  |  " + Util.Get(d, "KillsPerHour") + " kills/h"
                            + (rares != "" && rares != "0" ? "  |  " + rares + " rare" + (rares == "1" ? "" : "s") : "") + "  |  ";
                        tail = "VT";                       // the colour says it: green on, red off
                        tailCol = macro == "ON" ? VtOn : macro == "OFF" ? VtOff : CapNormal;   // only the VT part changes colour
                    }
                    cap.Set(main, col, tail, tailCol);
                }
            }
            catch (Exception ex) { FeLog.W("RefreshCaptions EX: " + ex.Message); }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (active >= 0 && active < dests.Count)
                using (var gold = new Pen(Color.FromArgb(201, 171, 95), 3))
                {
                    var r = dests[active].Key; if (!textMode) r.Inflate(2, 2);
                    e.Graphics.DrawRectangle(gold, r);
                }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000008;   // WS_EX_NOACTIVATE | WS_EX_TOPMOST
                return cp;
            }
        }
        static string CharName(string title)
        {
            var parts = title.Split('-');
            return parts.Length > 0 ? parts[parts.Length - 1].Trim() : title;
        }
    }

    // Caption in two colours: the stats in gold, then "VT ON" / "VT OFF" in its own colour.
    class CaptionLabel : Label
    {
        string main = "", tail = "";
        Color mainCol, tailCol;
        public void Set(string m, Color mc, string t, Color tc)
        {
            if (m == main && t == tail && mc == mainCol && tc == tailCol) return;
            main = m; tail = t; mainCol = mc; tailCol = tc;
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            int wm = TextRenderer.MeasureText(e.Graphics, main, Font, Size.Empty, flags).Width;
            int wt = tail == "" ? 0 : TextRenderer.MeasureText(e.Graphics, tail, Font, Size.Empty, flags).Width;
            int x = TextAlign == ContentAlignment.MiddleCenter ? Math.Max(0, (Width - wm - wt) / 2) : 0;
            TextRenderer.DrawText(e.Graphics, main, Font, new Rectangle(x, 0, wm + 2, Height), mainCol, flags);
            if (tail != "") TextRenderer.DrawText(e.Graphics, tail, Font, new Rectangle(x + wm, 0, wt + 2, Height), tailCol, flags);
        }
    }
}