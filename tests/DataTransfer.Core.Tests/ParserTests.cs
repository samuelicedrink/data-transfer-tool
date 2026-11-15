using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DataTransfer.Core;
using NUnit.Framework;

namespace DataTransfer.Core.Tests
{
    /// <summary>Parser tests against logs captured from real Robocopy runs in M1 (tests/fixtures).</summary>
    [TestFixture]
    public class ParserTests
    {
        internal static string FixturePath(string name) =>
            Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", name);

        static RobocopyLogParser Parse(string name, List<LogEntry> entries = null)
        {
            var p = new RobocopyLogParser();
            if (entries != null) p.OnEntry = entries.Add;
            foreach (var line in File.ReadAllText(FixturePath(name), Encoding.Unicode).Split('\n')) p.Feed(line);
            return p;
        }

        [Test]
        public void MultiThreadedCopy_CountsAndSummary()
        {
            var entries = new List<LogEntry>();
            var p = Parse(@"m1\t3_copy_mt.log", entries);
            Assert.That(p.HasSummary && p.Ended);
            Assert.That(p.Files, Is.EqualTo(new long[] { 232, 228, 4, 0, 0, 0 }));
            Assert.That(p.Dirs, Is.EqualTo(new long[] { 21, 18, 3, 0, 0, 0 }));
            Assert.That(p.Bytes, Is.EqualTo(new long[] { 46603291, 46602891, 400, 0, 0, 0 }));
            Assert.That(p.FilesStarted, Is.EqualTo(228));
            Assert.That(p.BytesStarted, Is.EqualTo(46602891), "sum of file lines equals the summary's copied bytes");
            Assert.That(p.Options, Does.Contain("/MT:8").And.Contain("/BYTES"));
            Assert.That(p.Failures, Is.Empty);
            Assert.That(p.FatalError, Is.Null);
            Assert.That(entries.All(e => e.Class == "New File"));
        }

        [Test]
        public void UnicodeLongAndZeroByteNames_SurviveParsing()
        {
            var entries = new List<LogEntry>();
            Parse(@"m1\t3_copy_mt.log", entries);
            Assert.That(entries.Any(e => e.Path.EndsWith(@"\Été & Co (2024)\résumé – final version.docx") && e.Size == 12345));
            Assert.That(entries.Any(e => e.Path.EndsWith(@"\日本語フォルダ\報告書 ①.txt") && e.Size == 2345));
            Assert.That(entries.Any(e => e.Path.EndsWith(@"\zero_bytes.txt") && e.Size == 0));
            Assert.That(entries.Max(e => e.Path.Length), Is.GreaterThan(600), "long path line parsed whole");
        }

        [Test]
        public void SingleThreadedCopy_SameFileCounts_AndDirectoryLines()
        {
            var entries = new List<LogEntry>();
            var p = Parse(@"m1\t3_copy_nomt.log", entries);
            Assert.That(p.FilesStarted, Is.EqualTo(228));
            Assert.That(p.BytesStarted, Is.EqualTo(46602891));
            Assert.That(entries.Count(e => e.IsDirectory && e.Class == "New Dir"), Is.EqualTo(18));
            Assert.That(p.Files[1], Is.EqualTo(228));
        }

        [Test]
        public void WithoutBytesFlag_RoundedSizesAreConverted()
        {
            var p = Parse(@"m1\t2_L_spec.log");
            Assert.That(p.Files, Is.EqualTo(new long[] { 232, 228, 4, 0, 0, 0 }));
            Assert.That(p.Bytes[0], Is.EqualTo((long)(44.44m * 1048576)), "'44.44 m' converted");
            Assert.That(p.Bytes[2], Is.EqualTo(400));
            Assert.That(p.FilesStarted, Is.EqualTo(228));
        }

        [Test]
        public void IdenticalRerun_NothingStarted_AllSkipped()
        {
            var p = Parse(@"m1\t4_rerun_identical.log");
            Assert.That(p.FilesStarted, Is.EqualTo(0));
            Assert.That(p.Files, Is.EqualTo(new long[] { 232, 0, 232, 0, 0, 0 }));
            Assert.That(p.ToCounts().FilesSkipped, Is.EqualTo(232));
        }

