using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace DataTransfer.Core
{
    public enum JobState { NotStarted, Running, Completed, Cancelled, DestinationFull, Failed }

    public enum SourceStatus { Pending, Running, Completed, CompletedWithErrors, Failed }

    public enum FailureCategory { Other, Locked, AccessDenied, NotFound, PathTooLong, DiskFull, DestinationUnavailable }

    /// <summary>Editable copy settings. Defaults are the flag set approved after M1.</summary>
    [DataContract]
    public sealed class CopyOptions
    {
        [DataMember] public int Threads { get; set; } = 8;
        [DataMember] public int Retries { get; set; } = 1;
        [DataMember] public int WaitSeconds { get; set; } = 2;
        /// <summary>/B. Needs an elevated process; Robocopy refuses to start otherwise.</summary>
        [DataMember] public bool BackupMode { get; set; } = true;
        /// <summary>/XA:O. Skips cloud-only (OneDrive) files without downloading them.</summary>
        [DataMember] public bool ExcludeCloudOnly { get; set; } = true;
        /// <summary>
        /// False (default): copied files and folders are made visible at the destination: /A-:SH for
        /// files, and the tool clears Hidden and System on folders after each folder job. True keeps
        /// the source's Hidden and System attributes, as v1.0.0 to v1.0.2 did.
        /// </summary>
        [DataMember] public bool KeepHiddenAttributes { get; set; }
        /// <summary>Folder names excluded only directly under a user profile (passed as full paths).</summary>
        [DataMember] public List<string> ExcludeProfileDirs { get; set; } = new List<string> { "AppData" };
        /// <summary>Folder names excluded at any depth.</summary>
        [DataMember] public List<string> ExcludeDirNames { get; set; } = new List<string>();
        [DataMember] public List<string> ExcludeFiles { get; set; } = new List<string> { "desktop.ini", "thumbs.db", "~$*", "*.tmp" };
        /// <summary>Pause between the main pass and the retry of locked files.</summary>
        [DataMember] public int LockedRetryDelaySeconds { get; set; } = 10;
        /// <summary>How often the destination is checked while copying and while paused.</summary>
        [DataMember] public int DestinationPollSeconds { get; set; } = 3;

        [OnDeserialized]
        void Fix(StreamingContext c)
        {
            ExcludeProfileDirs = ExcludeProfileDirs ?? new List<string>();
            ExcludeDirNames = ExcludeDirNames ?? new List<string>();
            ExcludeFiles = ExcludeFiles ?? new List<string>();
            if (Threads < 1) Threads = 1;
        }
    }

    /// <summary>Editable pre-flight settings.</summary>
    [DataContract]
    public sealed class PreflightOptions
    {
        /// <summary>Process names (without .exe) that should be closed before a run.</summary>
        [DataMember] public List<string> KnownApps { get; set; } = new List<string>(AppCloser.DefaultKnownApps);
        /// <summary>How long applications get to close by themselves before they are force-closed.</summary>
        [DataMember] public int AppCloseTimeoutSeconds { get; set; } = 20;

        [OnDeserialized]
        void Fix(StreamingContext c)
        {
            KnownApps = KnownApps ?? new List<string>(AppCloser.DefaultKnownApps);
            if (AppCloseTimeoutSeconds < 1) AppCloseTimeoutSeconds = 20;
        }
    }

    [DataContract]
    public sealed class Counts
    {
        [DataMember] public long FilesTotal { get; set; }
        [DataMember] public long FilesCopied { get; set; }
        [DataMember] public long FilesSkipped { get; set; }
        [DataMember] public long FilesFailed { get; set; }
        [DataMember] public long BytesTotal { get; set; }
        [DataMember] public long BytesCopied { get; set; }
        [DataMember] public long BytesSkipped { get; set; }
        [DataMember] public long BytesFailed { get; set; }

        public void Add(Counts o)
        {
            FilesTotal += o.FilesTotal; FilesCopied += o.FilesCopied; FilesSkipped += o.FilesSkipped; FilesFailed += o.FilesFailed;
            BytesTotal += o.BytesTotal; BytesCopied += o.BytesCopied; BytesSkipped += o.BytesSkipped; BytesFailed += o.BytesFailed;
        }

        public override string ToString() =>
            $"files total {FilesTotal}, copied {FilesCopied}, skipped {FilesSkipped}, failed {FilesFailed}; bytes total {BytesTotal}, copied {BytesCopied}";
    }

    [DataContract]
    public sealed class FileFailure
    {
        [DataMember] public string SourcePath { get; set; }
        [DataMember] public bool IsDirectory { get; set; }
        [DataMember] public int ErrorCode { get; set; }
        [DataMember] public string Operation { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public FailureCategory Category { get; set; }
        [DataMember] public long Size { get; set; }
        [DataMember] public int Attempts { get; set; }
        [DataMember] public string Time { get; set; }
        /// <summary>Name of the Robocopy log (in the Logs folder) that holds this error; the latest one when the item was retried.</summary>
        [DataMember] public string LogFile { get; set; }
        /// <summary>Line number of the ERROR entry in that log, counted from 1 as a text editor shows it.</summary>
        [DataMember] public int LogLine { get; set; }

        public override string ToString() => $"{Category} ({ErrorCode}) {SourcePath}: {Message}";
    }

    /// <summary>One ticked top-level folder = one Robocopy job.</summary>
    [DataContract]
    public sealed class SourceSelection
    {
        [DataMember] public string Path { get; set; }
        /// <summary>Full paths of unticked subfolders; each becomes /XD.</summary>
        [DataMember] public List<string> ExcludedSubfolders { get; set; } = new List<string>();
        [DataMember] public SourceStatus Status { get; set; }
        [DataMember] public int LastExitCode { get; set; }
        [DataMember] public Counts Totals { get; set; } = new Counts();
        [DataMember] public List<FileFailure> Failures { get; set; } = new List<FileFailure>();
        /// <summary>True when counts include an estimate because a pass was interrupted.</summary>
        [DataMember] public bool CountsApproximate { get; set; }

        [OnDeserialized]
        void Fix(StreamingContext c)
        {
            ExcludedSubfolders = ExcludedSubfolders ?? new List<string>();
            Totals = Totals ?? new Counts();
            Failures = Failures ?? new List<FileFailure>();
        }
    }

    /// <summary>Everything needed to run, report on and resume a job. Never holds a password.</summary>
    [DataContract]
    public sealed class JobDefinition
    {
        [DataMember] public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 12);
        [DataMember] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        [DataMember] public string ComputerName { get; set; } = Environment.MachineName;
        [DataMember] public string DestinationRoot { get; set; }
        /// <summary>User name for a UNC destination. The password is not part of the job.</summary>
        [DataMember] public string DestinationUserName { get; set; }
        [DataMember] public CopyOptions Options { get; set; } = new CopyOptions();
        [DataMember] public PreflightOptions Preflight { get; set; } = new PreflightOptions();
        [DataMember] public List<SourceSelection> Sources { get; set; } = new List<SourceSelection>();
        [DataMember] public JobState State { get; set; }
        [DataMember] public DateTime? StartedUtc { get; set; }
        [DataMember] public DateTime? FinishedUtc { get; set; }
        [DataMember] public string LastMessage { get; set; }
        /// <summary>Name of this job's log folder (see JobLogs.FolderName); kept so a resumed job uses the same one.</summary>
        [DataMember] public string LogFolderName { get; set; }

        [OnDeserialized]
        void Fix(StreamingContext c)
        {
            Options = Options ?? new CopyOptions();
            Preflight = Preflight ?? new PreflightOptions();
            Sources = Sources ?? new List<SourceSelection>();
        }

        public bool IsUnfinished => State == JobState.Running || State == JobState.Cancelled || State == JobState.DestinationFull;

        /// <summary>&lt;Dest&gt;\&lt;COMPUTERNAME&gt;\&lt;drive letter&gt;\&lt;original path&gt;</summary>
        public string DestinationFor(string sourcePath)
        {
            string full = System.IO.Path.GetFullPath(sourcePath).TrimEnd('\\');
            if (full.Length < 2 || full[1] != ':') throw new ArgumentException("Source must be a local drive path: " + sourcePath);
            string rest = full.Substring(2).TrimStart('\\');
            string d = System.IO.Path.Combine(DestinationRoot.TrimEnd('\\') + "\\", ComputerName, char.ToUpperInvariant(full[0]).ToString());
            return rest.Length == 0 ? d : System.IO.Path.Combine(d, rest);
        }

        public SourceSelection OwnerOf(string path) =>
            Sources.Where(s => PathUtil.IsSameOrUnder(path, s.Path)).OrderByDescending(s => s.Path.Length).FirstOrDefault();

        public Counts Totals()
        {
            var c = new Counts();
            foreach (var s in Sources) c.Add(s.Totals);
            return c;
        }

        public IEnumerable<FileFailure> AllFailures() => Sources.SelectMany(s => s.Failures);
    }

    public static class PathUtil
    {
        /// <summary>Prefix for the app's own file calls; long paths work regardless of the LongPathsEnabled policy.</summary>
        public static string Long(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith(@"\\?\")) return path;
            if (path.StartsWith(@"\\")) return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }

        public static string Trim(string path)
        {
            string t = path.TrimEnd('\\');
            return t.EndsWith(":") ? t + "\\" : t;
        }

        public static bool IsSameOrUnder(string path, string root)
        {
            string p = path.TrimEnd('\\'), r = root.TrimEnd('\\');
            return p.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                   p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Where user profiles live; injectable so tests need no real profiles.</summary>
    public sealed class ProfileInfo
    {
        public string ProfilesDirectory { get; set; }
        public List<string> ProfileFolders { get; set; } = new List<string>();

        public static ProfileInfo FromSystem()
        {
            var p = new ProfileInfo();
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
                    p.ProfilesDirectory = Environment.ExpandEnvironmentVariables((k?.GetValue("ProfilesDirectory") as string) ?? "");
            }
            catch { }
            if (string.IsNullOrEmpty(p.ProfilesDirectory))
                p.ProfilesDirectory = System.IO.Path.Combine(System.IO.Path.GetPathRoot(Environment.SystemDirectory), "Users");
            try { p.ProfileFolders = Directory.GetDirectories(p.ProfilesDirectory).ToList(); } catch { }
            return p;
        }
    }
}
