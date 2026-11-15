using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace DataTransfer.Core
{
    // ------------------------------------------------------------------ applications

    public sealed class RunningApp
    {
        public int ProcessId;
        public string Name;
        public int SessionId;
        public bool HasWindow;
        public string WindowTitle;
        public override string ToString() => HasWindow && !string.IsNullOrEmpty(WindowTitle) ? $"{Name} (\"{WindowTitle}\")" : Name;
    }

    public sealed class CloseReport
    {
        public int ClosedGracefully, ForceClosed, StillRunning;
        public List<string> Notes = new List<string>();
    }

    /// <summary>Finds and closes applications from the known list.</summary>
    public static class AppCloser
    {
        public static readonly string[] DefaultKnownApps =
        {
            // Office
            "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "olk", "ONENOTE", "MSACCESS", "MSPUB", "VISIO", "WINPROJ", "lync",
            // browsers
            "chrome", "msedge", "firefox", "brave", "opera",
            // Teams, OneDrive
            "ms-teams", "Teams", "OneDrive",
            // PDF readers
            "Acrobat", "AcroRd32", "FoxitPDFReader", "FoxitReader", "SumatraPDF", "PDFXEdit"
        };

        static string Normalize(string name) =>
            name.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Trim().Substring(0, name.Trim().Length - 4) : name.Trim();

        /// <summary>Processes from the list. sessionFilter picks own session, other sessions, or all.</summary>
        public static List<RunningApp> Find(IEnumerable<string> knownApps, Func<int, bool> sessionFilter)
        {
            var names = new HashSet<string>(knownApps.Where(n => !string.IsNullOrWhiteSpace(n)).Select(Normalize), StringComparer.OrdinalIgnoreCase);
            int self = Process.GetCurrentProcess().Id;
            var found = new List<RunningApp>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == self || !names.Contains(p.ProcessName) || !sessionFilter(p.SessionId)) continue;
                    var app = new RunningApp { ProcessId = p.Id, Name = p.ProcessName, SessionId = p.SessionId };
                    try { app.HasWindow = p.MainWindowHandle != IntPtr.Zero; app.WindowTitle = p.MainWindowTitle; } catch { }
                    found.Add(app);
                }
                catch (InvalidOperationException) { }
                finally { p.Dispose(); }
            }
            return found;
        }

        /// <summary>
        /// Asks each application to close (as if its window's X was clicked), waits up to the timeout,
        /// then ends whatever is left. Processes without a window, or in another user's session,
        /// cannot be asked and are ended after the wait.
        /// </summary>
        public static CloseReport Close(List<RunningApp> apps, TimeSpan timeout, CancellationToken ct)
        {
            var report = new CloseReport();
            var asked = new HashSet<int>();
            int ownSession = Process.GetCurrentProcess().SessionId;
            foreach (var a in apps.Where(a => a.HasWindow && a.SessionId == ownSession))
            {
                try { using (var p = Process.GetProcessById(a.ProcessId)) if (p.CloseMainWindow()) asked.Add(a.ProcessId); }
                catch (ArgumentException) { } catch (InvalidOperationException) { }
            }
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested && asked.Any(IsRunning)) Thread.Sleep(250);

            foreach (var a in apps)
            {
                if (!IsRunning(a.ProcessId)) { if (asked.Contains(a.ProcessId)) report.ClosedGracefully++; else report.ForceClosed++; continue; }
                if (ct.IsCancellationRequested) { report.StillRunning++; continue; }
                try
                {
                    using (var p = Process.GetProcessById(a.ProcessId)) { p.Kill(); p.WaitForExit(5000); }
                    report.ForceClosed++;
                }
                catch (ArgumentException) { report.ForceClosed++; } // exited in the meantime
                catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
                {
                    report.StillRunning++;
                    report.Notes.Add($"Could not close {a.Name} (session {a.SessionId}): {ex.Message}");
                }
            }
            return report;
        }

        static bool IsRunning(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception) { return true; }
        }
    }

    /// <summary>Stops the OneDrive sync client for the duration of a job and starts it again afterwards.</summary>
    public static class OneDriveControl
    {
        /// <summary>Returns the paths of the clients that were stopped (to restart later).</summary>
        public static List<string> Stop(List<string> notes)
        {
            var paths = new List<string>();
            int session = Process.GetCurrentProcess().SessionId;
            var running = Process.GetProcessesByName("OneDrive").Where(p => p.SessionId == session).ToList();
            if (running.Count == 0) return paths;
            foreach (var p in running)
            {
                string path = null;
                try { path = p.MainModule.FileName; } catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException) { }
                if (path != null && !paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
            }
            // the client's own shutdown switch lets it finish cleanly
            foreach (var path in paths)
            {
                try { using (var s = Process.Start(new ProcessStartInfo(path, "/shutdown") { UseShellExecute = false, CreateNoWindow = true })) s?.WaitForExit(10000); }
                catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException) { notes?.Add("OneDrive /shutdown failed: " + ex.Message); }
            }
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && running.Any(p => { try { return !p.HasExited; } catch { return false; } })) Thread.Sleep(250);
            foreach (var p in running)
            {
                try { if (!p.HasExited) { p.Kill(); notes?.Add("OneDrive did not shut down by itself and was ended."); } }
                catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException) { notes?.Add("Could not stop OneDrive: " + ex.Message); }
                p.Dispose();
            }
            return paths;
        }

        public static bool IsRunning()
        {
            int session = Process.GetCurrentProcess().SessionId;
            var list = Process.GetProcessesByName("OneDrive");
            try { return list.Any(p => p.SessionId == session); } finally { foreach (var p in list) p.Dispose(); }
        }

        /// <summary>
        /// Starts the client again. From an elevated process it is started through Explorer so that
        /// it does not run elevated (OneDrive refuses to sync as administrator).
        /// </summary>
        public static void Restart(IEnumerable<string> paths, List<string> notes)
        {
            bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            foreach (var path in paths)
            {
                try
                {
                    if (!File.Exists(path)) { notes?.Add("OneDrive not found at " + path); continue; }
                    var psi = elevated
                        ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + path + "\"")
                        : new ProcessStartInfo(path, "/background");
                    psi.UseShellExecute = false;
                    using (Process.Start(psi)) { }
                }
                catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException) { notes?.Add("Could not restart OneDrive: " + ex.Message); }
            }
        }
    }

    // ------------------------------------------------------------------ sessions

    public sealed class UserSession
    {
        public int SessionId;
        public string UserName;
        public string State;
        public override string ToString() => $"{UserName} (session {SessionId}, {State})";
    }

    public static class Sessions
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WTS_SESSION_INFO { public int SessionId; public IntPtr WinStationName; public int State; }

        [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr info, out int count);
        [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr memory);

        static readonly string[] StateNames = { "active", "connected", "connect query", "shadow", "disconnected", "idle", "listening", "reset", "down", "init" };

        /// <summary>Signed-in users other than the one running the tool.</summary>
        public static List<UserSession> OtherUsers()
        {
            var list = new List<UserSession>();
            int own = Process.GetCurrentProcess().SessionId;
            if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out IntPtr info, out int count)) return list;
            try
            {
                int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    var s = (WTS_SESSION_INFO)Marshal.PtrToStructure(IntPtr.Add(info, i * size), typeof(WTS_SESSION_INFO));
                    if (s.SessionId == own) continue;
                    string user = Query(s.SessionId, 5 /* WTSUserName */);
                    if (string.IsNullOrEmpty(user)) continue; // services session, listener
                    string domain = Query(s.SessionId, 7 /* WTSDomainName */);
                    list.Add(new UserSession
                    {
                        SessionId = s.SessionId,
                        UserName = string.IsNullOrEmpty(domain) ? user : domain + "\\" + user,
                        State = s.State >= 0 && s.State < StateNames.Length ? StateNames[s.State] : s.State.ToString()
                    });
                }
            }
            finally { WTSFreeMemory(info); }
            return list;
        }

        static string Query(int sessionId, int infoClass)
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out IntPtr buffer, out int bytes)) return null;
            try { return Marshal.PtrToStringUni(buffer); } finally { WTSFreeMemory(buffer); }
        }
    }

    // ------------------------------------------------------------------ network destination

    /// <summary>
    /// Opens the tool's own connection to a share. An elevated process does not see the signed-in
    /// user's mapped drives or their saved connections, so it has to authenticate itself.
    /// </summary>
    public static class NetworkConnection
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NETRESOURCE
        {
            public int Scope, Type, DisplayType, Usage;
            public string LocalName, RemoteName, Comment, Provider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)] static extern int WNetAddConnection2W(ref NETRESOURCE resource, string password, string user, int flags);
        [DllImport("mpr.dll", CharSet = CharSet.Unicode)] static extern int WNetCancelConnection2W(string name, int flags, bool force);

        public static bool IsUnc(string path) => path != null && path.StartsWith(@"\\") && !path.StartsWith(@"\\?\");

        /// <summary>\\server\share of a UNC path, or null.</summary>
        public static string ShareRoot(string path)
        {
            if (!IsUnc(path)) return null;
            var parts = path.Substring(2).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length < 2 ? null : @"\\" + parts[0] + @"\" + parts[1];
        }

        /// <summary>
        /// Connects with the given credentials (kept in memory only; never logged). Returns 0 on
        /// success, otherwise the Windows error code with a readable message.
        /// </summary>
        public static int Connect(string uncPath, string user, string password, out string message)
        {
            message = null;
            string share = ShareRoot(uncPath);
            if (share == null) { message = "Not a \\\\server\\share path."; return 67; }
            var nr = new NETRESOURCE { Type = 1 /* RESOURCETYPE_DISK */, RemoteName = share };
            int rc = WNetAddConnection2W(ref nr, password, user, 0);
            if (rc == 1219) // already connected to this server with other credentials: replace that connection
            {
                WNetCancelConnection2W(share, 0, true);
                rc = WNetAddConnection2W(ref nr, password, user, 0);
            }
            if (rc != 0)
            {
                message = new Win32Exception(rc).Message;
                if (rc == 1219) message += " Close other connections to this server (net use) or use its IP address or full name instead.";
            }
            return rc;
        }

        public static void Disconnect(string uncPath)
        {
            string share = ShareRoot(uncPath);
            if (share != null) WNetCancelConnection2W(share, 0, true);
        }
    }

    // ------------------------------------------------------------------ volume, power

    public static class SystemProbes
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumeInformationW(string root, StringBuilder name, int nameSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder fileSystem, int fsSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumePathNameW(string path, StringBuilder volume, int size);

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, Reserved; public int BatteryLifeTime, BatteryFullLifeTime; }

        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        /// <summary>"NTFS", "FAT32", "exFAT"... for the volume or share that holds the path; null if unknown.</summary>
        public static string FileSystemOf(string path)
        {
            string root = NetworkConnection.ShareRoot(path);
            if (root == null)
            {
                var volume = new StringBuilder(512);
                root = GetVolumePathNameW(path, volume, volume.Capacity) ? volume.ToString() : Path.GetPathRoot(path);
            }
            if (string.IsNullOrEmpty(root)) return null;
            if (!root.EndsWith("\\")) root += "\\";
            var fs = new StringBuilder(64);
            return GetVolumeInformationW(root, null, 0, out _, out _, out _, fs, fs.Capacity) ? fs.ToString() : null;
        }

        public static long? FreeSpace(string directory)
        {
            string dir = directory.TrimEnd('\\') + "\\";
            return Native.GetDiskFreeSpaceEx(dir, out ulong free, out _, out _) ? (long?)free : null;
        }

        /// <summary>True on battery, false on mains or a PC without a battery, null if unknown.</summary>
        public static bool? OnBattery(out int percent)
        {
            percent = -1;
            if (!GetSystemPowerStatus(out var s)) return null;
            if ((s.BatteryFlag & 128) != 0) return false;      // no system battery
            if (s.BatteryLifePercent <= 100) percent = s.BatteryLifePercent;
            if (s.ACLineStatus == 0) return true;
            if (s.ACLineStatus == 1) return false;
            return null;
        }

        public static bool IsElevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
