using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DataTransfer.Core;
using NUnit.Framework;

namespace DataTransfer.Core.Tests
{
    /// <summary>
    /// Pre-flight checks against generated folders. The only "application" these tests ever close is
    /// DttDummyApp, a stand-in built for the purpose; the known-apps list is replaced with it.
    /// </summary>
    [TestFixture, Category("Integration"), NonParallelizable]
    public class PreflightTests
    {
        static readonly bool Elevated = SystemProbes.IsElevated();
        string root;

        [OneTimeSetUp]
        public void SetUp()
        {
            string baseDir = Environment.GetEnvironmentVariable("DTT_TEST_ROOT") ?? Path.GetTempPath();
            root = Path.Combine(baseDir, "DttM3_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            foreach (var p in Process.GetProcessesByName("DttDummyApp")) { try { p.Kill(); } catch { } }
            try { Directory.Delete(root, true); } catch { }
        }

        static void Write(string path, int size)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[size]);
        }

        static int Cmd(string exe, string args)
        {
            using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            { p.StandardOutput.ReadToEnd(); p.WaitForExit(); return p.ExitCode; }
        }

        JobDefinition NewJob(string name, out string src, out string dest)
        {
            src = Path.Combine(root, name, "src"); dest = Path.Combine(root, name, "dest");
            var job = new JobDefinition { ComputerName = "TESTPC", DestinationRoot = dest };
            job.Options.BackupMode = Elevated;
            job.Preflight.KnownApps = new List<string> { "DttDummyApp" };
            job.Preflight.AppCloseTimeoutSeconds = 3;
            job.Sources.Add(new SourceSelection { Path = src });
            return job;
        }

        PreflightEngine NewEngine(JobDefinition job) => new PreflightEngine(job, Path.Combine(root, "logs")) { Profiles = new ProfileInfo() };

        // ------------------------------------------------------------ walker

        [Test]
        public void Walker_FollowsTheSameExclusionsAsRobocopy_AndAgreesWithItsScan()
        {
            var job = NewJob("walk", out string src, out string dest);
            for (int i = 0; i < 30; i++) Write(Path.Combine(src, "docs", $"f{i}.docx"), 1000 + i);
            Write(Path.Combine(src, "docs", "deep", "a", "b", "c.pdf"), 5000);
            Write(Path.Combine(src, "docs", "desktop.ini"), 10);
            Write(Path.Combine(src, "docs", "~$f1.docx"), 10);
            Write(Path.Combine(src, "docs", "old.TMP"), 10);
            Write(Path.Combine(src, "Unticked", "x.txt"), 10);
            Write(Path.Combine(src, "pics", "big.jpg"), 300000);
            Write(Path.Combine(src, "node_modules", "pkg", "index.js"), 10);
            Write(Path.Combine(src, "docs", "node_modules", "y.js"), 10);
            Directory.CreateDirectory(Path.Combine(src, "empty"));
            Cmd("cmd.exe", $"/c mklink /J \"{Path.Combine(src, "docs", "JunctionToPics")}\" \"{Path.Combine(src, "pics")}\"");
            job.Sources[0].ExcludedSubfolders.Add(Path.Combine(src, "Unticked"));
            job.Options.ExcludeDirNames.Add("node_modules");

            var walk = SourceWalker.Walk(job, new ProfileInfo(), CancellationToken.None);
            Assert.That(walk.Unreadable, Is.Empty);
            Assert.That(walk.Files, Is.EqualTo(35), "30 docs + c.pdf + 3 name-excluded + big.jpg; unticked, node_modules and the junction are not entered");
            Assert.That(walk.NameExcludedFiles, Is.EqualTo(3));
            Assert.That(walk.EligibleFiles, Is.EqualTo(32));
            Assert.That(walk.EligibleBytes, Is.EqualTo(Enumerable.Range(0, 30).Sum(i => 1000L + i) + 5000 + 300000));
            Assert.That(walk.LargestFile, Is.EqualTo(300000));
            Assert.That(walk.LargestFilePath, Does.EndWith("big.jpg"));
            Assert.That(walk.CloudFiles, Is.EqualTo(0));
            Assert.That(walk.EfsFiles, Is.EqualTo(0));

            var scan = new CopyEngine(job, Path.Combine(root, "logs")) { Profiles = new ProfileInfo() }.Scan(CancellationToken.None);
            Assert.That(scan.FatalError, Is.Null);
            Assert.That(walk.Files, Is.EqualTo(scan.Totals.FilesTotal), "the walk sees the same files as Robocopy's scan");
            Assert.That(walk.Bytes, Is.EqualTo(scan.Totals.BytesTotal));
            Assert.That(walk.EligibleFiles, Is.EqualTo(scan.Totals.FilesCopied), "and expects the same files to be copied");
            Assert.That(walk.EligibleBytes, Is.EqualTo(scan.Totals.BytesCopied));
        }

