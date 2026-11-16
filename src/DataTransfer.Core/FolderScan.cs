using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace DataTransfer.Core
{
    /// <summary>Size and dates of everything inside a folder (junctions not followed, as with /XJ).</summary>
    public sealed class FolderSummary
    {
        public long Files, Bytes;
        /// <summary>Cloud-only (OneDrive) files: counted in Files and Bytes, but Robocopy skips them.</summary>
        public long CloudFiles, CloudBytes;
        /// <summary>Newest last-modified time of any file inside, UTC. Null when there are no files.</summary>
        public DateTime? NewestUtc;
        /// <summary>Folders inside (this one included) that could not be listed: the totals are a minimum.</summary>
        public int UnreadableFolders;
    }

    /// <summary>One file directly in a folder.</summary>
    public sealed class FileItem
    {
        public string Path, Name;
        public long Size;
        public DateTime? ModifiedUtc;
        public bool CloudOnly;
        public override string ToString() => Path;
    }

    /// <summary>The files directly in one folder, at most a given number of them.</summary>
    public sealed class FileListing
    {
        public List<FileItem> Files = new List<FileItem>();
        /// <summary>All files in the folder, including the ones not listed.</summary>
        public int TotalFiles;
        public int ErrorCode;
    }

    /// <summary>
    /// Works out folder sizes and newest dates in the background. Requests are served newest
    /// first, and a newer request interrupts a long scan (the long scan carries on afterwards),
    /// so the folders the technician opens get their numbers first. Results are kept, so
    /// subfolders of a scanned folder are instant. Read-only: never opens a file, so nothing is
    /// downloaded from OneDrive.
    /// </summary>
    public sealed class FolderScanner : IDisposable
    {
        readonly ConcurrentDictionary<string, FolderSummary> done = new ConcurrentDictionary<string, FolderSummary>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> pending = new List<string>();   // last = served next
        readonly List<string> walking = new List<string>();   // folders being scanned, outermost first
        readonly List<string> deferred = new List<string>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly Thread worker;
        readonly bool backupPrivilege;
        volatile bool disposed;
        volatile int pendingCount;
        IntPtr buffer;

        /// <param name="useBackupPrivilege">Enable SeBackupPrivilege when held (elevated), to read folders as Robocopy /B does.</param>
        public FolderScanner(bool useBackupPrivilege = true)
        {
            backupPrivilege = useBackupPrivilege;
            // deep trees and nested interruptions recurse; give the thread room
            worker = new Thread(Run, 16 * 1024 * 1024) { IsBackground = true, Name = "FolderScanner", Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }

        /// <summary>Number of folders scanned so far (for tests and the status line).</summary>
        public int ScannedCount => done.Count;

        public bool TryGet(string folder, out FolderSummary summary) => done.TryGetValue(Key(folder), out summary);

        /// <summary>Asks for folders to be scanned; the first one is served first. Ahead of every earlier request.</summary>
        public void Request(IEnumerable<string> folders)
        {
            var list = folders.Select(Key).Where(f => !done.ContainsKey(f)).ToList();
            if (list.Count == 0) return;
            lock (pending)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    pending.RemoveAll(p => string.Equals(p, list[i], StringComparison.OrdinalIgnoreCase));
                    pending.Add(list[i]);
                }
                pendingCount = pending.Count;
            }
            signal.Set();
        }

        public void Request(string folder) => Request(new[] { folder });

        public void Dispose()
        {
            disposed = true;
            signal.Set();
        }

        static string Key(string folder) => PathUtil.Trim(folder);

        void Run()
        {
            if (backupPrivilege) Privileges.TryEnable("SeBackupPrivilege");
            buffer = Marshal.AllocHGlobal(SourceWalker.BufferSize);
            try
            {
                while (!disposed)
                {
                    signal.WaitOne();
                    ServePending();
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        bool TryPop(out string folder)
        {
            lock (pending)
            {
                folder = null;
                if (pending.Count == 0) return false;
                folder = pending[pending.Count - 1];
                pending.RemoveAt(pending.Count - 1);
                pendingCount = pending.Count;
                return true;
            }
        }

        /// <summary>Serves every waiting request, newest first. Called between folders of a running scan.</summary>
        void ServePending()
        {
            while (!disposed && TryPop(out string f))
            {
                if (done.ContainsKey(f)) continue;
                // a folder that contains the one being scanned now is finished after that scan,
                // otherwise its scan would walk into the unfinished folder a second time
                if (walking.Any(w => PathUtil.IsSameOrUnder(w, f))) { deferred.Add(f); continue; }
                Walk(f);
            }
            if (walking.Count == 0 && deferred.Count > 0 && !disposed)
            {
                var again = deferred.ToList();
                deferred.Clear();
                Request(again);
            }
        }

        /// <summary>Scans a folder and everything below it; null when the scanner was disposed meanwhile.</summary>
        FolderSummary Walk(string dir)
        {
            if (done.TryGetValue(dir, out var cached)) return cached;
            var s = new FolderSummary();
            var subfolders = new List<string>();
            SourceWalker.Enumerate(dir, buffer, err => s.UnreadableFolders++, (name, attrs, size, tag, written) =>
            {
                string full = dir.EndsWith("\\") ? dir + name : dir + "\\" + name;
                if ((attrs & SourceWalker.FILE_ATTRIBUTE_DIRECTORY) != 0)
                {
                    if ((attrs & SourceWalker.FILE_ATTRIBUTE_REPARSE_POINT) != 0 &&
                        (tag == SourceWalker.IO_REPARSE_TAG_MOUNT_POINT || tag == SourceWalker.IO_REPARSE_TAG_SYMLINK)) return;
                    subfolders.Add(full);
                    return;
                }
                if ((attrs & SourceWalker.FILE_ATTRIBUTE_REPARSE_POINT) != 0 && tag == SourceWalker.IO_REPARSE_TAG_SYMLINK) return;
                Add(s, size, written, SourceWalker.IsCloudOnly(attrs));
            });

            walking.Add(dir);
            try
            {
                foreach (var sub in subfolders)
                {
                    if (disposed) return null;
                    if (pendingCount > 0) ServePending();
                    var c = Walk(sub);
                    if (c == null) return null;
                    s.Files += c.Files; s.Bytes += c.Bytes;
                    s.CloudFiles += c.CloudFiles; s.CloudBytes += c.CloudBytes;
                    s.UnreadableFolders += c.UnreadableFolders;
                    if (c.NewestUtc.HasValue && (!s.NewestUtc.HasValue || c.NewestUtc > s.NewestUtc)) s.NewestUtc = c.NewestUtc;
                }
            }
            finally { walking.RemoveAt(walking.Count - 1); }
            done[dir] = s;
            return s;
        }

        static void Add(FolderSummary s, long size, long written, bool cloud)
        {
            s.Files++; s.Bytes += size;
            if (cloud) { s.CloudFiles++; s.CloudBytes += size; }
            var t = Time(written);
            if (t.HasValue && (!s.NewestUtc.HasValue || t > s.NewestUtc)) s.NewestUtc = t;
        }

        static DateTime? Time(long fileTime)
        {
            if (fileTime <= 0) return null;
            try { return DateTime.FromFileTimeUtc(fileTime); } catch (ArgumentOutOfRangeException) { return null; }
        }

        /// <summary>
        /// The files directly in a folder, sorted by name, at most <paramref name="max"/> of them.
        /// File symbolic links are left out (Robocopy does not copy them with /XJ).
        /// </summary>
        public static FileListing ListFiles(string folder, int max)
        {
            var result = new FileListing();
            var all = new List<FileItem>();
            string dir = Key(folder);
            IntPtr buf = Marshal.AllocHGlobal(SourceWalker.BufferSize);
            try
            {
                SourceWalker.Enumerate(dir, buf, err => result.ErrorCode = err, (name, attrs, size, tag, written) =>
                {
                    if ((attrs & SourceWalker.FILE_ATTRIBUTE_DIRECTORY) != 0) return;
                    if ((attrs & SourceWalker.FILE_ATTRIBUTE_REPARSE_POINT) != 0 && tag == SourceWalker.IO_REPARSE_TAG_SYMLINK) return;
                    all.Add(new FileItem
                    {
                        Name = name, Path = dir.EndsWith("\\") ? dir + name : dir + "\\" + name, Size = size,
                        ModifiedUtc = Time(written), CloudOnly = SourceWalker.IsCloudOnly(attrs)
                    });
                });
            }
            finally { Marshal.FreeHGlobal(buf); }
            result.TotalFiles = all.Count;
            result.Files = all.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).Take(max).ToList();
            return result;
        }
    }
}
