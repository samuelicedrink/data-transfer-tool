using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DataTransfer.Core
{
    public enum CheckStatus { NotRun, Pass, Warn, Fail }

    public sealed class CheckResult
    {
        public string Id;
        public string Title;
        public CheckStatus Status;
        public string Summary;
        public List<string> Details = new List<string>();
        /// <summary>A blocking failure cannot be acknowledged; the job cannot work until it is fixed.</summary>
        public bool Blocking;
        public bool Acknowledged;
        /// <summary>Text for the fix button, or null when there is nothing the tool can do itself.</summary>
        public string FixLabel;

        public bool AllowsRun => Status == CheckStatus.Pass || ((Status == CheckStatus.Warn || Status == CheckStatus.Fail) && !Blocking && Acknowledged);
        public override string ToString() => $"[{Status.ToString().ToUpperInvariant()}] {Title}: {Summary}";
    }

    /// <summary>
    /// Runs the pre-flight checks for a job. Checks only look; nothing is closed or changed unless
    /// ApplyFix is called for a check that offers a fix.
    /// </summary>
    public sealed class PreflightEngine
    {
        public const string Elevation = "elevation", LogFolder = "logfolder", Destination = "destination", Battery = "battery",
                            Apps = "apps", SessionsId = "sessions", SourceScan = "sourcescan", Cloud = "cloud", Efs = "efs", FreeSpaceId = "freespace";

        static readonly string[] Order = { Elevation, LogFolder, Destination, Battery, Apps, SessionsId, SourceScan, Cloud, Efs, FreeSpaceId };
        static readonly Dictionary<string, string> Titles = new Dictionary<string, string>
        {
            { Elevation, "Administrator rights and backup privilege" },
            { LogFolder, "Log folder writable" },
            { Destination, "Destination reachable and writable" },
            { Battery, "Power" },
            { Apps, "Open applications" },
            { SessionsId, "Other signed-in users" },
            { SourceScan, "Source scan" },
            { Cloud, "Cloud-only OneDrive files" },
            { Efs, "EFS-encrypted files" },
            { FreeSpaceId, "Free space at destination" },
        };

        readonly JobDefinition job;
        readonly string logDirectory;
        readonly Dictionary<string, CheckResult> results = new Dictionary<string, CheckResult>();
        readonly List<string> stoppedOneDrive = new List<string>();

        public ProfileInfo Profiles = ProfileInfo.FromSystem();
        /// <summary>Password for a UNC destination. Held in memory only; never written or logged.</summary>
        public string DestinationPassword;
        public event Action<CheckResult> CheckCompleted;
        public event Action<string> Activity;

        public ScanResult Scan { get; private set; }
        public WalkResult Walk { get; private set; }
        public bool OneDriveWasStopped => stoppedOneDrive.Count > 0;

        public PreflightEngine(JobDefinition job, string logDirectory)
        {
            this.job = job ?? throw new ArgumentNullException(nameof(job));
            this.logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
            foreach (var id in Order) results[id] = new CheckResult { Id = id, Title = Titles[id], Summary = "not run yet" };
        }

        public IReadOnlyList<CheckResult> Results => Order.Select(id => results[id]).ToList();
        public CheckResult this[string id] => results[id];

        /// <summary>Run is allowed when every check passed or was acknowledged; blocking failures cannot be acknowledged.</summary>
        public bool CanRun => results.Values.All(r => r.AllowsRun);

        public void RunAll(CancellationToken ct)
        {
            foreach (var id in Order)
            {
                if (ct.IsCancellationRequested) return;
                Run(id, ct);
            }
        }

        public CheckResult Run(string id, CancellationToken ct)
        {
            var r = new CheckResult { Id = id, Title = Titles[id] };
            Activity?.Invoke("Checking: " + r.Title);
            try
            {
                switch (id)
                {
                    case Elevation: CheckElevation(r); break;
                    case LogFolder: CheckLogFolder(r); break;
                    case Destination: CheckDestination(r); break;
                    case Battery: CheckBattery(r); break;
                    case Apps: CheckApps(r); break;
                    case SessionsId: CheckSessions(r); break;
                    case SourceScan: CheckSourceScan(r, ct); break;
                    case Cloud: CheckCloud(r); break;
                    case Efs: CheckEfs(r); break;
                    case FreeSpaceId: CheckFreeSpace(r); break;
                    default: throw new ArgumentException("Unknown check " + id);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                Set(r, CheckStatus.Fail, "The check itself failed: " + ex.Message);
            }
            results[id] = r;
            CheckCompleted?.Invoke(r);
            return r;
        }

        public bool Acknowledge(string id)
        {
            var r = results[id];
            if (r.Blocking || (r.Status != CheckStatus.Warn && r.Status != CheckStatus.Fail)) return false;
            r.Acknowledged = true;
            return true;
        }

        /// <summary>Applies the fix a check offers, then runs that check again.</summary>
        public CheckResult ApplyFix(string id, CancellationToken ct)
        {
            if (results[id].FixLabel == null) return results[id];
            var notes = new List<string>();
            var timeout = TimeSpan.FromSeconds(Math.Max(1, job.Preflight.AppCloseTimeoutSeconds));
            if (id == Apps)
            {
                int own = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                var apps = AppCloser.Find(job.Preflight.KnownApps, s => s == own);
                if (apps.Any(a => a.Name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)))
                {
                    Activity?.Invoke("Stopping OneDrive");
                    foreach (var p in OneDriveControl.Stop(notes))
                        if (!stoppedOneDrive.Contains(p, StringComparer.OrdinalIgnoreCase)) stoppedOneDrive.Add(p);
                    // helper processes of the client take a moment to follow it down
                    for (int i = 0; i < 40 && OneDriveControl.IsRunning() && !ct.IsCancellationRequested; i++) Thread.Sleep(250);
                    notes.Add(OneDriveControl.IsRunning() ? "OneDrive is still running." : "OneDrive stopped; it will be started again after the job.");
                    apps = apps.Where(a => !a.Name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)).ToList();
                }
                Activity?.Invoke($"Asking {apps.Count} application process(es) to close (force-close after {timeout.TotalSeconds:0} s)");
                var rep = AppCloser.Close(apps, timeout, ct);
                notes.Add($"{rep.ClosedGracefully} closed on request, {rep.ForceClosed} force-closed, {rep.StillRunning} still running.");
                notes.AddRange(rep.Notes);
            }
            else if (id == SessionsId)
            {
                var others = new HashSet<int>(Sessions.OtherUsers().Select(s => s.SessionId));
                var apps = AppCloser.Find(job.Preflight.KnownApps, s => others.Contains(s));
                Activity?.Invoke($"Closing {apps.Count} application process(es) of other users");
                var rep = AppCloser.Close(apps, TimeSpan.Zero, ct);
                notes.Add($"{rep.ForceClosed} force-closed, {rep.StillRunning} still running.");
                notes.AddRange(rep.Notes);
            }
            var r = Run(id, ct);
            r.Details.InsertRange(0, notes.Select(n => "Fix: " + n));
            return r;
        }

        /// <summary>Starts OneDrive again if the apps fix stopped it. Call when the job has ended.</summary>
        public List<string> RestartOneDrive()
        {
            var notes = new List<string>();
            if (stoppedOneDrive.Count > 0) OneDriveControl.Restart(stoppedOneDrive, notes);
            stoppedOneDrive.Clear();
            return notes;
        }

        // ---------------------------------------------------------------- individual checks

        static void Set(CheckResult r, CheckStatus status, string summary, bool blocking = false)
        {
            r.Status = status; r.Summary = summary; r.Blocking = blocking && status == CheckStatus.Fail;
        }

        void CheckElevation(CheckResult r)
        {
            bool elevated = SystemProbes.IsElevated();
            bool backup = Privileges.TryEnable("SeBackupPrivilege"), restore = Privileges.TryEnable("SeRestorePrivilege");
            r.Details.Add($"Elevated: {(elevated ? "yes" : "no")}; backup privilege: {(backup ? "yes" : "no")}; restore privilege: {(restore ? "yes" : "no")}");
            if (!job.Options.BackupMode)
                Set(r, CheckStatus.Warn, "Backup mode is switched off. Files this account has no permission to read will fail.");
            else if (elevated && backup && restore)
                Set(r, CheckStatus.Pass, "Running as administrator with backup privilege.");
            else
                Set(r, CheckStatus.Fail, elevated ? "This account does not hold the backup and restore privileges; Robocopy /B will refuse to start."
                                                  : "Not running as administrator; Robocopy /B will refuse to start. Start the tool with \"Run as administrator\".", blocking: true);
        }

        void CheckLogFolder(CheckResult r)
        {
            try { Directory.CreateDirectory(logDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            r.Details.Add(logDirectory);
            if (JobStore.IsWritable(logDirectory)) Set(r, CheckStatus.Pass, "Writable.");
            else Set(r, CheckStatus.Fail, "Cannot write to the log folder.", blocking: true);
        }

        void CheckDestination(CheckResult r)
        {
            string root = job.DestinationRoot;
            if (string.IsNullOrWhiteSpace(root)) { Set(r, CheckStatus.Fail, "No destination chosen.", true); return; }
            if (NetworkConnection.IsUnc(root))
            {
                if (NetworkConnection.ShareRoot(root) == null) { Set(r, CheckStatus.Fail, "A network destination must be \\\\server\\share or a folder below it.", true); return; }
                if (!string.IsNullOrEmpty(job.DestinationUserName))
                {
                    int rc = NetworkConnection.Connect(root, job.DestinationUserName, DestinationPassword, out string message);
                    if (rc != 0) { Set(r, CheckStatus.Fail, $"Could not connect to {NetworkConnection.ShareRoot(root)} as {job.DestinationUserName}: {message} (error {rc})", true); return; }
                    r.Details.Add($"Connected to {NetworkConnection.ShareRoot(root)} as {job.DestinationUserName}.");
                }
            }
            else if (root.Length < 3 || root[1] != ':')
            { Set(r, CheckStatus.Fail, "The destination must be a drive folder (E:\\Export) or a network path (\\\\server\\share).", true); return; }

            string probe = Path.Combine(PathUtil.Trim(root), ".dtt-preflight-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(PathUtil.Long(PathUtil.Trim(root)));
                Directory.CreateDirectory(PathUtil.Long(probe));
                RoundTrip(Path.Combine(probe, "write-test.txt"));
                r.Details.Add("Wrote, read back and deleted a test file.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TryDelete(probe);
                Set(r, CheckStatus.Fail, "Cannot write to the destination: " + ex.Message, true);
                return;
            }

            string fs = SystemProbes.FileSystemOf(root);
            if (fs != null) r.Details.Add("File system: " + fs);
            try
            {
                string deep = probe;
                while (deep.Length < 300) deep = Path.Combine(deep, "long-path-test-" + new string('x', 30));
                Directory.CreateDirectory(PathUtil.Long(deep));
                string file = Path.Combine(deep, "write-test.txt");
                RoundTrip(file);
                r.Details.Add($"Wrote, read back and deleted a test file with a {file.Length}-character path.");
                Set(r, CheckStatus.Pass, "Reachable and writable, including paths longer than 260 characters.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Set(r, CheckStatus.Fail, "The destination is writable, but not for paths longer than 260 characters: " + ex.Message + " Files with long paths will fail.");
            }
            finally { TryDelete(probe); }
        }

        static void RoundTrip(string file)
        {
            string text = "data transfer tool pre-flight " + Guid.NewGuid();
            File.WriteAllText(PathUtil.Long(file), text);
            if (File.ReadAllText(PathUtil.Long(file)) != text) throw new IOException("The test file read back differently from what was written.");
            File.Delete(PathUtil.Long(file));
        }

        static void TryDelete(string dir)
        {
            try { if (Directory.Exists(PathUtil.Long(dir))) Directory.Delete(PathUtil.Long(dir), true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        void CheckBattery(CheckResult r)
        {
            bool? battery = SystemProbes.OnBattery(out int percent);
            if (battery == true) Set(r, CheckStatus.Warn, $"Running on battery{(percent >= 0 ? $" ({percent}%)" : "")}. Connect the charger before a long copy.");
            else if (battery == false) Set(r, CheckStatus.Pass, percent >= 0 ? $"On mains power (battery {percent}%)." : "On mains power.");
            else Set(r, CheckStatus.Warn, "Power state unknown.");
        }

        void CheckApps(CheckResult r)
        {
            int own = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            var apps = AppCloser.Find(job.Preflight.KnownApps, s => s == own);
            if (apps.Count == 0)
            {
                Set(r, CheckStatus.Pass, stoppedOneDrive.Count > 0 ? "None open. OneDrive is stopped and will be started again after the job." : "None open.");
                return;
            }
            foreach (var g in apps.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key))
            {
                string titles = string.Join("; ", g.Where(a => a.HasWindow && !string.IsNullOrEmpty(a.WindowTitle)).Select(a => a.WindowTitle).Distinct().Take(3));
                r.Details.Add($"{g.Key}: {g.Count()} process(es){(titles.Length > 0 ? " - " + titles : "")}");
            }
            Set(r, CheckStatus.Warn, $"{apps.Select(a => a.Name.ToLowerInvariant()).Distinct().Count()} application(s) open. Open files may be locked and unsaved work is not copied.");
            r.FixLabel = $"Close applications (force-close after {job.Preflight.AppCloseTimeoutSeconds} s)";
        }

        void CheckSessions(CheckResult r)
        {
            var others = Sessions.OtherUsers();
            if (others.Count == 0) { Set(r, CheckStatus.Pass, "No other user is signed in."); return; }
            var ids = new HashSet<int>(others.Select(s => s.SessionId));
            var apps = AppCloser.Find(job.Preflight.KnownApps, s => ids.Contains(s));
            foreach (var s in others)
                r.Details.Add($"{s}: {apps.Count(a => a.SessionId == s.SessionId)} known application process(es) open");
            Set(r, CheckStatus.Warn, $"{others.Count} other user(s) signed in. Their open files may be locked.");
            if (apps.Count > 0) r.FixLabel = "Close their applications";
        }

        void CheckSourceScan(CheckResult r, CancellationToken ct)
        {
            Scan = null; Walk = null;
            if (job.Sources.Count == 0) { Set(r, CheckStatus.Fail, "No folders selected.", true); return; }
            var missing = job.Sources.Where(s => !Directory.Exists(PathUtil.Long(PathUtil.Trim(s.Path)))).Select(s => s.Path).ToList();
            if (missing.Count > 0)
            {
                r.Details.AddRange(missing);
                Set(r, CheckStatus.Fail, $"{missing.Count} selected folder(s) do not exist.", true);
                return;
            }

            Activity?.Invoke("Scanning with Robocopy /L");
            var engine = new CopyEngine(job, logDirectory) { Profiles = Profiles };
            engine.Progress += p => Activity?.Invoke($"Scanning: {p.FilesProcessed} files - {p.CurrentFolder}");
            var scan = engine.Scan(ct);
            if (scan.Cancelled) { Set(r, CheckStatus.Fail, "Scan cancelled.", true); return; }
            if (scan.FatalError != null) { Set(r, CheckStatus.Fail, "Robocopy could not scan: " + scan.FatalError, true); return; }

            Activity?.Invoke("Looking for unreadable folders, cloud-only and encrypted files");
            long folders = 0;
            var walk = SourceWalker.Walk(job, Profiles, ct, d => { if (++folders % 500 == 0) Activity?.Invoke($"Walking: {folders} folders - {d}"); });
            if (walk.Cancelled) { Set(r, CheckStatus.Fail, "Scan cancelled.", true); return; }
            Scan = scan; Walk = walk;

            var t = scan.Totals;
            r.Details.Add($"To copy: {t.FilesCopied:N0} files, {Size(t.BytesCopied)}.");
            r.Details.Add($"In the selected folders: {t.FilesTotal:N0} files, {Size(t.BytesTotal)}; {t.FilesSkipped:N0} are excluded or already at the destination.");
            r.Details.Add($"Longest source path: {walk.LongestPath} characters. Largest file: {Size(walk.LargestFile)}.");
            if (walk.Files != t.FilesTotal)
                r.Details.Add($"Note: the tool's own walk counted {walk.Files:N0} files, Robocopy {t.FilesTotal:N0} (files may have changed in between).");

            long unreadable = Math.Max(scan.UnreadableFolders, walk.Unreadable.Count);
            if (unreadable > 0)
            {
                foreach (var u in walk.Unreadable.Take(50)) r.Details.Add("Unreadable: " + u);
                if (walk.Unreadable.Count > 50) r.Details.Add($"... and {walk.Unreadable.Count - 50} more");
                if (walk.Unreadable.Count == 0) r.Details.Add("Robocopy reported unreadable folders that the tool's own walk could read; they are not named.");
                Set(r, CheckStatus.Warn, $"{t.FilesCopied:N0} files, {Size(t.BytesCopied)} to copy. {unreadable} folder(s) cannot be read and will be missing.");
            }
            else Set(r, CheckStatus.Pass, $"{t.FilesCopied:N0} files, {Size(t.BytesCopied)} to copy. All folders readable.");
        }

        void CheckCloud(CheckResult r)
        {
            if (Walk == null || Scan == null) { Set(r, CheckStatus.Fail, "Needs a completed source scan.", true); return; }
            if (Walk.CloudFiles == 0) { Set(r, CheckStatus.Pass, "None found."); return; }
            foreach (var s in Walk.CloudSamples.Take(20)) r.Details.Add(s);
            if (Walk.CloudFiles > 20) r.Details.Add($"... and {Walk.CloudFiles - 20:N0} more");
            if (!job.Options.ExcludeCloudOnly)
                Set(r, CheckStatus.Warn, $"{Walk.CloudFiles:N0} cloud-only files ({Size(Walk.CloudBytes)}) will be downloaded and copied because the exclusion is switched off.");
            else if (Scan.Totals.FilesCopied > Walk.EligibleFiles)
                // Robocopy wants to copy more than the files that are local: its /XA:O did not catch them
                Set(r, CheckStatus.Fail, $"{Walk.CloudFiles:N0} cloud-only files ({Size(Walk.CloudBytes)}) found, and Robocopy on this Windows version does not exclude them: it would download them.");
            else
                Set(r, CheckStatus.Warn, $"{Walk.CloudFiles:N0} cloud-only files ({Size(Walk.CloudBytes)}) are NOT copied. They exist only in OneDrive.");
        }

        void CheckEfs(CheckResult r)
        {
            if (Walk == null) { Set(r, CheckStatus.Fail, "Needs a completed source scan.", true); return; }
            if (Walk.EfsFiles == 0) { Set(r, CheckStatus.Pass, "None found."); return; }
            foreach (var s in Walk.EfsSamples.Take(20)) r.Details.Add(s);
            if (Walk.EfsFiles > 20) r.Details.Add($"... and {Walk.EfsFiles - 20:N0} more");
            Set(r, CheckStatus.Warn, $"{Walk.EfsFiles:N0} EFS-encrypted files ({Size(Walk.EfsBytes)}). They can only be read by the user who encrypted them and are likely to fail.");
        }

        void CheckFreeSpace(CheckResult r)
        {
            if (Scan == null || Walk == null) { Set(r, CheckStatus.Fail, "Needs a completed source scan.", true); return; }
            string dir = PathUtil.Trim(job.DestinationRoot ?? "");
            long? free = null;
            try
            {
                while (!string.IsNullOrEmpty(dir) && !Directory.Exists(PathUtil.Long(dir))) dir = Path.GetDirectoryName(dir);
                if (!string.IsNullOrEmpty(dir)) free = SystemProbes.FreeSpace(dir);
            }
            catch (ArgumentException) { }
            string fs = string.IsNullOrEmpty(job.DestinationRoot) ? null : SystemProbes.FileSystemOf(job.DestinationRoot);
            EvaluateFreeSpace(r, Scan.Totals.BytesCopied, free, fs, Walk.LargestFile, Walk.LargestFilePath);
        }

        /// <summary>Pure decision logic for the free-space check, separated so it can be tested.</summary>
        public static void EvaluateFreeSpace(CheckResult r, long required, long? free, string fileSystem, long largestFile, string largestFilePath)
        {
            const long FourGb = 4L * 1024 * 1024 * 1024;
            var status = CheckStatus.Pass;
            string summary;
            r.Details.Add($"Needed: {Size(required)}. Free: {(free.HasValue ? Size(free.Value) : "unknown")}. File system: {fileSystem ?? "unknown"}.");
            if (!free.HasValue) { status = CheckStatus.Warn; summary = $"Free space could not be read; {Size(required)} is needed."; }
            else if (required > free.Value) { status = CheckStatus.Fail; summary = $"Not enough space: {Size(required)} needed, {Size(free.Value)} free. Short by {Size(required - free.Value)}."; }
            else if (required + required / 20 > free.Value) { status = CheckStatus.Warn; summary = $"Very tight: {Size(required)} needed, {Size(free.Value)} free."; }
            else summary = $"{Size(required)} needed, {Size(free.Value)} free.";

            if (string.Equals(fileSystem, "FAT32", StringComparison.OrdinalIgnoreCase) || string.Equals(fileSystem, "FAT", StringComparison.OrdinalIgnoreCase))
            {
                if (largestFile >= FourGb)
                {
                    status = CheckStatus.Fail;
                    summary += $" The destination is {fileSystem}, which cannot hold files of 4 GB or more; the largest selected file is {Size(largestFile)}.";
                    r.Details.Add("Largest file: " + largestFilePath);
                }
                else
                {
                    if (status == CheckStatus.Pass) status = CheckStatus.Warn;
                    summary += $" The destination is {fileSystem}: no file of 4 GB or more, and timestamps are kept to 2 seconds only.";
                }
            }
            Set(r, status, summary);
        }

        public static string Size(long bytes)
        {
            string[] units = { "bytes", "KB", "MB", "GB", "TB" };
            double v = bytes; int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? $"{bytes:N0} bytes" : $"{v:0.##} {units[u]}";
        }
    }
}
