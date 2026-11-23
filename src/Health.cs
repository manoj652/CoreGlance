using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;
using System.Xml;

namespace CoreGlance
{
    enum Severity { Good = 0, Info = 1, Warn = 2, Critical = 3 }

    sealed class HealthFlag
    {
        public readonly string Id, Title, Detail, Action;
        public readonly Severity Level;
        public HealthFlag(string id, Severity level, string title, string detail, string action)
        {
            Id = id; Level = level; Title = title; Detail = detail; Action = action;
        }
    }

    sealed class Projection
    {
        public readonly string Title, Detail;
        public readonly Severity Level;
        public Projection(Severity level, string title, string detail) { Level = level; Title = title; Detail = detail; }
    }

    /// <summary>One day of aggregated readings, persisted as a CSV row. Sums are time-weighted (seconds).</summary>
    sealed class DayRecord
    {
        public const int Seconds = 0, LoadSec = 1, TempLoadSum = 2, TempLoadN = 3, MhzLoadSum = 4, MhzLoadN = 5,
            CoolSum = 6, CoolN = 7, FanLoadSum = 8, FanLoadN = 9, MaxCpuTemp = 10, HotSec = 11, ThrottleSec = 12,
            MaxGpuTemp = 13, MemHighSec = 14, CommitPeak = 15, DiskFreeGB = 16, BatFullWh = 17, BatDesignWh = 18,
            SsdWear = 19, SsdTempMax = 20, DischargeWSum = 21, DischargeN = 22, Count = 23;
        static readonly int[] MaxCols = { MaxCpuTemp, MaxGpuTemp, DiskFreeGB, BatFullWh, BatDesignWh, SsdWear, SsdTempMax };

        public string Date;
        public readonly double[] V = new double[Count];

        public DayRecord(string date)
        {
            Date = date;
            foreach (int c in MaxCols) V[c] = double.NaN;
        }

        public double Avg(int sum, int n) { return V[n] >= 60 ? V[sum] / V[n] : double.NaN; }
        public double AvgTempLoad { get { return Avg(TempLoadSum, TempLoadN); } }
        public double AvgMhzLoad { get { return Avg(MhzLoadSum, MhzLoadN); } }
        public double Cooling { get { return Avg(CoolSum, CoolN); } }       // (°C above 30) per watt under load: lower is better
        public double AvgDischargeW { get { return Avg(DischargeWSum, DischargeN); } }

        public void Max(int col, double v)
        {
            if (double.IsNaN(v)) return;
            if (double.IsNaN(V[col]) || v > V[col]) V[col] = v;
        }

