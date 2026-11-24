using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace CoreGlance
{
    /// <summary>
    /// "Show yourself" signal from a second launch to the running copy. A named event rather than a broadcast window
    /// message: Windows does not deliver broadcasts to hidden owned windows, and the running copy may be elevated.
    /// </summary>
    static class ShowSignal
    {
        const string Name = @"Local\CoreGlance.Show";

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllowSetForegroundWindow(uint processId);

        public static EventWaitHandle Create()
        {
            bool created;
            var sec = new EventWaitHandleSecurity();
            // Same user only; lets the normal-user launcher signal the admin copy.
            sec.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            return new EventWaitHandle(false, EventResetMode.AutoReset, Name, out created, sec);
        }

        /// <summary>Signals the running copy, waiting up to waitMs for it to come up. False when nobody is listening.</summary>
        public static bool Send(int waitMs)
        {
            try { AllowSetForegroundWindow(0xFFFFFFFF); } catch { }
            Stopwatch sw = Stopwatch.StartNew();
            do
            {
                try
                {
                    using (EventWaitHandle e = EventWaitHandle.OpenExisting(Name, EventWaitHandleRights.Modify))
                    {
                        e.Set();
                        return true;
                    }
                }
                catch (WaitHandleCannotBeOpenedException) { }
                catch (UnauthorizedAccessException) { return false; }
                Thread.Sleep(200);
            }
            while (sw.ElapsedMilliseconds < waitMs);
            return false;
        }
    }

    /// <summary>Taskbar identity, so a pinned icon, the Start Menu shortcut and the running window are one app.</summary>
    static class TaskbarId
    {
        public const string AppId = "CoreGlance.Widget";
        static readonly Guid AppUserModel = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        const uint PidRelaunchCommand = 2, PidRelaunchIcon = 3, PidRelaunchName = 4, PidId = 5;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Sequential)]
        struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p1, p2; }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint i, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
            [PreserveSig] int Commit();
        }

        // Vtable order matters; unused methods are placeholders.
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW
        {
            void _GetPath(); void _GetIDList(); void _SetIDList(); void _GetDescription();
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string s);
            void _GetWorkingDirectory();
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string s);
            void _GetArguments(); void _SetArguments(); void _GetHotkey(); void _SetHotkey(); void _GetShowCmd(); void _SetShowCmd(); void _GetIconLocation();
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void _SetRelativePath(); void _Resolve();
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string s);
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        [DllImport("shell32.dll")]
        static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IPropertyStore store);

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PROPVARIANT pv);

        public static void SetProcess()
        {
            try { SetCurrentProcessExplicitAppUserModelID(AppId); } catch { }
        }

        /// <summary>The normal-user launcher (hands over to the admin copy when sensor mode is on).</summary>
        static string LauncherPath
        {
            get
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\CoreGlance\CoreGlance.exe");
                return File.Exists(p) ? p : Application.ExecutablePath;
            }
        }

        /// <summary>Pinning the running window then pins the launcher, not the admin copy.</summary>
        public static void SetWindow(IntPtr hwnd)
        {
            try
            {
                Guid iid = typeof(IPropertyStore).GUID;
                IPropertyStore store;
                if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null) return;
                Put(store, PidRelaunchCommand, "\"" + LauncherPath + "\"");
                Put(store, PidRelaunchName, "CoreGlance");
                Put(store, PidRelaunchIcon, LauncherPath + ",0");
                Put(store, PidId, AppId);
                store.Commit();
                Marshal.ReleaseComObject(store);
            }
            catch (Exception ex) { Settings.Log("taskbar: " + ex.Message); }
        }

        static void Put(IPropertyStore store, uint pid, string value)
        {
            var key = new PROPERTYKEY();
            key.fmtid = AppUserModel;
            key.pid = pid;
            var pv = new PROPVARIANT();
            pv.vt = 31;   // VT_LPWSTR
            pv.p1 = Marshal.StringToCoTaskMemUni(value);
            try { store.SetValue(ref key, ref pv); }
            finally { PropVariantClear(ref pv); }
        }

        /// <summary>Writes the Start Menu shortcut with the app id (CoreGlance.exe --make-shortcut).</summary>
        public static int MakeShortcut()
        {
            try
            {
                string exe = Application.ExecutablePath;
                string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "CoreGlance.lnk");
                var link = (IShellLinkW)new ShellLink();
                link.SetPath(exe);
                link.SetWorkingDirectory(Path.GetDirectoryName(exe));
                link.SetDescription("Live CPU, memory, GPU, temperature, fan and PC health widget");
                link.SetIconLocation(exe, 0);
                var store = (IPropertyStore)link;
                Put(store, PidId, AppId);
                store.Commit();
                ((IPersistFile)link).Save(lnk, true);
                return 0;
            }
            catch (Exception ex)
            {
                Settings.Log("shortcut: " + ex.Message);
                return 1;
            }
        }
    }
}
