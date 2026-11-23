using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;

namespace CoreGlance
{
    /// <summary>Draws the full health report. Used by the window and by --health-snapshot.</summary>
    sealed class HealthPainter : IDisposable
    {
        static readonly Color Bg = Color.FromArgb(17, 19, 24);
        static readonly Color Card = Color.FromArgb(27, 30, 37);
        static readonly Color Track = Color.FromArgb(40, 44, 53);
        static readonly Color TextHi = Color.FromArgb(236, 239, 244);
        static readonly Color TextMid = Color.FromArgb(158, 165, 178);
        static readonly Color TextLo = Color.FromArgb(104, 111, 124);
        static readonly Color Accent = Color.FromArgb(34, 211, 238);
        public static readonly Color Good = Color.FromArgb(52, 211, 153);
        public static readonly Color Info = Color.FromArgb(96, 165, 250);
        public static readonly Color Warn = Color.FromArgb(251, 191, 36);
        public static readonly Color Bad = Color.FromArgb(248, 113, 113);

        readonly float S;
        readonly Font fHuge, fH1, fSection, fTitle, fBody, fSmall;
        readonly StringFormat wrap, center;
        public RectangleF RunButton = RectangleF.Empty;

        public HealthPainter(float scale)
        {
            S = scale;
            fHuge = F("Segoe UI Semibold", 30);
            fH1 = F("Segoe UI Semibold", 20);
            fSection = F("Segoe UI Semibold", 11);
            fTitle = F("Segoe UI Semibold", 13.5f);
            fBody = F("Segoe UI", 12.5f);
            fSmall = F("Segoe UI", 11.5f);
            wrap = new StringFormat(StringFormat.GenericTypographic);
            wrap.FormatFlags &= ~StringFormatFlags.NoWrap;
            wrap.Trimming = StringTrimming.Word;
            center = new StringFormat(StringFormat.GenericTypographic);
            center.Alignment = StringAlignment.Center;
            center.LineAlignment = StringAlignment.Center;
        }

        Font F(string family, float px) { return new Font(family, px * S, FontStyle.Regular, GraphicsUnit.Pixel); }

        public static Color Of(Severity s)
        {
            return s == Severity.Critical ? Bad : s == Severity.Warn ? Warn : s == Severity.Info ? Info : Good;
        }

        public static Color ScoreColor(int score) { return score >= 85 ? Good : score >= 60 ? Warn : Bad; }

        public static string ScoreLabel(int score) { return score >= 85 ? "Good" : score >= 60 ? "Fair" : "Needs attention"; }

