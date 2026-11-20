using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("CoreGlance")]
[assembly: AssemblyDescription("Lightweight live CPU, memory, GPU, temperature, fan, disk and network widget")]
[assembly: AssemblyProduct("CoreGlance")]
[assembly: AssemblyCopyright("Copyright 2026")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace CoreGlance
{
    static class Program
    {
        public const string Version = "1.1.0";
        const string MutexName = @"Local\CoreGlance.SingleInstance";

        [STAThread]
        static int Main(string[] args)
        {
            bool fromTask = Has(args, "--from-task");
            if (Has(args, "--install-task")) return Elevation.InstallTask();
            if (Has(args, "--remove-task")) return Elevation.RemoveTask();
            if (Has(args, "--install-pawnio")) return Elevation.InstallPawnIo();
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
                uint msg = Native.RegisterWindowMessage(WidgetForm.ShowMessageName);
                if (msg != 0) Native.PostMessage((IntPtr)0xFFFF, msg, IntPtr.Zero, IntPtr.Zero);
                return 0;
            }

            // Sensor mode installed: hand over to the admin copy via the scheduled task (no UAC prompt).
            if (!fromTask && !Elevation.IsElevated && !Has(args, "--no-elevate") && Elevation.TaskExists())
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
                if (Elevation.RunTask()) return 0;
                mutex = Acquire(0);
                if (mutex == null) return 0;
            }

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
            try { m = new Mutex(false, MutexName); }
            catch (UnauthorizedAccessException) { return null; }   // held by the admin copy
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