        [Test]
        public void Walker_ProfileExclusionAndLongPaths()
        {
            var job = NewJob("walk2", out string src, out string dest);
            Write(Path.Combine(src, "Anna", "AppData", "Local", "cache.bin"), 100);
            Write(Path.Combine(src, "Anna", "Documents", "AppData", "kept.txt"), 100);
            string deep = Path.Combine(src, "Anna", "Documents");
            for (int i = 0; i < 8; i++) deep = Path.Combine(deep, "level-" + new string('z', 34));
            Directory.CreateDirectory(@"\\?\" + deep);
            File.WriteAllBytes(@"\\?\" + Path.Combine(deep, "deep.txt"), new byte[50]);

            var profiles = new ProfileInfo { ProfilesDirectory = src, ProfileFolders = { Path.Combine(src, "Anna") } };
            var walk = SourceWalker.Walk(job, profiles, CancellationToken.None);
            Assert.That(walk.Files, Is.EqualTo(2), "AppData under the profile is skipped, AppData inside Documents is not");
            Assert.That(walk.LongestPath, Is.GreaterThan(300));
            Assert.That(walk.Unreadable, Is.Empty);
        }

        [Test]
        public void Walker_NamesUnreadableFolders_WhichRobocopyOnlyCounts()
        {
            if (Elevated) Assert.Ignore("with backup privilege the folder is readable; run not elevated");
            var job = NewJob("walk3", out string src, out string dest);
            Write(Path.Combine(src, "ok", "a.txt"), 10);
            string denied = Path.Combine(src, "No Entry");
            Write(Path.Combine(denied, "secret.txt"), 10);
            Cmd("icacls.exe", $"\"{denied}\" /deny *S-1-1-0:(OI)(CI)(RX)");
            try
            {
                var walk = SourceWalker.Walk(job, new ProfileInfo(), CancellationToken.None);
                Assert.That(walk.Unreadable.Select(u => u.Path), Is.EqualTo(new[] { denied }));
                Assert.That(walk.Unreadable[0].ErrorCode, Is.EqualTo(5));
                Assert.That(walk.Files, Is.EqualTo(1));

                var pf = NewEngine(job);
                var r = pf.Run(PreflightEngine.SourceScan, CancellationToken.None);
                Assert.That(r.Status, Is.EqualTo(CheckStatus.Warn));
                Assert.That(r.Summary, Does.Contain("1 folder(s) cannot be read"));
                Assert.That(r.Details.Any(d => d.StartsWith("Unreadable: " + denied)), "the folder is named");
                Assert.That(pf.Scan.UnreadableFolders, Is.EqualTo(1), "Robocopy counted the same folder");
                Assert.That(r.Blocking, Is.False);
            }
            finally { Cmd("icacls.exe", $"\"{denied}\" /remove:d *S-1-1-0"); }
        }

        [TestCase(0x00500020u, true, TestName = "CloudOnly_AsADotNetProcessSeesIt")]
        [TestCase(0x00501620u, true, TestName = "CloudOnly_AsRobocopySeesIt")]
        [TestCase(0x00000020u, false, TestName = "CloudOnly_OrdinaryFile")]
        [TestCase(0x00080020u, false, TestName = "CloudOnly_AlwaysKeepOnDevice")]
        [TestCase(0x00100020u, false, TestName = "CloudOnly_UnpinnedButStillLocal")]
        public void IsCloudOnly(uint attributes, bool expected) => Assert.That(SourceWalker.IsCloudOnly(attributes), Is.EqualTo(expected));

        // ------------------------------------------------------------ whole pre-flight

        [Test]
        public void AllChecks_OnAHealthyJob_PassOrWarn_AndRunUnlocksAfterAcknowledging()
        {
            var job = NewJob("ok", out string src, out string dest);
            for (int i = 0; i < 20; i++) Write(Path.Combine(src, $"f{i}.txt"), 2000);
            var pf = NewEngine(job);
            var seen = new List<string>();
            pf.CheckCompleted += r => seen.Add(r.Id);
            pf.RunAll(CancellationToken.None);
            foreach (var r in pf.Results) TestContext.Progress.WriteLine("  " + r);

            Assert.That(seen.Count, Is.EqualTo(10), "all ten checks ran");
            Assert.That(pf.Results.All(r => r.Status != CheckStatus.NotRun));
            Assert.That(pf[PreflightEngine.LogFolder].Status, Is.EqualTo(CheckStatus.Pass));
            Assert.That(pf[PreflightEngine.Destination].Status, Is.EqualTo(CheckStatus.Pass));
            Assert.That(pf[PreflightEngine.Destination].Details.Any(d => d.Contains("-character path")), "long-path test file was written");
            Assert.That(pf[PreflightEngine.SourceScan].Status, Is.EqualTo(CheckStatus.Pass));
            Assert.That(pf[PreflightEngine.SourceScan].Summary, Does.StartWith("20 files, 39.06 KB to copy"));
            Assert.That(pf[PreflightEngine.Cloud].Status, Is.EqualTo(CheckStatus.Pass));
            Assert.That(pf[PreflightEngine.Efs].Status, Is.EqualTo(CheckStatus.Pass));
            Assert.That(pf[PreflightEngine.FreeSpaceId].Status, Is.EqualTo(CheckStatus.Pass));
            Assert.That(pf[PreflightEngine.Apps].Status, Is.EqualTo(CheckStatus.Pass));
            Assert.That(pf[PreflightEngine.Elevation].Status, Is.EqualTo(Elevated ? CheckStatus.Pass : CheckStatus.Warn), "backup mode is off when the test is not elevated");
            Assert.That(pf.Results.Any(r => r.Blocking), Is.False);
            Assert.That(Directory.GetFileSystemEntries(dest), Is.Empty, "the destination test left nothing behind");

            bool anyWarn = pf.Results.Any(r => r.Status != CheckStatus.Pass);
            Assert.That(pf.CanRun, Is.EqualTo(!anyWarn), "Run is disabled while any warning is unacknowledged");
            foreach (var r in pf.Results.Where(r => r.Status != CheckStatus.Pass)) Assert.That(pf.Acknowledge(r.Id));
            Assert.That(pf.CanRun);
            Assert.That(pf.Acknowledge(PreflightEngine.LogFolder), Is.False, "a passed check has nothing to acknowledge");
        }

        [Test]
        public void BackupModeWithoutElevation_IsBlocking_AndCannotBeAcknowledged()
        {
            if (Elevated) Assert.Ignore("only meaningful when not elevated");
            var job = NewJob("blocked", out string src, out string dest);
            Write(Path.Combine(src, "a.txt"), 10);
            job.Options.BackupMode = true;
            var pf = NewEngine(job);
            pf.RunAll(CancellationToken.None);
            var e = pf[PreflightEngine.Elevation];
            Assert.That(e.Status, Is.EqualTo(CheckStatus.Fail));
            Assert.That(e.Blocking);
            Assert.That(e.Summary, Does.Contain("Run as administrator"));
            Assert.That(pf[PreflightEngine.SourceScan].Blocking, "Robocopy's refusal surfaces in the scan too");
            Assert.That(pf[PreflightEngine.SourceScan].Summary, Does.Contain("Backup and Restore Files user rights"));
            foreach (var r in pf.Results) pf.Acknowledge(r.Id);
            Assert.That(pf.Acknowledge(PreflightEngine.Elevation), Is.False);
            Assert.That(pf.CanRun, Is.False);
        }

        [Test]
        public void Destination_Problems_AreBlocking()
        {
            var job = NewJob("baddest", out string src, out string dest);
            Write(Path.Combine(src, "a.txt"), 10);

            job.DestinationRoot = "";
            Assert.That(NewEngine(job).Run(PreflightEngine.Destination, CancellationToken.None).Blocking);

            char missing = "ZYXWVUT".First(c => !DriveInfo.GetDrives().Any(d => d.Name[0] == c));
            job.DestinationRoot = missing + @":\Export";
            var r = NewEngine(job).Run(PreflightEngine.Destination, CancellationToken.None);
            Assert.That(r.Status, Is.EqualTo(CheckStatus.Fail));
            Assert.That(r.Blocking);
            Assert.That(r.Summary, Does.StartWith("Cannot write to the destination"));

            job.DestinationRoot = @"\\serveronly";
            Assert.That(NewEngine(job).Run(PreflightEngine.Destination, CancellationToken.None).Summary, Does.Contain(@"\\server\share"));

            // wrong share with credentials: fails at connect, and the password never appears in the result
            job.DestinationRoot = @"\\127.0.0.1\dtt-no-such-share\Export";
            job.DestinationUserName = @"TESTPC\nobody";
            var pf = NewEngine(job); pf.DestinationPassword = "S3cret-Passw0rd!";
            r = pf.Run(PreflightEngine.Destination, CancellationToken.None);
            Assert.That(r.Status, Is.EqualTo(CheckStatus.Fail));
            Assert.That(r.Blocking);
            Assert.That(r.Summary, Does.StartWith(@"Could not connect to \\127.0.0.1\dtt-no-such-share as TESTPC\nobody"));
            Assert.That(r.Summary + string.Join("|", r.Details), Does.Not.Contain("S3cret"));
            TestContext.Progress.WriteLine("  " + r.Summary);
        }

        [Test]
        public void MissingSourceFolder_BlocksTheScanAndEverythingThatNeedsIt()
        {
            var job = NewJob("nosrc", out string src, out string dest);
            var pf = NewEngine(job);
            pf.RunAll(CancellationToken.None);
            Assert.That(pf[PreflightEngine.SourceScan].Blocking);
            Assert.That(pf[PreflightEngine.SourceScan].Details, Does.Contain(src));
            foreach (var id in new[] { PreflightEngine.Cloud, PreflightEngine.Efs, PreflightEngine.FreeSpaceId })
                Assert.That(pf[id].Blocking && pf[id].Summary.Contains("source scan"), id);
            Assert.That(pf.CanRun, Is.False);
        }

        // ------------------------------------------------------------ applications

        static string DummyExe
        {
            get
            {
                string dir = TestContext.CurrentContext.TestDirectory; // ...\DataTransfer.Core.Tests\bin\<config>
                string config = Path.GetFileName(dir.TrimEnd('\\'));
                return Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "DttDummyApp", "bin", config, "DttDummyApp.exe"));
            }
        }

