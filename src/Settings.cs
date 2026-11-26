using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CoreGlance
{
    /// <summary>Per-user settings in %APPDATA%\CoreGlance\settings.ini (plain key=value).</summary>
    sealed class Settings
    {
        public int X = int.MinValue, Y = int.MinValue;
        public int View = 1;          // 0 compact, 1 standard, 2 detailed
        public int Interval = 1000;   // ms
        public int ProcSort = 0;      // 0 cpu, 1 memory, 2 gpu
        public int CpuMetric = 0;     // 0 busy time (like NZXT CAM), 1 utility (like Task Manager)
        public bool TopMost = true, Locked, ClickThrough, Visible = true, HideFullscreen = true, InTaskbar = true;
        public bool LowPower, LowPowerAuto, LowPowerByAuto;
        public int LowPowerLevel = 0;          // 0 battery saver, 1 max saver
        public string LowPowerPrev = "";       // power plan to go back to
        public double Opacity = 1.0;
        public bool FirstRun;

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoreGlance"); }
        }

        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) { s.FirstRun = true; return s; }
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = raw.Substring(0, eq).Trim(), v = raw.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "X": s.X = Int(v, s.X); break;
                        case "Y": s.Y = Int(v, s.Y); break;
                        case "View": s.View = Int(v, s.View); break;
                        case "Interval": s.Interval = Int(v, s.Interval); break;
                        case "ProcSort": s.ProcSort = Int(v, s.ProcSort); break;
                        case "CpuMetric": s.CpuMetric = Int(v, s.CpuMetric) == 1 ? 1 : 0; break;
                        case "Opacity": s.Opacity = Dbl(v, s.Opacity); break;
                        case "TopMost": s.TopMost = v == "1"; break;
                        case "Locked": s.Locked = v == "1"; break;
                        case "ClickThrough": s.ClickThrough = v == "1"; break;
                        case "Visible": s.Visible = v == "1"; break;
                        case "HideFullscreen": s.HideFullscreen = v == "1"; break;
                        case "InTaskbar": s.InTaskbar = v == "1"; break;
                        case "LowPower": s.LowPower = v == "1"; break;
                        case "LowPowerAuto": s.LowPowerAuto = v == "1"; break;
                        case "LowPowerByAuto": s.LowPowerByAuto = v == "1"; break;
                        case "LowPowerLevel": s.LowPowerLevel = Int(v, 0) == 1 ? 1 : 0; break;
                        case "LowPowerPrev": s.LowPowerPrev = v; break;
                    }
                }
            }
            catch (Exception ex) { Log("settings load: " + ex.Message); }

            if (s.View < 0 || s.View > 2) s.View = 1;
            if (s.Interval != 500 && s.Interval != 1000 && s.Interval != 2000 && s.Interval != 5000) s.Interval = 1000;
            if (s.Opacity < 0.4 || s.Opacity > 1) s.Opacity = 1.0;
            if (s.ProcSort < 0 || s.ProcSort > 2) s.ProcSort = 0;
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.AppendLine("; CoreGlance settings");
                Line(sb, "X", X); Line(sb, "Y", Y);
                Line(sb, "View", View); Line(sb, "Interval", Interval); Line(sb, "ProcSort", ProcSort); Line(sb, "CpuMetric", CpuMetric);
                sb.AppendLine("Opacity=" + Opacity.ToString("0.00", CultureInfo.InvariantCulture));
                Line(sb, "TopMost", TopMost ? 1 : 0); Line(sb, "Locked", Locked ? 1 : 0);
                Line(sb, "ClickThrough", ClickThrough ? 1 : 0); Line(sb, "Visible", Visible ? 1 : 0);
                Line(sb, "HideFullscreen", HideFullscreen ? 1 : 0); Line(sb, "InTaskbar", InTaskbar ? 1 : 0);
                Line(sb, "LowPower", LowPower ? 1 : 0); Line(sb, "LowPowerAuto", LowPowerAuto ? 1 : 0); Line(sb, "LowPowerByAuto", LowPowerByAuto ? 1 : 0);
                Line(sb, "LowPowerLevel", LowPowerLevel); sb.Append("LowPowerPrev=").AppendLine(LowPowerPrev);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log("settings save: " + ex.Message); }
        }

        static void Line(StringBuilder sb, string k, int v)
        {
            sb.Append(k).Append('=').AppendLine(v.ToString(CultureInfo.InvariantCulture));
        }

        static int Int(string v, int d)
        {
            int r;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : d;
        }

        static double Dbl(string v, double d)
        {
            double r;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : d;
        }

        public static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string path = Path.Combine(Dir, "error.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        // ---------------- Start with Windows (HKCU Run key, no admin needed) ----------------
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "CoreGlance";

        public static bool IsAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    string v = k == null ? null : k.GetValue(RunName) as string;
                    return !string.IsNullOrEmpty(v);
                }
            }
            catch { return false; }
        }

        public static void SetAutoStart(bool on) { SetAutoStart(on, Application.ExecutablePath); }

        public static void SetAutoStart(bool on, string exePath)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + exePath + "\" --autostart");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch (Exception ex) { Log("autostart: " + ex.Message); }
        }
    }
}
