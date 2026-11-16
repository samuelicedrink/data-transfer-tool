using System;
using System.IO;
using System.Linq;

namespace DataTransfer.Core
{
    /// <summary>
    /// One log folder per job (v1.0.3, owner 2026-11-15). While the tool runs, the logs are written
    /// next to the tool (Logs\&lt;folder&gt;), where they survive a lost or full destination. When a
    /// run ends and when the tool is closed, the folder is copied to
    /// &lt;Destination&gt;\Transfer logs\&lt;folder&gt;, so each job's logs are found with its copy.
    ///
    /// Inside the folder every kind of Robocopy run appends to one file: copy.log (main pass of every
    /// folder job), retry.log (locked-file retry and Retry failed) and scan.log (pre-flight scans),
    /// next to failures.csv and, once the job is closed, job.json.
    /// </summary>
    public static class JobLogs
    {
        public const string DestinationFolder = "Transfer logs";
        public const string CopyLog = "copy.log", RetryLog = "retry.log", ScanLog = "scan.log", FailuresCsv = "failures.csv", JobFile = "job.json";

        static readonly object publishLock = new object();

        /// <summary>
        /// "&lt;COMPUTERNAME&gt; yyyy-MM-dd HHmm &lt;first 6 characters of the job id&gt;", set once and kept in the
        /// job, so a resumed job keeps writing to the same folder.
        /// </summary>
        public static string FolderName(JobDefinition job)
        {
            if (string.IsNullOrEmpty(job.LogFolderName))
            {
                string name = $"{job.ComputerName} {job.CreatedUtc.ToLocalTime():yyyy-MM-dd HHmm} {job.Id.Substring(0, Math.Min(6, job.Id.Length))}";
                foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                job.LogFolderName = name;
            }
            return job.LogFolderName;
        }

        /// <summary>Where the job's logs are copied: &lt;Destination&gt;\Transfer logs\&lt;folder&gt;.</summary>
        public static string DestinationFor(JobDefinition job) =>
            Path.Combine(job.DestinationRoot.TrimEnd('\\') + "\\", DestinationFolder, FolderName(job));

        /// <summary>
        /// Copies the job's log folder to the destination. Files already there with the same size and
        /// time are left alone, so publishing again is cheap. Returns the destination folder, or throws
        /// IOException / UnauthorizedAccessException when the destination cannot be written; the logs
        /// next to the tool are kept either way.
        /// </summary>
        public static string Publish(JobDefinition job, string localFolder)
        {
            if (string.IsNullOrEmpty(job.DestinationRoot)) throw new IOException("The job has no destination.");
            string target = DestinationFor(job);
            lock (publishLock)
            {
                if (!Directory.Exists(PathUtil.Long(localFolder))) return target;
                Directory.CreateDirectory(PathUtil.Long(target));
                foreach (var f in new DirectoryInfo(PathUtil.Long(localFolder)).GetFiles())
                {
                    var t = new FileInfo(Path.Combine(PathUtil.Long(target), f.Name));
                    if (t.Exists && t.Length == f.Length && t.LastWriteTimeUtc == f.LastWriteTimeUtc) continue;
                    f.CopyTo(t.FullName, true);
                }
            }
            return target;
        }

        /// <summary>
        /// Prepares a log for appending: creates it with a UTF-16 byte order mark (without one,
        /// Robocopy /UNILOG+ writes a new file as ANSI), or measures an existing one. Returns the byte
        /// offset where new lines start and the number of lines already in the file.
        /// </summary>
        public static (long offset, int lines) PrepareForAppend(string logPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            if (!File.Exists(logPath))
            {
                File.WriteAllBytes(logPath, new byte[] { 0xFF, 0xFE });
                return (2, 0);
            }
            int lines = 0;
            long length;
            using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                var buf = new byte[1 << 20];
                int n, carry = -1;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    int i = 0;
                    if (carry >= 0) { if (carry == 0x0A && buf[0] == 0) lines++; i = 1; carry = -1; }
                    for (; i + 1 < n; i += 2) if (buf[i] == 0x0A && buf[i + 1] == 0) lines++;
                    if (i < n) carry = buf[i];
                }
                length = fs.Length;
                // a run killed in the middle of a character leaves an odd length: pad so that new text stays aligned
                if (length % 2 == 1) { fs.Seek(0, SeekOrigin.End); fs.WriteByte(0); length++; }
            }
            return (length, lines);
        }
    }
}