        static Process StartDummy(string args)
        {
            var p = Process.Start(new ProcessStartInfo(DummyExe, args) { UseShellExecute = false });
            p.WaitForInputIdle(10000);
            for (int i = 0; i < 100 && p.MainWindowHandle == IntPtr.Zero; i++) { Thread.Sleep(100); p.Refresh(); }
            return p;
        }

        [Test]
        public void OpenApps_AreListed_ClosedOnRequest_AndForceClosedAfterTheTimeout()
        {
            Assert.That(File.Exists(DummyExe), DummyExe);
            var job = NewJob("apps", out string src, out string dest);
            var pf = NewEngine(job);
            Assert.That(pf.Run(PreflightEngine.Apps, CancellationToken.None).Status, Is.EqualTo(CheckStatus.Pass));

            using (var polite = StartDummy("--title \"Quarterly report.docx\""))
            using (var stubborn = StartDummy("--stubborn --title \"Unsaved budget.xlsx\""))
            {
                var r = pf.Run(PreflightEngine.Apps, CancellationToken.None);
                Assert.That(r.Status, Is.EqualTo(CheckStatus.Warn));
                Assert.That(r.FixLabel, Does.Contain("force-close after 3 s"));
                Assert.That(r.Details.Single(), Does.StartWith("DttDummyApp: 2 process(es)").And.Contain("Quarterly report.docx"));
                Assert.That(polite.HasExited || stubborn.HasExited, Is.False, "checking does not close anything");
                Assert.That(pf.CanRun, Is.False);

                var sw = Stopwatch.StartNew();
                r = pf.ApplyFix(PreflightEngine.Apps, CancellationToken.None);
                TestContext.Progress.WriteLine($"  fix took {sw.Elapsed.TotalSeconds:0.0}s: {r.Details.FirstOrDefault()}");
                Assert.That(polite.WaitForExit(2000) && stubborn.WaitForExit(2000), "both are gone");
                Assert.That(sw.Elapsed.TotalSeconds, Is.GreaterThan(2.5).And.LessThan(15), "the stubborn one got its timeout before being ended");
                Assert.That(r.Details[0], Is.EqualTo("Fix: 1 closed on request, 1 force-closed, 0 still running."));
                Assert.That(r.Status, Is.EqualTo(CheckStatus.Pass));
                Assert.That(r.FixLabel, Is.Null);
            }
        }