        [Test]
        public void ChangedFiles_NewerAndNewFileClasses()
        {
            var entries = new List<LogEntry>();
            var p = Parse(@"m1\t4_rerun_3_changed.log", entries);
            Assert.That(p.FilesStarted, Is.EqualTo(3));
            Assert.That(p.Files[1], Is.EqualTo(3));
            Assert.That(entries.Select(e => e.Class).Distinct().OrderBy(c => c), Is.EquivalentTo(new[] { "New File", "Newer" }));
        }

        [Test]
        public void VerboseListing_ExcludedItemsAreNotCountedAsStarted()
        {
            var entries = new List<LogEntry>();
            var p = Parse(@"m1\t2_L_v_ts.log", entries);
            Assert.That(entries.Count(e => !e.IsDirectory && e.Class == "named"), Is.EqualTo(4), "desktop.ini, thumbs.db, ~$*, *.tmp");
            Assert.That(entries.Any(e => e.IsDirectory && e.Class == "junction"));
            Assert.That(entries.Any(e => e.IsDirectory && e.Class == "named" && e.Path.TrimEnd('\\').EndsWith("AppData")));
            Assert.That(p.FilesStarted, Is.EqualTo(228));
        }

        [Test]
        public void Errors_LockedDeniedAndFolder_NotElevated()
        {
            var p = Parse(@"m1\t7_errors_copy.log");
            var f = p.Failures.ToDictionary(x => Path.GetFileName(x.SourcePath));
            Assert.That(f.Keys, Is.EquivalentTo(new[] { "locked.docx", "denied.txt", "DeniedDir" }));

            Assert.That(f["locked.docx"].ErrorCode, Is.EqualTo(32));
            Assert.That(f["locked.docx"].Category, Is.EqualTo(FailureCategory.Locked));
            Assert.That(f["locked.docx"].IsDirectory, Is.False);
            Assert.That(f["locked.docx"].Attempts, Is.EqualTo(2), "/R:1 = first try + one retry");
            Assert.That(f["locked.docx"].Size, Is.EqualTo(2000));
            Assert.That(f["locked.docx"].Message, Does.Contain("being used by another process"));
            Assert.That(f["locked.docx"].Operation, Is.EqualTo("Copying File"));

            Assert.That(f["denied.txt"].Category, Is.EqualTo(FailureCategory.AccessDenied));
            Assert.That(f["denied.txt"].Size, Is.EqualTo(3000));
            Assert.That(f["DeniedDir"].IsDirectory, Is.True);
            Assert.That(f["DeniedDir"].SourcePath, Does.Not.EndWith("\\"));

            Assert.That(p.FilesStarted, Is.EqualTo(4), "a retried file's repeated line is counted once");
            Assert.That(p.BytesStarted, Is.EqualTo(11000));
            Assert.That(p.Files[4], Is.EqualTo(2));
            Assert.That(p.DiskFullErrors, Is.EqualTo(0));
            Assert.That(p.DestinationSuspectErrors, Is.EqualTo(0));
        }

        [Test]
        public void Errors_Elevated_BackupModeLeavesOnlyTheLockedFile()
        {
            var p = Parse(@"m1\elevated\t7_errors_copy.log");
            Assert.That(p.Failures.Select(x => Path.GetFileName(x.SourcePath)), Is.EquivalentTo(new[] { "locked.docx" }));
            Assert.That(p.Files, Is.EqualTo(new long[] { 5, 4, 0, 0, 1, 0 }));
            Assert.That(p.Options, Does.Contain("/B "));
        }

        [Test]
        public void ErrorOnScan_UnreadableFolderIsCountedButNotNamed()
        {
            // Robocopy /L writes no error line for a folder it cannot read, with or without /MT;
            // the only trace is the FAILED column of the Dirs row.
            foreach (var name in new[] { @"m1\t7_errors_L.log", @"m1\t7_errors_L_nomt.log" })
            {
                var p = Parse(name);
                Assert.That(p.Failures, Is.Empty, name);
                Assert.That(p.Dirs[4], Is.EqualTo(1), name);
            }
        }

