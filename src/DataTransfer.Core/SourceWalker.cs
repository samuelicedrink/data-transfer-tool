using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DataTransfer.Core
{
    public sealed class FolderError
    {
        public string Path;
        public int ErrorCode;
        public string Message;
        public override string ToString() => $"{Path}: {Message} ({ErrorCode})";
    }

    public sealed class WalkResult
    {
        /// <summary>Every file in folders that are not excluded (what Robocopy reports as Total).</summary>
        public long Files, Bytes;
        /// <summary>Files Robocopy is expected to copy: not excluded by name, not cloud-only.</summary>
        public long EligibleFiles, EligibleBytes;
        public long CloudFiles, CloudBytes;
        public long EfsFiles, EfsBytes;
        public long NameExcludedFiles;
        public long Folders;
        public long LargestFile;
        public string LargestFilePath;
        public int LongestPath;
        public List<string> EfsSamples = new List<string>();
        public List<string> CloudSamples = new List<string>();
        public List<FolderError> Unreadable = new List<FolderError>();
        public bool Cancelled;
        public const int MaxSamples = 200;
    }

    /// <summary>
    /// The tool's own read-only pass over the selected folders, following the same exclusions as
    /// the Robocopy command. It finds what Robocopy's scan cannot report: the names of unreadable
    /// folders, cloud-only files and EFS-encrypted files. It never opens a file, so nothing is
    /// downloaded from OneDrive.
    /// </summary>
    public static class SourceWalker
    {
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_REPARSE_POINT = 0x400, FILE_ATTRIBUTE_OFFLINE = 0x1000,
                   FILE_ATTRIBUTE_ENCRYPTED = 0x4000, FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000, FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;
        const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003, IO_REPARSE_TAG_SYMLINK = 0xA000000C;

        /// <summary>
        /// Cloud-only test. A .NET process is shown these files without OFFLINE (M1 finding), so the
        /// recall bits are what counts; OFFLINE is included for processes that do see it.
        /// </summary>
        public static bool IsCloudOnly(uint attributes) =>
            (attributes & (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_OFFLINE)) != 0;

        public static WalkResult Walk(JobDefinition job, ProfileInfo profiles, CancellationToken ct, Action<string> onFolder = null)
        {
            var result = new WalkResult();
            if (job.Options.BackupMode) Privileges.TryEnable("SeBackupPrivilege"); // read like Robocopy /B does
            var filePatterns = job.Options.ExcludeFiles.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Wildcard).ToList();
            var dirPatterns = job.Options.ExcludeDirNames.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Wildcard).ToList();
            IntPtr buffer = Marshal.AllocHGlobal(BufferSize);
            try
            {
                foreach (var source in job.Sources)
                {
                    var excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var x in source.ExcludedSubfolders) excludedPaths.Add(x.TrimEnd('\\'));
                    foreach (var x in RobocopyCommandBuilder.ProfileExclusions(source.Path, job.Options.ExcludeProfileDirs, profiles)) excludedPaths.Add(x.TrimEnd('\\'));
                    if (!string.IsNullOrEmpty(job.DestinationRoot) && PathUtil.IsSameOrUnder(job.DestinationRoot, source.Path)) excludedPaths.Add(job.DestinationRoot.TrimEnd('\\'));

                    var stack = new Stack<string>();
                    stack.Push(PathUtil.Trim(source.Path));
                    while (stack.Count > 0)
                    {
                        if (ct.IsCancellationRequested) { result.Cancelled = true; return result; }
                        string dir = stack.Pop();
                        result.Folders++;
                        onFolder?.Invoke(dir);
                        Enumerate(dir, buffer, result, (name, attrs, size, tag) =>
                        {
                            string full = dir.EndsWith("\\") ? dir + name : dir + "\\" + name;
                            if ((attrs & FILE_ATTRIBUTE_DIRECTORY) != 0)
                            {
                                // /XJ: junctions and directory symlinks are not followed
                                if ((attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0 && (tag == IO_REPARSE_TAG_MOUNT_POINT || tag == IO_REPARSE_TAG_SYMLINK)) return;
                                if (excludedPaths.Contains(full) || dirPatterns.Any(p => p.IsMatch(name))) return;
                                stack.Push(full);
                                return;
                            }
                            result.Files++; result.Bytes += size;
                            if (full.Length > result.LongestPath) result.LongestPath = full.Length;
                            bool nameExcluded = filePatterns.Any(p => p.IsMatch(name)) ||
                                                ((attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0 && tag == IO_REPARSE_TAG_SYMLINK);
                            if (nameExcluded) { result.NameExcludedFiles++; return; }
                            if (IsCloudOnly(attrs))
                            {
                                result.CloudFiles++; result.CloudBytes += size;
                                if (result.CloudSamples.Count < WalkResult.MaxSamples) result.CloudSamples.Add(full);
                                if (job.Options.ExcludeCloudOnly) return;
                            }
                            if ((attrs & FILE_ATTRIBUTE_ENCRYPTED) != 0)
                            {
                                result.EfsFiles++; result.EfsBytes += size;
                                if (result.EfsSamples.Count < WalkResult.MaxSamples) result.EfsSamples.Add(full);
                            }
                            result.EligibleFiles++; result.EligibleBytes += size;
                            if (size > result.LargestFile) { result.LargestFile = size; result.LargestFilePath = full; }
                        });
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return result;
        }

        static Regex Wildcard(string pattern) =>
            new Regex("^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // ------------------------------------------------------------ directory enumeration

        const int BufferSize = 256 * 1024;
        const int FileFullDirectoryInfo = 14, FileFullDirectoryRestartInfo = 15;

        /// <summary>
        /// Lists one folder through a handle opened with backup semantics, so that with the backup
        /// privilege enabled it reads folders the account has no permission for, as Robocopy /B does.
        /// </summary>
        static void Enumerate(string dir, IntPtr buffer, WalkResult result, Action<string, uint, long, uint> onEntry)
        {
            using (var h = CreateFileW(PathUtil.Long(dir), 0x0001 /* FILE_LIST_DIRECTORY */, 7, IntPtr.Zero, 3, 0x02000000 /* BACKUP_SEMANTICS */, IntPtr.Zero))
            {
                if (h.IsInvalid) { Fail(dir, Marshal.GetLastWin32Error(), result); return; }
                bool first = true;
                while (true)
                {
                    if (!GetFileInformationByHandleEx(h, first ? FileFullDirectoryRestartInfo : FileFullDirectoryInfo, buffer, BufferSize))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != 18 /* NO_MORE_FILES */ && !(first && err == 2)) Fail(dir, err, result);
                        return;
                    }
                    first = false;
                    IntPtr p = buffer;
                    while (true)
                    {
                        int next = Marshal.ReadInt32(p, 0);
                        long size = Marshal.ReadInt64(p, 40);
                        uint attrs = (uint)Marshal.ReadInt32(p, 56);
                        int nameBytes = Marshal.ReadInt32(p, 60);
                        uint tag = (uint)Marshal.ReadInt32(p, 64); // EaSize holds the reparse tag for reparse points
                        string name = Marshal.PtrToStringUni(IntPtr.Add(p, 68), nameBytes / 2);
                        if (name != "." && name != "..") onEntry(name, attrs, size, tag);
                        if (next == 0) break;
                        p = IntPtr.Add(p, next);
                    }
                }
            }
        }

        static void Fail(string dir, int err, WalkResult result)
        {
            if (result.Unreadable.Count < 5000)
                result.Unreadable.Add(new FolderError { Path = dir, ErrorCode = err, Message = new Win32Exception(err).Message });
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, IntPtr buffer, int size);
    }

    /// <summary>Token privileges of this process.</summary>
    public static class Privileges
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct TOKEN_PRIVILEGES { public uint Count; public long Luid; public uint Attributes; }

        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool LookupPrivilegeValueW(string system, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int length, IntPtr previous, IntPtr returnLength);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        /// <summary>True when the privilege is held by this process and is now enabled.</summary>
        public static bool TryEnable(string privilege)
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008 /* ADJUST | QUERY */, out IntPtr token)) return false;
            try
            {
                if (!LookupPrivilegeValueW(null, privilege, out long luid)) return false;
                var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
                return Marshal.GetLastWin32Error() == 0; // 1300 = not all assigned = not held
            }
            finally { CloseHandle(token); }
        }
    }
}