        [Test]
        public void KnownAppsList_IsEditable_AndDefaultsCoverTheSpec()
        {
            var defaults = new JobDefinition().Preflight.KnownApps;
            foreach (var name in new[] { "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "chrome", "msedge", "firefox", "ms-teams", "OneDrive", "Acrobat", "AcroRd32" })
                Assert.That(defaults, Does.Contain(name));
            // accepted with or without .exe, any case
            using (var p = StartDummy(""))
            {
                Assert.That(AppCloser.Find(new[] { "dttdummyapp.EXE" }, s => true).Any(a => a.ProcessId == p.Id));
                Assert.That(AppCloser.Find(new[] { "SomethingElse" }, s => true), Is.Empty);
                p.Kill();
            }
        }

        // ------------------------------------------------------------ small probes

        [Test]
        public void FreeSpaceDecision()
        {
            const long GB = 1L << 30;
            CheckResult Eval(long need, long? free, string fs, long largest = 0)
            { var r = new CheckResult(); PreflightEngine.EvaluateFreeSpace(r, need, free, fs, largest, @"C:\x\largest.bin"); return r; }

            Assert.That(Eval(100 * GB, 500 * GB, "NTFS").Status, Is.EqualTo(CheckStatus.Pass));
            var shortBy = Eval(200 * GB, 150 * GB, "NTFS");
            Assert.That(shortBy.Status, Is.EqualTo(CheckStatus.Fail));
            Assert.That(shortBy.Blocking, Is.False, "can be acknowledged; the run then stops cleanly when full");
            Assert.That(shortBy.Summary, Does.Contain("Short by 50 GB"));
            Assert.That(Eval(100 * GB, 102 * GB, "NTFS").Status, Is.EqualTo(CheckStatus.Warn), "less than 5% headroom");
            Assert.That(Eval(100 * GB, null, "NTFS").Status, Is.EqualTo(CheckStatus.Warn));
            var fat = Eval(10 * GB, 50 * GB, "FAT32", 1 * GB);
            Assert.That(fat.Status, Is.EqualTo(CheckStatus.Warn));
            Assert.That(fat.Summary, Does.Contain("FAT32"));
            var fatBig = Eval(10 * GB, 50 * GB, "FAT32", 5 * GB);
            Assert.That(fatBig.Status, Is.EqualTo(CheckStatus.Fail));
            Assert.That(fatBig.Details, Does.Contain(@"Largest file: C:\x\largest.bin"));
            Assert.That(Eval(10 * GB, 50 * GB, "exFAT", 5 * GB).Status, Is.EqualTo(CheckStatus.Pass));
        }

        [Test]
        public void Probes_DoNotThrow_AndGiveSaneAnswers()
        {
            Assert.That(SystemProbes.FileSystemOf(root), Is.Not.Null.And.Not.Empty);
            Assert.That(SystemProbes.FreeSpace(root), Is.GreaterThan(0));
            var battery = new CheckResult();
            var pf = NewEngine(NewJob("probe", out _, out _));
            Assert.That(pf.Run(PreflightEngine.Battery, CancellationToken.None).Status, Is.EqualTo(CheckStatus.Pass).Or.EqualTo(CheckStatus.Warn));
            var others = Sessions.OtherUsers();
            Assert.That(others.All(o => !string.IsNullOrEmpty(o.UserName)));
            var s = pf.Run(PreflightEngine.SessionsId, CancellationToken.None);
            Assert.That(s.Status, Is.EqualTo(others.Count == 0 ? CheckStatus.Pass : CheckStatus.Warn));
            TestContext.Progress.WriteLine($"  file system {SystemProbes.FileSystemOf(root)}; other sessions: {others.Count}; {pf[PreflightEngine.Battery]}");
        }

        [Test]
        public void ShareRootAndSizeFormatting()
        {
            Assert.That(NetworkConnection.ShareRoot(@"\\nas\share\a\b"), Is.EqualTo(@"\\nas\share"));
            Assert.That(NetworkConnection.ShareRoot(@"\\nas\share"), Is.EqualTo(@"\\nas\share"));
            Assert.That(NetworkConnection.ShareRoot(@"\\nas"), Is.Null);
            Assert.That(NetworkConnection.ShareRoot(@"E:\x"), Is.Null);
            Assert.That(PreflightEngine.Size(0), Is.EqualTo("0 bytes"));
            Assert.That(PreflightEngine.Size(1536), Is.EqualTo("1.5 KB"));
            Assert.That(PreflightEngine.Size(200L << 30), Is.EqualTo("200 GB"));
        }

        [Test]
        public void PreflightOptions_AreSavedWithTheJob()
        {
            string dir = Path.Combine(root, "store");
            Directory.CreateDirectory(dir);
            var job = new JobDefinition { DestinationRoot = @"E:\x" };
            job.Preflight.KnownApps.Add("MyLineOfBusinessApp");
            job.Preflight.AppCloseTimeoutSeconds = 45;
            var store = new JobStore(dir);
            store.Save(job);
            var back = store.Load();
            Assert.That(back.Preflight.KnownApps, Does.Contain("MyLineOfBusinessApp").And.Contain("WINWORD"));
            Assert.That(back.Preflight.AppCloseTimeoutSeconds, Is.EqualTo(45));
        }
    }
}
