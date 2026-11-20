using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CoreGlance
{
    /// <summary>One poll's worth of firmware/driver readings. NaN = not provided. First provider to set a value wins.</summary>
    sealed class SensorFrame
    {
        public double CpuTemp = double.NaN, CoreMaxTemp = double.NaN, GpuTemp = double.NaN, SysTemp = double.NaN;
        public double CpuFan = double.NaN, GpuFan = double.NaN, MidFan = double.NaN;
        public string CpuTempSource = "";

        public void SetCpuTemp(double v, string source)
        {
            if (double.IsNaN(CpuTemp) && Valid(v)) { CpuTemp = v; CpuTempSource = source; }
        }
        public void Set(ref double field, double v, bool isTemp)
        {
            if (double.IsNaN(field) && (isTemp ? Valid(v) : ValidFan(v))) field = v;
        }
        public static bool Valid(double t) { return !double.IsNaN(t) && t > 5 && t < 125; }
        public static bool ValidFan(double r) { return !double.IsNaN(r) && r >= 0 && r < 20000; }
    }

    abstract class SensorProvider : IDisposable
    {
        public abstract string Name { get; }
        public virtual int PollEvery { get { return 1; } }   // in sensor-loop iterations (~2 s each)
        public abstract void Read(SensorFrame f);
        public virtual void Dispose() { }
    }

    static class Io
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out uint returned, IntPtr overlapped);

        [StructLayout(LayoutKind.Sequential)]
        public struct GROUP_AFFINITY { public UIntPtr Mask; public ushort Group; public ushort R0, R1, R2; }

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetThreadGroupAffinity(IntPtr thread, ref GROUP_AFFINITY affinity, out GROUP_AFFINITY previous);

        public static bool WmiClassExists(string cls)
        {
            try
            {
                using (var c = new ManagementClass(@"root\wmi", cls, null)) { c.Get(); return true; }
            }
            catch { return false; }
        }

        public static ManagementObject FirstInstance(string cls)
        {
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM " + cls))
                foreach (ManagementObject o in s.Get()) return o;
            return null;
        }

        public static int Invoke(ManagementObject obj, string method, string inName, object inValue, string outName)
        {
            using (ManagementBaseObject inP = obj.GetMethodParameters(method))
            {
                if (inName != null) inP[inName] = inValue;
                using (ManagementBaseObject outP = obj.InvokeMethod(method, inP, null))
                    return Convert.ToInt32(outP[outName]);
            }
        }
    }

    // ===================================================================== vendor firmware

    /// <summary>Acer Nitro/Predator/Aspire: root\wmi AcerGamingFunction.GetGamingSysInfo (as Linux acer-wmi).</summary>
    sealed class AcerProvider : SensorProvider
    {
        readonly ManagementObject acer;
        readonly uint supported;
        public override string Name { get { return "Acer firmware"; } }

        AcerProvider(ManagementObject o, uint sup) { acer = o; supported = sup; }

        public static AcerProvider TryCreate()
        {
            ManagementObject o = Io.FirstInstance("AcerGamingFunction");
            if (o == null) return null;
            ulong sup;
            uint bitmap = Query(o, 0x0000, out sup) && (sup & 0xFF) == 0 ? (uint)((sup >> 24) & 0x1FFFF) : 0;
            if (bitmap == 0) bitmap = 0x227;
            return new AcerProvider(o, bitmap);
        }

        static bool Query(ManagementObject o, uint input, out ulong output)
        {
            using (ManagementBaseObject inP = o.GetMethodParameters("GetGamingSysInfo"))
            {
                inP["gmInput"] = input;
                using (ManagementBaseObject outP = o.InvokeMethod("GetGamingSysInfo", inP, null))
                {
                    output = Convert.ToUInt64(outP["gmOutput"]);
                    return true;
                }
            }
        }

        double Sensor(int id)
        {
            if ((supported & (1u << (id - 1))) == 0) return double.NaN;
            ulong r;
            if (!Query(acer, (uint)(0x01 | (id << 8)), out r) || (r & 0xFF) != 0) return double.NaN;
            return (r >> 8) & 0xFFFF;
        }

        public override void Read(SensorFrame f)
        {
            f.SetCpuTemp(Sensor(0x01), "Acer");
            f.Set(ref f.CpuFan, Sensor(0x02), false);
            f.Set(ref f.SysTemp, Sensor(0x03), true);
            f.Set(ref f.GpuFan, Sensor(0x06), false);
            f.Set(ref f.GpuTemp, Sensor(0x0A), true);
        }

        public override void Dispose() { acer.Dispose(); }
    }

    /// <summary>ASUS ROG/TUF/Zenbook/Vivobook: ATKACPI driver DSTS calls (as G-Helper).</summary>
    sealed class AsusProvider : SensorProvider
    {
        const uint ControlCode = 0x0022240C, DSTS = 0x53545344;
        const uint CpuFan = 0x00110013, GpuFan = 0x00110014, MidFan = 0x00110031, TempCpu = 0x00120094, TempGpu = 0x00120097;
        readonly SafeFileHandle h;
        readonly byte[] inBuf = new byte[16], outBuf = new byte[16];
        public override string Name { get { return "ASUS firmware"; } }

        AsusProvider(SafeFileHandle handle) { h = handle; }

        public static AsusProvider TryCreate()
        {
            SafeFileHandle h = Io.CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return null; }
            var p = new AsusProvider(h);
            int probe = p.Get(CpuFan);
            if (probe == int.MinValue) { p.Dispose(); return null; }
            return p;
        }

        int Get(uint device)
        {
            BitConverter.GetBytes(DSTS).CopyTo(inBuf, 0);
            BitConverter.GetBytes(8u).CopyTo(inBuf, 4);
            BitConverter.GetBytes(device).CopyTo(inBuf, 8);
            BitConverter.GetBytes(0u).CopyTo(inBuf, 12);
            Array.Clear(outBuf, 0, outBuf.Length);
            uint ret;
            if (!Io.DeviceIoControl(h, ControlCode, inBuf, inBuf.Length, outBuf, outBuf.Length, out ret, IntPtr.Zero)) return int.MinValue;
            return BitConverter.ToInt32(outBuf, 0) - 65536;
        }

        double Fan(uint device)
        {
            int raw = Get(device);
            if (raw == int.MinValue) return double.NaN;
            int fan = raw & 0xFFFF;
            if (fan > 120 || (fan == 0 && raw < 0)) return double.NaN;
            return fan * 100;
        }

        double Temp(uint device)
        {
            int raw = Get(device);
            return raw == int.MinValue ? double.NaN : raw;
        }

        public override void Read(SensorFrame f)
        {
            f.SetCpuTemp(Temp(TempCpu), "ASUS");
            f.Set(ref f.GpuTemp, Temp(TempGpu), true);
            f.Set(ref f.CpuFan, Fan(CpuFan), false);
            f.Set(ref f.GpuFan, Fan(GpuFan), false);
            f.Set(ref f.MidFan, Fan(MidFan), false);
        }

        public override void Dispose() { h.Dispose(); }
    }

    /// <summary>Lenovo Legion / LOQ / IdeaPad Gaming: LENOVO_OTHER_METHOD or LENOVO_FAN_METHOD (as Legion Toolkit).</summary>
    sealed class LenovoProvider : SensorProvider
    {
        readonly ManagementObject other, fan;
        readonly bool v3;
        public override string Name { get { return "Lenovo firmware"; } }

        LenovoProvider(ManagementObject o, ManagementObject fm, bool useV3) { other = o; fan = fm; v3 = useV3; }

        public static LenovoProvider TryCreate()
        {
            ManagementObject other = null, fan = null;
            try { other = Io.FirstInstance("LENOVO_OTHER_METHOD"); } catch { }
            try { fan = Io.FirstInstance("LENOVO_FAN_METHOD"); } catch { }
            if (other != null)
            {
                try
                {
                    int t = Io.Invoke(other, "GetFeatureValue", "IDs", 0x05040000, "Value");
                    if (SensorFrame.Valid(t)) return new LenovoProvider(other, fan, true);
                }
                catch { }
            }
            if (fan != null)
            {
                try
                {
                    Io.Invoke(fan, "Fan_GetCurrentFanSpeed", "FanID", 0, "CurrentFanSpeed");
                    return new LenovoProvider(other, fan, false);
                }
                catch { }
            }
            return null;
        }

        double Feature(int id)
        {
            try { return Io.Invoke(other, "GetFeatureValue", "IDs", id, "Value"); } catch { return double.NaN; }
        }

        double FanSpeed(int id)
        {
            try { return Io.Invoke(fan, "Fan_GetCurrentFanSpeed", "FanID", id, "CurrentFanSpeed"); } catch { return double.NaN; }
        }

        double SensorTemp(int id)
        {
            try { return Io.Invoke(fan, "Fan_GetCurrentSensorTemperature", "SensorID", id, "CurrentSensorTemperature"); } catch { return double.NaN; }
        }

        public override void Read(SensorFrame f)
        {
            if (v3)
            {
                f.SetCpuTemp(Feature(0x05040000), "Lenovo");
                f.Set(ref f.GpuTemp, Feature(0x05050000), true);
                f.Set(ref f.CpuFan, Feature(0x04030001), false);
                f.Set(ref f.GpuFan, Feature(0x04030002), false);
            }
            else
            {
                f.SetCpuTemp(SensorTemp(3), "Lenovo");
                f.Set(ref f.GpuTemp, SensorTemp(4), true);
                f.Set(ref f.CpuFan, FanSpeed(0), false);
                f.Set(ref f.GpuFan, FanSpeed(1), false);
            }
        }

        public override void Dispose()
        {
            if (other != null) other.Dispose();
            if (fan != null) fan.Dispose();
        }
    }

    /// <summary>HP Omen / Victus: hpqBIntM BIOS calls, command 0x20008 (as OmenMon).</summary>
    sealed class HpProvider : SensorProvider
    {
        static readonly byte[] Sign = { 0x53, 0x45, 0x43, 0x55 };
        readonly ManagementObject methods;
        readonly ManagementClass dataClass;
        public override string Name { get { return "HP firmware"; } }

        HpProvider(ManagementObject m, ManagementClass d) { methods = m; dataClass = d; }

        public static HpProvider TryCreate()
        {
            ManagementObject m = Io.FirstInstance("hpqBIntM");
            if (m == null) return null;
            var p = new HpProvider(m, new ManagementClass(@"root\wmi", "hpqBDataIn", null));
            byte[] probe = p.Send(0x2D, 128);
            if (probe == null) { p.Dispose(); return null; }
            return p;
        }

        byte[] Send(uint commandType, int outSize)
        {
            byte[] inData = commandType == 0x23 ? new byte[] { 1, 0, 0, 0 } : new byte[4];
            using (ManagementObject input = dataClass.CreateInstance())
            {
                input["Sign"] = Sign;
                input["Command"] = 0x20008u;
                input["CommandType"] = commandType;
                input["Size"] = (uint)inData.Length;
                input["hpqBData"] = inData;
                string method = "hpqBIOSInt" + outSize;
                using (ManagementBaseObject inP = methods.GetMethodParameters(method))
                {
                    inP["InData"] = input;
                    using (ManagementBaseObject outP = methods.InvokeMethod(method, inP, null))
                    {
                        var data = outP["OutData"] as ManagementBaseObject;
                        if (data == null || Convert.ToInt32(data["rwReturnCode"]) != 0) return null;
                        return data["Data"] as byte[];
                    }
                }
            }
        }

        public override void Read(SensorFrame f)
        {
            byte[] t = Send(0x23, 4);
            if (t != null && t.Length > 0) f.SetCpuTemp(t[0], "HP");
            byte[] fans = Send(0x2D, 128);
            if (fans != null && fans.Length > 1)
            {
                f.Set(ref f.CpuFan, fans[0] * 100.0, false);   // fan levels are in units of 100 rpm
                f.Set(ref f.GpuFan, fans[1] * 100.0, false);
            }
        }

        public override void Dispose() { methods.Dispose(); dataClass.Dispose(); }
    }

    /// <summary>Dell Alienware / G-series: AWCCWmiMethodFunction (as alienfx-tools).</summary>
    sealed class DellAwccProvider : SensorProvider
    {
        static readonly string[] Commands = { "Thermal_Information", "Thermal_Control", "GameShiftStatus", "GetFanSensors", "GetThermalInfo2", "SetThermalControl2", "TccControl", "MemoryOCControl" };
        static readonly byte[][] FunctionId = { new byte[] { 0, 0, 0, 0, 0 }, new byte[] { 5, 5, 5, 5, 5 } };
        static readonly byte[] DevControl = { 3, 5, 6, 0xC, 4 };   // getPowerID, getFanRPM, getFanPercent, getFanBoost, getTemp
        const int GetPowerId = 0, GetFanRpm = 1, GetTemp = 4;

        readonly ManagementObject inst;
        readonly int sysType;
        readonly List<int> fans = new List<int>(), temps = new List<int>();
        public override string Name { get { return "Dell AWCC firmware"; } }

        DellAwccProvider(ManagementObject o, int type) { inst = o; sysType = type; }

        public static DellAwccProvider TryCreate()
        {
            ManagementObject o = Io.FirstInstance("AWCCWmiMethodFunction");
            if (o == null) return null;
            int type = -1;
            using (var cls = new ManagementClass(@"root\wmi", "AWCCWmiMethodFunction", null))
            {
                for (int t = 0; t < 2 && type < 0; t++)
                    foreach (MethodData m in cls.Methods)
                        if (m.Name == Commands[FunctionId[t][GetPowerId]]) { type = t; break; }
            }
            if (type < 0) { o.Dispose(); return null; }
            var p = new DellAwccProvider(o, type);
            for (int i = 0; i < 64; i++)
            {
                int id = p.Call(GetPowerId, i, 0);
                if (id <= 0) break;
                if (id > 0x100 && id < 0x110) p.temps.Add(id & 0xFF);
                else if (id <= 0x8F) p.fans.Add(id & 0xFF);
            }
            if (p.fans.Count == 0 && p.temps.Count == 0) { p.Dispose(); return null; }
            return p;
        }

        int Call(int com, int arg1, int arg2)
        {
            string method = Commands[FunctionId[sysType][com]];
            uint args = (uint)(DevControl[com] | (arg1 << 8) | (arg2 << 16));
            return Io.Invoke(inst, method, "arg2", args, "argr");
        }

        public override void Read(SensorFrame f)
        {
            if (temps.Count > 0) { int t = Call(GetTemp, temps[0], 0); if (t <= 150) f.SetCpuTemp(t, "Dell"); }
            if (temps.Count > 1) { int t = Call(GetTemp, temps[1], 0); if (t <= 150) f.Set(ref f.GpuTemp, t, true); }
            if (temps.Count > 2) { int t = Call(GetTemp, temps[2], 0); if (t <= 150) f.Set(ref f.SysTemp, t, true); }
            if (fans.Count > 0) f.Set(ref f.CpuFan, Call(GetFanRpm, fans[0], 0), false);
            if (fans.Count > 1) f.Set(ref f.GpuFan, Call(GetFanRpm, fans[1], 0), false);
            if (fans.Count > 2) f.Set(ref f.MidFan, Call(GetFanRpm, fans[2], 0), false);
        }

        public override void Dispose() { inst.Dispose(); }
    }

    // ===================================================================== PawnIO (optional signed driver)

    /// <summary>One loaded PawnIO module (the official signed .bin files are embedded in the exe).</summary>
    sealed class PawnModule : IDisposable
    {
        const uint LoadBinary = 0xA1B22084, Execute = 0xA1B22104;
        readonly SafeFileHandle h;

        PawnModule(SafeFileHandle handle) { h = handle; }

        public static bool Installed
        {
            get
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (RegistryKey b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                        using (RegistryKey k = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
                            if (k != null) return true;
                    }
                    catch { }
                }
                return false;
            }
        }

        public static PawnModule Load(string module)
        {
            byte[] bin;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("pawnio." + module))
            {
                if (s == null) return null;
                bin = new byte[s.Length];
                int read = 0;
                while (read < bin.Length) { int n = s.Read(bin, read, bin.Length - read); if (n <= 0) break; read += n; }
            }
            SafeFileHandle h = Io.CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 3, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return null; }
            uint ret;
            if (!Io.DeviceIoControl(h, LoadBinary, bin, bin.Length, null, 0, out ret, IntPtr.Zero)) { h.Dispose(); return null; }
            return new PawnModule(h);
        }

        public bool Exec(string fn, long[] input, long[] output)
        {
            byte[] inb = new byte[32 + input.Length * 8];
            Encoding.ASCII.GetBytes(fn, 0, Math.Min(31, fn.Length), inb, 0);
            Buffer.BlockCopy(input, 0, inb, 32, input.Length * 8);
            byte[] outb = new byte[output.Length * 8];
            uint read;
            if (!Io.DeviceIoControl(h, Execute, inb, inb.Length, outb, outb.Length, out read, IntPtr.Zero)) return false;
            Array.Clear(output, 0, output.Length);
            Buffer.BlockCopy(outb, 0, output, 0, (int)Math.Min(read, (uint)outb.Length));
            return true;
        }

        public void Dispose() { h.Dispose(); }
    }

    /// <summary>True CPU temperatures: Intel package + hottest core (MSR), AMD Zen Tctl (SMN).</summary>
    sealed class PawnCpuProvider : SensorProvider
    {
        readonly PawnModule mod;
        readonly bool intel;
        readonly double tjMax;
        readonly List<KeyValuePair<int, int>> cores;   // (group, number) of the first thread of each core
        readonly long[] one = new long[1], res = new long[1];
        public override string Name { get { return intel ? "PawnIO (Intel MSR)" : "PawnIO (AMD SMN)"; } }

        PawnCpuProvider(PawnModule m, bool isIntel, double tj, List<KeyValuePair<int, int>> coreList)
        {
            mod = m; intel = isIntel; tjMax = tj; cores = coreList;
        }

        public static PawnCpuProvider TryCreate(List<KeyValuePair<int, int>> coreList)
        {
            string vendor = "", ident = "";
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    vendor = Convert.ToString(k.GetValue("VendorIdentifier"));
                    ident = Convert.ToString(k.GetValue("Identifier"));
                }
            }
            catch { return null; }

            if (vendor == "GenuineIntel")
            {
                PawnModule m = PawnModule.Load("IntelMSR.bin");
                if (m == null) return null;
                var p = new PawnCpuProvider(m, true, 100, coreList);
                ulong target;
                double tj = p.ReadMsr(0x1A2, out target) ? (target >> 16) & 0xFF : 0;
                if (tj < 50 || tj > 130) tj = 100;
                return new PawnCpuProvider(m, true, tj, coreList);
            }
            if (vendor == "AuthenticAMD" && Family(ident) >= 0x17)
            {
                PawnModule m = PawnModule.Load("AMDFamily17.bin");
                return m == null ? null : new PawnCpuProvider(m, false, 0, coreList);
            }
            return null;
        }

        static int Family(string ident)
        {
            int i = ident.IndexOf("Family ", StringComparison.Ordinal);
            if (i < 0) return 0;
            int end = i + 7;
            while (end < ident.Length && char.IsDigit(ident[end])) end++;
            int f;
            return int.TryParse(ident.Substring(i + 7, end - i - 7), out f) ? f : 0;
        }

        bool ReadMsr(uint index, out ulong value)
        {
            one[0] = index;
            if (!mod.Exec("ioctl_read_msr", one, res)) { value = 0; return false; }
            value = (ulong)res[0];
            return true;
        }

        double ThermFromMsr(uint index)
        {
            ulong v;
            if (!ReadMsr(index, out v) || ((v >> 31) & 1) == 0) return double.NaN;
            return tjMax - ((v >> 16) & 0x7F);
        }

        public override void Read(SensorFrame f)
        {
            if (!intel)
            {
                one[0] = 0x00059800;
                if (!mod.Exec("ioctl_read_smn", one, res)) return;
                uint v = (uint)res[0];
                bool offset = (v & 0x80000) != 0 || (v & 0x30000) == 0x30000;
                double t = ((v >> 21) * 125) * 0.001;
                if (offset) t -= 49;
                f.SetCpuTemp(t, "CPU Tctl");
                return;
            }

            f.SetCpuTemp(ThermFromMsr(0x1B1), "CPU package");

            // Per-core sensors are only readable from the core itself: hop the thread across cores.
            double max = double.NaN;
            IntPtr me = Io.GetCurrentThread();
            Io.GROUP_AFFINITY prev;
            bool haveOriginal = false;
            Io.GROUP_AFFINITY original = new Io.GROUP_AFFINITY();
            foreach (KeyValuePair<int, int> c in cores)
            {
                var ga = new Io.GROUP_AFFINITY();
                ga.Mask = new UIntPtr(1UL << c.Value);
                ga.Group = (ushort)c.Key;
                if (!Io.SetThreadGroupAffinity(me, ref ga, out prev)) continue;
                if (!haveOriginal) { original = prev; haveOriginal = true; }
                double t = ThermFromMsr(0x19C);
                if (SensorFrame.Valid(t) && (double.IsNaN(max) || t > max)) max = t;
            }
            if (haveOriginal) Io.SetThreadGroupAffinity(me, ref original, out prev);
            f.Set(ref f.CoreMaxTemp, max, true);
        }

        public override void Dispose() { mod.Dispose(); }
    }

    /// <summary>Ordinary Dell laptops (Inspiron, Latitude, Vostro, XPS): Dell SMM fan/temperature calls (as Linux dell-smm-hwmon).</summary>
    sealed class PawnDellSmmProvider : SensorProvider
    {
        readonly PawnModule mod;
        readonly long[] inr = new long[6], outr = new long[6];
        readonly List<int> fans = new List<int>();
        int cpuSensor = -1, gpuSensor = -1, ambientSensor = -1;
        public override string Name { get { return "PawnIO (Dell SMM)"; } }
        public override int PollEvery { get { return 2; } }   // SMM calls stall the CPU briefly: poll every ~4 s

        PawnDellSmmProvider(PawnModule m) { mod = m; }

        public static PawnDellSmmProvider TryCreate()
        {
            PawnModule m = PawnModule.Load("DellSMM.bin");   // the module itself refuses to load on non-Dell firmware
            if (m == null) return null;
            var p = new PawnDellSmmProvider(m);
            for (int fan = 0; fan < 3; fan++) if (p.FanRaw(fan) >= 0) p.fans.Add(fan);
            for (int s = 0; s < 6; s++)
            {
                int type = p.Smm(0x11A3, s);
                if (type < 0) continue;
                type &= 0xFF;
                if (type == 0 && p.cpuSensor < 0) p.cpuSensor = s;
                else if (type == 1 && p.gpuSensor < 0) p.gpuSensor = s;
                else if (type == 4 && p.ambientSensor < 0) p.ambientSensor = s;
            }
            if (p.fans.Count == 0 && p.cpuSensor < 0) { p.Dispose(); return null; }
            return p;
        }

        int Smm(uint eax, int ebx)
        {
            Array.Clear(inr, 0, inr.Length);
            inr[0] = eax;    // registers: eax ecx edx ebx esi edi
            inr[3] = ebx;
            if (!mod.Exec("ioctl_query_smm", inr, outr)) return -1;
            uint r = (uint)(outr[0] & 0xFFFFFFFF);
            return r == 0xFFFFFFFF ? -1 : (int)r;
        }

        int FanRaw(int fan)
        {
            int r = Smm(0x02A3, fan);
            if (r < 0) return -1;
            r &= 0xFFFF;
            return r == 0xFFFF ? -1 : r;
        }

        double Temp(int sensor)
        {
            if (sensor < 0) return double.NaN;
            int r = Smm(0x10A3, sensor);
            if (r < 0) return double.NaN;
            r &= 0xFF;
            return r >= 127 ? double.NaN : r;
        }

        public override void Read(SensorFrame f)
        {
            f.SetCpuTemp(Temp(cpuSensor), "Dell");
            f.Set(ref f.GpuTemp, Temp(gpuSensor), true);
            f.Set(ref f.SysTemp, Temp(ambientSensor), true);
            for (int i = 0; i < fans.Count && i < 2; i++)
            {
                int raw = FanRaw(fans[i]);
                if (raw < 0) continue;
                double rpm = raw > 0 && raw < 300 ? raw * 30 : raw;   // some older models report in units of 30 rpm
                if (i == 0) f.Set(ref f.CpuFan, rpm, false);
                else f.Set(ref f.GpuFan, rpm, false);
            }
        }

        public override void Dispose() { mod.Dispose(); }
    }
}
