using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataTransfer.Core
{
    public enum JobOutcome { Completed, CompletedWithErrors, Cancelled, DestinationFull, Failed }

    public sealed class ScanResult
    {
        /// <summary>FilesCopied/BytesCopied here mean "would be copied".</summary>
        public Counts Totals = new Counts();
        /// <summary>Errors Robocopy named during the scan (it does not name unreadable folders).</summary>
        public List<FileFailure> Unreadable = new List<FileFailure>();
        /// <summary>Folders the scan could not read. Robocopy /L only counts them.</summary>
        public long UnreadableFolders;
        public bool Cancelled;
        public string FatalError;
    }

    /// <summary>
    /// Runs a job: one Robocopy per source, watched for cancel, destination loss and disk full,
    /// followed by a retry of locked files. Never prompts. Run it on a background thread; events
    /// are raised on that thread.
    /// </summary>
    public sealed class CopyEngine
    {
        enum AttemptEnd { Finished, Cancelled, DiskFull, DestinationLost, Fatal }

        sealed class Attempt
        {
            public AttemptEnd End;
            public RobocopyLogParser Parser;
            public int ExitCode;
            public string Detail;
        }

        readonly JobDefinition job;
        readonly Stopwatch clock = new Stopwatch();
        readonly RateEstimator rate = new RateEstimator();
        long doneFiles, doneBytesLog, failedBefore;
        long? baselineFree;
        long freeDelta;
        TimeSpan lastFreeCheck = TimeSpan.FromSeconds(-10), lastReport = TimeSpan.FromSeconds(-10);
        int sourceIndex;

        public event Action<ProgressSnapshot> Progress;
        public event Action<string> Message;
        /// <summary>Called whenever the job's saved state should be updated (wired to JobStore.Save).</summary>
        public Action<JobDefinition> SaveState = _ => { };
        public ProfileInfo Profiles = ProfileInfo.FromSystem();
        public string LogDirectory;
        /// <summary>From the pre-flight scan; enables percentage and ETA.</summary>
        public long? ExpectedFiles, ExpectedBytes;
        /// <summary>Loop interval while Robocopy runs.</summary>
        public int TickMilliseconds = 200;
        /// <summary>Minimum time between two Progress events.</summary>
        public int ReportIntervalMilliseconds = 400;

        public CopyEngine(JobDefinition job, string logDirectory)
        {
            this.job = job ?? throw new ArgumentNullException(nameof(job));
            LogDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
            if (string.IsNullOrWhiteSpace(job.DestinationRoot)) throw new ArgumentException("Job has no destination.");
        }

        // ---------------------------------------------------------------- public operations

        public JobOutcome Run(CancellationToken ct)
        {
            clock.Restart();
            job.State = JobState.Running;
            job.StartedUtc = job.StartedUtc ?? DateTime.UtcNow;
            job.FinishedUtc = null;
            SaveState(job);
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED); // no sleep during the run
            try
            {
                if (!EnsureDestination(ct)) return Finish(JobOutcome.Cancelled);
                try { Directory.CreateDirectory(PathUtil.Long(job.DestinationRoot)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                baselineFree = FreeSpace();

                for (int i = 0; i < job.Sources.Count; i++)
                {
                    var src = job.Sources[i];
                    sourceIndex = i + 1;
                    if (src.Status == SourceStatus.Completed || src.Status == SourceStatus.CompletedWithErrors)
                    {
                        failedBefore += src.Failures.Count;
                        continue; // main pass already done in an earlier session
                    }
                    var end = RunSource(src, ct);
                    if (end != AttemptEnd.Finished) return Finish(Map(end));
                    failedBefore += src.Failures.Count;
                }

                var locked = job.AllFailures().Where(f => f.Category == FailureCategory.Locked && !f.IsDirectory).ToList();
                if (locked.Count > 0)
                {
                    Say($"{locked.Count} locked file(s); retrying in {job.Options.LockedRetryDelaySeconds} s");
                    Report(EnginePhase.WaitingToRetryLocked, null, force: true);
                    if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(job.Options.LockedRetryDelaySeconds))) return Finish(JobOutcome.Cancelled);
                    var end = RetryCore(locked, ct);
                    if (end != AttemptEnd.Finished) return Finish(Map(end));
                }
                return Finish(job.AllFailures().Any() ? JobOutcome.CompletedWithErrors : JobOutcome.Completed);
            }
            finally
            {
                Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
            }
        }

        /// <summary>Retries recorded failures (all of them, or those the filter accepts).</summary>
        public JobOutcome RetryFailed(CancellationToken ct, Func<FileFailure, bool> filter = null)
        {
            clock.Restart();
            var list = job.AllFailures().Where(f => filter == null || filter(f)).ToList();
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED);
            try
            {
                if (!EnsureDestination(ct)) return Finish(JobOutcome.Cancelled);
                baselineFree = FreeSpace();
                var end = list.Count == 0 ? AttemptEnd.Finished : RetryCore(list, ct);
                if (end != AttemptEnd.Finished) return Finish(Map(end));
                return Finish(job.AllFailures().Any() ? JobOutcome.CompletedWithErrors : JobOutcome.Completed);
            }
            finally { Native.SetThreadExecutionState(Native.ES_CONTINUOUS); }
        }

        /// <summary>Robocopy /L with the real flag set: what would be copied, and what cannot be read.</summary>
        public ScanResult Scan(CancellationToken ct)
        {
            clock.Restart();
            var result = new ScanResult();
            for (int i = 0; i < job.Sources.Count; i++)
            {
                var src = job.Sources[i];
                sourceIndex = i + 1;
                var a = RunMonitored(log => RobocopyCommandBuilder.Build(job, src, log, true, Profiles), EnginePhase.Scanning, "scan", ct, watchDestination: false);
                doneFiles += a.Parser.FilesStarted; doneBytesLog += a.Parser.BytesStarted;
                if (a.End == AttemptEnd.Cancelled) { result.Cancelled = true; return result; }
                if (a.End == AttemptEnd.Fatal) { result.FatalError = a.Detail; return result; }
                result.Totals.Add(a.Parser.ToCounts());
                result.Unreadable.AddRange(a.Parser.Failures);
                if (a.Parser.Dirs != null) result.UnreadableFolders += a.Parser.Dirs[4];
            }
            return result;
        }

        // ---------------------------------------------------------------- one source

        AttemptEnd RunSource(SourceSelection src, CancellationToken ct)
        {
            var end = CopySource(src, ct);
            return job.Options.KeepHiddenAttributes ? end : MakeVisible(src, end, ct);
        }

        /// <summary>
        /// Clears Hidden and System at the destination of one folder job. Robocopy copies a
        /// folder's attributes, so the copy of a drive root would otherwise be invisible in Explorer.
        /// After an interrupted pass only the top folders are cleared (quick); the whole tree is
        /// done when the folder job has finished.
        /// </summary>
        AttemptEnd MakeVisible(SourceSelection src, AttemptEnd end, CancellationToken ct)
        {
            string dest;
            try { dest = job.DestinationFor(src.Path); } catch (ArgumentException) { return end; }
            var r = Unhide.ClearChain(dest, job.DestinationRoot);
            if (end != AttemptEnd.Finished) return end;
            Unhide.ClearTree(dest, ct, r);
            if (r.Cancelled)
            {
                // not finished: the next run goes over this folder job again (files already there are skipped)
                Interrupted(src, src.Totals.FilesCopied, src.Totals.BytesCopied, src.CountsApproximate);
                return AttemptEnd.Cancelled;
            }
            if (r.Cleared > 0 || r.Failed > 0)
                Say($"Made {r.Cleared:N0} hidden item(s) visible at the destination" + (r.Failed > 0 ? $"; {r.Failed:N0} could not be changed ({r.FirstError})" : ""));
            return end;
        }

        AttemptEnd CopySource(SourceSelection src, CancellationToken ct)
        {
            // copied counts carry over from an interrupted earlier session
            long copiedFiles = src.Totals.FilesCopied, copiedBytes = src.Totals.BytesCopied;
            bool approximate = src.CountsApproximate;
            int bogusFinishes = 0;
            src.Status = SourceStatus.Running;
            SaveState(job);

            while (true)
            {
                if (!EnsureDestination(ct)) { Interrupted(src, copiedFiles, copiedBytes, approximate); return AttemptEnd.Cancelled; }
                Say($"Copying {src.Path}");
                var a = RunMonitored(log => RobocopyCommandBuilder.Build(job, src, log, false, Profiles), EnginePhase.Copying, "copy", ct, watchDestination: true);
                var p = a.Parser;
                doneFiles += p.FilesStarted; doneBytesLog += p.BytesStarted;
                if (p.HasSummary) { copiedFiles += p.Files[1]; copiedBytes += p.Bytes[1]; }
                else
                {
                    // killed: no summary, so estimate from the file lines (over-counts files that were in flight)
                    copiedFiles += Math.Max(0, p.FilesStarted - p.Failures.Count(f => !f.IsDirectory));
                    copiedBytes += Math.Max(0, p.BytesStarted - p.Failures.Sum(f => f.Size));
                    approximate = true;
                }

                switch (a.End)
                {
                    case AttemptEnd.Cancelled:
                    case AttemptEnd.DiskFull:
                        if (a.End == AttemptEnd.DiskFull) Say("Destination is full; stopping.");
                        Interrupted(src, copiedFiles, copiedBytes, approximate);
                        return a.End;
                    case AttemptEnd.Fatal:
                        src.Status = SourceStatus.Failed;
                        src.LastExitCode = a.ExitCode;
                        job.LastMessage = a.Detail;
                        Say("Robocopy could not run: " + a.Detail);
                        SaveState(job);
                        return AttemptEnd.Fatal;
                    case AttemptEnd.DestinationLost:
                        Say("Destination became unreachable; pausing.");
                        continue;
                }

                var failures = DropFalseFailures(p.Failures);
                // finished, but only because every remaining file failed against a missing destination
                if (failures.Any(f => f.Category == FailureCategory.DestinationUnavailable) && bogusFinishes++ < 3) continue;

                var t = p.ToCounts();
                t.FilesFailed = failures.Count(f => !f.IsDirectory);
                t.BytesFailed = failures.Where(f => !f.IsDirectory).Sum(f => f.Size);
                t.FilesCopied = Math.Min(copiedFiles, Math.Max(0, t.FilesTotal - t.FilesFailed));
                t.BytesCopied = Math.Min(copiedBytes, Math.Max(0, t.BytesTotal - t.BytesFailed));
                t.FilesSkipped = Math.Max(0, t.FilesTotal - t.FilesCopied - t.FilesFailed);
                t.BytesSkipped = Math.Max(0, t.BytesTotal - t.BytesCopied - t.BytesFailed);
                src.Totals = t;
                src.CountsApproximate = approximate;
                src.Failures = failures;
                src.LastExitCode = a.ExitCode;
                src.Status = failures.Count > 0 ? SourceStatus.CompletedWithErrors : SourceStatus.Completed;
                SaveState(job);
                Say($"Finished {src.Path}: {t}");
                return AttemptEnd.Finished;
            }
        }

        void Interrupted(SourceSelection src, long copiedFiles, long copiedBytes, bool approximate)
        {
            src.Status = SourceStatus.Pending;
            src.Totals.FilesCopied = copiedFiles;
            src.Totals.BytesCopied = copiedBytes;
            src.CountsApproximate = approximate;
            SaveState(job);
        }

        // ---------------------------------------------------------------- retry

        AttemptEnd RetryCore(List<FileFailure> toRetry, CancellationToken ct)
        {
            foreach (var group in toRetry.GroupBy(f => job.OwnerOf(f.SourcePath)).Where(g => g.Key != null).ToList())
            {
                var src = group.Key;
                sourceIndex = job.Sources.IndexOf(src) + 1;
                var commands = new List<Func<string, string>>();
                foreach (var dir in group.Where(f => !f.IsDirectory).GroupBy(f => Path.GetDirectoryName(f.SourcePath), StringComparer.OrdinalIgnoreCase))
                    foreach (var cmd in RobocopyCommandBuilder.BuildFileRetry(job, dir.Key, dir.Select(f => Path.GetFileName(f.SourcePath))))
                    { string c = cmd; commands.Add(log => RobocopyCommandBuilder.WithLog(c, log)); }
                foreach (var d in group.Where(f => f.IsDirectory))
                { string folder = d.SourcePath; commands.Add(log => RobocopyCommandBuilder.BuildTree(job, src, folder, log, false, Profiles)); }

                var still = new Dictionary<string, FileFailure>(StringComparer.OrdinalIgnoreCase);
                var added = new Counts();
                foreach (var command in commands)
                {
                    while (true)
                    {
                        if (!EnsureDestination(ct)) return AttemptEnd.Cancelled;
                        var a = RunMonitored(command, EnginePhase.RetryingFailed, "retry", ct, watchDestination: true);
                        doneFiles += a.Parser.FilesStarted; doneBytesLog += a.Parser.BytesStarted;
                        if (a.End == AttemptEnd.DestinationLost) continue;
                        if (a.End == AttemptEnd.Cancelled || a.End == AttemptEnd.DiskFull) return a.End;
                        if (a.End == AttemptEnd.Fatal) { Say("Retry could not run: " + a.Detail); break; }
                        foreach (var f in DropFalseFailures(a.Parser.Failures)) still[f.SourcePath] = f;
                        added.Add(a.Parser.ToCounts());
                        break;
                    }
                }

                var retried = new HashSet<string>(group.Select(f => f.SourcePath), StringComparer.OrdinalIgnoreCase);
                bool folderRetried = group.Any(f => f.IsDirectory);
                var merged = src.Failures.Where(f => !retried.Contains(f.SourcePath)).ToDictionary(f => f.SourcePath, StringComparer.OrdinalIgnoreCase);
                foreach (var f in still.Values) merged[f.SourcePath] = f;
                src.Failures = merged.Values.ToList();

                var t = src.Totals;
                t.FilesCopied += added.FilesCopied; t.BytesCopied += added.BytesCopied;
                if (folderRetried)
                {
                    // a folder that could not be read was never counted; its content is new to the totals
                    long known = group.Count(f => !f.IsDirectory);
                    t.FilesTotal += Math.Max(0, added.FilesTotal - known);
                    t.BytesTotal = Math.Max(t.BytesTotal, t.BytesCopied);
                }
                t.FilesFailed = src.Failures.Count(f => !f.IsDirectory);
                t.BytesFailed = src.Failures.Where(f => !f.IsDirectory).Sum(f => f.Size);
                t.FilesTotal = Math.Max(t.FilesTotal, t.FilesCopied + t.FilesFailed);
                t.FilesSkipped = Math.Max(0, t.FilesTotal - t.FilesCopied - t.FilesFailed);
                t.BytesSkipped = Math.Max(0, t.BytesTotal - t.BytesCopied - t.BytesFailed);
                src.Status = src.Failures.Count > 0 ? SourceStatus.CompletedWithErrors : SourceStatus.Completed;
                SaveState(job);
                // a retried folder is created by Robocopy with its source attributes again
                if (folderRetried && !job.Options.KeepHiddenAttributes)
                    foreach (var d in group.Where(f => f.IsDirectory))
                        try { Unhide.ClearTree(job.DestinationFor(d.SourcePath), ct); } catch (ArgumentException) { }
                Say($"Retry for {src.Path}: {retried.Count - still.Keys.Count(retried.Contains)} of {retried.Count} resolved");
            }
            return AttemptEnd.Finished;
        }

        /// <summary>
        /// Removes failures that did not last: a file that failed once and succeeded on Robocopy's
        /// own retry still has an error line, so the destination is checked for each one.
        /// </summary>
        List<FileFailure> DropFalseFailures(IEnumerable<FileFailure> failures)
        {
            var keep = new List<FileFailure>();
            foreach (var f in failures)
            {
                if (!f.IsDirectory && job.OwnerOf(f.SourcePath) != null)
                {
                    try
                    {
                        var s = new FileInfo(PathUtil.Long(f.SourcePath));
                        var d = new FileInfo(PathUtil.Long(job.DestinationFor(f.SourcePath)));
                        if (s.Exists && d.Exists && s.Length == d.Length &&
                            Math.Abs((s.LastWriteTimeUtc - d.LastWriteTimeUtc).TotalSeconds) <= 2) continue;
                        if (f.Size == 0 && s.Exists) f.Size = s.Length;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                keep.Add(f);
            }
            return keep;
        }

        // ---------------------------------------------------------------- one Robocopy process

        Attempt RunMonitored(Func<string, string> argumentsFor, EnginePhase phase, string tag, CancellationToken ct, bool watchDestination)
        {
            string log = Path.Combine(LogDirectory, tag == "copy" ? JobLogs.CopyLog : tag == "retry" ? JobLogs.RetryLog : tag == "scan" ? JobLogs.ScanLog : tag + ".log");
            using (var run = new RobocopyRun(argumentsFor(log), log))
            {
                var a = new Attempt { Parser = run.Parser };
                run.Start();
                var lastProbe = clock.Elapsed;
                int suspectsSeen = 0;
                var poll = TimeSpan.FromSeconds(Math.Max(1, job.Options.DestinationPollSeconds));
                while (!run.HasExited)
                {
                    run.Pump();
                    Report(phase, run.Parser);
                    if (ct.IsCancellationRequested) { run.Kill(); run.Finish(); a.End = AttemptEnd.Cancelled; return a; }
                    if (watchDestination)
                    {
                        if (run.Parser.DiskFullErrors > 0) { run.Kill(); run.Finish(); a.End = AttemptEnd.DiskFull; return a; }
                        bool suspect = run.Parser.DestinationSuspectErrors > suspectsSeen;
                        if (suspect || clock.Elapsed - lastProbe >= poll)
                        {
                            suspectsSeen = run.Parser.DestinationSuspectErrors;
                            lastProbe = clock.Elapsed;
                            if (!DestinationReachable()) { run.Kill(); run.Finish(); a.End = AttemptEnd.DestinationLost; return a; }
                        }
                    }
                    Thread.Sleep(TickMilliseconds);
                }
                run.Finish();
                Report(phase, run.Parser, force: true);
                a.ExitCode = run.ExitCode;
                if (watchDestination && run.Parser.DiskFullErrors > 0) { a.End = AttemptEnd.DiskFull; return a; }
                if (a.ExitCode >= 16 || a.ExitCode < 0 || !run.Parser.HasSummary)
                {
                    if (watchDestination && !DestinationReachable()) { a.End = AttemptEnd.DestinationLost; return a; }
                    a.End = AttemptEnd.Fatal;
                    a.Detail = run.Parser.FatalError ?? (run.ConsoleOutput.Trim().Length > 0 ? run.ConsoleOutput.Trim() : $"Robocopy exit code {a.ExitCode}");
                    return a;
                }
                if (watchDestination && run.Parser.DestinationSuspectErrors > 0 && !DestinationReachable()) { a.End = AttemptEnd.DestinationLost; return a; }
                a.End = AttemptEnd.Finished;
                return a;
            }
        }

        // ---------------------------------------------------------------- destination

        /// <summary>Blocks while the destination is unreachable. False when cancelled.</summary>
        bool EnsureDestination(CancellationToken ct)
        {
            if (DestinationReachable()) return !ct.IsCancellationRequested;
            Say("Destination unreachable; waiting for it to return.");
            rate.Reset();
            while (true)
            {
                Report(EnginePhase.PausedDestinationUnavailable, null, force: true);
                if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Max(1, job.Options.DestinationPollSeconds)))) return false;
                if (DestinationReachable()) { Say("Destination is back; continuing."); rate.Reset(); return true; }
            }
        }

        /// <summary>
        /// The destination root, or (before the first run creates it) its volume, can be reached.
        /// A dead network path can block for a long time, so the check is bounded.
        /// </summary>
        bool DestinationReachable()
        {
            string root = job.DestinationRoot;
            bool seen = rootEverSeen;
            var probe = Task.Run(() =>
            {
                if (Directory.Exists(PathUtil.Long(PathUtil.Trim(root)))) return 2;
                // before the first run has created the folder, its volume being there is enough
                string volume = Path.GetPathRoot(root);
                return !seen && !string.IsNullOrEmpty(volume) && Directory.Exists(volume) ? 1 : 0;
            });
            int state;
            try { state = probe.Wait(TimeSpan.FromSeconds(8)) ? probe.Result : 0; }
            catch (AggregateException) { state = 0; }
            if (state == 2) rootEverSeen = true;
            return state > 0;
        }
        bool rootEverSeen;

        long? FreeSpace()
        {
            try
            {
                string dir = job.DestinationRoot.TrimEnd('\\') + "\\";
                while (!Directory.Exists(dir))
                {
                    string parent = Path.GetDirectoryName(dir.TrimEnd('\\'));
                    if (string.IsNullOrEmpty(parent)) return null;
                    dir = parent.TrimEnd('\\') + "\\";
                }
                return Native.GetDiskFreeSpaceEx(dir, out ulong free, out _, out _) ? (long?)free : null;
            }
            catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------- reporting

        void Report(EnginePhase phase, RobocopyLogParser parser, bool force = false)
        {
            var now = clock.Elapsed;
            if (!force && (now - lastReport).TotalMilliseconds < ReportIntervalMilliseconds) return;
            lastReport = now;
            if ((force || (now - lastFreeCheck).TotalSeconds >= 1) && phase != EnginePhase.Scanning && phase != EnginePhase.PausedDestinationUnavailable)
            {
                lastFreeCheck = now;
                long? free = FreeSpace();
                if (free.HasValue && baselineFree.HasValue) freeDelta = Math.Max(freeDelta, baselineFree.Value - free.Value);
            }
            long bytesLog = doneBytesLog + (parser?.BytesStarted ?? 0);
            bool useFree = baselineFree.HasValue && phase != EnginePhase.Scanning;
            long bytes = useFree ? freeDelta : bytesLog;
            if (ExpectedBytes.HasValue) bytes = Math.Min(bytes, ExpectedBytes.Value);
            if (phase == EnginePhase.Copying || phase == EnginePhase.RetryingFailed) rate.Add(now, bytes);

            Progress?.Invoke(new ProgressSnapshot
            {
                Phase = phase,
                SourceIndex = sourceIndex,
                SourceCount = job.Sources.Count,
                CurrentSource = sourceIndex >= 1 && sourceIndex <= job.Sources.Count ? job.Sources[sourceIndex - 1].Path : null,
                CurrentFolder = parser?.CurrentFolder,
                FilesProcessed = doneFiles + (parser?.FilesStarted ?? 0),
                FilesFailed = failedBefore + (parser?.Failures.Count ?? 0),
                BytesCopied = bytes,
                BytesFromLog = bytesLog,
                BytesFromFreeSpace = useFree,
                TotalFiles = ExpectedFiles,
                TotalBytes = ExpectedBytes,
                BytesPerSecond = rate.BytesPerSecond,
                Elapsed = now,
                Eta = phase == EnginePhase.Copying ? rate.Eta(bytes, ExpectedBytes) : null
            });
        }

        void Say(string text)
        {
            job.LastMessage = text;
            Message?.Invoke(text);
        }

        static JobOutcome Map(AttemptEnd end)
        {
            switch (end)
            {
                case AttemptEnd.Cancelled: return JobOutcome.Cancelled;
                case AttemptEnd.DiskFull: return JobOutcome.DestinationFull;
                default: return JobOutcome.Failed;
            }
        }

        JobOutcome Finish(JobOutcome outcome)
        {
            switch (outcome)
            {
                case JobOutcome.Cancelled: job.State = JobState.Cancelled; break;
                case JobOutcome.DestinationFull: job.State = JobState.DestinationFull; break;
                case JobOutcome.Failed: job.State = JobState.Failed; break;
                default: job.State = JobState.Completed; job.FinishedUtc = DateTime.UtcNow; break;
            }
            SaveState(job);
            Report(EnginePhase.Finished, null, force: true);
            return outcome;
        }
    }
}
