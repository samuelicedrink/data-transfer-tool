using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DataTransfer.Core
{
    /// <summary>Decodes a growing UTF-16 log file into lines.</summary>
    public sealed class LogTail : IDisposable
    {
        readonly string path;
        readonly Action<string> onLine;
        readonly Decoder decoder = Encoding.Unicode.GetDecoder();
        readonly byte[] bytes = new byte[64 * 1024];
        readonly char[] chars = new char[64 * 1024 + 2];
        readonly StringBuilder pending = new StringBuilder();
        FileStream fs;

        public LogTail(string path, Action<string> onLine) { this.path = path; this.onLine = onLine; }

        /// <summary>Reads whatever has been appended since the last call.</summary>
        public void Pump()
        {
            if (fs == null)
            {
                if (!File.Exists(path)) return;
                try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                catch (IOException) { return; }
            }
            int n;
            while ((n = fs.Read(bytes, 0, bytes.Length)) > 0) Decode(bytes, n);
        }

        /// <summary>For tests and for callers that already hold bytes.</summary>
        public void Decode(byte[] data, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int take = Math.Min(count - offset, bytes.Length);
                int c = decoder.GetChars(data, offset, take, chars, 0, false);
                offset += take;
                for (int i = 0; i < c; i++)
                {
                    if (chars[i] == '\n') { onLine(pending.ToString().TrimEnd('\r')); pending.Clear(); }
                    else pending.Append(chars[i]);
                }
            }
        }

        /// <summary>Delivers a last line that has no line break (after the writer has finished).</summary>
        public void Flush()
        {
            if (pending.Length > 0) { onLine(pending.ToString().TrimEnd('\r')); pending.Clear(); }
        }

        public void Dispose() { fs?.Dispose(); fs = null; }
    }

    /// <summary>One Robocopy process plus the live reader of its /UNILOG file.</summary>
    public sealed class RobocopyRun : IDisposable
    {
        readonly Process process;
        readonly LogTail tail;
        readonly StringBuilder stdout = new StringBuilder();

        public RobocopyLogParser Parser { get; } = new RobocopyLogParser();
        public string LogPath { get; }
        public string Arguments { get; }
        public bool Killed { get; private set; }

        public static string RobocopyPath
        {
            get
            {
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                // a 32-bit process on 64-bit Windows must not be redirected to the 32-bit Robocopy
                string sysnative = Path.Combine(win, "Sysnative", "robocopy.exe");
                if (!Environment.Is64BitProcess && Environment.Is64BitOperatingSystem && File.Exists(sysnative)) return sysnative;
                return Path.Combine(Environment.SystemDirectory, "robocopy.exe");
            }
        }

        public RobocopyRun(string arguments, string logPath)
        {
            Arguments = arguments;
            LogPath = logPath;
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            if (File.Exists(logPath)) File.Delete(logPath);
            tail = new LogTail(logPath, Parser.Feed);
            process = new Process
            {
                StartInfo = new ProcessStartInfo(RobocopyPath, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.OutputDataReceived += (s, e) => { if (e.Data != null && stdout.Length < 8000) lock (stdout) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null && stdout.Length < 8000) lock (stdout) stdout.AppendLine(e.Data); };
        }

        public void Start()
        {
            process.Start();
            ChildProcessGuard.Adopt(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        /// <summary>What Robocopy printed to the console; only non-empty when it could not use the log file.</summary>
        public string ConsoleOutput { get { lock (stdout) return stdout.ToString(); } }

        public void Pump() => tail.Pump();

        public void Kill()
        {
            try { if (!process.HasExited) { Killed = true; process.Kill(); } } catch (InvalidOperationException) { } catch (Win32Exception) { }
            process.WaitForExit(10000);
        }

        /// <summary>Call once after the process has exited to read the rest of the log.</summary>
        public void Finish()
        {
            process.WaitForExit();
            tail.Pump();
            tail.Flush();
        }

        public void Dispose()
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            tail.Dispose();
            process.Dispose();
        }
    }

    /// <summary>
    /// Puts child processes in a job object that Windows closes when this process ends, so a
    /// Robocopy never keeps running unattended after the tool crashes or is closed.
    /// </summary>
    static class ChildProcessGuard
    {
        static readonly object gate = new object();
        static IntPtr job;

        public static void Adopt(Process p)
        {
            try
            {
                lock (gate)
                {
                    if (job == IntPtr.Zero)
                    {
                        job = Native.CreateJobObject(IntPtr.Zero, null);
                        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                        info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                        int len = Marshal.SizeOf(info);
                        IntPtr mem = Marshal.AllocHGlobal(len);
                        try { Marshal.StructureToPtr(info, mem, false); Native.SetInformationJobObject(job, 9, mem, (uint)len); }
                        finally { Marshal.FreeHGlobal(mem); }
                    }
                }
                Native.AssignProcessToJobObject(job, p.Handle);
            }
            catch { /* protection is best effort; the copy itself does not depend on it */ }
        }
    }

    static class Native
    {
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll")] public static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll")] public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeForCaller, out ulong total, out ulong totalFree);
    }
}
