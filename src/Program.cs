using System;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("CoreGlance")]
[assembly: AssemblyDescription("Lightweight live CPU, memory, GPU, temperature, fan, disk and network widget")]
[assembly: AssemblyProduct("CoreGlance")]
[assembly: AssemblyCopyright("Copyright 2026")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

namespace CoreGlance
{
    static class Program
    {
        public const string Version = "1.3.0";
        const string MutexName = @"Local\CoreGlance.SingleInstance";

        /// <summary>Signaled by a second launch; the widget shows itself.</summary>
        public static EventWaitHandle ShowEvent;
        /// <summary>The user opened the app (as opposed to Windows starting it at sign-in).</summary>
        public static bool OpenedByUser;

        [STAThread]
        static int Main(string[] args)
        {
            bool fromTask = Has(args, "--from-task");
            if (Has(args, "--install-task")) return Elevation.InstallTask();
            if (Has(args, "--remove-task")) return Elevation.RemoveTask();
            if (Has(args, "--install-pawnio")) return Elevation.InstallPawnIo();
            if (Has(args, "--make-shortcut")) return TaskbarId.MakeShortcut();
            if (Has(args, "--enable-autostart")) { Settings.SetAutoStart(true); return Settings.IsAutoStart() ? 0 : 1; }
            bool autostart = Has(args, "--autostart");
            int lpt = Array.FindIndex(args, delegate(string a) { return string.Equals(a, "--low-power-test", StringComparison.OrdinalIgnoreCase); });
            if (lpt >= 0 && lpt + 1 < args.Length)
            {
                string report = LowPower.SelfTest();
                System.IO.File.WriteAllText(args[lpt + 1], report);
                return report.Contains("ALL PASSED") ? 0 : 1;
            }
            int bench = Array.FindIndex(args, delegate(string a) { return string.Equals(a, "--bench-test", StringComparison.OrdinalIgnoreCase); });
            if (bench >= 0 && bench + 1 < args.Length)
            {
                // Diagnostic: a short render-speed check written to a text file.
                var st = new StressTest();
                var done = new ManualResetEvent(false);
                st.Start(8, delegate { return double.NaN; }, delegate { return 0; }, delegate(StressTest t) { done.Set(); });
                bool ok = done.WaitOne(30000);
                System.IO.File.WriteAllText(args[bench + 1], string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "finished={0} score={1:0.0} threads={2}", ok, st.Score, Environment.ProcessorCount));
                return ok ? 0 : 1;
            }
            int hsnap = Array.FindIndex(args, delegate(string a) { return string.Equals(a, "--health-snapshot", StringComparison.OrdinalIgnoreCase); });
            if (hsnap >= 0 && hsnap + 1 < args.Length)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WidgetForm(args[hsnap + 1], true));
                return 0;
            }
            int snap = Array.FindIndex(args, delegate(string a) { return string.Equals(a, "--snapshot", StringComparison.OrdinalIgnoreCase); });
            if (snap >= 0 && snap + 1 < args.Length)
            {
                // Diagnostic: sample for a few seconds and render the widget off-screen to a PNG (no tray, no settings saved).
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WidgetForm(args[snap + 1]));
                return 0;
            }

            Mutex mutex = Acquire(fromTask ? 10000 : 0);
            if (mutex == null)
            {
                // Already running: ask the running copy to show itself, then quit.
                if (!autostart && !fromTask) ShowSignal.Send(5000);
                return 0;
            }

            // Sensor mode installed: hand over to the admin copy via the scheduled task (no UAC prompt).
            if (!fromTask && !Elevation.IsElevated && !Has(args, "--no-elevate") && Elevation.TaskExists())
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
                if (Elevation.RunTask())
                {
                    // Wait for the admin copy to come up; opening the app must always end with a visible window.
                    if (autostart || ShowSignal.Send(12000)) return 0;
                    Settings.Log("admin copy did not start; running without admin");
                }
                mutex = Acquire(0);
                if (mutex == null) return 0;
            }
            OpenedByUser = !autostart && !fromTask;
            try { ShowEvent = ShowSignal.Create(); } catch (Exception ex) { Settings.Log("show signal: " + ex.Message); }
            TaskbarId.SetProcess();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Settings.Log(e.Exception.ToString()); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Settings.Log(Convert.ToString(e.ExceptionObject)); };
            ToolStripManager.Renderer = new DarkRenderer();

            Application.Run(new WidgetForm());
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
            return 0;
        }

        /// <summary>Returns the owned single-instance mutex, or null when another copy holds it.</summary>
        static Mutex Acquire(int waitMs)
        {
            Mutex m;
            try
            {
                bool created;
                var sec = new MutexSecurity();   // same user at either integrity level, so launcher and admin copy see each other
                sec.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User, MutexRights.FullControl, AccessControlType.Allow));
                m = new Mutex(false, MutexName, out created, sec);
            }
            catch (UnauthorizedAccessException) { return null; }   // held by another copy
            try
            {
                if (m.WaitOne(waitMs, false)) return m;
            }
            catch (AbandonedMutexException) { return m; }           // previous copy exited without releasing
            m.Dispose();
            return null;
        }

        static bool Has(string[] args, string flag)
        {
            foreach (string a in args) if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
