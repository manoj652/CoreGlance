using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CoreGlance
{
    sealed class WidgetForm : Form
    {
        public const string ShowMessageName = "CoreGlance.ShowWidget";

        const int WM_DPICHANGED = 0x02E0, WM_HOTKEY = 0x0312, WM_NCLBUTTONDOWN = 0x00A1, HTCAPTION = 2;
        const int WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, CS_DROPSHADOW = 0x20000;
        const int HOTKEY_ID = 0xC61;

        // Palette
        static readonly Color Bg = Color.FromArgb(17, 19, 24);
        static readonly Color PanelC = Color.FromArgb(27, 30, 37);
        static readonly Color SparkBg = Color.FromArgb(22, 25, 31);
        static readonly Color Track = Color.FromArgb(36, 40, 49);
        static readonly Color DividerC = Color.FromArgb(33, 37, 45);
        static readonly Color TextHi = Color.FromArgb(236, 239, 244);
        static readonly Color TextMid = Color.FromArgb(158, 165, 178);
        static readonly Color TextLo = Color.FromArgb(104, 111, 124);
        static readonly Color CCpu = Color.FromArgb(34, 211, 238);
        static readonly Color CMem = Color.FromArgb(167, 139, 250);
        static readonly Color CGpu = Color.FromArgb(52, 211, 153);
        static readonly Color CDisk = Color.FromArgb(251, 191, 36);
        static readonly Color CNet = Color.FromArgb(244, 114, 182);
        static readonly Color CTemp = Color.FromArgb(251, 146, 60);
        static readonly Color CFan = Color.FromArgb(45, 212, 191);
        static readonly Color CPower = Color.FromArgb(250, 204, 21);
        static readonly Color Good = Color.FromArgb(52, 211, 153);
        static readonly Color Warn = Color.FromArgb(251, 191, 36);
        static readonly Color Bad = Color.FromArgb(248, 113, 113);

        readonly Settings cfg;
        readonly SysMonitor mon;
        readonly Sensors sens;
        readonly History hCpuT = new History(60), hGpuT = new History(60), hSysT = new History(60);
        readonly History hCpuFan = new History(60), hGpuFan = new History(60), hCpuW = new History(60), hGpuW = new History(60), hBatW = new History(60);
        double maxFan = 5000, maxCpuW = 15, maxGpuW = 15;
        ToolStripMenuItem miSensors, miCpuBusy, miCpuUtil;
        readonly System.Windows.Forms.Timer timer;
        readonly NotifyIcon tray;
        ContextMenuStrip menu;
        ToolStripMenuItem miShow, miCompact, miStandard, miDetailed, miTop, miLock, miClick, miFull, miAuto;
        readonly List<ToolStripMenuItem> opacityItems = new List<ToolStripMenuItem>();
        readonly List<ToolStripMenuItem> intervalItems = new List<ToolStripMenuItem>();

        float S = 1f;
        Font fTitle, fLabel, fBig, fMid, fBody, fSmall, fTiny;
        readonly StringFormat sfL, sfC, sfR, sfCC;
        readonly Dictionary<int, SolidBrush> brushes = new Dictionary<int, SolidBrush>();

        readonly List<RectangleF> coreRects = new List<RectangleF>();
        RectangleF tabCpu, tabMem, btnView, btnMin;
        int hoverCore = -1, hoverBtn, downBtn;   // buttons: 1 view toggle, 2 minimize
        bool mouseInside, minTipShown;
        Point downPt;
        bool downLeft, resizePending, exiting, paused, autoHidden;
        int tick, lastTrayKey = -1;
        IntPtr trayHicon = IntPtr.Zero;
        uint showMsg;

        long selfCpuPrev;
        DateTime selfT;
        double selfCpu, selfMem;

        readonly string snapshotPath;

        public WidgetForm() : this(null) { }

        public WidgetForm(string snapshot)
        {
            snapshotPath = snapshot;
            cfg = Settings.Load();
            if (snapshotPath != null) { cfg.Visible = false; cfg.FirstRun = false; }

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Bg;
            Text = "CoreGlance";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            sfL = MakeFormat(StringAlignment.Near, StringAlignment.Near);
            sfC = MakeFormat(StringAlignment.Center, StringAlignment.Near);
            sfR = MakeFormat(StringAlignment.Far, StringAlignment.Near);
            sfCC = MakeFormat(StringAlignment.Center, StringAlignment.Center);

            mon = new SysMonitor();
            mon.UseUtility = cfg.CpuMetric == 1;
            sens = new Sensors();
            BuildMenu();
            ContextMenuStrip = menu;

            tray = new NotifyIcon();
            tray.Text = "CoreGlance";
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ToggleVisible(); };
            UpdateTray();
            tray.Visible = snapshotPath == null;

            showMsg = Native.RegisterWindowMessage(ShowMessageName);
            long c, x, k, u;
            Native.GetProcessTimes(Native.GetCurrentProcess(), out c, out x, out k, out u);
            selfCpuPrev = k + u;
            selfT = DateTime.UtcNow;

            // Initial placement before the handle exists, so the window is created on the right monitor/DPI.
            float sysS = 1f;
            using (Graphics g0 = Graphics.FromHwnd(IntPtr.Zero)) sysS = g0.DpiX / 96f;
            Size = new Size((int)(BaseWidth() * sysS), (int)(400 * sysS));
            if (cfg.X == int.MinValue || cfg.Y == int.MinValue)
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - Width - (int)(16 * sysS), wa.Top + (int)(16 * sysS));
            }
            else Location = new Point(cfg.X, cfg.Y);

            TopMost = cfg.TopMost;
            ApplyOpacity();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = cfg.Interval;
            timer.Tick += OnTick;
            timer.Start();

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        static StringFormat MakeFormat(StringAlignment h, StringAlignment v)
        {
            var f = new StringFormat(StringFormat.GenericTypographic);
            f.Alignment = h;
            f.LineAlignment = v;
            f.Trimming = StringTrimming.EllipsisCharacter;
            f.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            return f;
        }

        int BaseWidth() { return cfg.View == 0 ? 236 : 318; }
        int ScaledWidth() { return (int)Math.Round(BaseWidth() * S); }

        // ============================== window plumbing ==============================

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW;      // no Alt+Tab / taskbar entry
                cp.ClassStyle |= CS_DROPSHADOW;
                if (cfg != null && cfg.ClickThrough) cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void SetVisibleCore(bool value)
        {
            if (!IsHandleCreated)
            {
                CreateHandle();
                if (!cfg.Visible) value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            uint dpi = 96;
            try { dpi = Native.GetDpiForWindow(Handle); } catch { }
            if (dpi == 0) dpi = 96;
            SetScale(dpi / 96f);
            ClientSize = new Size(ScaledWidth(), (int)(400 * S));
            try
            {
                int round = 2;                       // DWMWCP_ROUND (Windows 11)
                Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
                int border = 0x003D332E;             // COLORREF for RGB(46,51,61)
                Native.DwmSetWindowAttribute(Handle, 34, ref border, 4);
            }
            catch { }
            // Let a normal-user second launch reach this window when it runs as admin.
            if (showMsg != 0) Native.ChangeWindowMessageFilterEx(Handle, showMsg, 1, IntPtr.Zero);
            // Ctrl+Alt+Shift+M toggles the widget from anywhere.
            Native.RegisterHotKey(Handle, HOTKEY_ID, 0x0001 | 0x0002 | 0x0004 | 0x4000, (uint)Keys.M);
            KeepOnScreen();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (cfg.FirstRun)
            {
                cfg.FirstRun = false;
                cfg.Save();
                tray.ShowBalloonTip(5000, "CoreGlance is running",
                    "Drag to move. Double-click to change view. Right-click for options. Ctrl+Alt+Shift+M shows or hides it.",
                    ToolTipIcon.Info);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                float old = S;
                int dpi = (int)(m.WParam.ToInt64() & 0xFFFF);
                var r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                SetScale(dpi / 96f);
                Bounds = new Rectangle(r.Left, r.Top, ScaledWidth(), (int)(Height * S / old));
                Invalidate();
                return;
            }
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID) { ToggleVisible(); return; }
            if (showMsg != 0 && m.Msg == (int)showMsg) { ShowWidget(); return; }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;   // Alt+F4 hides instead of quitting
                HideWidget();
                return;
            }
            timer.Stop();
            if (snapshotPath == null) cfg.Save();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                if (IsHandleCreated) Native.UnregisterHotKey(Handle, HOTKEY_ID);
                timer.Dispose();
                tray.Visible = false;
                tray.Dispose();
                mon.Dispose();
                sens.Dispose();
                DisposeFonts();
                foreach (SolidBrush b in brushes.Values) b.Dispose();
                brushes.Clear();
                if (menu != null) menu.Dispose();
                sfL.Dispose(); sfC.Dispose(); sfR.Dispose(); sfCC.Dispose();
            }
            if (trayHicon != IntPtr.Zero) { Native.DestroyIcon(trayHicon); trayHicon = IntPtr.Zero; }
            base.Dispose(disposing);
        }

        void SetScale(float s)
        {
            S = s;
            DisposeFonts();
            fTitle = MakeFont("Segoe UI Semibold", 13f);
            fLabel = MakeFont("Segoe UI Semibold", 10.5f);
            fBig = MakeFont("Segoe UI Semibold", 28f);
            fMid = MakeFont("Segoe UI Semibold", 17f);
            fBody = MakeFont("Segoe UI", 12.5f);
            fSmall = MakeFont("Segoe UI", 11f);
            fTiny = MakeFont("Segoe UI", 10f);
        }

        Font MakeFont(string family, float px) { return new Font(family, px * S, FontStyle.Regular, GraphicsUnit.Pixel); }

        void DisposeFonts()
        {
            foreach (Font f in new[] { fTitle, fLabel, fBig, fMid, fBody, fSmall, fTiny }) if (f != null) f.Dispose();
        }

        // ============================== sampling loop ==============================

        void OnTick(object sender, EventArgs e)
        {
            if (paused) return;
            tick++;
            bool visible = Visible || snapshotPath != null;
            bool wantProcs = visible && cfg.View != 0 && (cfg.View == 2 ? (tick % 2 == 0 || cfg.Interval >= 2000) : tick % 5 == 0);
            try { mon.Sample(wantProcs); }
            catch (Exception ex) { Settings.Log("sample: " + ex); }
            sens.NvidiaActive = mon.NvidiaActive;
            RecordSensorHistory();

            if (tick % 3 == 0) UpdateSelfUsage();
            if (snapshotPath != null) { if (tick >= 8) TakeSnapshot(); return; }
            UpdateTray();
            CheckFullscreen();
            if (Visible) Invalidate();
            if (tick == 5) TrimMemory();
        }

        void UpdateSelfUsage()
        {
            try
            {
                IntPtr me = Native.GetCurrentProcess();
                long c, x, k, u;
                if (Native.GetProcessTimes(me, out c, out x, out k, out u))
                {
                    DateTime now = DateTime.UtcNow;
                    double wallMs = (now - selfT).TotalMilliseconds;
                    if (wallMs > 0) selfCpu = (k + u - selfCpuPrev) / 10000.0 / wallMs / Math.Max(1, mon.Logical) * 100.0;
                    selfCpuPrev = k + u;
                    selfT = now;
                }
                Native.PROCESS_MEMORY_COUNTERS pmc;
                if (Native.GetProcessMemoryInfo(me, out pmc, (uint)Marshal.SizeOf(typeof(Native.PROCESS_MEMORY_COUNTERS))))
                    selfMem = pmc.WorkingSetSize.ToUInt64();
            }
            catch { }
        }

        void TakeSnapshot()
        {
            try
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    using (var probe = new Bitmap(Math.Max(1, Width), Math.Max(1, Height))) DrawToBitmap(probe, new Rectangle(Point.Empty, Size));
                    Application.DoEvents();   // lets the height correction from the first paint apply
                }
                using (var bmp = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
                    bmp.Save(snapshotPath, ImageFormat.Png);
                }
            }
            catch (Exception ex) { Settings.Log("snapshot: " + ex.Message); }
            exiting = true;
            Close();
        }

        static void TrimMemory()
        {
            // Release start-up allocations so the resident footprint reflects steady state.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }

        void CheckFullscreen()
        {
            if (!cfg.HideFullscreen || !cfg.Visible) return;
            int state;
            bool fs = Native.SHQueryUserNotificationState(out state) == 0 && (state == 2 || state == 3 || state == 4);
            if (fs && Visible) { autoHidden = true; Hide(); }
            else if (!fs && autoHidden) { autoHidden = false; Show(); }
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) paused = true;
            else if (e.Reason == SessionSwitchReason.SessionUnlock) paused = false;
            sens.Paused = paused;
        }

        static float Hv(double v) { return double.IsNaN(v) ? 0 : (float)v; }

        void RecordSensorHistory()
        {
            hCpuT.Add(Hv(CpuTempBest()));
            hGpuT.Add(Hv(GpuTempBest()));
            hSysT.Add(Hv(sens.SysTemp));
            hCpuFan.Add(Hv(sens.CpuFan));
            hGpuFan.Add(Hv(sens.GpuFan));
            hCpuW.Add(Hv(mon.CpuPkgW));
            hGpuW.Add(Hv(sens.NvPowerW));
            hBatW.Add(Hv(Math.Abs(sens.BatRateW)));
            if (!double.IsNaN(sens.CpuFan)) maxFan = Math.Max(maxFan, sens.CpuFan * 1.1);
            if (!double.IsNaN(sens.GpuFan)) maxFan = Math.Max(maxFan, sens.GpuFan * 1.1);
            if (!double.IsNaN(mon.CpuPkgW)) maxCpuW = Math.Max(maxCpuW, mon.CpuPkgW * 1.1);
            if (!double.IsNaN(sens.NvPowerW)) maxGpuW = Math.Max(maxGpuW, sens.NvPowerW * 1.1);
        }

        double CpuTempBest() { return !double.IsNaN(sens.CpuTemp) ? sens.CpuTemp : mon.AcpiTemp; }
        double GpuTempBest() { return !double.IsNaN(sens.GpuTemp) ? sens.GpuTemp : sens.NvTempC; }

        void ToggleSensorMode()
        {
            if (sens.Elevated)
            {
                if (MessageBox.Show("Turn off admin sensor mode?\n\nCoreGlance will stop reading fan speeds and firmware temperatures after its next restart.",
                    "CoreGlance", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                if (Elevation.RemoveTask() == 0)
                    MessageBox.Show("Sensor mode is off. Exit and start CoreGlance again to run it without admin rights.", "CoreGlance");
                return;
            }
            if (MessageBox.Show(
                "Fan speeds and CPU/GPU temperatures come from your laptop's firmware, which Windows only lets admin apps read.\n\n" +
                "CoreGlance will ask for admin once, copy itself to Program Files and add a scheduled task, so it starts with " +
                "those rights from then on without asking again. It only reads sensors; it never changes fan or power settings.\n\nContinue?",
                "Enable temperatures and fans", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            if (!Elevation.RunElevated("--install-task"))
            {
                MessageBox.Show("Sensor mode was not enabled (admin permission was declined or the task could not be created).", "CoreGlance");
                return;
            }
            if (Settings.IsAutoStart()) Settings.SetAutoStart(true, Elevation.SecureExePath);
            cfg.Save();
            if (Elevation.RunTask()) { exiting = true; Close(); }
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume || !IsHandleCreated) return;
            BeginInvoke((MethodInvoker)delegate
            {
                try { mon.Reset(); } catch (Exception ex) { Settings.Log("reset: " + ex.Message); }
            });
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            if (IsHandleCreated) BeginInvoke((MethodInvoker)KeepOnScreen);
        }

        // ============================== tray ==============================

        void UpdateTray()
        {
            float gpu = 0;
            foreach (Gpu g in mon.Gpus) gpu = Math.Max(gpu, g.Util);
            double ct = CpuTempBest();
            string tip = double.IsNaN(ct)
                ? string.Format("CPU {0:0}%  ·  RAM {1:0}%  ·  GPU {2:0}%", mon.Cpu, mon.MemPct, gpu)
                : string.Format("CPU {0:0}% {3:0}°C  ·  RAM {1:0}%  ·  GPU {2:0}%", mon.Cpu, mon.MemPct, gpu, ct);
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;

            int key = (int)(mon.Cpu / 6) * 100 + (int)(mon.MemPct / 6);
            if (key == lastTrayKey) return;
            lastTrayKey = key;

            int sz = SystemInformation.SmallIconSize.Width;
            using (var bmp = new Bitmap(sz, sz, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float bw = sz * 0.38f, gap = sz * 0.12f;
                float x = (sz - 2 * bw - gap) / 2f;
                TrayBar(g, x, bw, sz, mon.Cpu, CCpu);
                TrayBar(g, x + bw + gap, bw, sz, mon.MemPct, CMem);
                IntPtr h = bmp.GetHicon();
                Icon icon = Icon.FromHandle(h);
                Icon old = tray.Icon;
                tray.Icon = icon;
                if (old != null) old.Dispose();
                if (trayHicon != IntPtr.Zero) Native.DestroyIcon(trayHicon);
                trayHicon = h;
            }
        }

        static void TrayBar(Graphics g, float x, float w, int sz, float pct, Color c)
        {
            var track = new RectangleF(x, 0.5f, w, sz - 1f);
            using (var b = new SolidBrush(Color.FromArgb(200, 45, 50, 60))) FillRound(g, b, track, w * 0.3f);
            float h = Math.Max(2f, (sz - 1f) * pct / 100f);
            using (var b = new SolidBrush(c)) FillRound(g, b, new RectangleF(x, sz - 0.5f - h, w, h), Math.Min(w * 0.3f, h / 2));
        }

        // ============================== menu ==============================

        ToolStripMenuItem Item(string text, EventHandler onClick)
        {
            var i = new ToolStripMenuItem(text);
            if (onClick != null) i.Click += onClick;
            return i;
        }

        void BuildMenu()
        {
            menu = new ContextMenuStrip();
            miShow = Item("Minimize to tray", delegate { if (Visible || autoHidden) MinimizeToTray(); else ShowWidget(); });
            miShow.ShortcutKeyDisplayString = "Ctrl+Alt+Shift+M";

            var view = new ToolStripMenuItem("View");
            miCompact = Item("Compact", delegate { SetView(0); });
            miStandard = Item("Standard", delegate { SetView(1); });
            miDetailed = Item("Detailed (top processes)", delegate { SetView(2); });
            view.DropDownItems.AddRange(new ToolStripItem[] { miCompact, miStandard, miDetailed });

            var cpuMetric = new ToolStripMenuItem("CPU load");
            miCpuBusy = Item("Busy time (like NZXT CAM)", delegate { SetCpuMetric(0); });
            miCpuUtil = Item("Utility, scaled by clock (like Task Manager)", delegate { SetCpuMetric(1); });
            cpuMetric.DropDownItems.AddRange(new ToolStripItem[] { miCpuBusy, miCpuUtil });

            miTop = Item("Always on top", delegate { cfg.TopMost = !cfg.TopMost; TopMost = cfg.TopMost; cfg.Save(); });
            miLock = Item("Lock position", delegate { cfg.Locked = !cfg.Locked; cfg.Save(); });
            miClick = Item("Click-through", delegate { SetClickThrough(!cfg.ClickThrough); });
            miFull = Item("Hide over full-screen apps", delegate
            {
                cfg.HideFullscreen = !cfg.HideFullscreen;
                if (!cfg.HideFullscreen && autoHidden) { autoHidden = false; Show(); }
                cfg.Save();
            });

            var opacity = new ToolStripMenuItem("Opacity");
            foreach (int v in new[] { 100, 95, 90, 80, 70, 60 })
            {
                int pct = v;
                ToolStripMenuItem it = Item(pct + "%", delegate { cfg.Opacity = pct / 100.0; ApplyOpacity(); cfg.Save(); });
                it.Tag = pct;
                opacity.DropDownItems.Add(it);
                opacityItems.Add(it);
            }

            var refresh = new ToolStripMenuItem("Refresh rate");
            foreach (int v in new[] { 500, 1000, 2000, 5000 })
            {
                int ms = v;
                ToolStripMenuItem it = Item(ms < 1000 ? "0.5 s" : (ms / 1000) + " s", delegate { cfg.Interval = ms; timer.Interval = ms; cfg.Save(); });
                it.Tag = ms;
                refresh.DropDownItems.Add(it);
                intervalItems.Add(it);
            }

            miAuto = Item("Start with Windows", delegate { Settings.SetAutoStart(!Settings.IsAutoStart()); });
            miSensors = Item("Enable temperatures && fans (admin)...", delegate { ToggleSensorMode(); });

            menu.Items.AddRange(new ToolStripItem[]
            {
                miShow,
                new ToolStripSeparator(),
                view, cpuMetric, miTop, miLock, miClick, miFull, opacity, refresh,
                new ToolStripSeparator(),
                miAuto,
                miSensors,
                Item("Open Task Manager", delegate { try { Process.Start("taskmgr.exe"); } catch { } }),
                Item("Reset position", delegate { cfg.X = cfg.Y = int.MinValue; PlaceDefault(); cfg.Save(); }),
                Item("About CoreGlance", delegate { ShowAbout(); }),
                new ToolStripSeparator(),
                Item("Exit", delegate { exiting = true; Close(); })
            });
            menu.Opening += delegate { RefreshMenu(); };
        }

        void RefreshMenu()
        {
            miShow.Text = Visible || autoHidden ? "Minimize to tray" : "Show widget";
            miCompact.Checked = cfg.View == 0;
            miStandard.Checked = cfg.View == 1;
            miDetailed.Checked = cfg.View == 2;
            miCpuBusy.Checked = cfg.CpuMetric == 0;
            miCpuUtil.Checked = cfg.CpuMetric == 1;
            miCpuUtil.Enabled = mon.HasUtility;
            miTop.Checked = cfg.TopMost;
            miLock.Checked = cfg.Locked;
            miClick.Checked = cfg.ClickThrough;
            miFull.Checked = cfg.HideFullscreen;
            miAuto.Checked = Settings.IsAutoStart();
            miSensors.Visible = sens.AcerAvailable || sens.Elevated;
            miSensors.Text = sens.Elevated ? "Admin sensor mode (on)" : "Enable temperatures && fans (admin)...";
            miSensors.Checked = sens.Elevated;
            foreach (ToolStripMenuItem it in opacityItems) it.Checked = Math.Abs((int)it.Tag - cfg.Opacity * 100) < 0.5;
            foreach (ToolStripMenuItem it in intervalItems) it.Checked = (int)it.Tag == cfg.Interval;
        }

        void ShowAbout()
        {
            MessageBox.Show(
                "CoreGlance " + Program.Version + "\n\n" +
                "Live CPU (per thread), memory, GPU, disk and network monitor.\n" +
                "Data comes from Windows performance counters, the same source Task Manager uses.\n\n" +
                "Settings: " + Settings.Dir,
                "About CoreGlance", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void SetView(int v)
        {
            if (v == cfg.View) return;
            Rectangle wa = Screen.FromRectangle(Bounds).WorkingArea;
            bool anchorRight = Left + Width / 2 > wa.Left + wa.Width / 2;
            int right = Right;
            cfg.View = v;
            ClientSize = new Size(ScaledWidth(), ClientSize.Height);
            if (anchorRight) Left = right - Width;
            KeepOnScreen();
            SaveLocation();
            tick = 0;
            Invalidate();
        }

        void SetCpuMetric(int m)
        {
            if (cfg.CpuMetric == m) return;
            cfg.CpuMetric = m;
            mon.UseUtility = m == 1;
            cfg.Save();
        }

        void SetSort(int s)
        {
            if (cfg.ProcSort == s) return;
            cfg.ProcSort = s;
            cfg.Save();
            Invalidate();
        }

        void ApplyOpacity()
        {
            double o = cfg.Opacity;
            if (cfg.ClickThrough && o > 0.99) o = 0.99;   // keeps the window layered so WS_EX_TRANSPARENT works
            Opacity = o;
        }

        void SetClickThrough(bool on)
        {
            cfg.ClickThrough = on;
            if (on)
            {
                ApplyOpacity();
                UpdateStyles();
                tray.ShowBalloonTip(4000, "Click-through is on",
                    "The widget now ignores the mouse. Right-click the tray icon to turn it off.", ToolTipIcon.Info);
            }
            else
            {
                UpdateStyles();
                ApplyOpacity();
            }
            cfg.Save();
        }

        void ToggleVisible()
        {
            if (Visible || autoHidden) HideWidget();
            else ShowWidget();
        }

        void MinimizeToTray()
        {
            HideWidget();
            if (!minTipShown)
            {
                minTipShown = true;
                tray.ShowBalloonTip(4000, "CoreGlance is still running",
                    "Click the tray icon or press Ctrl+Alt+Shift+M to bring it back.", ToolTipIcon.Info);
            }
        }

        void HideWidget()
        {
            autoHidden = false;
            cfg.Visible = false;
            Hide();
            cfg.Save();
        }

        void ShowWidget()
        {
            autoHidden = false;
            cfg.Visible = true;
            Show();
            KeepOnScreen();
            if (cfg.TopMost) { TopMost = false; TopMost = true; }
            cfg.Save();
            Invalidate();
        }

        // ============================== position ==============================

        void PlaceDefault()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - (int)(16 * S), wa.Top + (int)(16 * S));
        }

        void KeepOnScreen()
        {
            Rectangle b = Bounds;
            bool onAny = false;
            foreach (Screen s in Screen.AllScreens)
            {
                Rectangle inter = Rectangle.Intersect(s.WorkingArea, b);
                if (inter.Width >= 60 && inter.Height >= 40) { onAny = true; break; }
            }
            if (!onAny) { PlaceDefault(); return; }
            Rectangle wa = Screen.FromRectangle(b).WorkingArea;
            if (b.Height <= wa.Height && b.Bottom > wa.Bottom) b.Y = wa.Bottom - b.Height;
            if (b.Y < wa.Top) b.Y = wa.Top;
            if (b.Right > wa.Right) b.X = wa.Right - b.Width;
            if (b.X < wa.Left) b.X = wa.Left;
            if (b.Location != Location) Location = b.Location;
        }

        void SnapAndSave()
        {
            Rectangle wa = Screen.FromRectangle(Bounds).WorkingArea;
            int t = (int)(20 * S), m = (int)(8 * S);
            int x = Left, y = Top;
            if (Math.Abs(x - (wa.Left + m)) < t) x = wa.Left + m;
            if (Math.Abs(x + Width - (wa.Right - m)) < t) x = wa.Right - m - Width;
            if (Math.Abs(y - (wa.Top + m)) < t) y = wa.Top + m;
            if (Math.Abs(y + Height - (wa.Bottom - m)) < t) y = wa.Bottom - m - Height;
            Location = new Point(x, y);
            KeepOnScreen();
            SaveLocation();
        }

        void SaveLocation()
        {
            cfg.X = Left;
            cfg.Y = Top;
            cfg.Save();
        }

        // ============================== mouse ==============================

        int ButtonAt(Point p)
        {
            if (btnView.Contains(p)) return 1;
            if (btnMin.Contains(p)) return 2;
            return 0;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { downPt = e.Location; downLeft = true; downBtn = ButtonAt(e.Location); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!mouseInside) { mouseInside = true; if (cfg.View == 0) Invalidate(); }
            int hb = ButtonAt(e.Location);
            if (hb != hoverBtn) { hoverBtn = hb; Invalidate(); }
            if (downLeft && downBtn == 0 && e.Button == MouseButtons.Left && !cfg.Locked &&
                (Math.Abs(e.X - downPt.X) > 3 || Math.Abs(e.Y - downPt.Y) > 3))
            {
                downLeft = false;
                Native.ReleaseCapture();
                Native.SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero); // returns when the drag ends
                SnapAndSave();
                return;
            }
            int h = -1;
            for (int i = 0; i < coreRects.Count; i++) if (coreRects[i].Contains(e.Location)) { h = i; break; }
            if (h != hoverCore) { hoverCore = h; Invalidate(); }
            Cursor want = hb != 0 || tabCpu.Contains(e.Location) || tabMem.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            if (Cursor != want) Cursor = want;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !downLeft) return;
            downLeft = false;
            int b = ButtonAt(e.Location);
            if (b != 0 && b == downBtn)
            {
                downBtn = 0;
                if (b == 2) MinimizeToTray();
                else SetView(cfg.View == 0 ? 1 : 0);
                return;
            }
            downBtn = 0;
            if (tabCpu.Contains(e.Location)) SetSort(0);
            else if (tabMem.Contains(e.Location)) SetSort(1);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && ButtonAt(e.Location) == 0 && !tabCpu.Contains(e.Location) && !tabMem.Contains(e.Location))
                SetView((cfg.View + 1) % 3);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            mouseInside = false;
            hoverBtn = 0;
            hoverCore = -1;
            Invalidate();
        }

        /// <summary>Top-right buttons: view toggle (compact / full) and minimize to tray.</summary>
        void HeaderButtons(Graphics g, float right, float top, bool overlay)
        {
            float bs = 20 * S;
            btnMin = new RectangleF(right - bs, top, bs, bs);
            btnView = new RectangleF(btnMin.X - bs - 2 * S, top, bs, bs);
            RectangleF[] rs = { btnView, btnMin };
            for (int i = 0; i < 2; i++)
            {
                RectangleF r = rs[i];
                bool hot = hoverBtn == i + 1;
                FillRound(g, B(hot ? (i == 1 ? Color.FromArgb(70, 60, 66) : Color.FromArgb(52, 57, 68)) : PanelC), r, 5 * S);
                Color c = hot ? TextHi : Color.FromArgb(190, 196, 206);
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                using (var pen = new Pen(c, 1.7f * S))
                {
                    if (i == 1) g.DrawLine(pen, cx - 4.5f * S, cy + 0.5f * S, cx + 4.5f * S, cy + 0.5f * S);
                    else if (cfg.View == 0) g.DrawRectangle(pen, cx - 4.5f * S, cy - 4.5f * S, 9 * S, 9 * S);          // expand
                    else g.DrawRectangle(pen, cx - 4 * S, cy - 1.5f * S, 8 * S, 5 * S);                             // collapse
                }
            }
        }

        // ============================== painting ==============================

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Bg);
            coreRects.Clear();
            tabCpu = RectangleF.Empty;
            tabMem = RectangleF.Empty;
            btnView = RectangleF.Empty;
            btnMin = RectangleF.Empty;

            float h = cfg.View == 0 ? PaintCompact(g) : PaintFull(g, cfg.View == 2);

            int need = (int)Math.Ceiling(h);
            if (Math.Abs(need - ClientSize.Height) > 1 && !resizePending)
            {
                resizePending = true;
                BeginInvoke((MethodInvoker)delegate
                {
                    resizePending = false;
                    ClientSize = new Size(ScaledWidth(), need);
                    KeepOnScreen();
                });
            }
        }

        float PaintFull(Graphics g, bool detailed)
        {
            float p = 14 * S, x0 = p, x1 = ClientSize.Width - p, cw = x1 - x0, y = 11 * S;

            // Header: name, battery, uptime
            Txt(g, "CoreGlance", fTitle, TextHi, x0, y, 0, 0);
            HeaderButtons(g, x1 + 5 * S, y - 3 * S, false);
            float hr = btnView.X - 8 * S;
            string up = "up " + Uptime();
            Txt(g, up, fSmall, TextLo, hr, y + 2 * S, 2, 0);
            if (mon.HasBattery && mon.BatteryPct >= 0)
            {
                float hx = hr - Measure(g, up, fSmall) - 12 * S;
                string bt = mon.BatteryPct + "%";
                Txt(g, bt, fSmall, TextMid, hx, y + 2 * S, 2, 0);
                hx -= Measure(g, bt, fSmall) + 5 * S;
                DrawBattery(g, hx - 17 * S, y + 4.5f * S);
            }
            y += 26 * S;

            y = CpuSection(g, x0, x1, y);
            List<Tile> tiles = BuildTiles();
            if (tiles.Count > 0 || sens.AcerNeedsAdmin)
            {
                y = Separator(g, x0, x1, y);
                y = SensorSection(g, tiles, x0, x1, y);
            }
            y = Separator(g, x0, x1, y);
            y = MemorySection(g, x0, x1, y);
            if (mon.Gpus.Count > 0)
            {
                y = Separator(g, x0, x1, y);
                for (int i = 0; i < mon.Gpus.Count; i++)
                {
                    if (i > 0) y += 10 * S;
                    y = GpuRow(g, mon.Gpus[i], i, x0, x1, y);
                }
            }
            y = Separator(g, x0, x1, y);
            y = IoSection(g, x0, x1, y);
            if (detailed)
            {
                y = Separator(g, x0, x1, y);
                y = ProcessSection(g, x0, x1, y);
            }
            y += 12 * S;
            y = Footer(g, x0, x1, y);
            return y + 10 * S;
        }

        float Separator(Graphics g, float x0, float x1, float y)
        {
            y += 10 * S;
            g.FillRectangle(B(DividerC), x0, y, x1 - x0, Math.Max(1f, S));
            return y + 10 * S;
        }

        float CpuSection(Graphics g, float x0, float x1, float y)
        {
            float cw = x1 - x0;
            Txt(g, "CPU", fLabel, CCpu, x0, y, 0, 0);
            Txt(g, mon.CpuName, fSmall, TextLo, x1, y - 0.5f * S, 2, cw - 40 * S);
            y += 15 * S;
            Txt(g, Pct(mon.Cpu), fBig, TextHi, x0 - 1.5f * S, y - 3 * S, 0, 0);
            Txt(g, Ghz(mon.CpuMhz), fBody, TextHi, x1, y + 2 * S, 2, 0);
            Txt(g, CoreSummary(), fSmall, TextMid, x1, y + 19 * S, 2, cw - 80 * S);
            y += 38 * S;
            Spark(g, mon.CpuHist, new RectangleF(x0, y, cw, 34 * S), CCpu);
            y += 42 * S;
            y = CoreBars(g, x0, cw, y, 34 * S, true);
            string info = hoverCore >= 0
                ? CoreDetail(hoverCore)
                : string.Format("{0}/{1} threads busy  ·  1-min avg {2:0}%  ·  {3:N0} processes",
                    mon.ActiveThreads, mon.Logical, Math.Max(0, mon.CpuAvg1m), mon.Processes);
            Txt(g, info, fSmall, hoverCore >= 0 ? TextHi : TextMid, x0, y, 0, cw);
            return y + 14 * S;
        }

        float CoreBars(Graphics g, float x0, float cw, float y, float bh, bool labels)
        {
            int n = mon.Logical;
            float gap = n > 24 ? 1.5f * S : n > 12 ? 2 * S : 3 * S;
            float bw = (cw - gap * (n - 1)) / n;
            float rad = Math.Min(3 * S, bw / 2);
            for (int i = 0; i < n; i++)
            {
                var r = new RectangleF(x0 + i * (bw + gap), y, bw, bh);
                coreRects.Add(new RectangleF(r.X - gap / 2, r.Y - 2 * S, r.Width + gap, r.Height + (labels ? 16 * S : 4 * S)));
                FillRound(g, B(Track), r, rad);
                float v = mon.CoreLoad[i];
                float fh = Math.Max(2 * S, v / 100f * bh);
                FillRound(g, B(LoadColor(v)), new RectangleF(r.X, r.Bottom - fh, r.Width, fh), Math.Min(rad, fh / 2));
                if (i == hoverCore)
                {
                    using (var pen = new Pen(Color.FromArgb(200, 255, 255, 255), Math.Max(1f, S)))
                    using (GraphicsPath path = RoundRect(r, rad)) g.DrawPath(pen, path);
                }
            }
            y += bh;
            if (labels && bw >= 7 * S)
            {
                y += 2 * S;
                for (int i = 0; i < n; i++)
                {
                    LpInfo lp = mon.Lps[i];
                    string t = mon.Hybrid ? (lp.Perf ? "P" : "E") : (n <= 16 ? i.ToString() : "");
                    float cx = x0 + i * (bw + gap) + bw / 2;
                    Txt(g, t, fTiny, i == hoverCore ? TextHi : lp.Perf ? TextMid : TextLo, cx, y, 1, 0);
                }
                y += 14 * S;
            }
            else y += 7 * S;
            return y;
        }

        sealed class Tile
        {
            public readonly string Label, Value, Unit;
            public readonly Color Accent, ValueColor;
            public readonly History Hist;
            public readonly double HistMax;
            public Tile(string label, string value, string unit, Color accent, Color valueColor, History hist, double histMax)
            {
                Label = label; Value = value; Unit = unit; Accent = accent; ValueColor = valueColor; Hist = hist; HistMax = histMax;
            }
        }

        static Color TempColor(double t) { return LoadColor((float)((t - 40) * 2)); }   // 40 °C green ... 90 °C red

        static string TimeLeft(double hours)
        {
            if (double.IsNaN(hours) || double.IsInfinity(hours) || hours <= 0 || hours > 48) return "";
            int m = (int)Math.Round(hours * 60);
            return m >= 60 ? string.Format("{0}h {1:00}m", m / 60, m % 60) : m + "m";
        }

        List<Tile> BuildTiles()
        {
            var list = new List<Tile>();
            double ct = sens.CpuTemp;
            if (!double.IsNaN(ct))
                list.Add(new Tile("CPU temp", ct.ToString("0"), "°C", TempColor(ct), TempColor(ct), hCpuT, 100));
            else if (!double.IsNaN(mon.AcpiTemp))
                list.Add(new Tile("Thermal zone", mon.AcpiTemp.ToString("0"), "°C", TempColor(mon.AcpiTemp), TempColor(mon.AcpiTemp), hCpuT, 100));
            double gt = GpuTempBest();
            if (!double.IsNaN(gt))
                list.Add(new Tile("GPU temp", gt.ToString("0"), "°C", TempColor(gt), TempColor(gt), hGpuT, 100));
            if (!double.IsNaN(sens.SysTemp))
                list.Add(new Tile("System temp", sens.SysTemp.ToString("0"), "°C", TempColor(sens.SysTemp), TempColor(sens.SysTemp), hSysT, 100));
            if (!double.IsNaN(sens.CpuFan))
                list.Add(new Tile("CPU fan", sens.CpuFan.ToString("0"), "rpm", CFan, TextHi, hCpuFan, maxFan));
            if (!double.IsNaN(sens.GpuFan))
                list.Add(new Tile("GPU fan", sens.GpuFan.ToString("0"), "rpm", CFan, TextHi, hGpuFan, maxFan));
            else if (!double.IsNaN(sens.NvFanPct))
                list.Add(new Tile("GPU fan", sens.NvFanPct.ToString("0"), "%", CFan, TextHi, null, 100));
            if (!double.IsNaN(mon.CpuPkgW))
                list.Add(new Tile("CPU power", mon.CpuPkgW.ToString("0.0"), "W", CPower, TextHi, hCpuW, maxCpuW));
            if (!double.IsNaN(sens.NvPowerW))
                list.Add(new Tile(double.IsNaN(sens.NvClockMhz) ? "dGPU power" : "dGPU " + sens.NvClockMhz.ToString("0") + " MHz",
                    sens.NvPowerW.ToString("0.0"), "W", CPower, TextHi, hGpuW, maxGpuW));
            if (mon.HasBattery && !double.IsNaN(sens.BatRateW))
            {
                double w = sens.BatRateW;
                if (w < -0.05)
                {
                    string left = TimeLeft(sens.BatRemainWh / -w);
                    list.Add(new Tile(left.Length > 0 ? "Battery " + left : "On battery", "−" + (-w).ToString("0.0"), "W", CNet, TextHi, hBatW, 0));
                }
                else if (w > 0.05)
                    list.Add(new Tile("Charging", "+" + w.ToString("0.0"), "W", CGpu, TextHi, hBatW, 0));
                else if (mon.BatteryPct >= 0)
                    list.Add(new Tile(sens.BatOnline || mon.OnAC ? "Battery, on AC" : "Battery", mon.BatteryPct.ToString(), "%", CGpu, TextHi, null, 0));
            }
            return list;
        }

        float SensorSection(Graphics g, List<Tile> tiles, float x0, float x1, float y)
        {
            float cw = x1 - x0;
            Txt(g, "THERMALS  ·  FANS  ·  POWER", fLabel, CTemp, x0, y, 0, 0);
            string thr; Color tc;
            if (mon.PassiveLimit < 100) { thr = string.Format("Thermal limit {0:0}%", mon.PassiveLimit); tc = Bad; }
            else if (mon.PerfLimit < 99) { thr = string.Format("Speed limited {0:0}%", mon.PerfLimit); tc = Warn; }
            else { thr = "No throttling"; tc = TextLo; }
            Txt(g, thr, fSmall, tc, x1, y - 0.5f * S, 2, 0);
            y += 18 * S;

            if (tiles.Count > 0)
            {
                const int cols = 3;
                float gap = 6 * S, tw = (cw - gap * (cols - 1)) / cols, th = 46 * S;
                for (int i = 0; i < tiles.Count; i++)
                {
                    Tile t = tiles[i];
                    var r = new RectangleF(x0 + (i % cols) * (tw + gap), y + (i / cols) * (th + gap), tw, th);
                    FillRound(g, B(PanelC), r, 6 * S);
                    if (t.Hist != null) SparkTile(g, t.Hist, r, t.Accent, t.HistMax);
                    Txt(g, t.Label, fTiny, TextMid, r.X + 8 * S, r.Y + 6 * S, 0, tw - 12 * S);
                    Txt(g, t.Value, fMid, t.ValueColor, r.X + 7 * S, r.Y + 19 * S, 0, 0);
                    float vw = Measure(g, t.Value, fMid);
                    Txt(g, t.Unit, fTiny, TextMid, r.X + 7 * S + vw + 3 * S, r.Y + 26 * S, 0, 0);
                }
                int rows = (tiles.Count + cols - 1) / cols;
                y += rows * th + (rows - 1) * gap;
            }
            if (sens.AcerNeedsAdmin)
            {
                y += 7 * S;
                Txt(g, "Fans + CPU temp need admin: right-click › Enable temps & fans", fTiny, TextLo, x0, y, 0, cw);
                y += 13 * S;
            }
            return y;
        }

        void SparkTile(Graphics g, History h, RectangleF r, Color c, double max)
        {
            if (h.Count < 2) return;
            if (max <= 0)
            {
                for (int i = 0; i < h.Count; i++) max = Math.Max(max, h[i]);
                max = Math.Max(1, max * 1.15);
            }
            float dx = r.Width / (h.Capacity - 1);
            float startX = r.Right - (h.Count - 1) * dx;
            float top = r.Y + r.Height * 0.38f, span = r.Bottom - 2 * S - top;
            var pts = new PointF[h.Count];
            for (int i = 0; i < h.Count; i++)
            {
                float v = (float)Math.Max(0, Math.Min(1, h[i] / max));
                pts[i] = new PointF(startX + i * dx, top + span - v * span);
            }
            GraphicsState state = g.Save();
            using (GraphicsPath clip = RoundRect(r, 6 * S)) g.SetClip(clip);
            using (var area = new GraphicsPath())
            {
                area.AddLines(pts);
                area.AddLine(pts[pts.Length - 1].X, pts[pts.Length - 1].Y, pts[pts.Length - 1].X, r.Bottom);
                area.AddLine(pts[pts.Length - 1].X, r.Bottom, pts[0].X, r.Bottom);
                area.CloseFigure();
                using (var fill = new LinearGradientBrush(new RectangleF(r.X, top - 1, r.Width, r.Bottom - top + 2),
                    Color.FromArgb(48, c), Color.FromArgb(0, c), LinearGradientMode.Vertical))
                    g.FillPath(fill, area);
            }
            using (var pen = new Pen(Color.FromArgb(150, c), 1.2f * S))
            {
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, pts);
            }
            g.Restore(state);
        }

        float MemorySection(Graphics g, float x0, float x1, float y)
        {
            float cw = x1 - x0;
            Txt(g, "MEMORY", fLabel, CMem, x0, y, 0, 0);
            Txt(g, string.Format("Committed {0} / {1} GB", Gb(mon.CommitUsed), Gb(mon.CommitLimit)), fSmall, TextLo, x1, y - 0.5f * S, 2, 0);
            y += 15 * S;
            Txt(g, Pct(mon.MemPct), fBig, TextHi, x0 - 1.5f * S, y - 3 * S, 0, 0);
            Txt(g, Gb(mon.MemUsed) + " / " + Gb(mon.MemTotal) + " GB", fBody, TextHi, x1, y + 2 * S, 2, 0);
            Txt(g, Gb(mon.MemAvail) + " GB available", fSmall, TextMid, x1, y + 19 * S, 2, 0);
            y += 38 * S;
            Bar(g, new RectangleF(x0, y, cw, 5 * S), mon.MemPct / 100f, CMem);
            y += 11 * S;
            Spark(g, mon.MemHist, new RectangleF(x0, y, cw, 26 * S), CMem);
            return y + 26 * S;
        }

        float GpuRow(Graphics g, Gpu gpu, int index, float x0, float x1, float y)
        {
            float cw = x1 - x0;
            string label = "GPU " + index;
            Txt(g, label, fLabel, CGpu, x0, y, 0, 0);
            float lw = Measure(g, label, fLabel);
            Txt(g, gpu.Name, fSmall, TextMid, x0 + lw + 6 * S, y - 0.5f * S, 0, cw * 0.5f);
            string mem = gpu.DedTotal >= 512.0 * 1048576
                ? string.Format("VRAM {0} / {1} GB", Gb(gpu.DedUsed), Gb(gpu.DedTotal))
                : "Shared " + Gb(gpu.SharedUsed) + " GB";
            Txt(g, mem, fSmall, TextLo, x1, y - 0.5f * S, 2, 0);
            y += 16 * S;
            Txt(g, Pct(gpu.Util), fMid, TextHi, x0 - 1 * S, y, 0, 0);
            Txt(g, gpu.Util >= 1 && gpu.Engine.Length > 0 ? gpu.Engine : "idle", fTiny, TextLo, x0, y + 22 * S, 0, 58 * S);
            Spark(g, gpu.Hist, new RectangleF(x0 + 64 * S, y + 1 * S, cw - 64 * S, 32 * S), CGpu);
            return y + 34 * S;
        }

        float IoSection(Graphics g, float x0, float x1, float y)
        {
            float cw = x1 - x0, gap = 14 * S, colW = (cw - gap) / 2, xa = x0, xb = x0 + colW + gap;
            Txt(g, "DISK", fLabel, CDisk, xa, y, 0, 0);
            Txt(g, "NETWORK", fLabel, CNet, xb, y, 0, 0);
            y += 15 * S;
            Txt(g, Pct(mon.DiskActive), fMid, TextHi, xa - 1 * S, y, 0, colW);
            Txt(g, "↓ " + Rate(mon.NetRx), fMid, TextHi, xb - 1 * S, y, 0, colW);
            y += 23 * S;
            Txt(g, DiskRw(), fSmall, TextMid, xa, y, 0, colW);
            Txt(g, "↑ " + Rate(mon.NetTx) + " up", fSmall, TextMid, xb, y, 0, colW);
            return y + 14 * S;
        }

        float ProcessSection(Graphics g, float x0, float x1, float y)
        {
            float cw = x1 - x0;
            Txt(g, "TOP PROCESSES", fLabel, TextMid, x0, y, 0, 0);
            float wm = Measure(g, "MEM", fLabel), wc = Measure(g, "CPU", fLabel);
            float tx = x1 - wm;
            tabMem = new RectangleF(tx - 5 * S, y - 4 * S, wm + 10 * S, 20 * S);
            Txt(g, "MEM", fLabel, cfg.ProcSort == 1 ? CMem : TextLo, tx, y, 0, 0);
            if (cfg.ProcSort == 1) g.FillRectangle(B(CMem), tx, y + 14 * S, wm, 1.5f * S);
            tx -= 14 * S + wc;
            tabCpu = new RectangleF(tx - 5 * S, y - 4 * S, wc + 10 * S, 20 * S);
            Txt(g, "CPU", fLabel, cfg.ProcSort == 0 ? CCpu : TextLo, tx, y, 0, 0);
            if (cfg.ProcSort == 0) g.FillRectangle(B(CCpu), tx, y + 14 * S, wc, 1.5f * S);
            y += 21 * S;

            const int rows = 6;
            float rh = 19 * S;
            List<ProcInfo> list = cfg.ProcSort == 0 ? mon.TopCpu : mon.TopMem;
            if (!mon.ProcReady || list.Count == 0)
            {
                Txt(g, "Collecting process data…", fSmall, TextLo, x0, y + 3 * S, 0, cw);
            }
            else
            {
                Color accent = cfg.ProcSort == 0 ? CCpu : CMem;
                double max = 0.0001;
                foreach (ProcInfo pi in list) max = Math.Max(max, cfg.ProcSort == 0 ? pi.Cpu : pi.Mem);
                if (cfg.ProcSort == 0) max = Math.Max(max, 10);
                for (int k = 0; k < Math.Min(rows, list.Count); k++)
                {
                    ProcInfo pi = list[k];
                    float ry = y + k * rh;
                    double val = cfg.ProcSort == 0 ? pi.Cpu : pi.Mem;
                    float frac = (float)Math.Min(1, val / max);
                    FillRound(g, B(Color.FromArgb(30, accent)), new RectangleF(x0, ry + 1 * S, Math.Max(3 * S, cw * frac), rh - 3 * S), 3 * S);
                    string nm = pi.Name + (pi.Count > 1 ? "  ×" + pi.Count : "");
                    Txt(g, nm, fSmall, TextHi, x0 + 6 * S, ry + 3.5f * S, 0, cw - 124 * S);
                    Txt(g, CpuText(pi.Cpu), fSmall, cfg.ProcSort == 0 ? TextHi : TextMid, x1 - 66 * S, ry + 3.5f * S, 2, 0);
                    Txt(g, Mb(pi.Mem), fSmall, cfg.ProcSort == 1 ? TextHi : TextMid, x1 - 6 * S, ry + 3.5f * S, 2, 0);
                }
            }
            return y + rows * rh;
        }

        float Footer(Graphics g, float x0, float x1, float y)
        {
            float cw = x1 - x0;
            string text; Color dot;
            Insight(out text, out dot);
            var box = new RectangleF(x0, y, cw, 26 * S);
            FillRound(g, B(PanelC), box, 7 * S);
            g.FillEllipse(B(dot), x0 + 10 * S, y + 10 * S, 6 * S, 6 * S);
            Txt(g, text, fSmall, TextHi, x0 + 23 * S, y + 6.5f * S, 0, cw - 30 * S);
            y += 33 * S;
            Txt(g, string.Format("Widget cost: {0:0.0}% CPU  ·  {1:0} MB RAM  ·  every {2}", selfCpu, selfMem / 1048576.0,
                cfg.Interval < 1000 ? "0.5 s" : (cfg.Interval / 1000) + " s"), fTiny, TextLo, x0, y, 0, cw);
            return y + 13 * S;
        }

        float PaintCompact(Graphics g)
        {
            float p = 12 * S, x0 = p, x1 = ClientSize.Width - p, cw = x1 - x0, y = 12 * S;
            float d = 60 * S, sp = (cw - 3 * d) / 2;
            float gpu = 0;
            foreach (Gpu gg in mon.Gpus) gpu = Math.Max(gpu, gg.Util);
            Ring(g, x0, y, d, mon.Cpu, CCpu, "CPU");
            Ring(g, x0 + d + sp, y, d, mon.MemPct, CMem, "RAM");
            Ring(g, x0 + 2 * (d + sp), y, d, gpu, CGpu, "GPU");
            y += d + 8 * S;
            y = CoreBars(g, x0, cw, y, 18 * S, false);
            double ct = CpuTempBest();
            string info;
            if (hoverCore >= 0) info = CoreDetail(hoverCore);
            else if (!double.IsNaN(ct) && !double.IsNaN(sens.CpuFan))
                info = string.Format("{0}  ·  {1:0}°C  ·  fan {2:0} rpm", Ghz(mon.CpuMhz), ct, sens.CpuFan);
            else if (!double.IsNaN(ct))
                info = string.Format("{0}  ·  {1:0}°C  ·  {2}/{3} busy", Ghz(mon.CpuMhz), ct, mon.ActiveThreads, mon.Logical);
            else
                info = string.Format("{0}  ·  {1}/{2} busy  ·  ↓ {3}", Ghz(mon.CpuMhz), mon.ActiveThreads, mon.Logical, Rate(mon.NetRx));
            Txt(g, info, fSmall, hoverCore >= 0 ? TextHi : TextMid, x0 + cw / 2, y, 1, cw);
            if (mouseInside) HeaderButtons(g, ClientSize.Width - 4 * S, 4 * S, true);
            return y + 15 * S + 9 * S;
        }

        void Ring(Graphics g, float x, float y, float d, float v, Color c, string label)
        {
            float pw = 5.5f * S;
            var r = new RectangleF(x + pw / 2, y + pw / 2, d - pw, d - pw);
            using (var pt = new Pen(Track, pw))
            {
                pt.StartCap = pt.EndCap = LineCap.Round;
                g.DrawArc(pt, r, 135, 270);
            }
            float sweep = 270f * Math.Max(0, Math.Min(100, v)) / 100f;
            if (sweep > 0.5f)
            {
                using (var pv = new Pen(c, pw))
                {
                    pv.StartCap = pv.EndCap = LineCap.Round;
                    g.DrawArc(pv, r, 135, sweep);
                }
            }
            g.DrawString(Pct(v), fMid, B(TextHi), new RectangleF(x, y - 2 * S, d, d), sfCC);
            Txt(g, label, fTiny, TextMid, x + d / 2, y + d - 13 * S, 1, 0);
        }

        void Spark(Graphics g, History h, RectangleF r, Color c)
        {
            float rad = 4 * S;
            FillRound(g, B(SparkBg), r, rad);
            g.FillRectangle(B(Color.FromArgb(14, 255, 255, 255)), r.X + 4 * S, r.Y + r.Height / 2, r.Width - 8 * S, Math.Max(1f, S * 0.75f));
            if (h.Count < 2) return;
            float dx = r.Width / (h.Capacity - 1);
            float startX = r.Right - (h.Count - 1) * dx;
            float top = r.Y + 2 * S, span = r.Height - 3.5f * S;
            var pts = new PointF[h.Count];
            for (int i = 0; i < h.Count; i++)
            {
                float v = Math.Max(0, Math.Min(100, h[i]));
                pts[i] = new PointF(startX + i * dx, top + span - v / 100f * span);
            }
            GraphicsState state = g.Save();
            using (GraphicsPath clip = RoundRect(r, rad)) g.SetClip(clip);
            using (var area = new GraphicsPath())
            {
                area.AddLines(pts);
                area.AddLine(pts[pts.Length - 1].X, pts[pts.Length - 1].Y, pts[pts.Length - 1].X, r.Bottom);
                area.AddLine(pts[pts.Length - 1].X, r.Bottom, pts[0].X, r.Bottom);
                area.CloseFigure();
                using (var fill = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2),
                    Color.FromArgb(80, c), Color.FromArgb(0, c), LinearGradientMode.Vertical))
                    g.FillPath(fill, area);
            }
            using (var pen = new Pen(c, 1.5f * S))
            {
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, pts);
            }
            g.Restore(state);
        }

        void Bar(Graphics g, RectangleF r, float frac, Color c)
        {
            float rad = r.Height / 2;
            FillRound(g, B(Track), r, rad);
            frac = Math.Max(0, Math.Min(1, frac));
            if (frac > 0) FillRound(g, B(c), new RectangleF(r.X, r.Y, Math.Max(r.Height, r.Width * frac), r.Height), rad);
        }

        void DrawBattery(Graphics g, float x, float y)
        {
            float w = 15 * S, h = 8 * S;
            using (var pen = new Pen(TextLo, Math.Max(1f, S)))
                g.DrawRectangle(pen, x, y, w, h);
            g.FillRectangle(B(TextLo), x + w, y + 2.5f * S, 1.6f * S, 3 * S);
            Color fill = mon.Charging ? CCpu : (mon.BatteryPct <= 20 && !mon.OnAC ? Bad : TextMid);
            float iw = (w - 3 * S) * Math.Max(0, Math.Min(100, mon.BatteryPct)) / 100f;
            g.FillRectangle(B(fill), x + 1.5f * S, y + 1.5f * S, Math.Max(1f, iw), h - 3 * S);
        }

        // ============================== text helpers ==============================

        void Txt(Graphics g, string s, Font f, Color c, float x, float y, int align, float maxW)
        {
            float w = maxW > 0 ? maxW : 2000;
            float h = f.GetHeight() * 1.4f;
            RectangleF r;
            StringFormat sf;
            if (align == 0) { r = new RectangleF(x, y, w, h); sf = sfL; }
            else if (align == 1) { r = new RectangleF(x - w / 2, y, w, h); sf = sfC; }
            else { r = new RectangleF(x - w, y, w, h); sf = sfR; }
            g.DrawString(s, f, B(c), r, sf);
        }

        float Measure(Graphics g, string s, Font f)
        {
            return g.MeasureString(s, f, PointF.Empty, sfL).Width;
        }

        SolidBrush B(Color c)
        {
            int k = c.ToArgb();
            SolidBrush b;
            if (!brushes.TryGetValue(k, out b))
            {
                if (brushes.Count > 256)
                {
                    foreach (SolidBrush old in brushes.Values) old.Dispose();
                    brushes.Clear();
                }
                b = new SolidBrush(c);
                brushes[k] = b;
            }
            return b;
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
            if (rad < 0.5f) { p.AddRectangle(r); return p; }
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static void FillRound(Graphics g, Brush b, RectangleF r, float rad)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath p = RoundRect(r, rad)) g.FillPath(b, p);
        }

        static Color LoadColor(float v)
        {
            v = (float)Math.Round(Math.Max(0, Math.Min(100, v)));
            return v < 50 ? Lerp(Good, Warn, v / 50f) : Lerp(Warn, Bad, (v - 50) / 50f);
        }

        static Color Lerp(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        // ============================== wording ==============================

        string CoreSummary()
        {
            return mon.Hybrid
                ? string.Format("{0} cores ({1}P + {2}E)  ·  {3} threads", mon.PhysicalCores, mon.PCores, mon.ECores, mon.Logical)
                : string.Format("{0} cores  ·  {1} threads", mon.PhysicalCores, mon.Logical);
        }

        string CoreDetail(int i)
        {
            LpInfo lp = mon.Lps[i];
            string kind = mon.Hybrid ? (lp.Perf ? "P-core " : "E-core ") + lp.TypeIndex : "Core " + lp.Core;
            if (lp.ThreadsInCore > 1) kind += ", thread " + (lp.Thread + 1);
            return string.Format("CPU {0}  ·  {1}  ·  {2:0}% busy", i, kind, mon.CoreLoad[i]);
        }

        void Insight(out string s, out Color c)
        {
            ProcInfo topC = mon.ProcReady && mon.TopCpu.Count > 0 ? mon.TopCpu[0] : null;
            ProcInfo topM = mon.ProcReady && mon.TopMem.Count > 0 ? mon.TopMem[0] : null;
            Gpu busy = null;
            foreach (Gpu g in mon.Gpus) if (busy == null || g.Util > busy.Util) busy = g;

            double ct = CpuTempBest();
            if (!double.IsNaN(ct) && ct >= 95)
            {
                c = Bad;
                s = double.IsNaN(sens.CpuFan)
                    ? string.Format("CPU very hot: {0:0} °C", ct)
                    : string.Format("CPU very hot: {0:0} °C, fan at {1:0} rpm", ct, sens.CpuFan);
                return;
            }
            if (mon.PassiveLimit < 100)
            {
                c = Bad;
                s = string.Format("Thermal throttling: CPU capped at {0:0}% speed", mon.PassiveLimit);
                return;
            }
            if (mon.Cpu >= 85)
            {
                c = Bad;
                s = topC != null && topC.Cpu >= 5
                    ? string.Format("CPU maxed: {0} is using {1:0}%", topC.Name, topC.Cpu)
                    : string.Format("CPU maxed at {0:0}% across {1}/{2} threads", mon.Cpu, mon.ActiveThreads, mon.Logical);
                return;
            }
            if (mon.MemPct >= 85)
            {
                c = Bad;
                s = topM != null
                    ? string.Format("RAM tight ({0:0}%): {1} holds {2}", mon.MemPct, topM.Name, Mb(topM.Mem))
                    : string.Format("RAM tight: only {0} GB free", Gb(mon.MemAvail));
                return;
            }
            if (busy != null && busy.Util >= 80)
            {
                c = Warn;
                s = string.Format("{0} busy at {1:0}% ({2})", busy.Name, busy.Util, busy.Engine);
                return;
            }
            if (mon.DiskActive >= 80)
            {
                c = Warn;
                s = string.Format("Disk is the bottleneck: {0:0}% active", mon.DiskActive);
                return;
            }
            if (mon.Cpu >= 60 && mon.BaseMhz > 0 && mon.CpuMhz > 0 && mon.CpuMhz < mon.BaseMhz * 0.75f)
            {
                c = Warn;
                s = string.Format("Clock held at {0} under load: power or heat limit", Ghz(mon.CpuMhz));
                return;
            }
            if (mon.Cpu >= 40)
            {
                c = Warn;
                s = topC != null
                    ? string.Format("Working: {0} leads at {1:0}% CPU", topC.Name, topC.Cpu)
                    : string.Format("Working: {0}/{1} threads busy", mon.ActiveThreads, mon.Logical);
                return;
            }
            c = Good;
            s = topC != null && topC.Cpu >= 1
                ? string.Format("Light load  ·  top: {0} at {1:0.0}% CPU", topC.Name, topC.Cpu)
                : string.Format("Light load  ·  {0}/{1} threads busy", mon.ActiveThreads, mon.Logical);
        }

        static string Pct(float v) { return Math.Max(0, Math.Min(100, v)).ToString("0") + "%"; }
        static string Gb(double bytes) { return (bytes / 1073741824.0).ToString("0.0"); }
        static string Ghz(float mhz) { return mhz > 0 ? (mhz / 1000f).ToString("0.00") + " GHz" : "- GHz"; }
        static string CpuText(double v) { return v < 10 ? v.ToString("0.0") + "%" : v.ToString("0") + "%"; }

        static string Mb(double bytes)
        {
            return bytes < 1073741824.0 ? (bytes / 1048576.0).ToString("0") + " MB" : (bytes / 1073741824.0).ToString("0.0") + " GB";
        }

        static void RateParts(double b, out string num, out string unit)
        {
            if (b < 1024.0 * 1000) { num = (b / 1024.0).ToString("0"); unit = "KB/s"; }
            else if (b < 1048576.0 * 1000) { num = (b / 1048576.0).ToString("0.0"); unit = "MB/s"; }
            else { num = (b / 1073741824.0).ToString("0.00"); unit = "GB/s"; }
        }

        static string Rate(double b)
        {
            string n, u;
            RateParts(b, out n, out u);
            return n + " " + u;
        }

        string DiskRw()
        {
            string rn, ru, wn, wu;
            RateParts(mon.DiskRead, out rn, out ru);
            RateParts(mon.DiskWrite, out wn, out wu);
            return ru == wu
                ? string.Format("R {0}  ·  W {1} {2}", rn, wn, ru)
                : string.Format("R {0} {1}  ·  W {2} {3}", rn, ru, wn, wu);
        }

        static string Uptime()
        {
            TimeSpan t = TimeSpan.FromMilliseconds(Native.GetTickCount64());
            if (t.TotalDays >= 1) return string.Format("{0}d {1}h", (int)t.TotalDays, t.Hours);
            if (t.TotalHours >= 1) return string.Format("{0}h {1}m", (int)t.TotalHours, t.Minutes);
            return string.Format("{0}m", Math.Max(1, t.Minutes));
        }
    }

    // ============================== dark context menu ==============================

    sealed class DarkColors : ProfessionalColorTable
    {
        static readonly Color Bg = Color.FromArgb(27, 30, 37);
        static readonly Color Hi = Color.FromArgb(44, 49, 60);
        static readonly Color Edge = Color.FromArgb(52, 57, 68);
        public override Color ToolStripDropDownBackground { get { return Bg; } }
        public override Color ImageMarginGradientBegin { get { return Bg; } }
        public override Color ImageMarginGradientMiddle { get { return Bg; } }
        public override Color ImageMarginGradientEnd { get { return Bg; } }
        public override Color MenuBorder { get { return Edge; } }
        public override Color MenuItemBorder { get { return Hi; } }
        public override Color MenuItemSelected { get { return Hi; } }
        public override Color MenuItemSelectedGradientBegin { get { return Hi; } }
        public override Color MenuItemSelectedGradientEnd { get { return Hi; } }
        public override Color MenuItemPressedGradientBegin { get { return Hi; } }
        public override Color MenuItemPressedGradientMiddle { get { return Hi; } }
        public override Color MenuItemPressedGradientEnd { get { return Hi; } }
        public override Color SeparatorDark { get { return Edge; } }
        public override Color SeparatorLight { get { return Bg; } }
        public override Color CheckBackground { get { return Bg; } }
        public override Color CheckSelectedBackground { get { return Hi; } }
        public override Color CheckPressedBackground { get { return Hi; } }
    }

    sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(230, 233, 239) : Color.FromArgb(110, 115, 125);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(180, 185, 195);
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = e.ImageRectangle;
            using (var p = new Pen(Color.FromArgb(34, 211, 238), Math.Max(1.5f, r.Height / 8f)))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                g.DrawLines(p, new[]
                {
                    new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
                    new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                    new PointF(r.Left + r.Width * 0.80f, r.Top + r.Height * 0.30f)
                });
            }
            g.SmoothingMode = old;
        }
    }
}