        float Text(Graphics g, string s, Font f, Color c, float x, float y, float w)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            SizeF sz = g.MeasureString(s, f, (int)Math.Max(10, w), wrap);
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, new RectangleF(x, y, w, sz.Height + 2), wrap);
            return sz.Height;
        }

        float Measure(Graphics g, string s, Font f, float w)
        {
            return string.IsNullOrEmpty(s) ? 0 : g.MeasureString(s, f, (int)Math.Max(10, w), wrap).Height;
        }

        static void Round(Graphics g, Color c, RectangleF r, float rad)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var p = new GraphicsPath())
            {
                rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
                float d = rad * 2;
                if (d < 1) p.AddRectangle(r);
                else
                {
                    p.AddArc(r.X, r.Y, d, d, 180, 90);
                    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                    p.CloseFigure();
                }
                using (var b = new SolidBrush(c)) g.FillPath(b, p);
            }
        }

        float Section(Graphics g, string title, float x, float y)
        {
            y += 22 * S;
            using (var b = new SolidBrush(TextMid)) g.DrawString(title, fSection, b, x, y, StringFormat.GenericTypographic);
            return y + 22 * S;
        }

        /// <summary>Paints the whole report and returns its height.</summary>
        public float Paint(Graphics g, float width, HealthMonitor h, double liveTemp, double liveMhz)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Bg);
            float p = 20 * S, x0 = p, cw = width - 2 * p, y = 18 * S;

            // ---------- score ----------
            float d = 84 * S, pw = 7 * S;
            var ring = new RectangleF(x0 + pw / 2, y + pw / 2, d - pw, d - pw);
            Color sc = ScoreColor(h.Score);
            using (var pt = new Pen(Track, pw)) g.DrawEllipse(pt, ring);
            using (var pv = new Pen(sc, pw))
            {
                pv.StartCap = pv.EndCap = LineCap.Round;
                g.DrawArc(pv, ring, -90, Math.Max(1, 360f * h.Score / 100f));
            }
            using (var b = new SolidBrush(TextHi)) g.DrawString(h.Score.ToString(), fHuge, b, new RectangleF(x0, y, d, d), center);
            float tx = x0 + d + 18 * S, tw = cw - d - 18 * S;
            float ty = y + 8 * S;
            ty += Text(g, "PC health: " + ScoreLabel(h.Score), fH1, sc, tx, ty, tw) + 4 * S;
            int crit = 0, warn = 0, info = 0;
            foreach (HealthFlag f in h.Flags) { if (f.Level == Severity.Critical) crit++; else if (f.Level == Severity.Warn) warn++; else info++; }
            string counts = crit + warn + info == 0 ? "No problems found" :
                string.Format("{0} critical  ·  {1} warning{2}  ·  {3} note{4}", crit, warn, warn == 1 ? "" : "s", info, info == 1 ? "" : "s");
            ty += Text(g, counts, fBody, TextHi, tx, ty, tw) + 3 * S;
            int histDays = h.Days.Count;
            Text(g, string.Format("Checked {0:HH:mm}  ·  {1} day{2} of history", h.Evaluated, histDays, histDays == 1 ? "" : "s"), fSmall, TextLo, tx, ty, tw);
            y += d + 4 * S;

            // ---------- flags ----------
            y = Section(g, "FLAGS", x0, y);
            if (h.Flags.Count == 0)
            {
                var r = new RectangleF(x0, y, cw, 44 * S);
                Round(g, Card, r, 8 * S);
                Round(g, Good, new RectangleF(x0, y, 4 * S, r.Height), 2 * S);
                Text(g, "Nothing to worry about right now. CoreGlance keeps watching in the background.", fBody, TextHi, x0 + 16 * S, y + 13 * S, cw - 28 * S);
                y += r.Height + 8 * S;
            }
            foreach (HealthFlag f in h.Flags)
            {
                float iw = cw - 30 * S;
                float hh = 12 * S + Measure(g, f.Title, fTitle, iw) + 5 * S + Measure(g, f.Detail, fSmall, iw) + 6 * S + Measure(g, "→ " + f.Action, fSmall, iw) + 12 * S;
                var r = new RectangleF(x0, y, cw, hh);
                Round(g, Card, r, 8 * S);
                Round(g, Of(f.Level), new RectangleF(x0, y, 4 * S, hh), 2 * S);
                float cy = y + 12 * S;
                cy += Text(g, f.Title, fTitle, TextHi, x0 + 16 * S, cy, iw) + 5 * S;
                cy += Text(g, f.Detail, fSmall, TextMid, x0 + 16 * S, cy, iw) + 6 * S;
                Text(g, "→ " + f.Action, fSmall, Of(f.Level), x0 + 16 * S, cy, iw);
                y += hh + 8 * S;
            }

            // ---------- projections ----------
            if (h.Projections.Count > 0)
            {
                y = Section(g, "PROJECTIONS", x0, y);
                foreach (Projection pr in h.Projections)
                {
                    float iw = cw - 22 * S;
                    using (var b = new SolidBrush(Of(pr.Level))) g.FillEllipse(b, x0 + 2 * S, y + 6 * S, 8 * S, 8 * S);
                    y += Text(g, pr.Title, fTitle, TextHi, x0 + 20 * S, y, iw) + 2 * S;
                    y += Text(g, pr.Detail, fSmall, TextMid, x0 + 20 * S, y, iw) + 12 * S;
                }
            }

            // ---------- trends ----------
            y = Section(g, "TRENDS  ·  LAST 30 DAYS", x0, y);
            List<DayRecord> days = h.Days;
            float gap = 10 * S, chw = (cw - gap) / 2, chh = 92 * S;
            Chart(g, new RectangleF(x0, y, chw, chh), "CPU temp under load", "°C", days, delegate(DayRecord r) { return r.AvgTempLoad; }, Bad, "0");
            Chart(g, new RectangleF(x0 + chw + gap, y, chw, chh), "Cooling (lower is better)", "°C/W", days, delegate(DayRecord r) { return r.Cooling; }, Warn, "0.00");
            y += chh + gap;
            Chart(g, new RectangleF(x0, y, chw, chh), "Sustained clock under load", "GHz", days, delegate(DayRecord r) { return r.AvgMhzLoad / 1000; }, Accent, "0.00");
            Chart(g, new RectangleF(x0 + chw + gap, y, chw, chh), "System drive free", "GB", days, delegate(DayRecord r) { return r.V[DayRecord.DiskFreeGB]; }, Good, "0");
            y += chh + gap;
            Chart(g, new RectangleF(x0, y, chw, chh), "Minutes above 90 °C", "min", days, delegate(DayRecord r) { return r.V[DayRecord.HotSec] / 60; }, Bad, "0");
            Chart(g, new RectangleF(x0 + chw + gap, y, chw, chh), "Minutes with RAM above 90%", "min", days, delegate(DayRecord r) { return r.V[DayRecord.MemHighSec] / 60; }, Color.FromArgb(167, 139, 250), "0");
            y += chh;

            // ---------- storage ----------
            y = Section(g, "STORAGE", x0, y);
            List<DiskHealth> disks = h.Disks;
            if (disks != null)
                foreach (DiskHealth dk in disks)
                {
                    var rows = new List<string[]>();
                    rows.Add(new[] { "Status", dk.Health });
                    rows.Add(new[] { "Wear", double.IsNaN(dk.Wear) ? (h.SmartNeedsAdmin ? "needs admin mode" : "-") : dk.Wear.ToString("0") + "% used" });
                    rows.Add(new[] { "Temperature", double.IsNaN(dk.Temp) ? "-" : string.Format("{0:0} °C (max {1})", dk.Temp, double.IsNaN(dk.TempMax) ? "-" : dk.TempMax.ToString("0") + " °C") });
                    rows.Add(new[] { "Power-on hours", double.IsNaN(dk.PowerOnHours) ? "-" : dk.PowerOnHours.ToString("N0", CultureInfo.CurrentCulture) });
                    rows.Add(new[] { "Uncorrectable errors", double.IsNaN(dk.UncorrectedErrors) ? "-" : dk.UncorrectedErrors.ToString("0") });
                    y = KeyValueCard(g, x0, y, cw, dk.Name + "  ·  " + dk.Media, dk.HealthCode == 0 ? Good : Bad, rows) + 8 * S;
                }
            List<DriveSpace> drives = h.Drives;
            if (drives != null)
                foreach (DriveSpace s in drives)
                {
                    double used = s.TotalGB > 0 ? 1 - s.FreeGB / s.TotalGB : 0;
                    Color bc = used > 0.95 ? Bad : used > 0.9 ? Warn : Good;
                    Text(g, string.Format("{0}  {1:0.0} GB free of {2:0}", s.Name, s.FreeGB, s.TotalGB), fSmall, TextHi, x0, y, cw);
                    y += 18 * S;
                    Round(g, Track, new RectangleF(x0, y, cw, 6 * S), 3 * S);
                    Round(g, bc, new RectangleF(x0, y, (float)(cw * used), 6 * S), 3 * S);
                    y += 16 * S;
                }

            // ---------- battery ----------
            BatteryHealth bat = h.Battery;
            if (bat != null)
            {
                y = Section(g, "BATTERY", x0, y);
                double hp = bat.HealthPct;
                Color bc = hp < 60 ? Bad : hp < 80 ? Warn : Good;
                Text(g, string.Format("{0:0}% of design capacity  ·  {1:0.0} of {2:0.0} Wh{3}{4}", hp, bat.FullWh, bat.DesignWh,
                    double.IsNaN(bat.Cycles) ? "" : string.Format("  ·  {0:0} cycles", bat.Cycles),
                    bat.Model.Length > 0 ? "  ·  " + bat.Model : ""), fSmall, TextHi, x0, y, cw);
                y += 19 * S;
                Round(g, Track, new RectangleF(x0, y, cw, 8 * S), 4 * S);
                Round(g, bc, new RectangleF(x0, y, (float)(cw * Math.Max(0, Math.Min(1, hp / 100))), 8 * S), 4 * S);
                y += 14 * S;
            }

            // ---------- events ----------
            EventSummary ev = h.Events;
            y = Section(g, "WINDOWS EVENT LOG  ·  LAST 30 DAYS", x0, y);
            if (ev == null) { Text(g, "Reading the event log...", fSmall, TextLo, x0, y, cw); y += 18 * S; }
            else
            {
                var rows = new List<string[]>();
                rows.Add(new[] { "Hardware errors (WHEA)", ev.Whea.ToString() });
                rows.Add(new[] { "Blue-screen crashes", ev.BugChecks.ToString() });
                rows.Add(new[] { "Unexpected shutdowns", ev.Unexpected == 0 ? "0" : string.Format("{0} ({1} while entering sleep)", ev.Unexpected, ev.UnexpectedInSleep) });
                if (ev.Unexpected > 0) rows.Add(new[] { "Last unexpected shutdown", ev.LastUnexpected.ToString("ddd d MMM, HH:mm") });
                rows.Add(new[] { "Disk / file-system errors", ev.DiskErrors.ToString() });
                rows.Add(new[] { "Graphics driver resets", ev.GpuResets.ToString() });
                bool bad = ev.Whea + ev.BugChecks + ev.DiskErrors > 0;
                y = KeyValueCard(g, x0, y, cw, "Checked " + ev.Checked.ToString("HH:mm"), bad ? Bad : ev.Unexpected + ev.GpuResets > 0 ? Warn : Good, rows);
            }

            // ---------- render-speed check ----------
            y = Section(g, "RENDER-SPEED CHECK", x0, y);
            y += Text(g, "Loads every CPU thread for 30 seconds and scores throughput. Run it plugged in, with apps closed and the laptop cool, then again every few weeks: a falling score means heat, dust or a power limit is slowing your renders. Scores vary about 10% run to run, so CoreGlance compares against your usual (median) score.",
                fSmall, TextMid, x0, y, cw) + 10 * S;
            StressTest st = h.Stress;
            RunButton = new RectangleF(x0, y, 210 * S, 34 * S);
            if (st.Running)
            {
                Round(g, Track, RunButton, 8 * S);
                Round(g, Accent, new RectangleF(RunButton.X, RunButton.Y, (float)(RunButton.Width * st.Progress), RunButton.Height), 8 * S);
                using (var b = new SolidBrush(TextHi))
                    g.DrawString(string.Format("Running... {0:0}%", st.Progress * 100), fTitle, b, RunButton, center);
                Text(g, string.Format("{0}  ·  {1}", double.IsNaN(liveTemp) ? "-" : liveTemp.ToString("0") + " °C",
                    liveMhz > 0 ? (liveMhz / 1000).ToString("0.00") + " GHz" : "-"), fSmall, TextMid, RunButton.Right + 14 * S, y + 9 * S, cw - RunButton.Width - 14 * S);
            }
            else
            {
                Round(g, Accent, RunButton, 8 * S);
                using (var b = new SolidBrush(Bg)) g.DrawString("Run 30-second check", fTitle, b, RunButton, center);
            }
            y += RunButton.Height + 12 * S;
            if (h.Benchmarks.Count > 0)
            {
                double usual = h.UsualBenchScore();
                int start = Math.Max(0, h.Benchmarks.Count - 6);
                for (int i = h.Benchmarks.Count - 1; i >= start; i--)
                {
                    BenchResult r = h.Benchmarks[i];
                    double rel = !double.IsNaN(usual) && usual > 0 ? r.Score / usual : 1;
                    Color c = !r.OnAC ? TextMid : rel >= 0.9 ? Good : rel >= 0.8 ? Warn : Bad;
                    Text(g, string.Format("{0:d MMM HH:mm}   score {1:0}{2}   ·   max {3:0} °C   ·   {4:0.00} GHz   ·   {5}",
                        r.When, r.Score, r.OnAC && !double.IsNaN(usual) ? string.Format(" ({0:0}% of usual)", rel * 100) : "",
                        r.MaxTemp, r.AvgMhz / 1000, r.OnAC ? "plugged in" : "on battery"), fSmall, c, x0, y, cw);
                    y += 18 * S;
                }
            }
            else if (!st.Running) { Text(g, "No checks yet.", fSmall, TextLo, x0, y, cw); y += 18 * S; }

            y += 14 * S;
            y += Text(g, "History stays on this PC: %APPDATA%\\CoreGlance\\health", fSmall, TextLo, x0, y, cw);
            return y + 20 * S;
        }

        delegate double Metric(DayRecord r);

        void Chart(Graphics g, RectangleF r, string title, string unit, List<DayRecord> days, Metric metric, Color c, string fmt)
        {
            Round(g, Card, r, 8 * S);
            float ix = r.X + 12 * S, iw = r.Width - 24 * S;
            Text(g, title, fSmall, TextMid, ix, r.Y + 9 * S, iw);
            var vals = new double[30];
            DateTime start = DateTime.Today.AddDays(-29);
            double lo = double.MaxValue, hi = double.MinValue, last = double.NaN;
            for (int i = 0; i < 30; i++) vals[i] = double.NaN;
            foreach (DayRecord d in days)
            {
                DateTime dt;
                if (!DateTime.TryParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) continue;
                int idx = (int)(dt - start).TotalDays;
                if (idx < 0 || idx >= 30) continue;
                double v = metric(d);
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                vals[idx] = v;
                lo = Math.Min(lo, v); hi = Math.Max(hi, v);
                last = v;
            }
            string val = double.IsNaN(last) ? "no data yet" : last.ToString(fmt) + " " + unit;
            using (var b = new SolidBrush(double.IsNaN(last) ? TextLo : TextHi))
            {
                var sf = new StringFormat(StringFormat.GenericTypographic);
                sf.Alignment = StringAlignment.Far;
                g.DrawString(val, fSmall, b, new RectangleF(r.X, r.Y + 9 * S, r.Width - 12 * S, 18 * S), sf);
                sf.Dispose();
            }
            if (double.IsNaN(last)) return;
            float top = r.Y + 34 * S, bottom = r.Bottom - 10 * S, span = bottom - top;
            double min = lo > 0 && hi / Math.Max(lo, 1e-9) < 1.5 ? lo * 0.9 : 0;   // zoom in when values sit in a narrow band
            double max = hi <= min ? min + 1 : hi;
            float bw = iw / 30f;
            for (int i = 0; i < 30; i++)
            {
                if (double.IsNaN(vals[i])) continue;
                float hgt = (float)Math.Max(2 * S, (vals[i] - min) / (max - min) * span);
                Round(g, i == 29 ? c : Color.FromArgb(150, c), new RectangleF(ix + i * bw + bw * 0.15f, bottom - hgt, bw * 0.7f, hgt), 1.5f * S);
            }
        }

        float KeyValueCard(Graphics g, float x, float y, float w, string header, Color accent, List<string[]> rows)
        {
            float rh = 20 * S;
            float hh = 14 * S + 20 * S + rows.Count * rh + 8 * S;
            Round(g, Card, new RectangleF(x, y, w, hh), 8 * S);
            Round(g, accent, new RectangleF(x, y, 4 * S, hh), 2 * S);
            Text(g, header, fTitle, TextHi, x + 16 * S, y + 12 * S, w - 28 * S);
            float ry = y + 14 * S + 22 * S;
            foreach (string[] kv in rows)
            {
                Text(g, kv[0], fSmall, TextMid, x + 16 * S, ry, w * 0.45f);
                Text(g, kv[1], fSmall, TextHi, x + 16 * S + w * 0.45f, ry, w * 0.55f - 28 * S);
                ry += rh;
            }
            return y + hh;
        }

        public void Dispose()
        {
            foreach (Font f in new[] { fHuge, fH1, fSection, fTitle, fBody, fSmall }) f.Dispose();
            wrap.Dispose();
            center.Dispose();
        }
    }

    /// <summary>The PC health report window (opened from the widget's health badge or menu).</summary>
    sealed class HealthWindow : Form
    {
        readonly HealthMonitor health;
        readonly Func<double> temp, mhz;
        readonly Action evaluate;
        readonly Func<bool> onAC;
        readonly Canvas canvas;
        readonly Timer timer;
        HealthPainter painter;
        float S = 1;

        sealed class Canvas : Control
        {
            public Canvas()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            }
        }

        public HealthWindow(HealthMonitor h, Func<double> cpuTemp, Func<double> cpuMhz, Func<bool> pluggedIn, Action reevaluate)
        {
            health = h; temp = cpuTemp; mhz = cpuMhz; onAC = pluggedIn; evaluate = reevaluate;
            Text = "CoreGlance - PC health";
            BackColor = Color.FromArgb(17, 19, 24);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScroll = true;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            canvas = new Canvas();
            canvas.Location = Point.Empty;
            canvas.Paint += OnCanvasPaint;
            canvas.MouseUp += OnCanvasClick;
            canvas.MouseMove += delegate(object s, MouseEventArgs e)
            {
                Cursor want = painter != null && painter.RunButton.Contains(e.Location) && !health.Stress.Running ? Cursors.Hand : Cursors.Default;
                if (canvas.Cursor != want) canvas.Cursor = want;
            };
            Controls.Add(canvas);
            timer = new Timer();
            timer.Interval = 1000;
            timer.Tick += delegate { if (Visible) canvas.Invalidate(); };
            timer.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            uint dpi = 96;
            try { dpi = Native.GetDpiForWindow(Handle); } catch { }
            S = (dpi == 0 ? 96 : dpi) / 96f;
            painter = new HealthPainter(S);
            ClientSize = new Size((int)(540 * S), (int)(Math.Min(820 * S, Screen.FromHandle(Handle).WorkingArea.Height * 0.85)));
            try { int dark = 1; Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
            LayoutCanvas(canvas.Height);
        }

        void LayoutCanvas(int height)
        {
            int w = ClientSize.Width - (VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0);
            canvas.Size = new Size(Math.Max(100, w), Math.Max(height, 100));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (painter != null) LayoutCanvas(canvas.Height);
            canvas.Invalidate();
        }

        void OnCanvasPaint(object sender, PaintEventArgs e)
        {
            if (painter == null) return;
            float h = painter.Paint(e.Graphics, canvas.Width, health, temp(), mhz());
            int need = (int)Math.Ceiling(h);
            if (Math.Abs(need - canvas.Height) > 1) BeginInvoke((MethodInvoker)delegate { LayoutCanvas(need); });
        }

        void OnCanvasClick(object sender, MouseEventArgs e)
        {
            if (painter == null || e.Button != MouseButtons.Left || !painter.RunButton.Contains(e.Location) || health.Stress.Running) return;
            if (!onAC() && MessageBox.Show("You're on battery. Results on battery are lower and kept separate from plugged-in results. Run anyway?",
                "Render-speed check", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            bool ac = onAC();
            health.Stress.Start(30, delegate { return temp(); }, delegate { return mhz(); }, delegate(StressTest t)
            {
                if (!IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    health.RecordBench(t, ac);
                    evaluate();
                    canvas.Invalidate();
                });
            });
            canvas.Invalidate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            health.Stress.Cancel();
            timer.Dispose();
            if (painter != null) painter.Dispose();
            base.OnFormClosed(e);
        }

        public static void RenderSnapshot(HealthMonitor h, string path, double temp, double mhz)
        {
            using (var painter = new HealthPainter(1f))
            {
                float height;
                using (var probe = new Bitmap(1, 1))
                using (Graphics g = Graphics.FromImage(probe)) height = painter.Paint(g, 540, h, temp, mhz);
                using (var bmp = new Bitmap(540, (int)Math.Ceiling(height)))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    painter.Paint(g, 540, h, temp, mhz);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }
    }
}