        [Test]
        public void BackupRightsRefusal_IsAFatalErrorWithoutSummary()
        {
            var p = Parse(@"m1\t1_spec_verbatim.log");
            Assert.That(p.HasSummary, Is.False);
            Assert.That(p.FatalError, Does.Contain("Backup and Restore Files user rights"));
            Assert.That(p.FilesStarted, Is.EqualTo(0));
        }

        [Test]
        public void KilledRun_HasNoSummaryAndNoEnd()
        {
            var p = Parse(@"m1\t8_killed_run.log");
            Assert.That(p.HasSummary, Is.False);
            Assert.That(p.Ended, Is.False);
            Assert.That(p.FatalError, Is.Null);
        }

        [Test]
        public void ResumeRun_StartedFilesMatchCopiedColumn()
        {
            foreach (var name in new[] { @"m1\t8_resume_run.log", @"m1\elevated\t8_resume_run.log" })
            {
                var p = Parse(name);
                Assert.That(p.FilesStarted, Is.EqualTo(p.Files[1]), name);
                Assert.That(p.BytesStarted, Is.EqualTo(p.Bytes[1]), name);
                Assert.That(p.Files[2], Is.GreaterThan(0), name + ": finished files were skipped");
            }
        }

        [Test]
        public void EveryFixture_ParsesWithoutThrowing_AndFileLinesAgreeWithSummary()
        {
            int checkedLogs = 0;
            foreach (var path in Directory.GetFiles(FixturePath(""), "*.log", SearchOption.AllDirectories))
            {
                var p = new RobocopyLogParser();
                foreach (var line in File.ReadAllText(path, Encoding.Unicode).Split('\n')) p.Feed(line);
                if (!p.HasSummary || p.Options == null || !p.Options.Contains("/BYTES") || p.Options.Contains("/NFL")) continue;
                // every file Robocopy says it copied or failed has a line the parser counted
                Assert.That(p.FilesStarted, Is.EqualTo(p.Files[1] + p.Files[4]), Path.GetFileName(path));
                checkedLogs++;
            }
            Assert.That(checkedLogs, Is.GreaterThan(15));
        }

        [TestCase("1808733", 1808733L)]
        [TestCase("0", 0L)]
        [TestCase("1.7 m", 1782579L)]
        [TestCase("2.5 g", 2684354560L)]
        [TestCase("512 k", 524288L)]
        public void ParseSize(string text, long expected) => Assert.That(RobocopyLogParser.ParseSize(text), Is.EqualTo(expected));

        [Test]
        public void SyntheticDestinationErrors_AreFlagged()
        {
            // constructed lines in Robocopy's error format; real captures of these need hardware (see MANUAL tests)
            var p = new RobocopyLogParser();
            p.Feed("2026/11/14 10:00:00 ERROR 112 (0x00000070) Copying File C:\\src\\big.bin");
            p.Feed("There is not enough space on the disk.");
            p.Feed("2026/11/14 10:00:01 ERROR 53 (0x00000035) Copying File C:\\src\\a.txt");
            p.Feed("The network path was not found.");
            p.Feed("2026/11/14 10:00:02 ERROR 3 (0x00000003) Creating Destination Directory \\\\nas\\share\\PC\\C\\src\\");
            Assert.That(p.DiskFullErrors, Is.EqualTo(1));
            Assert.That(p.DestinationSuspectErrors, Is.EqualTo(2));
            var f = p.Failures.ToDictionary(x => x.SourcePath);
            Assert.That(f[@"C:\src\big.bin"].Category, Is.EqualTo(FailureCategory.DiskFull));
            Assert.That(f[@"C:\src\a.txt"].Category, Is.EqualTo(FailureCategory.DestinationUnavailable));
            Assert.That(f[@"\\nas\share\PC\C\src"].IsDirectory);
            Assert.That(f[@"\\nas\share\PC\C\src"].Operation, Is.EqualTo("Creating Destination Directory"));
        }
    }
}
