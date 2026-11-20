using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CoreGlance
{
    /// <summary>
    /// Slow sensors polled on a background thread so the UI never waits on WMI, firmware or drivers.
    /// Firmware/driver readings come from whichever providers this laptop supports (first one to report a value wins):
    /// PawnIO CPU temps, then Acer / ASUS / Lenovo / HP / Dell firmware, then Dell SMM.
    /// Also battery charge rate (WMI) and NVIDIA power/clock/temperature (NVML, only while the dGPU is awake).
    /// Values are NaN when unknown.
    /// </summary>
    sealed class Sensors : IDisposable
    {
        public volatile bool Paused;
        public volatile bool NvidiaActive;   // set by the UI thread from GPU engine activity

        public double CpuTemp = double.NaN, CoreMaxTemp = double.NaN, GpuTemp = double.NaN, SysTemp = double.NaN;
        public double CpuFan = double.NaN, GpuFan = double.NaN, MidFan = double.NaN;
        public string CpuTempSource = "";
        public double NvPowerW = double.NaN, NvClockMhz = double.NaN, NvTempC = double.NaN, NvFanPct = double.NaN;
        public double BatRateW = double.NaN, BatRemainWh = double.NaN, BatFullWh = double.NaN;
        public bool BatCharging, BatDischarging, BatOnline;

        public readonly bool Elevated;
        public volatile bool Ready;              // provider detection finished
        public volatile bool VendorDetected;     // a laptop-maker sensor interface exists (may need admin)
        public volatile bool PawnIoInstalled;
        public volatile string SourceList = "detecting...";

        readonly List<KeyValuePair<int, int>> cores;
        readonly Thread thread;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly List<SensorProvider> providers = new List<SensorProvider>();
        readonly List<int> failures = new List<int>();
        bool nvmlLoaded, nvmlMissing;
        IntPtr nvDev;
        DateTime nvLastActive = DateTime.MinValue;

        public Sensors(List<KeyValuePair<int, int>> physicalCores)
        {
            cores = physicalCores;
            Elevated = Elevation.IsElevated;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.BelowNormal;
            thread.Name = "CoreGlance sensors";
            thread.Start();
        }

        /// <summary>Running as admin would unlock more readings on this laptop.</summary>
        public bool NeedsAdmin { get { return Ready && !Elevated && (VendorDetected || PawnIoInstalled); } }

        void Detect()
        {
            PawnIoInstalled = PawnModule.Installed;
            string maker = "";
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_ComputerSystem"))
                    foreach (ManagementObject o in s.Get()) using (o) { maker = Convert.ToString(o["Manufacturer"]).ToLowerInvariant(); break; }
            }
            catch { }

            if (PawnIoInstalled) TryAdd(delegate { return PawnCpuProvider.TryCreate(cores); });

            bool acer = Io.WmiClassExists("AcerGamingFunction");
            bool lenovo = Io.WmiClassExists("LENOVO_OTHER_METHOD") || Io.WmiClassExists("LENOVO_FAN_METHOD");
            bool hp = Io.WmiClassExists("hpqBIntM") && maker.Contains("hp");
            bool awcc = Io.WmiClassExists("AWCCWmiMethodFunction");
            bool asus = maker.Contains("asus");
            VendorDetected = acer || lenovo || hp || awcc || asus;

            if (asus) TryAdd(delegate { return AsusProvider.TryCreate(); });
            if (acer) TryAdd(delegate { return AcerProvider.TryCreate(); });
            if (lenovo) TryAdd(delegate { return LenovoProvider.TryCreate(); });
            if (hp) TryAdd(delegate { return HpProvider.TryCreate(); });
            bool awccOk = awcc && TryAdd(delegate { return DellAwccProvider.TryCreate(); });
            if (PawnIoInstalled && maker.Contains("dell") && !awccOk) TryAdd(delegate { return PawnDellSmmProvider.TryCreate(); });

            var names = new List<string>();
            foreach (SensorProvider p in providers) names.Add(p.Name);
            SourceList = names.Count > 0 ? string.Join(", ", names.ToArray()) : "Windows counters only";
            Ready = true;
        }

        delegate SensorProvider Factory();

        bool TryAdd(Factory make)
        {
            try
            {
                SensorProvider p = make();
                if (p == null) return false;
                providers.Add(p);
                failures.Add(0);
                return true;
            }
            catch (Exception ex)
            {
                Settings.Log("sensor provider: " + ex.Message);
                return false;
            }
        }

        void Run()
        {
            try { Detect(); } catch (Exception ex) { Settings.Log("detect: " + ex); Ready = true; }
            int n = 0;
            while (!stop.WaitOne(n == 0 ? 200 : 2000))
            {
                if (Paused) continue;
                try { ReadProviders(n); } catch (Exception ex) { Settings.Log("sensors: " + ex.Message); }
                try { if (n % 2 == 0) ReadBattery(n % 30 == 0); } catch (Exception ex) { Settings.Log("battery: " + ex.Message); }
                try { ReadNvml(); } catch (Exception ex) { Settings.Log("nvml: " + ex.Message); nvmlMissing = true; }
                n++;
            }
            NvmlOff();
        }

        SensorFrame last = new SensorFrame();

        void ReadProviders(int n)
        {
            if (providers.Count == 0) return;
            var f = new SensorFrame();
            for (int i = 0; i < providers.Count; i++)
            {
                SensorProvider p = providers[i];
                if (failures[i] >= 5) continue;                // gave up on this one
                if (n % p.PollEvery != 0) continue;
                try { p.Read(f); failures[i] = 0; }
                catch (Exception ex)
                {
                    failures[i]++;
                    if (failures[i] == 5) Settings.Log(p.Name + " disabled: " + ex.Message);
                }
            }
            // Providers polled less often keep their last value in between.
            if (double.IsNaN(f.CpuTemp)) { f.CpuTemp = last.CpuTemp; f.CpuTempSource = last.CpuTempSource; }
            if (double.IsNaN(f.CoreMaxTemp)) f.CoreMaxTemp = last.CoreMaxTemp;
            if (double.IsNaN(f.GpuTemp)) f.GpuTemp = last.GpuTemp;
            if (double.IsNaN(f.SysTemp)) f.SysTemp = last.SysTemp;
            if (double.IsNaN(f.CpuFan)) f.CpuFan = last.CpuFan;
            if (double.IsNaN(f.GpuFan)) f.GpuFan = last.GpuFan;
            if (double.IsNaN(f.MidFan)) f.MidFan = last.MidFan;
            last = f;

            CpuTemp = f.CpuTemp; CpuTempSource = f.CpuTempSource; CoreMaxTemp = f.CoreMaxTemp;
            GpuTemp = f.GpuTemp; SysTemp = f.SysTemp;
            CpuFan = f.CpuFan; GpuFan = f.GpuFan; MidFan = f.MidFan;
        }

        // ---------------- Battery (root\wmi BatteryStatus, no admin) ----------------

        void ReadBattery(bool full)
        {
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT ChargeRate, DischargeRate, RemainingCapacity, Charging, Discharging, PowerOnline FROM BatteryStatus"))
            {
                bool found = false;
                foreach (ManagementObject o in s.Get())
                {
                    using (o)
                    {
                        found = true;
                        double charge = ToD(o["ChargeRate"]), discharge = ToD(o["DischargeRate"]);
                        BatCharging = ToB(o["Charging"]);
                        BatDischarging = ToB(o["Discharging"]);
                        BatOnline = ToB(o["PowerOnline"]);
                        BatRemainWh = ToD(o["RemainingCapacity"]) / 1000.0;
                        if (BatDischarging && discharge > 0) BatRateW = -discharge / 1000.0;
                        else if (charge > 0) BatRateW = charge / 1000.0;
                        else BatRateW = 0;
                    }
                    break;
                }
                if (!found) { BatRateW = double.NaN; return; }
            }
            if (full || double.IsNaN(BatFullWh))
            {
                using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
                    foreach (ManagementObject o in s.Get()) { using (o) BatFullWh = ToD(o["FullChargedCapacity"]) / 1000.0; break; }
            }
        }

        static double ToD(object o) { try { return o == null ? double.NaN : Convert.ToDouble(o); } catch { return double.NaN; } }
        static bool ToB(object o) { try { return o != null && Convert.ToBoolean(o); } catch { return false; } }

        // ---------------- NVIDIA (nvml.dll ships with the driver) ----------------

        void ReadNvml()
        {
            if (nvmlMissing) return;
            if (NvidiaActive) nvLastActive = DateTime.UtcNow;
            bool awake = (DateTime.UtcNow - nvLastActive).TotalSeconds < 15;
            if (!awake)
            {
                // Never query a sleeping dGPU: that would wake it and drain the battery.
                NvmlOff();
                NvPowerW = NvClockMhz = NvTempC = NvFanPct = double.NaN;
                return;
            }
            if (!nvmlLoaded)
            {
                if (Nvml.nvmlInit_v2() != 0) { nvmlMissing = true; return; }
                nvmlLoaded = true;
                if (Nvml.nvmlDeviceGetHandleByIndex_v2(0, out nvDev) != 0) { NvmlOff(); nvmlMissing = true; return; }
            }
            uint v;
            NvTempC = Nvml.nvmlDeviceGetTemperature(nvDev, 0, out v) == 0 ? v : double.NaN;
            NvPowerW = Nvml.nvmlDeviceGetPowerUsage(nvDev, out v) == 0 ? v / 1000.0 : double.NaN;
            NvClockMhz = Nvml.nvmlDeviceGetClockInfo(nvDev, 0, out v) == 0 ? v : double.NaN;
            NvFanPct = Nvml.nvmlDeviceGetFanSpeed(nvDev, out v) == 0 ? v : double.NaN;
        }

        void NvmlOff()
        {
            if (!nvmlLoaded) return;
            try { Nvml.nvmlShutdown(); } catch { }
            nvmlLoaded = false;
            nvDev = IntPtr.Zero;
        }

        public void Dispose()
        {
            stop.Set();
            thread.Join(3000);
            stop.Close();
            foreach (SensorProvider p in providers) { try { p.Dispose(); } catch { } }
        }
    }

    static class Nvml
    {
        [DllImport("nvml.dll")] public static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] public static extern int nvmlShutdown();
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
    }

    /// <summary>
    /// Optional "sensor mode": a scheduled task that starts CoreGlance with admin rights without a UAC prompt each time.
    /// The admin copy lives in Program Files so a normal-user process cannot swap the exe the task runs.
    /// </summary>
    static class Elevation
    {
        public const string TaskName = "CoreGlance";

        public static bool IsElevated
        {
            get
            {
                try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
                catch { return false; }
            }
        }

        public static string SecureExePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CoreGlance", "CoreGlance.exe"); }
        }

        static int Schtasks(string args)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return -1; }
                return p.ExitCode;
            }
        }

        public static bool TaskExists()
        {
            try { return Schtasks("/Query /TN \"" + TaskName + "\"") == 0; } catch { return false; }
        }

        public static bool RunTask()
        {
            try { return Schtasks("/Run /TN \"" + TaskName + "\"") == 0; } catch { return false; }
        }

        /// <summary>Runs in an elevated child (CoreGlance.exe --install-task). Returns the process exit code.</summary>
        public static int InstallTask()
        {
            try
            {
                string target = SecureExePath;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (!string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    // Replace the secure copy (an older copy may be running from there).
                    foreach (Process p in Process.GetProcessesByName("CoreGlance"))
                    {
                        try
                        {
                            if (p.Id != Process.GetCurrentProcess().Id && string.Equals(p.MainModule.FileName, target, StringComparison.OrdinalIgnoreCase))
                            { p.Kill(); p.WaitForExit(3000); }
                        }
                        catch { }
                    }
                    File.Copy(Application.ExecutablePath, target, true);
                }

                string sid = WindowsIdentity.GetCurrent().User.Value;
                string xml =
                    "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                    "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                    "  <RegistrationInfo><Description>Starts CoreGlance with admin rights so it can read temperatures and fan speeds. Started on demand by CoreGlance.exe.</Description></RegistrationInfo>\r\n" +
                    "  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(sid) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                    "  <Settings>\r\n" +
                    "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                    "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                    "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                    "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                    "    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
                    "    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
                    "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                    "    <Enabled>true</Enabled>\r\n" +
                    "    <Hidden>false</Hidden>\r\n" +
                    "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                    "    <Priority>7</Priority>\r\n" +
                    "  </Settings>\r\n" +
                    "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(target) + "</Command><Arguments>--from-task</Arguments></Exec></Actions>\r\n" +
                    "</Task>\r\n";
                string tmp = Path.Combine(Path.GetTempPath(), "CoreGlance-task.xml");
                File.WriteAllText(tmp, xml, Encoding.Unicode);
                int rc = Schtasks("/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F");
                try { File.Delete(tmp); } catch { }
                if (rc != 0) Settings.Log("schtasks /Create failed: " + rc);
                return rc == 0 ? 0 : 2;
            }
            catch (Exception ex)
            {
                Settings.Log("install-task: " + ex);
                return 1;
            }
        }

        public static int RemoveTask()
        {
            try { return Schtasks("/Delete /TN \"" + TaskName + "\" /F") == 0 ? 0 : 2; }
            catch (Exception ex) { Settings.Log("remove-task: " + ex.Message); return 1; }
        }

        /// <summary>Asks for UAC once and runs this exe with the given switch elevated. Returns true on success.</summary>
        /// <summary>Runs elevated (CoreGlance.exe --install-pawnio): installs the signed PawnIO driver from winget.</summary>
        public static int InstallPawnIo()
        {
            if (PawnModule.Installed) return 0;
            try
            {
                var psi = new ProcessStartInfo("winget.exe",
                    "install --id namazso.PawnIO --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(300000)) { try { p.Kill(); } catch { } return 3; }
                    if (p.ExitCode != 0) Settings.Log("winget PawnIO exit code " + p.ExitCode);
                }
            }
            catch (Exception ex)
            {
                Settings.Log("install-pawnio: " + ex.Message);
                return 1;
            }
            return PawnModule.Installed ? 0 : 2;
        }

        public static bool RunElevated(string arg)
        {
            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath, arg);
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch (System.ComponentModel.Win32Exception) { return false; }   // UAC declined
        }
    }
}