        public string ToCsv()
        {
            var sb = new StringBuilder(Date);
            foreach (double d in V) sb.Append(',').Append(double.IsNaN(d) ? "" : d.ToString("0.###", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static DayRecord FromCsv(string line)
        {
            string[] p = line.Split(',');
            if (p.Length < 2 || p[0].Length != 10) return null;
            var r = new DayRecord(p[0]);
            for (int i = 0; i < Count && i + 1 < p.Length; i++)
            {
                double d;
                if (p[i + 1].Length > 0 && double.TryParse(p[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) r.V[i] = d;
            }
            return r;
        }
    }

    sealed class EventSummary
    {
        public int Whea, Unexpected, UnexpectedInSleep, BugChecks, GpuResets, DiskErrors;
        public DateTime LastUnexpected = DateTime.MinValue;
        public DateTime Checked;
    }

    sealed class DiskHealth
    {
        public string Name, Health, Media;
        public int HealthCode;
        public double Wear = double.NaN, Temp = double.NaN, TempMax = double.NaN, PowerOnHours = double.NaN, UncorrectedErrors = double.NaN;
    }

    sealed class BatteryHealth
    {
        public double DesignWh, FullWh, Cycles = double.NaN;
        public string Model = "";
        public double HealthPct { get { return DesignWh > 0 ? FullWh / DesignWh * 100 : double.NaN; } }
    }

    sealed class DriveSpace
    {
        public string Name;
        public double FreeGB, TotalGB;
        public bool System;
    }

    sealed class BenchResult
    {
        public DateTime When;
        public double Score, MaxTemp, AvgMhz;
        public bool OnAC;
    }

    /// <summary>
    /// PC health: records daily aggregates, polls slow sources (event log, SSD SMART, battery report, disk space)
    /// on a background thread, and turns it all into flags, projections and a score.
    /// Everything stays in %APPDATA%\CoreGlance\health.
    /// </summary>
    sealed class HealthMonitor : IDisposable
    {
        public static string Dir { get { return Path.Combine(Settings.Dir, "health"); } }
        static string DailyPath { get { return Path.Combine(Dir, "daily.csv"); } }
        static string BenchPath { get { return Path.Combine(Dir, "benchmarks.csv"); } }
        static string AlertPath { get { return Path.Combine(Dir, "alerts.txt"); } }

        readonly bool persist, elevated;
        readonly List<DayRecord> days = new List<DayRecord>();
        readonly object dayLock = new object();
        DayRecord today;
        DateTime lastTick = DateTime.MinValue, lastFlush = DateTime.Now;

        public volatile EventSummary Events;
        public volatile List<DiskHealth> Disks;
        public volatile BatteryHealth Battery;
        public volatile List<DriveSpace> Drives;
        public volatile bool SmartNeedsAdmin;
        public readonly List<BenchResult> Benchmarks = new List<BenchResult>();

        public List<HealthFlag> Flags = new List<HealthFlag>();
        public List<Projection> Projections = new List<Projection>();
        public int Score = 100;
        public DateTime Evaluated = DateTime.MinValue;

        readonly Dictionary<string, DateTime> alerted = new Dictionary<string, DateTime>();
        readonly Thread probe;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        public readonly StressTest Stress = new StressTest();

        public HealthMonitor(bool persistHistory, bool isElevated)
        {
            persist = persistHistory;
            elevated = isElevated;
            Load();
            probe = new Thread(ProbeLoop);
            probe.IsBackground = true;
            probe.Priority = ThreadPriority.BelowNormal;
            probe.Name = "CoreGlance health";
            probe.Start();
        }

        public List<DayRecord> Days
        {
            get { lock (dayLock) return new List<DayRecord>(days); }
        }

        // ============================== persistence ==============================

        void Load()
        {
            try
            {
                if (File.Exists(DailyPath))
                    foreach (string line in File.ReadAllLines(DailyPath))
                    {
                        if (line.StartsWith("date", StringComparison.Ordinal)) continue;
                        DayRecord r = DayRecord.FromCsv(line);
                        if (r != null) days.Add(r);
                    }
                days.Sort(delegate(DayRecord a, DayRecord b) { return string.CompareOrdinal(a.Date, b.Date); });
                if (File.Exists(BenchPath))
                    foreach (string line in File.ReadAllLines(BenchPath))
                    {
                        string[] p = line.Split(',');
                        DateTime when; double s, t, m;
                        if (p.Length >= 5 && DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out when) &&
                            double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out s) &&
                            double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out t) &&
                            double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out m))
                        {
                            var b = new BenchResult();
                            b.When = when; b.Score = s; b.MaxTemp = t; b.AvgMhz = m; b.OnAC = p[4] == "1";
                            Benchmarks.Add(b);
                        }
                    }
                if (File.Exists(AlertPath))
                    foreach (string line in File.ReadAllLines(AlertPath))
                    {
                        int bar = line.IndexOf('|');
                        DateTime when;
                        if (bar > 0 && DateTime.TryParse(line.Substring(bar + 1), CultureInfo.InvariantCulture, DateTimeStyles.None, out when))
                            alerted[line.Substring(0, bar)] = when;
                    }
            }
            catch (Exception ex) { Settings.Log("health load: " + ex.Message); }
            Today(DateTime.Now);
        }

        DayRecord Today(DateTime now)
        {
            string d = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (today != null && today.Date == d) return today;
            lock (dayLock)
            {
                today = days.Count > 0 && days[days.Count - 1].Date == d ? days[days.Count - 1] : null;
                if (today == null)
                {
                    if (days.Count > 0) Flush();   // day changed: save yesterday
                    today = new DayRecord(d);
                    days.Add(today);
                    if (days.Count > 400) days.RemoveRange(0, days.Count - 400);
                }
            }
            return today;
        }

        public void Flush()
        {
            if (!persist) return;
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.Append("date");
                for (int i = 0; i < DayRecord.Count; i++) sb.Append(",c").Append(i);
                sb.AppendLine();
                lock (dayLock) foreach (DayRecord r in days) sb.AppendLine(r.ToCsv());
                string tmp = DailyPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(DailyPath)) File.Replace(tmp, DailyPath, null); else File.Move(tmp, DailyPath);
                lastFlush = DateTime.Now;
            }
            catch (Exception ex) { Settings.Log("health flush: " + ex.Message); }
        }

