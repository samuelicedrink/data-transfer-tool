using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DataTransfer.Core;
using NUnit.Framework;

namespace DataTransfer.Core.Tests
{
    [TestFixture]
    public class BuilderTests
    {
        static readonly ProfileInfo Profiles = new ProfileInfo
        {
            ProfilesDirectory = @"C:\Users",
            ProfileFolders = new List<string> { @"C:\Users\Anna", @"C:\Users\Bob" }
        };

        static JobDefinition Job(params string[] sources)
        {
            var j = new JobDefinition { ComputerName = "PC01", DestinationRoot = @"E:\Backup" };
            foreach (var s in sources) j.Sources.Add(new SourceSelection { Path = s });
            return j;
        }

        [Test]
        public void DestinationLayout_IsDestComputerDrivePath()
        {
            var j = Job();
            Assert.That(j.DestinationFor(@"C:\Users\Anna\Documents"), Is.EqualTo(@"E:\Backup\PC01\C\Users\Anna\Documents"));
            Assert.That(j.DestinationFor(@"d:\Data\"), Is.EqualTo(@"E:\Backup\PC01\D\Data"));
            Assert.That(j.DestinationFor(@"C:\"), Is.EqualTo(@"E:\Backup\PC01\C"));
            j.DestinationRoot = @"\\nas\share\exports\";
            Assert.That(j.DestinationFor(@"C:\Users"), Is.EqualTo(@"\\nas\share\exports\PC01\C\Users"));
        }

        [Test]
        public void MainPass_HasTheApprovedFlagSetAndDefaultFileExclusions()
        {
            var j = Job(@"C:\Users\Anna\Documents");
            string cmd = RobocopyCommandBuilder.Build(j, j.Sources[0], @"C:\Logs\a.log", false, Profiles);
            Assert.That(cmd, Is.EqualTo(
                "\"C:\\Users\\Anna\\Documents\" \"E:\\Backup\\PC01\\C\\Users\\Anna\\Documents\" " +
                "/E /B /R:1 /W:2 /XJ /COPY:DAT /DCOPY:DAT /FFT /MT:8 /NP /BYTES /FP /XA:O /A-:SH " +
                "/XF \"desktop.ini\" \"thumbs.db\" \"~$*\" \"*.tmp\" /UNILOG+:\"C:\\Logs\\a.log\""));
        }

        [Test]
        public void Scan_AddsListOnly()
        {
            var j = Job(@"C:\Data");
            Assert.That(RobocopyCommandBuilder.Build(j, j.Sources[0], @"C:\l.log", true, Profiles), Does.Contain(" /L "));
            Assert.That(RobocopyCommandBuilder.Build(j, j.Sources[0], @"C:\l.log", false, Profiles), Does.Not.Contain(" /L "));
        }

        [Test]
        public void Options_ChangeTheFlags()
        {
            var j = Job(@"C:\Data");
            j.Options.BackupMode = false; j.Options.ExcludeCloudOnly = false; j.Options.Threads = 4; j.Options.Retries = 3; j.Options.WaitSeconds = 9;
            string cmd = RobocopyCommandBuilder.Build(j, j.Sources[0], @"C:\l.log", false, Profiles);
            Assert.That(cmd, Does.Contain("/E /R:3 /W:9 /XJ /COPY:DAT /DCOPY:DAT /FFT /MT:4 /NP /BYTES /FP /A-:SH /XF"));
            Assert.That(cmd, Does.Not.Contain("/B ").And.Not.Contain("/XA:O"));
        }

        [Test]
        public void UntickedSubfolders_BecomeXd()
        {
            var j = Job(@"C:\Data");
            j.Sources[0].ExcludedSubfolders.Add(@"C:\Data\Old stuff");
            j.Sources[0].ExcludedSubfolders.Add(@"C:\Data\Projects\Archive\");
            string cmd = RobocopyCommandBuilder.Build(j, j.Sources[0], @"C:\l.log", false, Profiles);
            Assert.That(cmd, Does.Contain("/XD \"C:\\Data\\Old stuff\" \"C:\\Data\\Projects\\Archive\" /XF"));
        }

        [Test]
        public void AppData_IsExcludedByFullPathPerProfile_NotByName()
        {
            var j = Job(@"C:\Users", @"C:\Users\Anna", @"C:\Users\Anna\Documents", @"C:\");
            string users = RobocopyCommandBuilder.Build(j, j.Sources[0], @"C:\l.log", false, Profiles);
            Assert.That(users, Does.Contain("/XD \"C:\\Users\\Anna\\AppData\" \"C:\\Users\\Bob\\AppData\" /XF"));

            string anna = RobocopyCommandBuilder.Build(j, j.Sources[1], @"C:\l.log", false, Profiles);
            Assert.That(anna, Does.Contain("/XD \"C:\\Users\\Anna\\AppData\" /XF"));
            Assert.That(anna, Does.Not.Contain("Bob"));

            string docs = RobocopyCommandBuilder.Build(j, j.Sources[2], @"C:\l.log", false, Profiles);
            Assert.That(docs, Does.Not.Contain("/XD").And.Not.Contain("AppData"), "a folder named AppData inside Documents is copied");

            string drive = RobocopyCommandBuilder.Build(j, j.Sources[3], @"C:\l.log", false, Profiles);
            Assert.That(drive, Does.StartWith("\"C:\\\\\" \"E:\\Backup\\PC01\\C\" "), "drive root keeps a doubled backslash so the quote is not escaped");
            Assert.That(drive, Does.Contain("\"C:\\Users\\Anna\\AppData\" \"C:\\Users\\Bob\\AppData\""));
        }

        [Test]
        public void AnyDepthNames_AreStillPossible()
        {
            var j = Job(@"C:\Data");
            j.Options.ExcludeDirNames.Add("node_modules");
            Assert.That(RobocopyCommandBuilder.Build(j, j.Sources[0], @"C:\l.log", false, Profiles), Does.Contain("/XD \"node_modules\""));
        }

        [Test]
        public void DestinationInsideSource_IsExcluded()
        {
            var j = Job(@"E:\");
            Assert.That(RobocopyCommandBuilder.Build(j, j.Sources[0], @"C:\l.log", false, Profiles), Does.Contain("/XD \"E:\\Backup\""));
        }

        [Test]
        public void FileRetry_IsNotRecursive_AndChunksLongLists()
        {
            var j = Job(@"C:\Data");
            var one = RobocopyCommandBuilder.BuildFileRetry(j, @"C:\Data\Sub", new[] { "a b.docx", "c.xlsx" });
            Assert.That(one.Single(), Is.EqualTo(
                "\"C:\\Data\\Sub\" \"E:\\Backup\\PC01\\C\\Data\\Sub\" \"a b.docx\" \"c.xlsx\" " +
                "/B /R:1 /W:2 /XJ /COPY:DAT /DCOPY:DAT /FFT /MT:8 /NP /BYTES /FP /XA:O /A-:SH"));
            Assert.That(RobocopyCommandBuilder.WithLog(one[0], @"C:\x y\l.log"), Does.EndWith(" /UNILOG+:\"C:\\x y\\l.log\""));

            var names = Enumerable.Range(0, 3000).Select(i => $"file number {i:00000} with a longish name.docx").ToList();
            var many = RobocopyCommandBuilder.BuildFileRetry(j, @"C:\Data\Sub", names);
            Assert.That(many.Count, Is.GreaterThan(1));
            Assert.That(many.All(c => c.Length < RobocopyCommandBuilder.MaxArgumentLength));
            Assert.That(many.Sum(c => c.Split(new[] { "file number" }, StringSplitOptions.None).Length - 1), Is.EqualTo(3000));
        }

        [Test]
        public void Quote_RejectsQuotesAndTrimsTrailingBackslash()
        {
            Assert.That(RobocopyCommandBuilder.Quote(@"C:\a b\"), Is.EqualTo("\"C:\\a b\""));
            Assert.That(RobocopyCommandBuilder.Quote(@"C:\"), Is.EqualTo("\"C:\\\\\""));
            Assert.Throws<ArgumentException>(() => RobocopyCommandBuilder.Quote("a\"b"));
        }
    }

    [TestFixture]
    public class MiscTests
    {
        [Test]
        public void LogTail_ArbitraryChunking_GivesTheSameLines()
        {
            byte[] raw = File.ReadAllBytes(ParserTests.FixturePath(@"m1\t3_copy_mt.log"));
            var expected = Encoding.Unicode.GetString(raw).TrimStart('\uFEFF').Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (expected.Last().Length == 0) expected.RemoveAt(expected.Count - 1);

            var lines = new List<string>();
            using (var tail = new LogTail("unused", lines.Add))
            {
                var rng = new Random(1);
                for (int pos = 0; pos < raw.Length;)
                {
                    int n = Math.Min(raw.Length - pos, rng.Next(1, 37)); // odd sizes split characters and surrogates
                    var chunk = new byte[n]; Array.Copy(raw, pos, chunk, 0, n);
                    tail.Decode(chunk, n); pos += n;
                }
                tail.Flush();
            }
            lines[0] = lines[0].TrimStart('\uFEFF');
            Assert.That(lines, Is.EqualTo(expected));
        }

        [Test]
        public void LogTail_ReadsAFileWhileItGrows()
        {
            string path = Path.Combine(Path.GetTempPath(), "dtt_tail_" + Guid.NewGuid().ToString("N") + ".log");
            var lines = new List<string>();
            try
            {
                using (var tail = new LogTail(path, lines.Add))
                {
                    tail.Pump(); // file not there yet
                    using (var w = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        void Write(string s) { var b = Encoding.Unicode.GetBytes(s); w.Write(b, 0, b.Length); w.Flush(); }
                        Write("first\r\nsec"); tail.Pump();
                        Assert.That(lines, Is.EqualTo(new[] { "first" }));
                        Write("ond\r\nthird"); tail.Pump();
                        Assert.That(lines, Is.EqualTo(new[] { "first", "second" }));
                    }
                    tail.Pump(); tail.Flush();
                    Assert.That(lines, Is.EqualTo(new[] { "first", "second", "third" }));
                }
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void RateEstimator_AveragesOverTheWindow_AndToleratesStalls()
        {
            var r = new RateEstimator(TimeSpan.FromSeconds(30));
            Assert.That(r.Eta(0, 1000), Is.Null, "no rate yet");
            r.Add(TimeSpan.FromSeconds(0), 0);
            r.Add(TimeSpan.FromSeconds(10), 100L << 20);
            Assert.That(r.BytesPerSecond, Is.EqualTo(10.0 * (1 << 20)).Within(1));
            Assert.That(r.Eta(100L << 20, 200L << 20).Value.TotalSeconds, Is.EqualTo(10).Within(0.01));
            // a 10 s stall halves the average instead of dropping it to zero
            r.Add(TimeSpan.FromSeconds(20), 100L << 20);
            Assert.That(r.BytesPerSecond, Is.EqualTo(5.0 * (1 << 20)).Within(1));
            Assert.That(r.Eta(100L << 20, null), Is.Null, "unknown total");
            Assert.That(r.Eta(300L << 20, 200L << 20), Is.EqualTo(TimeSpan.Zero));
            // a stall longer than the window means "unknown", not a wild number
            r.Add(TimeSpan.FromSeconds(55), 100L << 20);
            r.Add(TimeSpan.FromSeconds(60), 100L << 20);
            Assert.That(r.Eta(100L << 20, 200L << 20), Is.Null);
            r.Reset();
            Assert.That(r.BytesPerSecond, Is.EqualTo(0));
        }

        [Test]
        public void JobStore_RoundTripsTheWholeJob_AndFallsBackWhenPreferredIsUnusable()
        {
            string dir = Path.Combine(Path.GetTempPath(), "dtt_store_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var job = new JobDefinition { ComputerName = "PC01", DestinationRoot = @"\\nas\share", DestinationUserName = @"corp\tech", State = JobState.Cancelled };
                job.Options.ExcludeFiles.Add("*.bak"); job.Options.Threads = 4;
                var s = new SourceSelection { Path = @"C:\Users\Anna\Documents", Status = SourceStatus.CompletedWithErrors };
                s.ExcludedSubfolders.Add(@"C:\Users\Anna\Documents\Old");
                s.Totals.FilesCopied = 12; s.Totals.BytesCopied = 3456;
                s.Failures.Add(new FileFailure { SourcePath = @"C:\Users\Anna\Documents\résumé.docx", ErrorCode = 32, Category = FailureCategory.Locked, Message = "in use", Size = 9 });
                job.Sources.Add(s);

                var store = new JobStore(dir);
                store.Save(job);
                store.Save(job); // second save replaces the first
                var back = store.Load();
                Assert.That(back.Id, Is.EqualTo(job.Id));
                Assert.That(back.State, Is.EqualTo(JobState.Cancelled));
                Assert.That(back.IsUnfinished);
                Assert.That(back.DestinationUserName, Is.EqualTo(@"corp\tech"));
                Assert.That(back.Options.ExcludeFiles, Does.Contain("*.bak"));
                Assert.That(back.Options.Threads, Is.EqualTo(4));
                Assert.That(back.Sources[0].ExcludedSubfolders.Single(), Is.EqualTo(@"C:\Users\Anna\Documents\Old"));
                Assert.That(back.Sources[0].Totals.BytesCopied, Is.EqualTo(3456));
                Assert.That(back.Sources[0].Failures.Single().SourcePath, Is.EqualTo(@"C:\Users\Anna\Documents\résumé.docx"));
                Assert.That(back.Sources[0].Failures.Single().Category, Is.EqualTo(FailureCategory.Locked));
                Assert.That(File.ReadAllText(store.JobFile).ToLowerInvariant(), Does.Not.Contain("password"));

                string missing = Path.Combine(dir, "does-not-exist");
                string fallback = Path.Combine(dir, "fallback");
                Assert.That(JobStore.Resolve(missing, fallback).Directory, Is.EqualTo(fallback));
                Assert.That(JobStore.Resolve(dir, fallback).Directory, Is.EqualTo(dir));
                store.Delete();
                Assert.That(store.Load(), Is.Null);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void JobModel_HasNoPlaceForAPassword()
        {
            foreach (var t in new[] { typeof(JobDefinition), typeof(CopyOptions), typeof(SourceSelection) })
                Assert.That(t.GetProperties().Select(p => p.Name.ToLowerInvariant()).Where(n => n.Contains("password") || n.Contains("secret")), Is.Empty);
        }

        [Test]
        public void PathUtil_LongPrefixAndContainment()
        {
            Assert.That(PathUtil.Long(@"C:\a"), Is.EqualTo(@"\\?\C:\a"));
            Assert.That(PathUtil.Long(@"\\nas\share\a"), Is.EqualTo(@"\\?\UNC\nas\share\a"));
            Assert.That(PathUtil.Long(@"\\?\C:\a"), Is.EqualTo(@"\\?\C:\a"));
            Assert.That(PathUtil.IsSameOrUnder(@"C:\Users\Anna\Docs", @"c:\users\anna\"));
            Assert.That(PathUtil.IsSameOrUnder(@"C:\Users\Annabel", @"C:\Users\Anna"), Is.False);
        }
    }
}
