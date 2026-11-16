using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DataTransfer.Core;
using NUnit.Framework;

namespace DataTransfer.Core.Tests
{
    /// <summary>
    /// Runs the real Robocopy against generated trees in a temporary folder. /B is used only when
    /// the test process is elevated. Tests that need a virtual disk are skipped when not elevated.
    /// </summary>
    [TestFixture, Category("Integration"), NonParallelizable]
    public class EngineTests
    {
        static readonly bool Elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        string root, bigSrc;
        int bigCount; long bigBytes;

        [OneTimeSetUp]
        public void CreateTrees()
        {
            string baseDir = Environment.GetEnvironmentVariable("DTT_TEST_ROOT") ?? Path.GetTempPath();
            root = Path.Combine(baseDir, "DttM2_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            bigSrc = Path.Combine(root, "big");
            var rng = new Random(42);
            for (int i = 0; i < 5000; i++) Write(Path.Combine(bigSrc, $"d{i / 250:00}", $"f_{i:0000}.dat"), 100_000, rng);
            for (int i = 0; i < 6; i++) Write(Path.Combine(bigSrc, "large", $"big_{i}.bin"), 80L << 20, rng);
            bigCount = 5006; bigBytes = 5000L * 100_000 + 6 * (80L << 20);
        }

        [OneTimeTearDown]
        public void RemoveTrees()
        {
            try { Directory.Delete(root, true); } catch { }
        }

        // ------------------------------------------------------------ helpers

        static void Write(string path, long size, Random rng)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var buf = new byte[1 << 20];
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                for (long left = size; left > 0;)
                {
                    rng.NextBytes(buf);
                    int n = (int)Math.Min(left, buf.Length);
                    fs.Write(buf, 0, n); left -= n;
                }
        }

        static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(sha.ComputeHash(fs));
        }

        JobDefinition NewJob(string dest, params string[] sources)
        {
            var job = new JobDefinition { ComputerName = "TESTPC", DestinationRoot = dest };
            job.Options.BackupMode = Elevated;
            job.Options.DestinationPollSeconds = 1;
            job.Options.LockedRetryDelaySeconds = 2;
            foreach (var s in sources) job.Sources.Add(new SourceSelection { Path = s });
            return job;
        }

        CopyEngine NewEngine(JobDefinition job, List<string> messages = null)
        {
            var e = new CopyEngine(job, Path.Combine(root, "logs"))
            {
                Profiles = new ProfileInfo(),
                TickMilliseconds = 25,
                ReportIntervalMilliseconds = 25
            };
            e.Message += m => { messages?.Add(m); TestContext.Progress.WriteLine("  engine: " + m); };
            return e;
        }

