using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CoreGlance
{
    /// <summary>Fixed-size ring buffer of recent samples (oldest first).</summary>
    sealed class History
    {
        readonly float[] data;
        int start, count;
        public History(int capacity) { data = new float[capacity]; }
        public int Capacity { get { return data.Length; } }
        public int Count { get { return count; } }
        public float this[int i] { get { return data[(start + i) % data.Length]; } }
        public void Add(float v)
        {
            if (count < data.Length) { data[(start + count) % data.Length] = v; count++; }
            else { data[start] = v; start = (start + 1) % data.Length; }
        }
    }

    sealed class LpInfo
    {
        public int Group, Number, Core, Thread, ThreadsInCore, TypeIndex;
        public bool Perf;
    }

    sealed class Gpu
    {
        public string Name, Luid;
        public uint VendorId;
        public double DedTotal, SharedTotal, DedUsed, SharedUsed;
        public float Util;
        public string Engine = "";
        public readonly History Hist = new History(60);
        public List<GpuUse> Procs = new List<GpuUse>();   // busiest apps on this GPU
    }

    /// <summary>One app's use of one GPU (all its processes added up).</summary>
    sealed class GpuUse
    {
        public string Name, Engine = "";
        public Gpu Gpu;
        public int Count;
        public double Util, Mem;
    }

    sealed class ProcInfo
    {
        public string Name;
        public int Count;
        public double Cpu, Mem;
    }

    /// <summary>Thin wrapper over one PDH query.</summary>
    sealed class Pdh : IDisposable
    {
        public delegate void ItemHandler(string name, double value);
        const uint Fmt = Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100;
        const int ItemSize = 24; // sizeof(PDH_FMT_COUNTERVALUE_ITEM_W) on both x86 and x64
        IntPtr query, buf;
        uint bufSize;

        public Pdh()
        {
            if (Native.PdhOpenQuery(null, IntPtr.Zero, out query) != 0) query = IntPtr.Zero;
        }

        public IntPtr Add(string path)
        {
            if (query == IntPtr.Zero) return IntPtr.Zero;
            IntPtr c;
            return Native.PdhAddEnglishCounter(query, path, IntPtr.Zero, out c) == 0 ? c : IntPtr.Zero;
        }

        public void Collect()
        {
            if (query != IntPtr.Zero) Native.PdhCollectQueryData(query);
        }

        public double Value(IntPtr counter, double fallback)
        {
            if (counter == IntPtr.Zero) return fallback;
            Native.PDH_FMT_COUNTERVALUE v;
            if (Native.PdhGetFormattedCounterValue(counter, Fmt, IntPtr.Zero, out v) != 0) return fallback;
            if (v.CStatus > 1 || double.IsNaN(v.doubleValue)) return fallback;
            return v.doubleValue;
        }

        public int Array(IntPtr counter, ItemHandler handler)
        {
            if (counter == IntPtr.Zero) return 0;
            uint size = bufSize, count;
            uint st = Native.PdhGetFormattedCounterArray(counter, Fmt, ref size, out count, buf);
            for (int attempt = 0; st == Native.PDH_MORE_DATA && attempt < 3; attempt++)
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                bufSize = size + 8192;
                buf = Marshal.AllocHGlobal((int)bufSize);
                size = bufSize;
                st = Native.PdhGetFormattedCounterArray(counter, Fmt, ref size, out count, buf);
            }
            if (st != 0) return 0;
            for (int i = 0; i < count; i++)
            {
                IntPtr item = IntPtr.Add(buf, i * ItemSize);
                uint status = (uint)Marshal.ReadInt32(item, 8);
                if (status > 1) continue;
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0));
                if (name != null && !double.IsNaN(value)) handler(name, value);
            }
            return (int)count;
        }

        public void Dispose()
        {
            if (query != IntPtr.Zero) { Native.PdhCloseQuery(query); query = IntPtr.Zero; }
            if (buf != IntPtr.Zero) { Marshal.FreeHGlobal(buf); buf = IntPtr.Zero; bufSize = 0; }
        }
    }

    /// <summary>Samples CPU (per logical processor), memory, GPU, disk, network, battery and processes.</summary>
    sealed class SysMonitor : IDisposable
    {
        public readonly int Logical;
        public readonly int PhysicalCores, PCores, ECores;
        public readonly bool Hybrid;
        public readonly LpInfo[] Lps;
        public readonly float[] CoreLoad;
        public readonly string CpuName;
        public readonly float BaseMhz;

        public float Cpu, CpuMhz, CpuAvg1m = -1;
        public int ActiveThreads;
        public readonly History CpuHist = new History(60), MemHist = new History(60);

        public double MemTotal, MemUsed, MemAvail, CommitUsed, CommitLimit;
        public float MemPct;

        public readonly List<Gpu> Gpus = new List<Gpu>();
        readonly Dictionary<string, Gpu> gpuByLuid = new Dictionary<string, Gpu>(StringComparer.OrdinalIgnoreCase);
        readonly bool gpusFromDxgi;

        public float DiskActive;
        public double DiskRead, DiskWrite, NetRx, NetTx;
        public int Processes, Threads;

        // Thermals and power from Windows counters (no admin): ACPI thermal zone, throttling, Intel/AMD RAPL energy meters.
        public double AcpiTemp = double.NaN, PassiveLimit = 100, PerfLimit = 100;
        public double CpuPkgW = double.NaN, CpuCoresW = double.NaN, IGpuW = double.NaN, DramW = double.NaN;
        public bool NvidiaActive;
        bool tzHighPrecision;

        public bool HasBattery, OnAC, Charging;
        public int BatteryPct = -1;

        public List<ProcInfo> TopCpu = new List<ProcInfo>(), TopMem = new List<ProcInfo>();
        public bool ProcReady;
        int procCollects;

        /// <summary>Busiest apps across all GPUs.</summary>
        public List<GpuUse> TopGpu = new List<GpuUse>();
        readonly List<KeyValuePair<string, double>> engInstances = new List<KeyValuePair<string, double>>();
        Dictionary<int, string> pidNames = new Dictionary<int, string>();
        double pidNamesT = -1000;
        IntPtr pGpuLocal, pGpuNonLocal;

        readonly Dictionary<int, int> lpIndex = new Dictionary<int, int>();
        readonly Dictionary<string, double> engSum = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> engType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastT;

        Pdh q, pq;
        IntPtr cCoresUtil, cTotalUtil;
        public bool UseUtility;   // false: busy time (default), true: Task Manager-style utility
        public bool HasUtility { get { return cCoresUtil != IntPtr.Zero; } }
        IntPtr cCores, cTotal, cPerf, cIdle, cRead, cWrite, cRx, cTx, cProcs, cThreads, cEng, cDed, cShared, pCpu, pMem;
        IntPtr cTz, cPassive, cPerfLimit, cRapl;

        public SysMonitor()
        {
            int phys, pc, ec; bool hybrid;
            Lps = ReadTopology(out phys, out pc, out ec, out hybrid);
            PhysicalCores = phys; PCores = pc; ECores = ec; Hybrid = hybrid;
            Logical = Lps.Length;
            CoreLoad = new float[Logical];
            for (int i = 0; i < Logical; i++) lpIndex[Lps[i].Group * 64 + Lps[i].Number] = i;
            CpuName = ShortCpuName(ReadCpuName());
            BaseMhz = ReadBaseMhz(Logical);

            foreach (Gpu g in EnumDxgiAdapters())
            {
                if (gpuByLuid.ContainsKey(g.Luid)) continue;
                gpuByLuid[g.Luid] = g;
                Gpus.Add(g);
            }
            gpusFromDxgi = Gpus.Count > 0;
            OpenQueries();
        }

        void OpenQueries()
        {
            q = new Pdh();
            // Busy time (what NZXT CAM / Resource Monitor show) and Utility (Task Manager: busy time x clock / base clock).
            cCores = q.Add(@"\Processor Information(*)\% Processor Time");
            if (cCores == IntPtr.Zero) cCores = q.Add(@"\Processor(*)\% Processor Time");
            cTotal = q.Add(@"\Processor Information(_Total)\% Processor Time");
            cCoresUtil = q.Add(@"\Processor Information(*)\% Processor Utility");
            cTotalUtil = q.Add(@"\Processor Information(_Total)\% Processor Utility");
            cPerf = q.Add(@"\Processor Information(_Total)\% Processor Performance");
            cIdle = q.Add(@"\PhysicalDisk(_Total)\% Idle Time");
            cRead = q.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
            cWrite = q.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
            cRx = q.Add(@"\Network Interface(*)\Bytes Received/sec");
            cTx = q.Add(@"\Network Interface(*)\Bytes Sent/sec");
            cProcs = q.Add(@"\System\Processes");
            cThreads = q.Add(@"\System\Threads");
            cEng = q.Add(@"\GPU Engine(*)\Utilization Percentage");
            cDed = q.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
            cShared = q.Add(@"\GPU Adapter Memory(*)\Shared Usage");
            cTz = q.Add(@"\Thermal Zone Information(*)\High Precision Temperature");
            tzHighPrecision = cTz != IntPtr.Zero;
            if (!tzHighPrecision) cTz = q.Add(@"\Thermal Zone Information(*)\Temperature");
            cPassive = q.Add(@"\Thermal Zone Information(*)\% Passive Limit");
            cPerfLimit = q.Add(@"\Processor Information(_Total)\% Performance Limit");
            cRapl = q.Add(@"\Energy Meter(*)\Power");
            q.Collect();

            pq = new Pdh();
            pCpu = pq.Add(@"\Process(*)\% Processor Time");
            pMem = pq.Add(@"\Process(*)\Working Set - Private");
            // Resident memory. "Shared Usage" over-counts per process (gigabytes for dwm), these add up to the adapter totals.
            pGpuLocal = pq.Add(@"\GPU Process Memory(*)\Local Usage");
            pGpuNonLocal = pq.Add(@"\GPU Process Memory(*)\Non Local Usage");
            procCollects = 0;
            ProcReady = false;
        }

        /// <summary>Rebuild counters (used after resume from sleep).</summary>
        public void Reset()
        {
            q.Dispose(); pq.Dispose();
            OpenQueries();
        }

        public void Sample(bool wantProcs)
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = lastT > 0 ? now - lastT : 1;
            lastT = now;
            q.Collect();

            // ---- CPU ----
            bool util = UseUtility && cCoresUtil != IntPtr.Zero;
            q.Array(util ? cCoresUtil : cCores, delegate(string n, double v)
            {
                if (n.IndexOf("_Total", StringComparison.OrdinalIgnoreCase) >= 0) return;
                int g = 0, num;
                int comma = n.IndexOf(',');
                if (comma >= 0)
                {
                    if (!int.TryParse(n.Substring(0, comma), out g)) return;
                    if (!int.TryParse(n.Substring(comma + 1), out num)) return;
                }
                else if (!int.TryParse(n, out num)) return;
                int idx;
                if (lpIndex.TryGetValue(g * 64 + num, out idx)) CoreLoad[idx] = Clamp((float)v);
            });
            double total = q.Value(util && cTotalUtil != IntPtr.Zero ? cTotalUtil : cTotal, -1);
            if (total < 0)
            {
                double s = 0; for (int i = 0; i < Logical; i++) s += CoreLoad[i];
                total = Logical > 0 ? s / Logical : 0;
            }
            Cpu = Clamp((float)total);
            int active = 0;
            for (int i = 0; i < Logical; i++) if (CoreLoad[i] >= 5f) active++;
            ActiveThreads = active;
            if (CpuAvg1m < 0) CpuAvg1m = Cpu;
            else CpuAvg1m += (float)(1 - Math.Exp(-dt / 60.0)) * (Cpu - CpuAvg1m);
            double perf = q.Value(cPerf, -1);
            CpuMhz = perf > 0 && BaseMhz > 0 ? (float)(perf * BaseMhz / 100.0) : 0;
            CpuHist.Add(Cpu);

            // ---- Memory ----
            var ms = new Native.MEMORYSTATUSEX();
            ms.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            if (Native.GlobalMemoryStatusEx(ref ms))
            {
                MemTotal = ms.ullTotalPhys;
                MemAvail = ms.ullAvailPhys;
                MemUsed = MemTotal - MemAvail;
                MemPct = MemTotal > 0 ? Clamp((float)(MemUsed / MemTotal * 100.0)) : 0;
                CommitLimit = ms.ullTotalPageFile;
                CommitUsed = ms.ullTotalPageFile - ms.ullAvailPageFile;
            }
            MemHist.Add(MemPct);

            // ---- Disk / network / system ----
            double idle = q.Value(cIdle, -1);
            DiskActive = idle < 0 ? 0 : Clamp((float)(100 - idle));
            DiskRead = Math.Max(0, q.Value(cRead, 0));
            DiskWrite = Math.Max(0, q.Value(cWrite, 0));
            double rx = 0, tx = 0;
            q.Array(cRx, delegate(string n, double v) { rx += v; });
            q.Array(cTx, delegate(string n, double v) { tx += v; });
            NetRx = rx; NetTx = tx;
            Processes = (int)q.Value(cProcs, 0);
            Threads = (int)q.Value(cThreads, 0);

            // ---- GPU (Task Manager method: sum per engine across processes, busiest engine wins) ----
            foreach (Gpu g in Gpus) { g.Util = 0; g.Engine = ""; }
            engSum.Clear(); engType.Clear(); engInstances.Clear();
            q.Array(cEng, delegate(string n, double v)
            {
                int li = n.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                int et = n.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
                if (li < 0 || et < li) return;
                if (wantProcs && v >= 0.05) engInstances.Add(new KeyValuePair<string, double>(n, v));
                string key = n.Substring(li, et - li);
                double cur; engSum.TryGetValue(key, out cur);
                engSum[key] = cur + v;
                if (!engType.ContainsKey(key)) engType[key] = n.Substring(et + 9);
            });
            q.Array(cDed, delegate(string n, double v) { Gpu g = GpuFor(n, true); if (g != null) g.DedUsed = v; });
            q.Array(cShared, delegate(string n, double v) { Gpu g = GpuFor(n, false); if (g != null) g.SharedUsed = v; });
            foreach (KeyValuePair<string, double> kv in engSum)
            {
                Gpu g = GpuFor(kv.Key, false);
                if (g == null) continue;
                float u = Clamp((float)kv.Value);
                if (u > g.Util) { g.Util = u; g.Engine = PrettyEngine(engType[kv.Key]); }
            }
            bool nvActive = false;
            foreach (Gpu g in Gpus)
            {
                g.Hist.Add(g.Util);
                if (g.VendorId == 0x10DE && g.Util >= 0.5f) nvActive = true;
            }
            NvidiaActive = nvActive;

            // ---- Thermals / power ----
            double tz = double.NaN, passive = 100;
            bool hp = tzHighPrecision;
            q.Array(cTz, delegate(string n, double v)
            {
                double c = hp ? v / 10.0 - 273.15 : v - 273.15;
                if (c > -40 && c < 150 && (double.IsNaN(tz) || c > tz)) tz = c;
            });
            q.Array(cPassive, delegate(string n, double v) { if (v > 0 && v < passive) passive = v; });
            AcpiTemp = tz;
            PassiveLimit = passive;
            PerfLimit = q.Value(cPerfLimit, 100);
            double pkg = double.NaN, pp0 = double.NaN, pp1 = double.NaN, dram = double.NaN;
            q.Array(cRapl, delegate(string n, double v)
            {
                string l = n.ToLowerInvariant();
                double w = v / 1000.0;
                if (l.EndsWith("_pkg")) pkg = w;
                else if (l.EndsWith("_pp0")) pp0 = w;
                else if (l.EndsWith("_pp1")) pp1 = w;
                else if (l.EndsWith("_dram")) dram = w;
            });
            CpuPkgW = pkg; CpuCoresW = pp0; IGpuW = pp1; DramW = dram;

            // ---- Battery ----
            Native.SYSTEM_POWER_STATUS ps;
            if (Native.GetSystemPowerStatus(out ps))
            {
                HasBattery = ps.BatteryFlag != 255 && (ps.BatteryFlag & 128) == 0;
                BatteryPct = ps.BatteryLifePercent == 255 ? -1 : ps.BatteryLifePercent;
                OnAC = ps.ACLineStatus == 1;
                Charging = (ps.BatteryFlag & 8) != 0;
            }

            if (wantProcs) { SampleProcesses(); SampleGpuProcesses(now); }
        }

        void SampleProcesses()
        {
            pq.Collect();
            procCollects++;
            var map = new Dictionary<string, ProcInfo>(StringComparer.OrdinalIgnoreCase);
            pq.Array(pCpu, delegate(string n, double v)
            {
                ProcInfo p = ProcFor(map, n);
                if (p == null) return;
                p.Count++;
                p.Cpu += v;
            });
            pq.Array(pMem, delegate(string n, double v)
            {
                ProcInfo p = ProcFor(map, n);
                if (p != null) p.Mem += v;
            });
            var list = new List<ProcInfo>(map.Values);
            foreach (ProcInfo p in list) p.Cpu = Math.Min(100, p.Cpu / Math.Max(1, Logical));
            list.Sort(delegate(ProcInfo a, ProcInfo b) { int c = b.Cpu.CompareTo(a.Cpu); return c != 0 ? c : b.Mem.CompareTo(a.Mem); });
            TopCpu = list.GetRange(0, Math.Min(6, list.Count));
            list.Sort(delegate(ProcInfo a, ProcInfo b) { return b.Mem.CompareTo(a.Mem); });
            TopMem = list.GetRange(0, Math.Min(6, list.Count));
            ProcReady = procCollects >= 2;
        }

        static int PidOf(string instance)
        {
            if (!instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return -1;
            int end = instance.IndexOf('_', 4), pid;
            return end > 4 && int.TryParse(instance.Substring(4, end - 4), out pid) ? pid : -1;
        }

        string NameOf(int pid, double now, ref bool refreshed)
        {
            string name;
            if (pidNames.TryGetValue(pid, out name)) return name;
            if (!refreshed)
            {
                refreshed = true;
                RefreshPidNames(now);
                if (pidNames.TryGetValue(pid, out name)) return name;
            }
            return null;   // the process has already exited
        }

        void RefreshPidNames(double now)
        {
            var map = new Dictionary<int, string>();
            foreach (Process p in Process.GetProcesses())
                using (p) { try { map[p.Id] = p.ProcessName; } catch { } }
            pidNames = map;
            pidNamesT = now;
        }

        sealed class GpuPid { public int Pid; public Gpu Gpu; public double Util, Mem; public string Engine = ""; }

        /// <summary>Per-app GPU use, the way Task Manager counts it: a process's busiest engine on each GPU, plus its GPU memory.</summary>
        void SampleGpuProcesses(double now)
        {
            var byPid = new Dictionary<string, GpuPid>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, double> kv in engInstances)
            {
                GpuPid u = GpuPidFor(byPid, kv.Key);
                if (u == null || kv.Value <= u.Util) continue;
                u.Util = kv.Value;
                int et = kv.Key.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
                u.Engine = et > 0 ? PrettyEngine(kv.Key.Substring(et + 9)) : "";
            }
            Pdh.ItemHandler addMem = delegate(string n, double v)
            {
                if (v <= 0) return;
                GpuPid u = GpuPidFor(byPid, n);
                if (u != null) u.Mem += v;
            };
            pq.Array(pGpuLocal, addMem);
            pq.Array(pGpuNonLocal, addMem);

            bool refreshed = false;
            if (now - pidNamesT > 60) { refreshed = true; RefreshPidNames(now); }   // process ids get reused
            var byName = new Dictionary<string, GpuUse>(StringComparer.OrdinalIgnoreCase);
            foreach (GpuPid u in byPid.Values)
            {
                string name = NameOf(u.Pid, now, ref refreshed);
                if (name == null) continue;
                string key = u.Gpu.Luid + "|" + name;
                GpuUse g;
                if (!byName.TryGetValue(key, out g)) { g = new GpuUse(); g.Name = name; g.Gpu = u.Gpu; byName[key] = g; }
                if (g.Count == 0 || u.Util > 0 && g.Engine.Length == 0) g.Engine = u.Engine;
                g.Count++;
                g.Util = Math.Min(100, g.Util + u.Util);
                g.Mem += u.Mem;
            }
            var all = new List<GpuUse>(byName.Values);
            all.Sort(delegate(GpuUse a, GpuUse b)
            {
                // Rounded so idle apps order by memory instead of jittering on tiny percentages.
                int c = Math.Round(b.Util, 1).CompareTo(Math.Round(a.Util, 1));
                return c != 0 ? c : b.Mem.CompareTo(a.Mem);
            });
            foreach (Gpu g in Gpus)
            {
                var list = new List<GpuUse>();
                foreach (GpuUse u in all) if (u.Gpu == g && list.Count < 10) list.Add(u);
                g.Procs = list;
            }
            TopGpu = all.GetRange(0, Math.Min(6, all.Count));
        }

        GpuPid GpuPidFor(Dictionary<string, GpuPid> map, string instance)
        {
            int pid = PidOf(instance);
            if (pid < 0) return null;
            Gpu g = GpuFor(instance, false);
            if (g == null) return null;
            string key = pid + "|" + g.Luid;
            GpuPid u;
            if (!map.TryGetValue(key, out u)) { u = new GpuPid(); u.Pid = pid; u.Gpu = g; map[key] = u; }
            return u;
        }

        static ProcInfo ProcFor(Dictionary<string, ProcInfo> map, string n)
        {
            int hash = n.LastIndexOf('#');
            if (hash > 0) n = n.Substring(0, hash);
            if (n == "_Total" || n == "Idle") return null;
            ProcInfo p;
            if (!map.TryGetValue(n, out p)) { p = new ProcInfo(); p.Name = n; map[n] = p; }
            return p;
        }

        Gpu GpuFor(string instance, bool allowCreate)
        {
            int li = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            if (li < 0 || instance.Length < li + 26) return null;
            string key = instance.Substring(li, 26);
            Gpu g;
            if (gpuByLuid.TryGetValue(key, out g)) return g;
            if (gpusFromDxgi || !allowCreate) return null;
            g = new Gpu();
            g.Name = "GPU " + Gpus.Count;
            g.Luid = key;
            gpuByLuid[key] = g;
            Gpus.Add(g);
            return g;
        }

        public void Dispose()
        {
            if (q != null) q.Dispose();
            if (pq != null) pq.Dispose();
        }

        static float Clamp(float v)
        {
            if (float.IsNaN(v) || v < 0) return 0;
            return v > 100 ? 100 : v;
        }

        // ---------------- one-time hardware discovery ----------------

        static LpInfo[] ReadTopology(out int phys, out int pCores, out int eCores, out bool hybrid)
        {
            var cores = new List<KeyValuePair<byte, List<int>>>();
            uint len = 0;
            Native.GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);
            if (len > 0)
            {
                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (Native.GetLogicalProcessorInformationEx(0, buf, ref len))
                    {
                        int off = 0;
                        int gaSize = IntPtr.Size == 8 ? 16 : 12;
                        while (off < len)
                        {
                            IntPtr p = IntPtr.Add(buf, off);
                            int rel = Marshal.ReadInt32(p, 0);
                            int size = Marshal.ReadInt32(p, 4);
                            if (size <= 0) break;
                            if (rel == 0)
                            {
                                byte eff = Marshal.ReadByte(p, 9);
                                int groupCount = Marshal.ReadInt16(p, 30);
                                var keys = new List<int>();
                                for (int k = 0; k < groupCount; k++)
                                {
                                    IntPtr ga = IntPtr.Add(p, 32 + k * gaSize);
                                    ulong mask = IntPtr.Size == 8 ? (ulong)Marshal.ReadInt64(ga, 0) : (uint)Marshal.ReadInt32(ga, 0);
                                    int grp = Marshal.ReadInt16(ga, IntPtr.Size);
                                    for (int b = 0; b < 64; b++) if (((mask >> b) & 1UL) != 0) keys.Add(grp * 64 + b);
                                }
                                if (keys.Count > 0) cores.Add(new KeyValuePair<byte, List<int>>(eff, keys));
                            }
                            off += size;
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }

            if (cores.Count == 0)
            {
                for (int i = 0; i < Environment.ProcessorCount; i++)
                    cores.Add(new KeyValuePair<byte, List<int>>(0, new List<int>(new[] { i })));
            }

            byte maxC = 0, minC = 255;
            foreach (var c in cores) { if (c.Key > maxC) maxC = c.Key; if (c.Key < minC) minC = c.Key; }
            hybrid = maxC != minC;
            cores.Sort(delegate(KeyValuePair<byte, List<int>> a, KeyValuePair<byte, List<int>> b) { return a.Value[0].CompareTo(b.Value[0]); });

            var lps = new List<LpInfo>();
            int pIdx = 0, eIdx = 0;
            for (int ci = 0; ci < cores.Count; ci++)
            {
                bool perf = !hybrid || cores[ci].Key == maxC;
                int ti = perf ? pIdx++ : eIdx++;
                List<int> keys = cores[ci].Value;
                for (int t = 0; t < keys.Count; t++)
                {
                    var lp = new LpInfo();
                    lp.Group = keys[t] / 64; lp.Number = keys[t] % 64;
                    lp.Core = ci; lp.Thread = t; lp.ThreadsInCore = keys.Count;
                    lp.Perf = perf; lp.TypeIndex = ti;
                    lps.Add(lp);
                }
            }
            lps.Sort(delegate(LpInfo a, LpInfo b) { return (a.Group * 64 + a.Number).CompareTo(b.Group * 64 + b.Number); });
            phys = cores.Count; pCores = pIdx; eCores = eIdx;
            return lps.ToArray();
        }

        static string ReadCpuName()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (k != null) return Convert.ToString(k.GetValue("ProcessorNameString"));
                }
            }
            catch { }
            return "";
        }

        static float ReadBaseMhz(int logical)
        {
            const int Size = 24; // PROCESSOR_POWER_INFORMATION
            IntPtr buf = Marshal.AllocHGlobal(Size * Math.Max(1, logical));
            try
            {
                if (Native.CallNtPowerInformation(11, IntPtr.Zero, 0, buf, (uint)(Size * logical)) == 0)
                {
                    int max = 0;
                    for (int i = 0; i < logical; i++) max = Math.Max(max, Marshal.ReadInt32(buf, i * Size + 4));
                    if (max > 0) return max;
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (k != null) return Convert.ToSingle(k.GetValue("~MHz", 0));
                }
            }
            catch { }
            return 0;
        }

        static List<Gpu> EnumDxgiAdapters()
        {
            var list = new List<Gpu>();
            try
            {
                Guid iid = typeof(IDXGIFactory1).GUID;
                object fo;
                if (Native.CreateDXGIFactory1(ref iid, out fo) != 0 || fo == null) return list;
                var factory = (IDXGIFactory1)fo;
                for (uint i = 0; i < 16; i++)
                {
                    IDXGIAdapter1 a;
                    if (factory.EnumAdapters1(i, out a) != 0 || a == null) break;
                    DXGI_ADAPTER_DESC1 d;
                    if (a.GetDesc1(out d) == 0 && (d.Flags & 2) == 0 && d.VendorId != 0x1414)
                    {
                        var g = new Gpu();
                        g.Name = ShortGpuName(d.Description);
                        g.Luid = string.Format("luid_0x{0:X8}_0x{1:X8}", unchecked((uint)d.LuidHigh), d.LuidLow);
                        g.DedTotal = d.DedicatedVideoMemory.ToUInt64();
                        g.SharedTotal = d.SharedSystemMemory.ToUInt64();
                        g.VendorId = d.VendorId;
                        list.Add(g);
                    }
                    Marshal.ReleaseComObject(a);
                }
                Marshal.ReleaseComObject(factory);
            }
            catch (Exception ex) { Settings.Log("DXGI: " + ex.Message); }
            return list;
        }

        static string ShortCpuName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "CPU";
            s = Regex.Replace(s, @"\((R|TM)\)", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s*@.*$", "");
            s = Regex.Replace(s, @"\s+(with|w/)\s+.*$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b\d+(st|nd|rd|th)\s+Gen\b", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(Intel|AMD|CPU|Processor|\d+-Core)\b", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length == 0 ? "CPU" : s;
        }

        static string ShortGpuName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "GPU";
            s = Regex.Replace(s, @"\((R|TM)\)", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(Intel|NVIDIA|GeForce|AMD|Laptop GPU)\b", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length == 0 ? "GPU" : s;
        }

        static string PrettyEngine(string t)
        {
            t = Regex.Replace(t, @"_\d+$", "");
            return Regex.Replace(t, "(?<=[a-z])(?=[A-Z])", " ");
        }
    }
}