        void SaveBench(BenchResult b)
        {
            Benchmarks.Add(b);
            if (!persist) return;
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(BenchPath, string.Format(CultureInfo.InvariantCulture, "{0:s},{1:0.###},{2:0.#},{3:0},{4}{5}",
                    b.When, b.Score, b.MaxTemp, b.AvgMhz, b.OnAC ? 1 : 0, Environment.NewLine));
            }
            catch (Exception ex) { Settings.Log("bench save: " + ex.Message); }
        }

        // ============================== per-second recording ==============================

        public void Tick(SysMonitor mon, Sensors sens, double cpuTemp)
        {
            DateTime now = DateTime.Now;
            double dt = lastTick == DateTime.MinValue ? 1 : (now - lastTick).TotalSeconds;
            lastTick = now;
            if (dt <= 0 || dt > 15) dt = 1;   // after sleep or a clock change, count one sample
            DayRecord r = Today(now);
            double[] v = r.V;
            v[DayRecord.Seconds] += dt;

            bool tempOk = !double.IsNaN(cpuTemp) && cpuTemp > 5 && cpuTemp < 125;
            if (mon.Cpu >= 60 && !Stress.Running)
            {
                v[DayRecord.LoadSec] += dt;
                if (tempOk) { v[DayRecord.TempLoadSum] += cpuTemp * dt; v[DayRecord.TempLoadN] += dt; }
                if (mon.CpuMhz > 0) { v[DayRecord.MhzLoadSum] += mon.CpuMhz * dt; v[DayRecord.MhzLoadN] += dt; }
                if (tempOk && !double.IsNaN(mon.CpuPkgW) && mon.CpuPkgW >= 8)
                {
                    v[DayRecord.CoolSum] += Math.Max(0, cpuTemp - 30) / mon.CpuPkgW * dt;
                    v[DayRecord.CoolN] += dt;
                }
                if (!double.IsNaN(sens.CpuFan)) { v[DayRecord.FanLoadSum] += sens.CpuFan * dt; v[DayRecord.FanLoadN] += dt; }
            }
            if (tempOk) r.Max(DayRecord.MaxCpuTemp, cpuTemp);
            if (tempOk && cpuTemp >= 90) v[DayRecord.HotSec] += dt;
            if (mon.PassiveLimit < 100) v[DayRecord.ThrottleSec] += dt;
            double gt = !double.IsNaN(sens.GpuTemp) ? sens.GpuTemp : sens.NvTempC;
            r.Max(DayRecord.MaxGpuTemp, gt);
            if (mon.MemPct >= 90) v[DayRecord.MemHighSec] += dt;
            if (mon.CommitLimit > 0) v[DayRecord.CommitPeak] = Math.Max(v[DayRecord.CommitPeak], mon.CommitUsed / mon.CommitLimit * 100);
            if (!double.IsNaN(sens.BatRateW) && sens.BatRateW < -0.3) { v[DayRecord.DischargeWSum] += -sens.BatRateW * dt; v[DayRecord.DischargeN] += dt; }

            if ((now - lastFlush).TotalMinutes >= 10) Flush();
        }

        // ============================== slow probes (background) ==============================

        void ProbeLoop()
        {
            DateTime nextEvents = DateTime.MinValue, nextSmart = DateTime.MinValue, nextBattery = DateTime.MinValue, nextDrives = DateTime.MinValue;
            int wait = 3000;
            while (!stop.WaitOne(wait))
            {
                wait = 60000;
                DateTime now = DateTime.Now;
                if (now >= nextDrives) { Try("drives", ProbeDrives); nextDrives = now.AddMinutes(10); }
                if (now >= nextSmart) { Try("smart", ProbeSmart); nextSmart = now.AddHours(1); }
                if (now >= nextEvents) { Try("events", ProbeEvents); nextEvents = now.AddHours(1); }
                if (now >= nextBattery) { Try("battery", ProbeBattery); nextBattery = now.AddHours(12); }
            }
        }

        delegate void Probe();

        static void Try(string what, Probe p)
        {
            try { p(); } catch (Exception ex) { Settings.Log("health " + what + ": " + ex.Message); }
        }

        void ProbeDrives()
        {
            var list = new List<DriveSpace>();
            string sys = Path.GetPathRoot(Environment.SystemDirectory);
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                var s = new DriveSpace();
                s.Name = d.Name.TrimEnd('\\');
                s.FreeGB = d.AvailableFreeSpace / 1073741824.0;
                s.TotalGB = d.TotalSize / 1073741824.0;
                s.System = string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase);
                list.Add(s);
                if (s.System) Today(DateTime.Now).V[DayRecord.DiskFreeGB] = s.FreeGB;
            }
            Drives = list;
        }

        void ProbeSmart()
        {
            const string ns = @"root\Microsoft\Windows\Storage";
            var order = new List<DiskHealth>();
            bool needsAdmin = false;
            using (var s = new ManagementObjectSearcher(ns, "SELECT * FROM MSFT_PhysicalDisk"))
                foreach (ManagementObject o in s.Get())
                    using (o)
                    {
                        var d = new DiskHealth();
                        d.Name = Convert.ToString(o["FriendlyName"]);
                        d.HealthCode = Convert.ToInt32(o["HealthStatus"]);
                        d.Health = d.HealthCode == 0 ? "Healthy" : d.HealthCode == 1 ? "Warning" : d.HealthCode == 2 ? "Unhealthy" : "Unknown";
                        int media = Convert.ToInt32(o["MediaType"]);
                        d.Media = media == 4 ? "SSD" : media == 3 ? "HDD" : media == 5 ? "SCM" : "Disk";
                        order.Add(d);
                        // SMART-style counters are an associated object of each disk (what Get-StorageReliabilityCounter reads); admin only.
                        try
                        {
                            foreach (ManagementObject rc in o.GetRelated("MSFT_StorageReliabilityCounter"))
                                using (rc)
                                {
                                    d.Wear = Num(rc["Wear"]);
                                    d.Temp = Num(rc["Temperature"]);
                                    d.TempMax = Num(rc["TemperatureMax"]);
                                    d.PowerOnHours = Num(rc["PowerOnHours"]);
                                    double ru = Num(rc["ReadErrorsUncorrected"]), wu = Num(rc["WriteErrorsUncorrected"]);
                                    d.UncorrectedErrors = double.IsNaN(ru) && double.IsNaN(wu) ? double.NaN : (double.IsNaN(ru) ? 0 : ru) + (double.IsNaN(wu) ? 0 : wu);
                                    if (d.Temp <= 0) d.Temp = double.NaN;
                                    if (d.TempMax <= 0) d.TempMax = double.NaN;
                                }
                        }
                        catch (ManagementException) { needsAdmin = !elevated; }
                        catch (UnauthorizedAccessException) { needsAdmin = !elevated; }
                        if (double.IsNaN(d.Wear) && double.IsNaN(d.PowerOnHours) && !elevated) needsAdmin = true;
                    }
            SmartNeedsAdmin = needsAdmin;
            Disks = order;
            if (order.Count > 0)
            {
                DayRecord r = Today(DateTime.Now);
                if (!double.IsNaN(order[0].Wear)) r.V[DayRecord.SsdWear] = order[0].Wear;
                r.Max(DayRecord.SsdTempMax, order[0].Temp);
            }
        }

        static double Num(object o)
        {
            if (o == null) return double.NaN;
            try { return Convert.ToDouble(o); } catch { return double.NaN; }
        }

        void ProbeEvents()
        {
            var e = new EventSummary();
            const string last30 = "TimeCreated[timediff(@SystemTime) <= 2592000000]";
            e.Whea = Count("*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2 or Level=3) and " + last30 + "]]", null);
            e.BugChecks = Count("*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001 and " + last30 + "]]", null);
            e.GpuResets = Count("*[System[Provider[@Name='Display'] and EventID=4101 and " + last30 + "]]", null);
            e.DiskErrors = Count("*[System[(Provider[@Name='disk'] or Provider[@Name='stornvme'] or Provider[@Name='storahci'] or Provider[@Name='Ntfs'] or Provider[@Name='Microsoft-Windows-Ntfs']) and (Level=1 or Level=2) and " + last30 + "]]", null);
            e.Unexpected = Count("*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and " + last30 + "]]",
                delegate(EventRecord rec)
                {
                    if (rec.TimeCreated.HasValue && rec.TimeCreated.Value > e.LastUnexpected) e.LastUnexpected = rec.TimeCreated.Value;
                    try
                    {
                        if (rec.Properties.Count > 5 && Convert.ToInt64(rec.Properties[5].Value) != 0) e.UnexpectedInSleep++;
                    }
                    catch { }
                });
            e.Checked = DateTime.Now;
            Events = e;
        }

        delegate void RecordHandler(EventRecord r);

        static int Count(string xpath, RecordHandler each)
        {
            int n = 0;
            var q = new EventLogQuery("System", PathType.LogName, xpath);
            using (var reader = new EventLogReader(q))
            {
                EventRecord rec;
                while ((rec = reader.ReadEvent()) != null)
                    using (rec)
                    {
                        n++;
                        if (each != null) each(rec);
                        if (n >= 10000) break;
                    }
            }
            return n;
        }

        void ProbeBattery()
        {
            string xml = Path.Combine(Path.GetTempPath(), "coreglance-battery.xml");
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), "/batteryreport /xml /output \"" + xml + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return; }
            }
            if (!File.Exists(xml)) return;
            try
            {
                var doc = new XmlDocument();
                doc.Load(xml);
                XmlNodeList list = doc.GetElementsByTagName("Battery");
                if (list.Count == 0) return;
                XmlElement el = (XmlElement)list[0];
                var b = new BatteryHealth();
                b.DesignWh = Child(el, "DesignCapacity") / 1000.0;
                b.FullWh = Child(el, "FullChargeCapacity") / 1000.0;
                double cycles = Child(el, "CycleCount");
                b.Cycles = cycles > 0 ? cycles : double.NaN;
                XmlNodeList id = el.GetElementsByTagName("Id");
                b.Model = id.Count > 0 ? id[0].InnerText.Trim() : "";
                if (b.DesignWh <= 0 || b.FullWh <= 0) return;
                Battery = b;
                DayRecord r = Today(DateTime.Now);
                r.V[DayRecord.BatFullWh] = b.FullWh;
                r.V[DayRecord.BatDesignWh] = b.DesignWh;
            }
            finally { try { File.Delete(xml); } catch { } }
        }

        static double Child(XmlElement el, string name)
        {
            XmlNodeList n = el.GetElementsByTagName(name);
            double d;
            return n.Count > 0 && double.TryParse(n[0].InnerText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        // ============================== evaluation ==============================

        static double Median(List<double> xs)
        {
            if (xs.Count == 0) return double.NaN;
            var s = new List<double>(xs);
            s.Sort();
            return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
        }

        public delegate double Metric(DayRecord r);

        /// <summary>Baseline (median of the earliest heavy-use days) vs current (mean of the last 3). NaN while still learning.</summary>
        public void Drift(Metric metric, out double baseline, out double current, out int qualifiedDays)
        {
            var q = new List<double>();
            foreach (DayRecord r in Days)
            {
                if (r.V[DayRecord.LoadSec] < 1800) continue;   // at least 30 min of real load that day
                double m = metric(r);
                if (!double.IsNaN(m) && !double.IsInfinity(m)) q.Add(m);
            }
            qualifiedDays = q.Count;
            baseline = current = double.NaN;
            if (q.Count < 8) return;
            baseline = Median(q.GetRange(0, Math.Min(7, q.Count - 3)));
            current = (q[q.Count - 1] + q[q.Count - 2] + q[q.Count - 3]) / 3;
        }

        /// <summary>Least-squares slope per day of a daily column over the last n days (NaN if too few points).</summary>
        public double Slope(int col, int lastDays, int minPoints, out double latest)
        {
            List<DayRecord> all = Days;
            latest = double.NaN;
            var xs = new List<double>();
            var ys = new List<double>();
            DateTime start = DateTime.Today.AddDays(-lastDays);
            foreach (DayRecord r in all)
            {
                DateTime d;
                if (double.IsNaN(r.V[col]) || !DateTime.TryParseExact(r.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) || d < start) continue;
                xs.Add((d - start).TotalDays);
                ys.Add(r.V[col]);
                latest = r.V[col];
            }
            if (xs.Count < minPoints || xs[xs.Count - 1] - xs[0] < minPoints - 1) return double.NaN;
            double mx = 0, my = 0;
            for (int i = 0; i < xs.Count; i++) { mx += xs[i]; my += ys[i]; }
            mx /= xs.Count; my /= xs.Count;
            double num = 0, den = 0;
            for (int i = 0; i < xs.Count; i++) { num += (xs[i] - mx) * (ys[i] - my); den += (xs[i] - mx) * (xs[i] - mx); }
            return den > 0 ? num / den : double.NaN;
        }

        static string Dur(double sec)
        {
            int m = (int)Math.Round(sec / 60);
            return m >= 60 ? string.Format("{0}h {1:00}m", m / 60, m % 60) : m + " min";
        }

        static string DaysText(double d)
        {
            if (d < 1) return "less than a day";
            if (d < 60) return string.Format("about {0:0} days", d);
            if (d < 730) return string.Format("about {0:0} months", d / 30.4);
            return string.Format("about {0:0.#} years", d / 365);
        }

        public void Evaluate(SysMonitor mon, Sensors sens, double cpuTemp)
        {
            var flags = new List<HealthFlag>();
            var proj = new List<Projection>();
            DayRecord t = Today(DateTime.Now);
            double[] v = t.V;

            // ---- Heat and cooling ----
            if (!double.IsNaN(cpuTemp) && cpuTemp >= 95)
                flags.Add(new HealthFlag("cpu-hot-now", Severity.Critical, string.Format("CPU at {0:0} °C right now", cpuTemp),
                    "This is at the chip's limit. Running here for long stretches ages the CPU and the solder around it faster.",
                    "Pause the heavy job, make sure the vents are not blocked, and raise the back of the laptop."));
            if (v[DayRecord.HotSec] >= 600)
                flags.Add(new HealthFlag("cpu-hot-today", Severity.Warn, "CPU spent " + Dur(v[DayRecord.HotSec]) + " above 90 °C today",
                    "Long periods near the thermal limit cut speed and shorten hardware life.",
                    "Use a cooling pad or raise the rear, keep the vents clear, and clean dust from the fans."));
            if (v[DayRecord.ThrottleSec] >= 300)
                flags.Add(new HealthFlag("throttle-today", Severity.Warn, "Thermal throttling for " + Dur(v[DayRecord.ThrottleSec]) + " today",
                    "Windows lowered the CPU speed to protect it from heat, so renders and builds ran slower.",
                    "Improve airflow; if this is new, clean the fans and vents."));
            double fanMin = Math.Min(Nz(sens.CpuFan, 99999), Nz(sens.GpuFan, 99999));
            if (!double.IsNaN(cpuTemp) && cpuTemp >= 80 && fanMin == 0)
                flags.Add(new HealthFlag("fan-stopped", Severity.Critical, string.Format("A fan reads 0 rpm at {0:0} °C", cpuTemp),
                    "The firmware reports a stopped fan while the CPU is hot. The fan may be failing or blocked.",
                    "Save your work, let the laptop cool, and get the fan checked."));
            double gt = !double.IsNaN(sens.GpuTemp) ? sens.GpuTemp : sens.NvTempC;
            if (!double.IsNaN(gt) && gt >= 87)
                flags.Add(new HealthFlag("gpu-hot", Severity.Warn, string.Format("GPU at {0:0} °C", gt),
                    "The graphics chip is close to its limit.", "Improve airflow or cap the frame rate in games and renders."));

            double cb, cc; int cq;
            Drift(delegate(DayRecord r) { return r.Cooling; }, out cb, out cc, out cq);
            if (!double.IsNaN(cb) && cb > 0 && cc / cb >= 1.15)
                flags.Add(new HealthFlag("cooling-drift", Severity.Warn,
                    string.Format("Cooling is {0:0}% worse than this laptop's normal", (cc / cb - 1) * 100),
                    "At the same power the CPU now runs hotter. Dust in the fans or dried thermal paste are the usual causes, and it gets worse over time.",
                    "Clean the vents and fans with compressed air; if the laptop is over 2 years old, consider new thermal paste."));
            double mb, mc; int mq;
            Drift(delegate(DayRecord r) { return r.AvgMhzLoad; }, out mb, out mc, out mq);
            if (!double.IsNaN(mb) && mb > 0 && mc / mb <= 0.85)
                flags.Add(new HealthFlag("speed-drift", Severity.Warn,
                    string.Format("Under load the CPU runs {0:0}% slower than its normal", (1 - mc / mb) * 100),
                    "Sustained clock speed is falling, so renders and exports take longer. Heat or a power limit is the usual cause.",
                    "Check the power mode, use the charger for heavy work, and clean the cooling system."));

            // ---- Memory ----
            if (v[DayRecord.MemHighSec] >= 900)
            {
                string hog = mon.ProcReady && mon.TopMem.Count > 0 ? string.Format(" (top: {0}, {1:0.0} GB)", mon.TopMem[0].Name, mon.TopMem[0].Mem / 1073741824.0) : "";
                flags.Add(new HealthFlag("ram-pressure", Severity.Warn, "RAM above 90% for " + Dur(v[DayRecord.MemHighSec]) + " today",
                    "Windows is paging to the SSD, which slows everything down and adds SSD wear" + hog + ".",
                    "Close unused browser tabs and apps during renders, or add RAM if this happens every day."));
            }
            if (v[DayRecord.CommitPeak] >= 95)
                flags.Add(new HealthFlag("commit-limit", Severity.Critical, string.Format("Memory commit reached {0:0}% of its limit", v[DayRecord.CommitPeak]),
                    "When commit hits 100%, apps can crash or refuse to open.", "Close heavy apps, or let Windows manage a larger page file."));

            // ---- Storage ----
            List<DiskHealth> disks = Disks;
            if (disks != null)
                foreach (DiskHealth d in disks)
                {
                    if (d.HealthCode == 1 || d.HealthCode == 2)
                        flags.Add(new HealthFlag("disk-health-" + d.Name, Severity.Critical, d.Name + " reports " + d.Health,
                            "The drive itself says it is degrading.", "Back up now and plan a replacement."));
                    if (!double.IsNaN(d.UncorrectedErrors) && d.UncorrectedErrors > 0)
                        flags.Add(new HealthFlag("disk-errors-" + d.Name, Severity.Critical, string.Format("{0}: {1:0} uncorrectable read/write errors", d.Name, d.UncorrectedErrors),
                            "Data could not be read or written correctly.", "Back up now and run a full drive check."));
                    if (!double.IsNaN(d.Wear) && d.Wear >= 70)
                        flags.Add(new HealthFlag("ssd-wear-" + d.Name, d.Wear >= 90 ? Severity.Critical : Severity.Warn, string.Format("{0} is {1:0}% worn", d.Name, d.Wear),
                            "SSDs have a limited write life.", "Keep backups current and plan a replacement."));
                    if (!double.IsNaN(d.Temp) && d.Temp >= 70)
                        flags.Add(new HealthFlag("ssd-hot-" + d.Name, Severity.Warn, string.Format("{0} at {1:0} °C", d.Name, d.Temp),
                            "Hot SSDs slow down and wear faster.", "Improve airflow under the laptop."));
                    if (!double.IsNaN(d.Wear) && !double.IsNaN(d.PowerOnHours) && d.Wear >= 1 && d.PowerOnHours > 50)
                    {
                        double hoursLeft = d.PowerOnHours * (100 - d.Wear) / d.Wear;
                        double perDay = AvgHoursPerDay();
                        double daysLeft = hoursLeft / Math.Max(1, perDay);
                        proj.Add(new Projection(daysLeft < 365 ? Severity.Warn : Severity.Good, d.Name + " write life",
                            string.Format("{0:0}% used after {1:0} power-on hours; at your usage it lasts {2} more.", d.Wear, d.PowerOnHours, DaysText(daysLeft))));
                    }
                    else if (!double.IsNaN(d.Wear))
                        proj.Add(new Projection(Severity.Good, d.Name + " write life", string.Format("{0:0}% used. Too new to project yet.", d.Wear)));
                }
            if (SmartNeedsAdmin)
                proj.Add(new Projection(Severity.Info, "SSD wear and temperature", "Turn on admin sensor mode (right-click the widget) to read the SSD's health counters."));

            List<DriveSpace> drives = Drives;
            if (drives != null)
                foreach (DriveSpace s in drives)
                {
                    double pct = s.TotalGB > 0 ? s.FreeGB / s.TotalGB * 100 : 100;
                    if (pct < 10)
                        flags.Add(new HealthFlag("space-" + s.Name, pct < 5 ? Severity.Critical : Severity.Warn,
                            string.Format("{0} has only {1:0.0} GB free ({2:0}%)", s.Name, s.FreeGB, pct),
                            "Low free space slows the SSD and can make updates and renders fail.", "Clear caches, old renders and Downloads, or move projects to another drive."));
                }
            double latestFree;
            double freeSlope = Slope(DayRecord.DiskFreeGB, 30, 5, out latestFree);
            if (!double.IsNaN(freeSlope) && freeSlope < -0.05 && !double.IsNaN(latestFree))
            {
                double d = latestFree / -freeSlope;
                proj.Add(new Projection(d < 30 ? Severity.Warn : Severity.Good, "System drive space",
                    string.Format("Losing {0:0.0} GB a day; full in {1} at this rate ({2:0} GB free).", -freeSlope, DaysText(d), latestFree)));
                if (d < 30)
                    flags.Add(new HealthFlag("space-trend", Severity.Warn, "System drive full in " + DaysText(d),
                        string.Format("Free space is dropping by {0:0.0} GB a day.", -freeSlope), "Find what is growing (renders, caches, Downloads) and clean or move it."));
            }
            else if (drives != null && drives.Count > 0)
                proj.Add(new Projection(Severity.Good, "System drive space", "Not shrinking fast enough to project (needs at least 5 days of history)."));

            // ---- Battery ----
            BatteryHealth b = Battery;
            if (b != null)
            {
                double h = b.HealthPct;
                string model = b.Model.Length > 0 ? " (" + b.Model + ")" : "";
                if (h < 80)
                    flags.Add(new HealthFlag("battery-wear", h < 60 ? Severity.Warn : Severity.Info,
                        string.Format("Battery holds {0:0}% of its design capacity", h),
                        string.Format("{0:0.0} Wh of the original {1:0.0} Wh{2}. Runtime is shorter, and a weak battery can shut the laptop down suddenly under load or in sleep.", b.FullWh, b.DesignWh, model),
                        "Avoid draining it below 20%; if your laptop's utility has a charge limit (80%), turn it on. A replacement battery restores full runtime."));
                double latestFull;
                double s = Slope(DayRecord.BatFullWh, 90, 14, out latestFull);
                if (!double.IsNaN(s) && s < -0.002 && b.DesignWh > 0)
                {
                    double target = b.DesignWh * 0.4;
                    double d = (latestFull - target) / -s;
                    proj.Add(new Projection(d < 180 ? Severity.Warn : Severity.Info, "Battery capacity",
                        string.Format("Losing {0:0.00} Wh a month; reaches 40% of design in {1}.", -s * 30, DaysText(Math.Max(0, d)))));
                }
                else proj.Add(new Projection(h < 60 ? Severity.Warn : Severity.Good, "Battery capacity",
                    string.Format("{0:0}% of design ({1:0.0} of {2:0.0} Wh){3}. Trend needs two weeks of history.", h, b.FullWh, b.DesignWh,
                        double.IsNaN(b.Cycles) ? "" : string.Format(", {0:0} cycles", b.Cycles))));
                double drain = t.AvgDischargeW;
                if (!double.IsNaN(drain) && drain > 1)
                    proj.Add(new Projection(Severity.Info, "Battery runtime", string.Format("About {0} from full at today's average drain of {1:0.0} W.",
                        HoursText(b.FullWh / drain), drain)));
            }

            // ---- Windows event log (last 30 days) ----
            EventSummary e = Events;
            if (e != null)
            {
                if (e.Whea > 0)
                    flags.Add(new HealthFlag("whea", Severity.Critical, string.Format("{0} hardware errors logged in 30 days", e.Whea),
                        "Windows recorded WHEA errors from the CPU, memory or PCIe bus. Corrected errors are early signs of failing hardware.",
                        "Run Windows Memory Diagnostic, update the BIOS and chipset drivers, and watch whether the count grows."));
                if (e.BugChecks > 0)
                    flags.Add(new HealthFlag("bsod", Severity.Critical, string.Format("{0} blue-screen crash{1} in 30 days", e.BugChecks, e.BugChecks == 1 ? "" : "es"),
                        "The system crashed with a stop error.", "Update drivers and BIOS; if crashes repeat, test RAM and the SSD."));
                if (e.DiskErrors > 0)
                    flags.Add(new HealthFlag("disk-events", Severity.Critical, string.Format("{0} disk or file-system errors in 30 days", e.DiskErrors),
                        "Windows logged storage errors.", "Back up now, then run chkdsk and check the SSD's health."));
                if (e.GpuResets > 0)
                    flags.Add(new HealthFlag("gpu-reset", Severity.Warn, string.Format("Graphics driver reset {0} time{1} in 30 days", e.GpuResets, e.GpuResets == 1 ? "" : "s"),
                        "The GPU stopped responding and Windows restarted its driver.", "Update the GPU driver; check GPU temperature during heavy work."));
                if (e.Unexpected >= 2)
                {
                    string detail = e.UnexpectedInSleep == e.Unexpected
                        ? "All of them happened while the laptop was going to sleep or hibernate, which usually means the battery ran flat or power was cut."
                        : e.UnexpectedInSleep > 0
                            ? string.Format("{0} happened while entering sleep; the rest were sudden power-offs or freezes.", e.UnexpectedInSleep)
                            : "The laptop lost power or froze and had to be forced off.";
                    if (b != null && b.HealthPct < 60) detail += " Your worn battery makes this more likely.";
                    flags.Add(new HealthFlag("unexpected-shutdown", e.Unexpected >= 5 ? Severity.Warn : Severity.Info,
                        string.Format("{0} unexpected shutdowns in 30 days", e.Unexpected), detail,
                        "Shut down or charge before the battery gets low, and save work often until the cause is fixed."));
                }
            }

            // ---- Uptime ----
            double upDays = Native.GetTickCount64() / 86400000.0;
            if (upDays >= 14)
                flags.Add(new HealthFlag("uptime", Severity.Info, string.Format("No restart for {0:0} days", upDays),
                    "Long uptimes let memory leaks build up and delay pending updates.", "Restart when convenient."));

            // ---- Render-speed check ----
            double usual = UsualBenchScore();
            BenchResult latest = null;
            foreach (BenchResult r in Benchmarks) if (r.OnAC) latest = r;
            if (!double.IsNaN(usual) && latest != null && latest.Score < usual * 0.8)
                flags.Add(new HealthFlag("bench-drop", Severity.Warn,
                    string.Format("Render-speed check is {0:0}% below your usual", (1 - latest.Score / usual) * 100),
                    string.Format("Latest score {0:0} vs usual {1:0} (median of your earlier plugged-in runs).", latest.Score, usual),
                    "Let the laptop cool, close background apps and check the power mode, then run the check again. If it stays low, clean the fans."));

            // ---- Drift learning status and sustained speed ----
            if (double.IsNaN(cb))
                proj.Add(new Projection(Severity.Info, "Cooling and speed baseline",
                    string.Format("Learning this laptop's normal: {0} of 8 days with 30+ min of heavy use recorded.", Math.Min(cq, 8))));
            else
                proj.Add(new Projection(cc / cb >= 1.15 ? Severity.Warn : Severity.Good, "Cooling efficiency",
                    string.Format("{0:0.00} °C per watt now vs {1:0.00} normal ({2:+0;-0}%).", cc, cb, (cc / cb - 1) * 100)));
            double todayMhz = t.AvgMhzLoad;
            if (!double.IsNaN(todayMhz))
                proj.Add(new Projection(Severity.Info, "Sustained speed under load today",
                    string.Format("{0:0.00} GHz average while busy{1}.", todayMhz / 1000, double.IsNaN(mb) ? "" : string.Format(" (normal {0:0.00} GHz)", mb / 1000))));

            flags.Sort(delegate(HealthFlag a, HealthFlag c) { return ((int)c.Level).CompareTo((int)a.Level); });
            int score = 100;
            foreach (HealthFlag f in flags) score -= f.Level == Severity.Critical ? 25 : f.Level == Severity.Warn ? 10 : 2;
            Score = Math.Max(0, Math.Min(100, score));
            Flags = flags;
            Projections = proj;
            Evaluated = DateTime.Now;
        }

        static double Nz(double v, double fallback) { return double.IsNaN(v) ? fallback : v; }

        /// <summary>Median of plugged-in check scores before the latest one (needs at least 2 earlier runs).</summary>
        public double UsualBenchScore()
        {
            var ac = new List<double>();
            foreach (BenchResult r in Benchmarks) if (r.OnAC) ac.Add(r.Score);
            if (ac.Count < 3) return double.NaN;
            return Median(ac.GetRange(0, ac.Count - 1));
        }

        static string HoursText(double h)
        {
            if (double.IsNaN(h) || h <= 0) return "-";
            int m = (int)Math.Round(h * 60);
            return string.Format("{0}h {1:00}m", m / 60, m % 60);
        }

        double AvgHoursPerDay()
        {
            double sum = 0; int n = 0;
            foreach (DayRecord r in Days) { if (r.V[DayRecord.Seconds] > 600) { sum += r.V[DayRecord.Seconds] / 3600; n++; } }
            return n > 0 ? Math.Max(2, sum / n) : 8;
        }

        /// <summary>Flags that deserve a tray notification now (critical: once a day, warning: once a week).</summary>
        public List<HealthFlag> TakeAlerts()
        {
            var list = new List<HealthFlag>();
            DateTime now = DateTime.Now;
            foreach (HealthFlag f in Flags)
            {
                if (f.Level < Severity.Warn) continue;
                DateTime last;
                TimeSpan gap = f.Level == Severity.Critical ? TimeSpan.FromHours(24) : TimeSpan.FromDays(7);
                if (alerted.TryGetValue(f.Id, out last) && now - last < gap) continue;
                alerted[f.Id] = now;
                list.Add(f);
            }
            if (list.Count > 0 && persist)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var sb = new StringBuilder();
                    foreach (KeyValuePair<string, DateTime> kv in alerted) sb.Append(kv.Key).Append('|').AppendLine(kv.Value.ToString("s", CultureInfo.InvariantCulture));
                    File.WriteAllText(AlertPath, sb.ToString());
                }
                catch { }
            }
            return list;
        }

        public void RecordBench(StressTest s, bool onAC)
        {
            var b = new BenchResult();
            b.When = DateTime.Now; b.Score = s.Score; b.MaxTemp = s.MaxTemp; b.AvgMhz = s.AvgMhz; b.OnAC = onAC;
            SaveBench(b);
        }

        public void Dispose()
        {
            stop.Set();
            probe.Join(2000);
            Stress.Cancel();
            Flush();
        }
    }

    /// <summary>
    /// "Render speed" check: loads every logical CPU with an integer workload for a fixed time and reports throughput,
    /// so slowdowns from heat, dust or power limits show up as a lower score than the laptop's own best.
    /// </summary>
    sealed class StressTest
    {
        public volatile bool Running;
        public double Progress, Score = double.NaN, MaxTemp = double.NaN, AvgMhz = double.NaN, Seconds;
        volatile bool cancel;
        long[] counters;

        public delegate double Reading();
        public delegate void Done(StressTest t);

        public void Start(int seconds, Reading temp, Reading mhz, Done done)
        {
            if (Running) return;
            Running = true;
            cancel = false;
            Progress = 0;
            Score = MaxTemp = AvgMhz = double.NaN;
            Seconds = seconds;
            int n = Environment.ProcessorCount;
            counters = new long[n * 16];   // one cache line apart
            var workers = new List<Thread>();
            var startGate = new ManualResetEvent(false);
            for (int i = 0; i < n; i++)
            {
                int slot = i * 16;
                var th = new Thread(delegate()
                {
                    startGate.WaitOne();
                    ulong x = 88172645463325252UL + (ulong)slot;
                    long count = 0;
                    while (!cancel)
                    {
                        for (int k = 0; k < 20000; k++)
                        {
                            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                            x = x * 0x2545F4914F6CDD1DUL + (x >> 11);
                        }
                        count += 20000;
                        Interlocked.Exchange(ref counters[slot], count);
                    }
                });
                th.IsBackground = true;
                th.Priority = ThreadPriority.BelowNormal;   // the desktop stays usable
                workers.Add(th);
                th.Start();
            }
            var monitor = new Thread(delegate()
            {
                var sw = Stopwatch.StartNew();
                startGate.Set();
                double mhzSum = 0; int mhzN = 0;
                while (sw.Elapsed.TotalSeconds < seconds && !cancel)
                {
                    Thread.Sleep(500);
                    Progress = Math.Min(1, sw.Elapsed.TotalSeconds / seconds);
                    double t = temp();
                    if (!double.IsNaN(t) && (double.IsNaN(MaxTemp) || t > MaxTemp)) MaxTemp = t;
                    double m = mhz();
                    if (m > 0 && sw.Elapsed.TotalSeconds > 3) { mhzSum += m; mhzN++; }
                }
                bool cancelled = cancel;
                cancel = true;
                foreach (Thread th in workers) th.Join(2000);
                long total = 0;
                for (int i = 0; i < counters.Length; i += 16) total += Interlocked.Read(ref counters[i]);
                double secs = sw.Elapsed.TotalSeconds;
                Score = cancelled ? double.NaN : total / secs / 1e6;   // million mixing rounds per second
                AvgMhz = mhzN > 0 ? mhzSum / mhzN : double.NaN;
                Progress = 1;
                Running = false;
                if (!cancelled && done != null) done(this);
            });
            monitor.IsBackground = true;
            monitor.Start();
        }

        public void Cancel() { cancel = true; }
    }
}
