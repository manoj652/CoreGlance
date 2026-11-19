using System;
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
    /// Slow sensors polled on a background thread so the UI never waits on WMI or the NVIDIA driver:
    /// Acer firmware (CPU/GPU/system temperature, fan RPM; needs admin), battery charge rate (WMI, no admin),
    /// NVIDIA power/clock/temperature (NVML, only while the dGPU is already awake so it is never woken up).
    /// Values are NaN when unknown.
    /// </summary>
    sealed class Sensors : IDisposable
    {
        public volatile bool Paused;
        public volatile bool NvidiaActive;   // set by the UI thread from GPU engine activity

        public double CpuTemp = double.NaN, GpuTemp = double.NaN, SysTemp = double.NaN;
        public double CpuFan = double.NaN, GpuFan = double.NaN;
        public double NvPowerW = double.NaN, NvClockMhz = double.NaN, NvTempC = double.NaN, NvFanPct = double.NaN;
        public double BatRateW = double.NaN, BatRemainWh = double.NaN, BatFullWh = double.NaN;
        public bool BatCharging, BatDischarging, BatOnline;

        public readonly bool Elevated;
        public readonly bool AcerAvailable;   // the laptop exposes Acer's sensor interface
        public bool AcerWorking;               // and we are reading it

        readonly Thread thread;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        ManagementObject acer;
        uint acerSensors;     // bitmap: bit (id-1) set when sensor id is supported
        DateTime acerRetry = DateTime.MinValue;
        bool nvmlLoaded, nvmlMissing;
        IntPtr nvDev;
        DateTime nvLastActive = DateTime.MinValue;

        public Sensors()
        {
            Elevated = Elevation.IsElevated;
            AcerAvailable = AcerClassExists();
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.BelowNormal;
            thread.Name = "CoreGlance sensors";
            thread.Start();
        }

        public bool AcerNeedsAdmin { get { return AcerAvailable && !Elevated; } }

        void Run()
        {
            int n = 0;
            while (!stop.WaitOne(n == 0 ? 300 : 2000))
            {
                if (Paused) continue;
                try { if (Elevated && AcerAvailable) ReadAcer(); } catch (Exception ex) { Settings.Log("acer: " + ex.Message); }
                try { if (n % 2 == 0) ReadBattery(n % 30 == 0); } catch (Exception ex) { Settings.Log("battery: " + ex.Message); }
                try { ReadNvml(); } catch (Exception ex) { Settings.Log("nvml: " + ex.Message); nvmlMissing = true; }
                n++;
            }
            NvmlOff();
        }

        // ---------------- Acer (root\wmi AcerGamingFunction.GetGamingSysInfo, read-only) ----------------

        static bool AcerClassExists()
        {
            try
            {
                using (var c = new ManagementClass(@"root\wmi", "AcerGamingFunction", null))
                {
                    c.Get();
                    return true;
                }
            }
            catch { return false; }
        }

        void ReadAcer()
        {
            if (acer == null)
            {
                if (DateTime.UtcNow < acerRetry) return;
                acerRetry = DateTime.UtcNow.AddSeconds(60);
                using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AcerGamingFunction"))
                    foreach (ManagementObject o in s.Get()) { acer = o; break; }
                if (acer == null) return;
                ulong sup;
                acerSensors = AcerQuery(0x0000, out sup) ? (uint)((sup >> 24) & 0x1FFFF) : 0x227;
                if (acerSensors == 0) acerSensors = 0x227;
            }
            bool any = false;
            CpuTemp = AcerSensor(0x01, ref any);
            CpuFan = AcerSensor(0x02, ref any);
            SysTemp = AcerSensor(0x03, ref any);
            GpuFan = AcerSensor(0x06, ref any);
            GpuTemp = AcerSensor(0x0A, ref any);
            AcerWorking = any;
        }

        double AcerSensor(int id, ref bool any)
        {
            if ((acerSensors & (1u << (id - 1))) == 0) return double.NaN;
            ulong r;
            if (!AcerQuery((uint)(0x01 | (id << 8)), out r) || (r & 0xFF) != 0) return double.NaN;
            double v = (r >> 8) & 0xFFFF;
            bool temp = id == 0x01 || id == 0x03 || id == 0x0A;
            if (temp && (v <= 0 || v > 125)) return double.NaN;
            if (!temp && v > 20000) return double.NaN;
            any = true;
            return v;
        }

        bool AcerQuery(uint input, out ulong output)
        {
            output = 0;
            try
            {
                using (ManagementBaseObject inP = acer.GetMethodParameters("GetGamingSysInfo"))
                {
                    inP["gmInput"] = input;
                    using (ManagementBaseObject outP = acer.InvokeMethod("GetGamingSysInfo", inP, null))
                    {
                        output = Convert.ToUInt64(outP["gmOutput"]);
                        return true;
                    }
                }
            }
            catch (ManagementException)
            {
                acer = null;   // e.g. access denied; retry later
                return false;
            }
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
            if (acer != null) acer.Dispose();
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