        /// <summary>Every file under source exists under the job's destination with the same content.</summary>
        static void AssertMirrored(JobDefinition job, string source, Func<string, bool> skip = null)
        {
            int n = 0;
            foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                if (skip != null && skip(f)) continue;
                string d = job.DestinationFor(f);
                Assert.That(File.Exists(d), "missing at destination: " + f);
                Assert.That(new FileInfo(d).Length, Is.EqualTo(new FileInfo(f).Length), f);
                Assert.That(Hash(d), Is.EqualTo(Hash(f)), "content differs: " + f);
                Assert.That(File.GetLastWriteTimeUtc(d), Is.EqualTo(File.GetLastWriteTimeUtc(f)), f);
                n++;
            }
            Assert.That(n, Is.GreaterThan(0));
        }

        static int Run(string exe, string args)
        {
            using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) TestContext.Progress.WriteLine($"  {exe} {args} -> {p.ExitCode}: {o.Trim()}");
                return p.ExitCode;
            }
        }

        static char FreeDriveLetter() =>
            "RSTUVWXYZQPONM".First(c => !DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == c));

        // ------------------------------------------------------------ tests

        [Test]
        public void TwoSources_CopiedToComputerDriveLayout_WithExclusions_ThenRerunSkipsAll()
        {
            var rng = new Random(1);
            string a = Path.Combine(root, "t1", "srcA"), b = Path.Combine(root, "t1", "src B (ü)"), dest = Path.Combine(root, "t1", "dest");
            for (int i = 0; i < 40; i++) Write(Path.Combine(a, "docs", $"doc {i}.docx"), 3000 + i, rng);
            Write(Path.Combine(a, "docs", "nested", "deep", "résumé.pdf"), 5000, rng);
            Write(Path.Combine(a, "Unticked", "not wanted.txt"), 100, rng);
            Write(Path.Combine(a, "docs", "desktop.ini"), 10, rng);
            Write(Path.Combine(a, "docs", "~$doc 1.docx"), 10, rng);
            Write(Path.Combine(a, "docs", "x.tmp"), 10, rng);
            Write(Path.Combine(a, "docs", "Thumbs.db"), 10, rng);
            Directory.CreateDirectory(Path.Combine(a, "empty folder"));
            for (int i = 0; i < 10; i++) Write(Path.Combine(b, $"pic{i}.jpg"), 20000, rng);

            var job = NewJob(dest, a, b);
            job.Sources[0].ExcludedSubfolders.Add(Path.Combine(a, "Unticked"));
            var saves = 0;
            var engine = NewEngine(job); engine.SaveState = _ => saves++;
            var scan = engine.Scan(CancellationToken.None);
            Assert.That(scan.Totals.FilesCopied, Is.EqualTo(51), "scan: files that would be copied");
            Assert.That(scan.Totals.BytesCopied, Is.EqualTo(Enumerable.Range(0, 40).Sum(i => 3000L + i) + 5000 + 200000));
            Assert.That(Directory.Exists(dest), Is.False, "scan writes nothing");

            var outcome = NewEngineWithSave(job, () => saves++).Run(CancellationToken.None);
            Assert.That(outcome, Is.EqualTo(JobOutcome.Completed));
            Assert.That(job.State, Is.EqualTo(JobState.Completed));
            Assert.That(job.Sources.All(s => s.Status == SourceStatus.Completed));
            Assert.That(saves, Is.GreaterThan(3), "state saved as the job progresses");

            string drive = char.ToUpperInvariant(a[0]).ToString();
            Assert.That(job.DestinationFor(a), Is.EqualTo(Path.Combine(dest, "TESTPC", drive, a.Substring(3))));
            bool excluded(string f) => f.Contains(@"\Unticked\") || new[] { "desktop.ini", "~$doc 1.docx", "x.tmp", "Thumbs.db" }.Contains(Path.GetFileName(f));
            AssertMirrored(job, a, excluded);
            AssertMirrored(job, b);
            Assert.That(Directory.Exists(job.DestinationFor(Path.Combine(a, "Unticked"))), Is.False, "unticked subfolder not copied");
            Assert.That(Directory.Exists(job.DestinationFor(Path.Combine(a, "empty folder"))), "empty folder copied");
            foreach (var f in Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories).Where(excluded))
                Assert.That(File.Exists(job.DestinationFor(f)), Is.False, "excluded file copied: " + f);

            var totals = job.Totals();
            Assert.That(totals.FilesCopied, Is.EqualTo(51));
            Assert.That(totals.FilesFailed, Is.EqualTo(0));
            Assert.That(totals.BytesCopied, Is.EqualTo(scan.Totals.BytesCopied));

            // same job again from scratch: everything is already there
            var again = NewJob(dest, a, b);
            again.Sources[0].ExcludedSubfolders.Add(Path.Combine(a, "Unticked"));
            Assert.That(NewEngine(again).Run(CancellationToken.None), Is.EqualTo(JobOutcome.Completed));
            Assert.That(again.Totals().FilesCopied, Is.EqualTo(0));
            Assert.That(again.Totals().FilesSkipped, Is.GreaterThanOrEqualTo(51));
        }

        CopyEngine NewEngineWithSave(JobDefinition job, Action onSave)
        {
            var e = NewEngine(job); e.SaveState = _ => onSave(); return e;
        }

        [Test]
        public void AppData_ExcludedOnlyDirectlyUnderAProfile()
        {
            var rng = new Random(2);
            string users = Path.Combine(root, "t2", "Users"), dest = Path.Combine(root, "t2", "dest");
            Write(Path.Combine(users, "Anna", "AppData", "Local", "cache.bin"), 500, rng);
            Write(Path.Combine(users, "Anna", "Documents", "letter.docx"), 500, rng);
            Write(Path.Combine(users, "Anna", "Documents", "AppData", "my own folder.txt"), 500, rng);
            Write(Path.Combine(users, "Bob", "AppData", "Roaming", "x.dat"), 500, rng);
            Write(Path.Combine(users, "Bob", "Desktop", "todo.txt"), 500, rng);

            var job = NewJob(dest, users);
            var engine = NewEngine(job);
            engine.Profiles = new ProfileInfo { ProfilesDirectory = users, ProfileFolders = Directory.GetDirectories(users).ToList() };
            Assert.That(engine.Run(CancellationToken.None), Is.EqualTo(JobOutcome.Completed));

            Assert.That(Directory.Exists(job.DestinationFor(Path.Combine(users, "Anna", "AppData"))), Is.False);
            Assert.That(Directory.Exists(job.DestinationFor(Path.Combine(users, "Bob", "AppData"))), Is.False);
            Assert.That(File.Exists(job.DestinationFor(Path.Combine(users, "Anna", "Documents", "AppData", "my own folder.txt"))), "user's own AppData-named folder is kept");
            Assert.That(job.Totals().FilesCopied, Is.EqualTo(3));
        }

        [Test]
        public void LockedFile_IsRetriedAfterTheMainPass_AndSucceedsOnceReleased()
        {
            var rng = new Random(3);
            string src = Path.Combine(root, "t3", "src"), dest = Path.Combine(root, "t3", "dest");
            for (int i = 0; i < 20; i++) Write(Path.Combine(src, $"f{i}.txt"), 1000, rng);
            string locked = Path.Combine(src, "sub", "open in word.docx");
            Write(locked, 4321, rng);

            var job = NewJob(dest, src);
            var messages = new List<string>();
            var engine = NewEngine(job, messages);
            var phases = new List<EnginePhase>();
            var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            engine.Progress += p =>
            {
                if (phases.Count == 0 || phases.Last() != p.Phase) phases.Add(p.Phase);
                if (p.Phase == EnginePhase.WaitingToRetryLocked) { handle?.Dispose(); handle = null; } // user closes the app
            };
            try
            {
                var outcome = engine.Run(CancellationToken.None);
                Assert.That(outcome, Is.EqualTo(JobOutcome.Completed));
            }
            finally { handle?.Dispose(); }

            Assert.That(phases, Does.Contain(EnginePhase.WaitingToRetryLocked).And.Contain(EnginePhase.RetryingFailed));
            Assert.That(job.AllFailures(), Is.Empty);
            Assert.That(job.Sources[0].Status, Is.EqualTo(SourceStatus.Completed));
            Assert.That(job.Totals().FilesCopied, Is.EqualTo(21));
            Assert.That(job.Totals().FilesFailed, Is.EqualTo(0));
            AssertMirrored(job, src);
            Assert.That(messages.Any(m => m.Contains("1 locked file")));
        }

        [Test]
        public void LockedFile_StillLocked_IsReportedWithReason_ThenRetryFailedCopiesIt()
        {
            var rng = new Random(4);
            string src = Path.Combine(root, "t4", "src"), dest = Path.Combine(root, "t4", "dest");
            for (int i = 0; i < 5; i++) Write(Path.Combine(src, $"f{i}.txt"), 1000, rng);
            string locked = Path.Combine(src, "still open.xlsx");
            Write(locked, 2222, rng);

            var job = NewJob(dest, src);
            var sw = Stopwatch.StartNew();
            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.That(NewEngine(job).Run(CancellationToken.None), Is.EqualTo(JobOutcome.CompletedWithErrors));
            }
            TestContext.Progress.WriteLine($"  run with a permanently locked file took {sw.Elapsed.TotalSeconds:0.0}s (no prompt, no hang)");
            var f = job.AllFailures().Single();
            Assert.That(f.SourcePath, Is.EqualTo(locked));
            Assert.That(f.Category, Is.EqualTo(FailureCategory.Locked));
            Assert.That(f.ErrorCode, Is.EqualTo(32));
            Assert.That(f.Size, Is.EqualTo(2222));
            Assert.That(f.Message, Does.Contain("being used by another process"));

            // the failure points at its entry in the Robocopy log (v1.0.2): the last attempt, a retry log
            string logFile = Path.Combine(root, "logs", f.LogFile);
            Assert.That(f.LogFile, Does.Contain("_retry_"), "the latest log that tried this file");
            Assert.That(File.Exists(logFile), "log file exists: " + f.LogFile);
            string[] logLines = File.ReadAllText(logFile, System.Text.Encoding.Unicode).Split('\n');
            string entry = logLines[f.LogLine - 1].TrimEnd('\r');
            Assert.That(entry, Does.Contain(" ERROR 32 ").And.EndWith(locked), "line " + f.LogLine + " is the error entry");
            string csv = Path.Combine(root, "t4", "failures.csv");
            FailureCsv.Write(job, csv);
            Assert.That(File.ReadAllLines(csv)[0], Does.EndWith("\"Log file\",\"Log line\""));
            Assert.That(File.ReadAllLines(csv)[1], Does.EndWith($"\"{f.LogFile}\",\"{f.LogLine}\""));

            Assert.That(job.Totals().FilesCopied, Is.EqualTo(5));
            Assert.That(job.Totals().FilesFailed, Is.EqualTo(1));
            Assert.That(job.State, Is.EqualTo(JobState.Completed));
            Assert.That(job.Sources[0].Status, Is.EqualTo(SourceStatus.CompletedWithErrors));

            // the report's "Retry failed" button, after the file was closed
            Assert.That(NewEngine(job).RetryFailed(CancellationToken.None), Is.EqualTo(JobOutcome.Completed));
            Assert.That(job.AllFailures(), Is.Empty);
            Assert.That(job.Totals().FilesCopied, Is.EqualTo(6));
            Assert.That(job.Totals().FilesFailed, Is.EqualTo(0));
            AssertMirrored(job, src);
        }

        [Test]
        public void Cancel_StopsRobocopy_AndASecondRunFinishesTheJob()
        {
            string dest = Path.Combine(root, "t5", "dest");
            var job = NewJob(dest, bigSrc);
            var cts = new CancellationTokenSource();
            var engine = NewEngine(job);
            engine.ExpectedBytes = bigBytes; engine.ExpectedFiles = bigCount;
            ProgressSnapshot atCancel = null;
            engine.Progress += p => { if (p.Phase == EnginePhase.Copying && p.FilesProcessed >= 800 && atCancel == null) { atCancel = p; cts.Cancel(); } };

            var sw = Stopwatch.StartNew();
            Assert.That(engine.Run(cts.Token), Is.EqualTo(JobOutcome.Cancelled));
            Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(60));
            Assert.That(job.State, Is.EqualTo(JobState.Cancelled));
            Assert.That(job.IsUnfinished);
            Assert.That(job.Sources[0].Status, Is.EqualTo(SourceStatus.Pending));
            Assert.That(Process.GetProcessesByName("robocopy").Length, Is.EqualTo(0), "no Robocopy left running after cancel");
            int atDest = Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Count();
            Assert.That(atDest, Is.GreaterThan(0).And.LessThan(bigCount), "cancelled mid-run");
            TestContext.Progress.WriteLine($"  cancelled at: {atCancel}; {atDest} of {bigCount} files at destination");

            // resume: the saved job is simply run again
            var resume = NewEngine(job);
            Assert.That(resume.Run(CancellationToken.None), Is.EqualTo(JobOutcome.Completed));
            Assert.That(job.State, Is.EqualTo(JobState.Completed));
            AssertMirrored(job, bigSrc);
            var t = job.Totals();
            Assert.That(t.FilesTotal, Is.EqualTo(bigCount));
            Assert.That(t.FilesFailed, Is.EqualTo(0));
            Assert.That(t.FilesCopied + t.FilesSkipped, Is.EqualTo(bigCount));
            Assert.That(job.Sources[0].CountsApproximate, "counts are flagged as approximate after an interruption");
        }

        [Test]
        public void Progress_ReportsFilesBytesFolderAndEta()
        {
            string dest = Path.Combine(root, "t6", "dest");
            var job = NewJob(dest, bigSrc);
            var engine = NewEngine(job);
            engine.ExpectedBytes = bigBytes; engine.ExpectedFiles = bigCount;
            var snaps = new List<ProgressSnapshot>();
            engine.Progress += snaps.Add;
            Assert.That(engine.Run(CancellationToken.None), Is.EqualTo(JobOutcome.Completed));

            var copying = snaps.Where(s => s.Phase == EnginePhase.Copying).ToList();
            Assert.That(copying.Count, Is.GreaterThan(5));
            Assert.That(copying.Select(s => s.FilesProcessed), Is.Ordered, "file count never goes backwards");
            Assert.That(copying.Select(s => s.BytesCopied), Is.Ordered, "byte count never goes backwards");
            Assert.That(copying.Last().FilesProcessed, Is.EqualTo(bigCount));
            Assert.That(copying.Any(s => s.CurrentFolder != null && s.CurrentFolder.StartsWith(bigSrc)));
            Assert.That(copying.All(s => s.BytesFromFreeSpace));
            Assert.That(copying.All(s => s.BytesCopied <= bigBytes));
            // free-space measurement ends up close to the real total (cluster rounding and metadata allowed)
            Assert.That(copying.Last().BytesCopied, Is.GreaterThan(bigBytes * 0.85), "bytes measured through destination free space");
            Assert.That(copying.Any(s => s.Eta.HasValue && s.BytesPerSecond > 0), "an ETA was produced");
            Assert.That(snaps.Last().Phase, Is.EqualTo(EnginePhase.Finished));
            TestContext.Progress.WriteLine("  mid-run sample: " + copying[copying.Count / 2]);
            TestContext.Progress.WriteLine("  last sample   : " + copying.Last());
        }

        [Test]
        public void DestinationDisappears_EnginePauses_ThenContinuesWhenItReturns()
        {
            // a SUBST drive stands in for a removable/network destination: removing the mapping makes
            // every new destination path fail, restoring it brings the same data back
            char letter = FreeDriveLetter();
            string backing = Path.Combine(root, "t7", "backing");
            Directory.CreateDirectory(backing);
            Assert.That(Run("subst.exe", $"{letter}: \"{backing}\""), Is.EqualTo(0));
            try
            {
                var job = NewJob($@"{letter}:\Export", bigSrc);
                var messages = new List<string>();
                var engine = NewEngine(job, messages);
                var phases = new List<EnginePhase>();
                bool pulled = false; Task restore = null; DateTime? pausedAt = null, resumedAt = null;
                engine.Progress += p =>
                {
                    if (phases.Count == 0 || phases.Last() != p.Phase) phases.Add(p.Phase);
                    if (!pulled && p.Phase == EnginePhase.Copying && p.FilesProcessed >= 800)
                    {
                        pulled = true;
                        Run("subst.exe", $"{letter}: /D");
                    }
                    if (p.Phase == EnginePhase.PausedDestinationUnavailable && restore == null)
                    {
                        pausedAt = DateTime.UtcNow;
                        restore = Task.Run(() => { Thread.Sleep(4000); Run("subst.exe", $"{letter}: \"{backing}\""); });
                    }
                    if (pausedAt.HasValue && resumedAt == null && p.Phase == EnginePhase.Copying) resumedAt = DateTime.UtcNow;
                };

                Assert.That(engine.Run(CancellationToken.None), Is.EqualTo(JobOutcome.Completed));
                Assert.That(pulled, "destination was removed mid-run");
                Assert.That(phases, Does.Contain(EnginePhase.PausedDestinationUnavailable));
                Assert.That(resumedAt, Is.Not.Null, "copying continued after the pause");
                Assert.That((resumedAt.Value - pausedAt.Value).TotalSeconds, Is.GreaterThan(3).And.LessThan(30), "waited for the destination, then continued promptly");
                Assert.That(messages.Any(m => m.Contains("unreachable")) && messages.Any(m => m.Contains("is back")));
                Assert.That(job.AllFailures(), Is.Empty, "no failures left over from the outage");
                Assert.That(job.Totals().FilesFailed, Is.EqualTo(0));
                AssertMirrored(job, bigSrc);
                TestContext.Progress.WriteLine($"  paused {(resumedAt.Value - pausedAt.Value).TotalSeconds:0.0}s; phases: {string.Join(" > ", phases)}");
            }
            finally { Run("subst.exe", $"{letter}: /D"); }
        }

        [Test]
        public void DestinationMissingAtStart_WaitsInsteadOfFailing_AndCanBeCancelled()
        {
            char letter = FreeDriveLetter();
            var job = NewJob($@"{letter}:\Export", Path.Combine(bigSrc, "d00"));
            var engine = NewEngine(job);
            var cts = new CancellationTokenSource();
            bool paused = false;
            engine.Progress += p => { if (p.Phase == EnginePhase.PausedDestinationUnavailable && !paused) { paused = true; cts.CancelAfter(1500); } };
            Assert.That(engine.Run(cts.Token), Is.EqualTo(JobOutcome.Cancelled));
            Assert.That(paused);
            Assert.That(job.State, Is.EqualTo(JobState.Cancelled));
        }

        [Test]
        public void BackupModeWithoutElevation_IsAFatalStartError_NotAHang()
        {
            if (Elevated) Assert.Ignore("only meaningful when not elevated");
            string dest = Path.Combine(root, "t9", "dest");
            var job = NewJob(dest, Path.Combine(bigSrc, "d00"));
            job.Options.BackupMode = true;
            Assert.That(NewEngine(job).Run(CancellationToken.None), Is.EqualTo(JobOutcome.Failed));
            Assert.That(job.State, Is.EqualTo(JobState.Failed));
            Assert.That(job.LastMessage, Does.Contain("Backup and Restore Files user rights"));
        }

        // ------------------------------------------------------------ elevated only: real volumes

        sealed class Vhd : IDisposable
        {
            public readonly string File; public readonly char Letter;
            public Vhd(string file, int megabytes, char letter)
            {
                File = file; Letter = letter;
                Diskpart($"create vdisk file=\"{file}\" maximum={megabytes} type=expandable\nattach vdisk\ncreate partition primary\nformat fs=ntfs quick label=DTTTEST\nassign letter={letter}");
            }
            public void Detach() => Diskpart($"select vdisk file=\"{File}\"\ndetach vdisk");
            public void Attach() => Diskpart($"select vdisk file=\"{File}\"\nattach vdisk");
            public void Dispose() { try { Detach(); } catch { } try { System.IO.File.Delete(File); } catch { } }
            static void Diskpart(string script)
            {
                string f = Path.GetTempFileName();
                System.IO.File.WriteAllText(f, script);
                try { if (Run("diskpart.exe", $"/s \"{f}\"") != 0) throw new InvalidOperationException("diskpart failed"); }
                finally { System.IO.File.Delete(f); }
            }
        }

        [Test]
        public void Elevated_DestinationFull_StopsCleanlyAndReports()
        {
            if (!Elevated) Assert.Ignore("needs an elevated prompt (creates a small virtual disk)");
            char letter = FreeDriveLetter();
            using (var vhd = new Vhd(Path.Combine(root, "full.vhdx"), 200, letter))
            {
                var job = NewJob($@"{letter}:\Export", bigSrc); // ~980 MB into a 200 MB volume
                var messages = new List<string>();
                var engine = NewEngine(job, messages);
                var sw = Stopwatch.StartNew();
                var outcome = engine.Run(CancellationToken.None);
                TestContext.Progress.WriteLine($"  outcome {outcome} after {sw.Elapsed.TotalSeconds:0.0}s; {job.Sources[0].Totals}");
                SaveFixture("diskfull");
                Assert.That(outcome, Is.EqualTo(JobOutcome.DestinationFull));
                Assert.That(job.State, Is.EqualTo(JobState.DestinationFull));
                Assert.That(job.IsUnfinished);
                Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(120), "stopped instead of grinding through every remaining file");
                Assert.That(Process.GetProcessesByName("robocopy").Length, Is.EqualTo(0));
                Assert.That(messages.Any(m => m.Contains("full")));
            }
        }

        [Test]
        public void Elevated_VolumeRemovedMidRun_PausesAndResumes()
        {
            if (!Elevated) Assert.Ignore("needs an elevated prompt (creates a virtual disk and detaches it mid-run)");
            char letter = FreeDriveLetter();
            using (var vhd = new Vhd(Path.Combine(root, "pull.vhdx"), 3000, letter))
            {
                var job = NewJob($@"{letter}:\Export", bigSrc);
                var engine = NewEngine(job);
                var phases = new List<EnginePhase>();
                bool pulled = false; Task restore = null;
                engine.Progress += p =>
                {
                    if (phases.Count == 0 || phases.Last() != p.Phase) phases.Add(p.Phase);
                    if (!pulled && p.Phase == EnginePhase.Copying && p.FilesProcessed >= 800) { pulled = true; vhd.Detach(); }
                    if (p.Phase == EnginePhase.PausedDestinationUnavailable && restore == null)
                        restore = Task.Run(() => { Thread.Sleep(5000); vhd.Attach(); });
                };
                var outcome = engine.Run(CancellationToken.None);
                SaveFixture("volume_removed");
                TestContext.Progress.WriteLine($"  outcome {outcome}; phases: {string.Join(" > ", phases)}");
                Assert.That(outcome, Is.EqualTo(JobOutcome.Completed));
                Assert.That(phases, Does.Contain(EnginePhase.PausedDestinationUnavailable));
                Assert.That(job.AllFailures(), Is.Empty);
                AssertMirrored(job, bigSrc);
            }
        }

        /// <summary>Keeps the Robocopy logs of an elevated scenario so they can become parser fixtures.</summary>
        void SaveFixture(string name)
        {
            string target = Path.Combine(Path.GetTempPath(), "DttM2_fixtures", name);
            Directory.CreateDirectory(target);
            foreach (var f in new DirectoryInfo(Path.Combine(root, "logs")).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(4))
                f.CopyTo(Path.Combine(target, f.Name), true);
            TestContext.Progress.WriteLine("  logs kept in " + target);
        }
    }
}
