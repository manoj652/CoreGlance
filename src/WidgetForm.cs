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
        const int WS_EX_APPWINDOW = 0x40000, WS_MINIMIZEBOX = 0x20000, WS_SYSMENU = 0x80000;
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
        readonly HealthMonitor health;
        HealthWindow healthWin;
        RectangleF btnHealth;
        readonly bool healthSnapshot;
        System.Threading.RegisteredWaitHandle showWait;
        ToolStripMenuItem miTaskbar;
        bool balloonIsHealth;
        readonly History hCpuT = new History(60), hGpuT = new History(60), hSysT = new History(60), hCoreT = new History(60), hMidFan = new History(60);
        readonly History hCpuFan = new History(60), hGpuFan = new History(60), hCpuW = new History(60), hGpuW = new History(60), hBatW = new History(60);
        double maxFan = 5000, maxCpuW = 15, maxGpuW = 15;
        ToolStripMenuItem miSensors, miPawn, miCpuBusy, miCpuUtil, miLowOff, miLowSaver, miLowMax, miLowAuto;
        bool lowBusy, lastOnAC = true;
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
        RectangleF tabCpu, tabMem, tabGpu, btnView, btnMin, btnPower;
        readonly RectangleF[] modeRects = new RectangleF[3];
        double drawW = double.NaN, cpuW = double.NaN;     // smoothed battery drain and CPU package power
        DateTime lastEnergy = DateTime.MinValue, platformSaved = DateTime.MinValue;
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

        public WidgetForm(string snapshot) : this(snapshot, false) { }

        public WidgetForm(string snapshot, bool healthReport)
        {
            snapshotPath = snapshot;
            healthSnapshot = healthReport;
            cfg = Settings.Load();
            if (snapshotPath != null)
            {
                cfg.Visible = false; cfg.FirstRun = false; cfg.InTaskbar = false;
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--as-low-power") >= 0) cfg.LowPower = true;   // render only; changes nothing
            }
            else if (Program.OpenedByUser) cfg.Visible = true;   // opening the app always shows it

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = cfg.InTaskbar;
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
            var physCores = new List<KeyValuePair<int, int>>();
            foreach (LpInfo lp in mon.Lps) if (lp.Thread == 0) physCores.Add(new KeyValuePair<int, int>(lp.Group, lp.Number));
            sens = new Sensors(physCores);
            health = new HealthMonitor(snapshotPath == null, sens.Elevated);
            BuildMenu();
            ContextMenuStrip = menu;

            tray = new NotifyIcon();
            tray.Text = "CoreGlance";
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ToggleVisible(); };
            tray.BalloonTipClicked += delegate { if (balloonIsHealth) ShowHealth(); };
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
            if (cfg.LowPower && snapshotPath == null && !LowPower.IsActive())
            {
                cfg.LowPower = false;   // the plan was changed elsewhere (Windows settings, or the copy was removed)
                cfg.LowPowerByAuto = false;
                cfg.Save();
            }
            timer.Interval = EffectiveInterval();
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
                if (cfg != null && cfg.InTaskbar)
                {
                    cp.ExStyle |= WS_EX_APPWINDOW;
                    cp.Style |= WS_MINIMIZEBOX | WS_SYSMENU;   // lets the taskbar button minimize and restore it
                }
                else cp.ExStyle |= WS_EX_TOOLWINDOW;   // no Alt+Tab / taskbar entry
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
            if (cfg.InTaskbar) TaskbarId.SetWindow(Handle);
            if (showWait == null && snapshotPath == null && Program.ShowEvent != null)
                showWait = System.Threading.ThreadPool.RegisterWaitForSingleObject(Program.ShowEvent, delegate
                {
                    try { BeginInvoke((MethodInvoker)ShowWidget); } catch { }   // handle may be mid-recreation or closing
                }, null, -1, false);
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
                if (showWait != null) showWait.Unregister(null);
                if (IsHandleCreated) Native.UnregisterHotKey(Handle, HOTKEY_ID);
                timer.Dispose();
                tray.Visible = false;
                tray.Dispose();
                mon.Dispose();
                sens.Dispose();
                health.Dispose();
                if (healthWin != null && !healthWin.IsDisposed) healthWin.Dispose();
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
            bool visible = (Visible && !IsMinimized) || snapshotPath != null;
            bool wantProcs = visible && cfg.View != 0 && (cfg.View == 2 ? (tick % 2 == 0 || cfg.Interval >= 2000) : tick % 5 == 0);
            try { mon.Sample(wantProcs); }
            catch (Exception ex) { Settings.Log("sample: " + ex); }
            sens.NvidiaActive = mon.NvidiaActive;
            RecordSensorHistory();
            UpdateEnergyModel();
            health.Tick(mon, sens, CpuTempBest());
            if (snapshotPath == null && (DateTime.Now - health.Evaluated).TotalSeconds >= 30 && tick >= 10) EvaluateHealth();

            if (tick % 3 == 0) UpdateSelfUsage();
            if (snapshotPath != null) { if (tick >= SnapshotTicks()) TakeSnapshot(); return; }
            UpdateTray();
            CheckAutoLowPower();
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

        void EvaluateHealth()
        {
            try { health.Evaluate(mon, sens, CpuTempBest()); }
            catch (Exception ex) { Settings.Log("health evaluate: " + ex); return; }
            List<HealthFlag> alerts = health.TakeAlerts();
            if (alerts.Count == 0) return;
            HealthFlag top = alerts[0];
            balloonIsHealth = true;
            tray.ShowBalloonTip(8000, "PC health: " + top.Title,
                top.Action + (alerts.Count > 1 ? string.Format("  (+{0} more: click to open the report)", alerts.Count - 1) : "  (click for the full report)"),
                top.Level == Severity.Critical ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }

        void ShowHealth()
        {
            if (healthWin == null || healthWin.IsDisposed)
            {
                try { health.Evaluate(mon, sens, CpuTempBest()); } catch (Exception ex) { Settings.Log("health evaluate: " + ex); }
                healthWin = new HealthWindow(health, CpuTempBest, delegate { return (double)mon.CpuMhz; },
                    delegate { return mon.OnAC || !mon.HasBattery; },
                    delegate { health.Evaluate(mon, sens, CpuTempBest()); Invalidate(); });
            }
            if (healthWin.WindowState == FormWindowState.Minimized) healthWin.WindowState = FormWindowState.Normal;
            healthWin.Show();
            healthWin.Activate();
        }

        void TakeSnapshot()
        {
            if (healthSnapshot)
            {
                try
                {
                    health.Evaluate(mon, sens, CpuTempBest());
                    HealthWindow.RenderSnapshot(health, snapshotPath, CpuTempBest(), mon.CpuMhz);
                }
                catch (Exception ex) { Settings.Log("health snapshot: " + ex); }
                exiting = true;
                Close();
                return;
            }
            try { health.Evaluate(mon, sens, CpuTempBest()); } catch { }
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
            if (fs && Visible && !IsMinimized) { autoHidden = true; Hide(); }
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
            hCoreT.Add(Hv(sens.CoreMaxTemp));
            hMidFan.Add(Hv(sens.MidFan));
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
                "Fan speeds and exact CPU/GPU temperatures come from your laptop's firmware or the PawnIO driver, which Windows only lets admin apps read.\n\n" +
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

        void InstallAdvancedSensors()
        {
            if (MessageBox.Show(
                "Advanced CPU sensors read the exact CPU package and per-core temperatures on any Intel or AMD Ryzen laptop, " +
                "and fan speeds on regular Dell laptops.\n\n" +
                "This installs PawnIO, a small open-source, signed driver (the one LibreHardwareMonitor uses), from winget. " +
                "Windows will ask for admin permission. CoreGlance only uses it to read sensors.\n\nContinue?",
                "Advanced CPU sensors", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            var worker = new System.Threading.Thread(delegate()
            {
                bool ok = Elevation.RunElevated("--install-pawnio") && PawnModule.Installed;
                if (!IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!ok)
                    {
                        MessageBox.Show("PawnIO could not be installed. You can install it from https://pawnio.eu, then restart CoreGlance.", "CoreGlance");
                        return;
                    }
                    if (sens.Elevated)
                    {
                        MessageBox.Show("PawnIO is installed. Exit CoreGlance and open it again to start reading the new sensors.", "CoreGlance");
                        return;
                    }
                    ToggleSensorMode();   // the driver is only reachable from admin mode
                });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        string SensorHint()
        {
            if (!sens.Ready) return null;
            if (sens.NeedsAdmin) return "More sensors need admin: right-click › Enable temps & fans";
            if (!sens.PawnIoInstalled && double.IsNaN(sens.CpuTemp)) return "Exact CPU temp: right-click › Advanced CPU sensors";
            return null;
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
            miShow = Item("Hide to tray", delegate { if ((Visible && !IsMinimized) || autoHidden) MinimizeToTray(); else ShowWidget(); });
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

            miTaskbar = Item("Show in taskbar (pin it from there)", delegate { SetInTaskbar(!cfg.InTaskbar); });
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
                ToolStripMenuItem it = Item(ms < 1000 ? "0.5 s" : (ms / 1000) + " s", delegate { cfg.Interval = ms; timer.Interval = EffectiveInterval(); cfg.Save(); });
                it.Tag = ms;
                refresh.DropDownItems.Add(it);
                intervalItems.Add(it);
            }

            var lowPowerMenu = new ToolStripMenuItem("Low power mode");
            miLowOff = Item("Off", delegate { SetLowPower(false, cfg.LowPowerLevel, false); });
            miLowSaver = Item("Battery saver (CPU up to 2.0 GHz)", delegate { SetLowPower(true, 0, false); });
            miLowMax = Item("Max saver (1.6 GHz, efficient cores first)", delegate { SetLowPower(true, 1, false); });
            miLowAuto = Item("Turn on automatically on battery", delegate { cfg.LowPowerAuto = !cfg.LowPowerAuto; cfg.Save(); lastOnAC = true; });
            lowPowerMenu.DropDownItems.AddRange(new ToolStripItem[] { miLowOff, miLowSaver, miLowMax, new ToolStripSeparator(), miLowAuto });
            miAuto = Item("Start with Windows", delegate { Settings.SetAutoStart(!Settings.IsAutoStart()); });
            miSensors = Item("Enable temperatures && fans (admin)...", delegate { ToggleSensorMode(); });
            miPawn = Item("Advanced CPU sensors (install PawnIO)...", delegate { InstallAdvancedSensors(); });

            menu.Items.AddRange(new ToolStripItem[]
            {
                miShow,
                Item("PC health report...", delegate { ShowHealth(); }),
                new ToolStripSeparator(),
                view, cpuMetric, miTaskbar, miTop, miLock, miClick, miFull, opacity, refresh,
                new ToolStripSeparator(),
                lowPowerMenu,
                miAuto,
                miSensors,
                miPawn,
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
            miShow.Text = (Visible && !IsMinimized) || autoHidden ? "Hide to tray" : "Show widget";
            miTaskbar.Checked = cfg.InTaskbar;
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
            miLowOff.Checked = !cfg.LowPower;
            miLowSaver.Checked = cfg.LowPower && cfg.LowPowerLevel == 0;
            miLowMax.Checked = cfg.LowPower && cfg.LowPowerLevel == 1;
            miLowAuto.Checked = cfg.LowPowerAuto;
            miLowOff.Enabled = miLowSaver.Enabled = miLowMax.Enabled = !lowBusy;
            miSensors.Visible = sens.NeedsAdmin || sens.Elevated;
            miPawn.Visible = sens.Ready && !sens.PawnIoInstalled;
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
                "Sensor sources: " + sens.SourceList + (sens.Elevated ? " (admin mode)" : "") + "\n" +
                "Settings: " + Settings.Dir,
                "About CoreGlance", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void SetView(int v)
        {
            if (v == cfg.View) return;
            if (IsMinimized) WindowState = FormWindowState.Normal;
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

        // ============================== battery time and power modes ==============================

        int CurrentMode { get { return cfg.LowPower ? cfg.LowPowerLevel + 1 : 0; } }
        static readonly string[] ModeNames = { "Off", "Saver", "Max" };

        void SetMode(int m)
        {
            if (m == 0) SetLowPower(false, cfg.LowPowerLevel, false);
            else SetLowPower(true, m - 1, false);
        }

        static double Ema(double old, double sample, double dt, double tau)
        {
            if (double.IsNaN(sample)) return old;
            if (double.IsNaN(old)) return sample;
            return old + (1 - Math.Exp(-dt / tau)) * (sample - old);
        }

        /// <summary>Smooths the battery drain and CPU power, and learns what the rest of the laptop draws while it runs on battery.</summary>
        void UpdateEnergyModel()
        {
            DateTime now = DateTime.Now;
            double dt = lastEnergy == DateTime.MinValue ? 1 : Math.Min(10, (now - lastEnergy).TotalSeconds);
            lastEnergy = now;
            double pkg = mon.CpuPkgW;
            if (tick <= 5) return;   // CoreGlance's own start-up burst is not your normal use
            if (!double.IsNaN(pkg) && pkg > 0) cpuW = Ema(cpuW, pkg, dt, 60);
            bool discharging = mon.HasBattery && !mon.OnAC && !double.IsNaN(sens.BatRateW) && sens.BatRateW < -0.3;
            if (!discharging) { drawW = double.NaN; return; }
            drawW = Ema(drawW, -sens.BatRateW, dt, 45);
            if (!double.IsNaN(cpuW))
            {
                double rest = -sens.BatRateW - cpuW - (double.IsNaN(sens.NvPowerW) ? 0 : sens.NvPowerW);
                if (rest > 0.5 && rest < 40) cfg.PlatformW = Ema(cfg.PlatformW, rest, dt, 600);
                if ((now - platformSaved).TotalMinutes >= 5) { platformSaved = now; cfg.Save(); }
            }
        }

        /// <summary>How much less CPU power each mode draws than Off. Measured on this laptop: light use 0.6 / 0.5, full load 0.27 / 0.22.</summary>
        static double ModeFactor(int mode, double offCpuW)
        {
            if (mode == 0) return 1;
            double heavy = Math.Max(0, Math.Min(1, (offCpuW - 10) / 20));
            return mode == 1 ? 0.6 + (0.27 - 0.6) * heavy : 0.5 + (0.22 - 0.5) * heavy;
        }

        /// <summary>Battery hours in a mode at the current activity, or NaN when there is no battery reading.</summary>
        double HoursIn(int mode)
        {
            if (!mon.HasBattery || double.IsNaN(sens.BatRemainWh) || double.IsNaN(cpuW)) return double.NaN;
            double full = double.IsNaN(sens.BatFullWh) ? sens.BatRemainWh : sens.BatFullWh;
            double usable = Math.Max(0, sens.BatRemainWh - 0.05 * full);   // Windows shuts down around 5%
            int cur = CurrentMode;
            double offCpu = cpuW / ModeFactor(cur, cpuW / ModeFactor(cur, cpuW));
            double gpu = double.IsNaN(sens.NvPowerW) ? 0 : sens.NvPowerW;
            double platform = !double.IsNaN(drawW) ? Math.Max(0.5, drawW - cpuW - gpu) : double.IsNaN(cfg.PlatformW) ? 4.5 : cfg.PlatformW;
            double watts = mode == cur && !double.IsNaN(drawW) ? drawW : platform + gpu + Math.Max(2, offCpu * ModeFactor(mode, offCpu));
            return watts > 0.5 ? usable / watts : double.NaN;
        }

        static string HoursText(double h)
        {
            if (double.IsNaN(h) || double.IsInfinity(h)) return "-";
            int m = (int)Math.Round(Math.Min(h, 99) * 60);
            return m >= 60 ? string.Format("{0}h {1:00}m", m / 60, m % 60) : m + "m";
        }

        /// <summary>Off | Saver | Max, each with its battery time. Returns the y below it.</summary>
        float ModeButtons(Graphics g, float x0, float x1, float y, float h, bool times)
        {
            float gap = 4 * S, w = (x1 - x0 - 2 * gap) / 3;
            int cur = CurrentMode;
            for (int i = 0; i < 3; i++)
            {
                var r = new RectangleF(x0 + i * (w + gap), y, w, h);
                modeRects[i] = r;
                bool sel = i == cur, hot = hoverBtn == 10 + i;
                Color accent = i == 0 ? TextMid : Good;
                FillRound(g, B(sel ? Color.FromArgb(i == 0 ? 60 : 55, accent) : hot ? Color.FromArgb(44, 49, 60) : PanelC), r, 6 * S);
                if (sel)
                    using (var pen = new Pen(Color.FromArgb(150, accent), Math.Max(1f, S)))
                    using (GraphicsPath p = RoundRect(r, 6 * S)) g.DrawPath(pen, p);
                string label = lowBusy && sel ? "\u2026" : ModeNames[i];
                if (times)
                {
                    Txt(g, label, fSmall, sel ? TextHi : TextMid, r.X + r.Width / 2, r.Y + 4 * S, 1, w);
                    Txt(g, mon.HasBattery ? "\u2248 " + HoursText(HoursIn(i)) : "", fTiny, sel ? (i == 0 ? TextHi : Good) : TextLo, r.X + r.Width / 2, r.Y + 19 * S, 1, w);
                }
                else Txt(g, label, fSmall, sel ? TextHi : TextMid, r.X + r.Width / 2, r.Y + (h - 14 * S) / 2, 1, w);
            }
            return y + h;
        }

        float PowerModeRow(Graphics g, float x0, float x1, float y)
        {
            Txt(g, "POWER MODE", fLabel, Good, x0, y, 0, 0);
            string status;
            if (!mon.HasBattery) status = "No battery";
            else if (!double.IsNaN(drawW)) status = string.Format("{0} left  \u00B7  {1:0.0} W", HoursText(HoursIn(CurrentMode)), drawW);
            else if (!double.IsNaN(sens.BatRemainWh)) status = "Plugged in  \u00B7  on battery \u2248 " + HoursText(HoursIn(CurrentMode));
            else status = "Reading battery\u2026";
            Txt(g, status, fSmall, !double.IsNaN(drawW) ? TextHi : TextLo, x1, y - 0.5f * S, 2, x1 - x0 - 80 * S);
            y += 17 * S;
            return ModeButtons(g, x0, x1, y, 34 * S, true);
        }

        int SnapshotTicks()
        {
            string[] a = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(a, "--ticks"), n;
            return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out n) ? n : healthSnapshot ? 15 : 8;
        }

        int EffectiveInterval() { return cfg.LowPower ? Math.Max(2000, cfg.Interval) : cfg.Interval; }   // the widget itself also saves power

        /// <summary>Switches the power plan on a worker thread (powercfg takes about a second) and updates the widget.</summary>
        void SetLowPower(bool on, int level, bool byAuto)
        {
            if (lowBusy || snapshotPath != null) return;
            if (on == cfg.LowPower && (!on || level == cfg.LowPowerLevel)) return;
            lowBusy = true;
            string prev = cfg.LowPowerPrev;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string result = null;
                bool ok;
                try
                {
                    if (on) { result = LowPower.Enable(level, prev); ok = result != null; }
                    else ok = LowPower.Disable(prev);
                }
                catch (Exception ex) { Settings.Log("low power: " + ex.Message); ok = false; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        lowBusy = false;
                        if (!ok)
                        {
                            tray.ShowBalloonTip(5000, "Low power mode", on ? "Windows did not accept the power plan change. Nothing was changed." : "Could not switch back to your power plan. Choose one in Windows Settings > Power.", ToolTipIcon.Warning);
                            return;
                        }
                        cfg.LowPower = on;
                        cfg.LowPowerByAuto = on && byAuto;
                        if (on) { cfg.LowPowerLevel = level; cfg.LowPowerPrev = result; }
                        cfg.Save();
                        timer.Interval = EffectiveInterval();
                        Invalidate();
                    });
                }
                catch (InvalidOperationException) { }   // closing
            });
        }

        /// <summary>"Turn on automatically on battery": on when unplugged, off again when plugged in (only if it turned it on).</summary>
        void CheckAutoLowPower()
        {
            if (!mon.HasBattery) return;
            bool ac = mon.OnAC;
            if (cfg.LowPowerAuto && !lowBusy)
            {
                if (!ac && lastOnAC && !cfg.LowPower) SetLowPower(true, cfg.LowPowerLevel, true);
                else if (ac && !lastOnAC && cfg.LowPower && cfg.LowPowerByAuto) SetLowPower(false, cfg.LowPowerLevel, true);
            }
            lastOnAC = ac;
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
            if (!((Visible && !IsMinimized) || autoHidden)) ShowWidget();
            else if (cfg.InTaskbar && !autoHidden) WindowState = FormWindowState.Minimized;   // stays on the taskbar
            else HideWidget();
        }

        bool IsMinimized { get { return WindowState == FormWindowState.Minimized; } }

        /// <summary>The widget's minimize button: to the taskbar when it has a button there, otherwise to the tray.</summary>
        void MinimizeWidget()
        {
            if (cfg.InTaskbar) WindowState = FormWindowState.Minimized;
            else MinimizeToTray();
        }

        void SetInTaskbar(bool on)
        {
            if (IsMinimized) WindowState = FormWindowState.Normal;
            Point at = Location;
            cfg.InTaskbar = on;
            ShowInTaskbar = on;      // recreates the window with the new styles
            if (cfg.Visible && !Visible) Show();
            Location = at;
            KeepOnScreen();
            cfg.Save();
            Invalidate();
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
            if (IsMinimized) WindowState = FormWindowState.Normal;
            KeepOnScreen();
            if (cfg.TopMost) { TopMost = false; TopMost = true; }
            else BringToFront();
            Activate();
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
            if (IsMinimized) return;   // minimized windows report an off-screen position
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
            if (!IsMinimized) { cfg.X = Left; cfg.Y = Top; }
            cfg.Save();
        }

        // ============================== mouse ==============================

        int ButtonAt(Point p)
        {
            if (btnView.Contains(p)) return 1;
            if (btnMin.Contains(p)) return 2;
            if (btnHealth.Contains(p)) return 3;
            if (btnPower.Contains(p)) return 4;
            for (int i = 0; i < 3; i++) if (modeRects[i].Contains(p)) return 10 + i;
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
            Cursor want = hb != 0 || tabGpu.Contains(e.Location) || tabCpu.Contains(e.Location) || tabMem.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
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
                if (b >= 10) SetMode(b - 10);
                else if (b == 4) SetMode((CurrentMode + 1) % 3);   // the battery cycles Off, Saver, Max
                else if (b == 3) ShowHealth();
                else if (b == 2) MinimizeWidget();
                else SetView(cfg.View == 0 ? 1 : 0);
                return;
            }
            downBtn = 0;
            if (tabCpu.Contains(e.Location)) SetSort(0);
            else if (tabMem.Contains(e.Location)) SetSort(1);
            else if (tabGpu.Contains(e.Location)) SetSort(2);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && ButtonAt(e.Location) == 0 && !tabGpu.Contains(e.Location) && !tabCpu.Contains(e.Location) && !tabMem.Contains(e.Location))
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
            tabGpu = RectangleF.Empty;
            btnView = RectangleF.Empty;
            btnHealth = RectangleF.Empty;
            btnPower = RectangleF.Empty;
            for (int i = 0; i < 3; i++) modeRects[i] = RectangleF.Empty;
            btnMin = RectangleF.Empty;

            float h = cfg.View == 0 ? PaintCompact(g) : PaintFull(g, cfg.View == 2);

            int need = (int)Math.Ceiling(h);
            if (Math.Abs(need - ClientSize.Height) > 1 && !resizePending)
            {
                resizePending = true;
                BeginInvoke((MethodInvoker)delegate
                {
                    resizePending = false;
                    if (IsMinimized) return;
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
            {
                float tw = Measure(g, "CoreGlance", fTitle);
                string hs = health.Evaluated == DateTime.MinValue ? "Health ..." : "Health " + health.Score;
                float pw = Measure(g, hs, fSmall) + 22 * S;
                btnHealth = new RectangleF(x0 + tw + 8 * S, y - 1 * S, pw, 19 * S);
                FillRound(g, B(hoverBtn == 3 ? Color.FromArgb(52, 57, 68) : PanelC), btnHealth, 9.5f * S);
                g.FillEllipse(B(health.Evaluated == DateTime.MinValue ? TextLo : HealthPainter.ScoreColor(health.Score)), btnHealth.X + 8 * S, btnHealth.Y + 6.5f * S, 6 * S, 6 * S);
                Txt(g, hs, fSmall, TextHi, btnHealth.X + 18 * S, btnHealth.Y + 3 * S, 0, 0);
            }
            HeaderButtons(g, x1 + 5 * S, y - 3 * S, false);
            float hr = btnView.X - 8 * S;
            if (mon.HasBattery && mon.BatteryPct >= 0)
            {
                // The battery doubles as the low power switch.
                float hx = hr;
                string bt = (cfg.LowPower ? "Saver " : "") + mon.BatteryPct + "%";
                float left = hx - Measure(g, bt, fSmall) - 5 * S - 17 * S;
                btnPower = new RectangleF(left - 6 * S, y - 1 * S, hx - left + 10 * S, 19 * S);
                if (cfg.LowPower || hoverBtn == 4)
                    FillRound(g, B(cfg.LowPower ? Color.FromArgb(hoverBtn == 4 ? 70 : 45, Good) : Color.FromArgb(52, 57, 68)), btnPower, 9.5f * S);
                Txt(g, bt, fSmall, cfg.LowPower ? Good : TextMid, hx, y + 2 * S, 2, 0);
                DrawBattery(g, left, y + 4.5f * S);
            }
            y += 26 * S;

            y = PowerModeRow(g, x0, x1, y);
            y = Separator(g, x0, x1, y);
            y = CpuSection(g, x0, x1, y);
            List<Tile> tiles = BuildTiles();
            if (tiles.Count > 0 || SensorHint() != null)
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
            string src = sens.CpuTempSource;
            string cpuLabel = src == "CPU package" ? "CPU package" : src == "CPU Tctl" ? "CPU Tctl" : "CPU temp";
            if (!double.IsNaN(ct))
                list.Add(new Tile(cpuLabel, ct.ToString("0"), "°C", TempColor(ct), TempColor(ct), hCpuT, 100));
            else if (!double.IsNaN(mon.AcpiTemp))
                list.Add(new Tile("Thermal zone", mon.AcpiTemp.ToString("0"), "°C", TempColor(mon.AcpiTemp), TempColor(mon.AcpiTemp), hCpuT, 100));
            double gt = GpuTempBest();
            if (!double.IsNaN(gt))
                list.Add(new Tile("GPU temp", gt.ToString("0"), "°C", TempColor(gt), TempColor(gt), hGpuT, 100));
            if (!double.IsNaN(sens.CoreMaxTemp))
                list.Add(new Tile("Hottest core", sens.CoreMaxTemp.ToString("0"), "°C", TempColor(sens.CoreMaxTemp), TempColor(sens.CoreMaxTemp), hCoreT, 100));
            if (!double.IsNaN(sens.SysTemp))
                list.Add(new Tile("System temp", sens.SysTemp.ToString("0"), "°C", TempColor(sens.SysTemp), TempColor(sens.SysTemp), hSysT, 100));
            if (!double.IsNaN(sens.CpuFan))
                list.Add(new Tile("CPU fan", sens.CpuFan.ToString("0"), "rpm", CFan, TextHi, hCpuFan, maxFan));
            if (!double.IsNaN(sens.GpuFan))
                list.Add(new Tile("GPU fan", sens.GpuFan.ToString("0"), "rpm", CFan, TextHi, hGpuFan, maxFan));
            else if (!double.IsNaN(sens.NvFanPct))
                list.Add(new Tile("GPU fan", sens.NvFanPct.ToString("0"), "%", CFan, TextHi, null, 100));
            if (!double.IsNaN(sens.MidFan))
                list.Add(new Tile("Mid fan", sens.MidFan.ToString("0"), "rpm", CFan, TextHi, hMidFan, maxFan));
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
            else if (cfg.LowPower) { thr = LowPower.LevelName(cfg.LowPowerLevel) + " on"; tc = Good; }
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
            string hint = SensorHint();
            if (hint != null)
            {
                y += 7 * S;
                Txt(g, hint, fTiny, TextLo, x0, y, 0, cw);
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
            y += 37 * S;
            string apps = GpuApps(gpu);
            Txt(g, apps, fTiny, TextMid, x0, y, 0, cw);
            return y + 13 * S;
        }

        /// <summary>One line naming what is using this GPU: busy apps first, otherwise whoever holds its memory.</summary>
        string GpuApps(Gpu gpu)
        {
            if (!mon.ProcReady) return "Reading GPU apps\u2026";
            var parts = new List<string>();
            foreach (GpuUse u in gpu.Procs)
                if (u.Util >= 0.5 && parts.Count < 3) parts.Add(string.Format("{0} {1}", u.Name, CpuText(u.Util)));
            if (parts.Count > 0) return string.Join("  \u00B7  ", parts.ToArray());
            var holders = new List<GpuUse>(gpu.Procs);
            holders.Sort(delegate(GpuUse p, GpuUse q) { return q.Mem.CompareTo(p.Mem); });
            foreach (GpuUse u in holders)
                if (u.Mem >= 10 * 1048576.0 && parts.Count < 2) parts.Add(u.Name + " " + Mb(u.Mem));
            return parts.Count > 0 ? "Idle, memory held by " + string.Join("  \u00B7  ", parts.ToArray()) : "No apps using this GPU";
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
            if (mon.Gpus.Count > 0)
            {
                float wg = Measure(g, "GPU", fLabel);
                tx -= 14 * S + wg;
                tabGpu = new RectangleF(tx - 5 * S, y - 4 * S, wg + 10 * S, 20 * S);
                Txt(g, "GPU", fLabel, cfg.ProcSort == 2 ? CGpu : TextLo, tx, y, 0, 0);
                if (cfg.ProcSort == 2) g.FillRectangle(B(CGpu), tx, y + 14 * S, wg, 1.5f * S);
            }
            y += 21 * S;

            const int rows = 6;
            float rh = 19 * S;
            if (cfg.ProcSort == 2 && mon.Gpus.Count > 0)
            {
                GpuRows(g, x0, x1, y, rows, rh);
                return y + rows * rh;
            }
            List<ProcInfo> list = cfg.ProcSort == 1 ? mon.TopMem : mon.TopCpu;
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

        /// <summary>Top apps by GPU use: name, which GPU, percent of its busiest engine, GPU memory.</summary>
        void GpuRows(Graphics g, float x0, float x1, float y, int rows, float rh)
        {
            float cw = x1 - x0;
            if (!mon.ProcReady || mon.TopGpu.Count == 0)
            {
                Txt(g, mon.ProcReady ? "No apps are using the GPU" : "Collecting process data\u2026", fSmall, TextLo, x0, y + 3 * S, 0, cw);
                return;
            }
            double max = 10;
            foreach (GpuUse u in mon.TopGpu) max = Math.Max(max, u.Util);
            for (int k = 0; k < Math.Min(rows, mon.TopGpu.Count); k++)
            {
                GpuUse u = mon.TopGpu[k];
                float ry = y + k * rh;
                FillRound(g, B(Color.FromArgb(30, CGpu)), new RectangleF(x0, ry + 1 * S, Math.Max(3 * S, cw * (float)(u.Util / max)), rh - 3 * S), 3 * S);
                Txt(g, u.Name + (u.Count > 1 ? "  \u00D7" + u.Count : ""), fSmall, TextHi, x0 + 6 * S, ry + 3.5f * S, 0, cw - 168 * S);
                Txt(g, "GPU " + mon.Gpus.IndexOf(u.Gpu), fTiny, TextLo, x1 - 120 * S, ry + 4 * S, 2, 0);
                Txt(g, CpuText(u.Util), fSmall, TextHi, x1 - 66 * S, ry + 3.5f * S, 2, 0);
                Txt(g, Mb(u.Mem), fSmall, TextMid, x1 - 6 * S, ry + 3.5f * S, 2, 0);
            }
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
            y = ModeButtons(g, x0, x1, y, 22 * S, false) + 7 * S;
            double ct = CpuTempBest();
            string info;
            if (hoverCore >= 0) info = CoreDetail(hoverCore);
            else if (!double.IsNaN(drawW)) info = string.Format("{0} left  \u00B7  {1:0.0} W  \u00B7  {2}", HoursText(HoursIn(CurrentMode)), drawW, Ghz(mon.CpuMhz));
            else if (!double.IsNaN(ct) && !double.IsNaN(sens.CpuFan))
                info = string.Format("{0}  ·  {1:0}°C  ·  fan {2:0} rpm", Ghz(mon.CpuMhz), ct, sens.CpuFan);
            else if (!double.IsNaN(ct))
                info = string.Format("{0}  ·  {1:0}°C  ·  {2}/{3} busy", Ghz(mon.CpuMhz), ct, mon.ActiveThreads, mon.Logical);
            else
                info = string.Format("{0}  ·  {1}/{2} busy  ·  ↓ {3}", Ghz(mon.CpuMhz), mon.ActiveThreads, mon.Logical, Rate(mon.NetRx));
            Txt(g, info, fSmall, hoverCore >= 0 ? TextHi : TextMid, x0 + cw / 2, y, 1, cw);
            if (mouseInside) HeaderButtons(g, ClientSize.Width - 4 * S, 4 * S, true);
            if (health.Evaluated != DateTime.MinValue)
            {
                btnHealth = new RectangleF(2 * S, 2 * S, 16 * S, 16 * S);
                g.FillEllipse(B(hoverBtn == 3 ? TextHi : HealthPainter.ScoreColor(health.Score)), 6 * S, 6 * S, 7 * S, 7 * S);
            }
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
            Color fill = cfg.LowPower ? Good : mon.Charging ? CCpu : (mon.BatteryPct <= 20 && !mon.OnAC ? Bad : TextMid);
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
            HealthFlag hf = health.Flags.Count > 0 ? health.Flags[0] : null;
            if (hf != null && hf.Level >= Severity.Warn)
            {
                c = hf.Level == Severity.Critical ? Bad : Warn;
                s = "Health: " + hf.Title;
                return;
            }
            if (cfg.LowPower)
            {
                c = Good;
                s = LowPower.LevelName(cfg.LowPowerLevel) + ": " + LowPower.LevelSummary(cfg.LowPowerLevel);
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
