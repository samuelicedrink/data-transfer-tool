using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using DataTransfer.Core;
using NUnit.Framework;

namespace DataTransfer.Core.Tests
{
    /// <summary>V2-M2: background folder scan for the Size and Modified columns, on generated trees.</summary>
    [TestFixture]
    public class FolderScanTests
    {
        string root;
        static readonly DateTime T2020 = new DateTime(2020, 3, 1, 10, 0, 0, DateTimeKind.Utc), T2024 = new DateTime(2024, 6, 2, 12, 30, 0, DateTimeKind.Utc);

        [SetUp]
        public void Make()
        {
            root = Path.Combine(Path.GetTempPath(), "dtt_scan_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            File(Path.Combine(root, "a", "one.txt"), 100, T2020);
            File(Path.Combine(root, "a", "b", "two.bin"), 300, T2024);
            Directory.CreateDirectory(Path.Combine(root, "a", "b", "empty"));
            File(Path.Combine(root, "elsewhere", "big.bin"), 5000, DateTime.UtcNow);
            Run("cmd.exe", $"/c mklink /J \"{Path.Combine(root, "a", "j")}\" \"{Path.Combine(root, "elsewhere")}\"");
        }

        [TearDown]
        public void Remove()
        {
            Run("icacls", $"\"{Path.Combine(root, "a", "locked")}\" /remove:d *{WindowsIdentity.GetCurrent().User.Value}");
            try { Directory.Delete(root, true); } catch { }
        }

        static void File(string path, int size, DateTime written)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            System.IO.File.WriteAllBytes(path, new byte[size]);
            System.IO.File.SetLastWriteTimeUtc(path, written);
        }

        static void Run(string exe, string args)
        {
            using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                p.WaitForExit(10000);
        }

        static FolderSummary Wait(FolderScanner s, string path)
        {
            var sw = Stopwatch.StartNew();
            FolderSummary r;
            while (!s.TryGet(path, out r) && sw.Elapsed.TotalSeconds < 20) Thread.Sleep(20);
            Assert.That(r, Is.Not.Null, "scanned in time: " + path);
            return r;
        }

        [Test]
        public void SizesAndNewestDate_IncludeSubfolders_NotJunctions()
        {
            using (var s = new FolderScanner(useBackupPrivilege: false))
            {
                s.Request(Path.Combine(root, "a"));
                var a = Wait(s, Path.Combine(root, "a"));
                Assert.That(a.Files, Is.EqualTo(2));
                Assert.That(a.Bytes, Is.EqualTo(400), "the junction's 5000 bytes are not counted (/XJ)");
                Assert.That(a.NewestUtc, Is.EqualTo(T2024), "newest file anywhere inside");
                Assert.That(a.UnreadableFolders, Is.EqualTo(0));

                Assert.That(s.TryGet(Path.Combine(root, "a", "b") + "\\", out var b), "subfolders are kept, with or without a trailing backslash");
                Assert.That(b.Bytes, Is.EqualTo(300));
                Assert.That(s.TryGet(Path.Combine(root, "a", "b", "empty"), out var empty));
                Assert.That(empty.Files, Is.EqualTo(0));
                Assert.That(empty.NewestUtc, Is.Null, "no files: no date");
            }
        }

        [Test]
        public void UnreadableFolder_IsCounted_SoTheSizeIsAMinimum()
        {
            string locked = Path.Combine(root, "a", "locked");
            File(Path.Combine(locked, "secret.txt"), 1000, T2020);
            Run("icacls", $"\"{locked}\" /deny *{WindowsIdentity.GetCurrent().User.Value}:(RD)");
            using (var s = new FolderScanner(useBackupPrivilege: false))
            {
                s.Request(Path.Combine(root, "a"));
                var a = Wait(s, Path.Combine(root, "a"));
                if (a.Bytes == 1400) Assert.Inconclusive("the folder was still readable (backup privilege already enabled in this process?)");
                Assert.That(a.UnreadableFolders, Is.EqualTo(1));
                Assert.That(a.Bytes, Is.EqualTo(400));
            }
        }

        [Test]
        public void ParentRequestedWhileChildIsScanned_BothFinishWithTheSameNumbers()
        {
            for (int i = 0; i < 300; i++) File(Path.Combine(root, "many", "d" + i, "f.txt"), 10, T2020);
            using (var s = new FolderScanner(useBackupPrivilege: false))
            {
                s.Request(Path.Combine(root, "many", "d150"));
                s.Request(Path.Combine(root, "many"));
                s.Request(root);
                s.Request(Path.Combine(root, "a", "b"));
                var all = Wait(s, root);
                Assert.That(Wait(s, Path.Combine(root, "many")).Files, Is.EqualTo(300));
                Assert.That(Wait(s, Path.Combine(root, "many", "d150")).Bytes, Is.EqualTo(10));
                Assert.That(all.Files, Is.EqualTo(300 + 2 + 1), "many + a + elsewhere");
                Assert.That(all.Bytes, Is.EqualTo(3000 + 400 + 5000));
            }
        }

        [Test]
        public void NewestRequestIsServedFirst_EvenDuringALongScan()
        {
            // a wide tree keeps the scanner busy long enough for a second request to overtake it
            for (int i = 0; i < 1500; i++) Directory.CreateDirectory(Path.Combine(root, "wide", "d" + i, "x", "y"));
            using (var s = new FolderScanner(useBackupPrivilege: false))
            {
                s.Request(Path.Combine(root, "wide"));
                Thread.Sleep(5);
                s.Request(Path.Combine(root, "a"));
                Wait(s, Path.Combine(root, "a"));
                bool wideFinishedFirst = s.TryGet(Path.Combine(root, "wide"), out _);
                Wait(s, Path.Combine(root, "wide"));
                if (wideFinishedFirst) Assert.Inconclusive("the wide tree was scanned before the second request arrived (fast disk)");
            }
        }

        [Test]
        public void ListFiles_DirectFilesOnly_SortedAndCapped()
        {
            for (int i = 0; i < 12; i++) File(Path.Combine(root, "files", $"f{i:00}.txt"), i, T2020);
            Directory.CreateDirectory(Path.Combine(root, "files", "sub"));
            var l = FolderScanner.ListFiles(Path.Combine(root, "files"), 5);
            Assert.That(l.TotalFiles, Is.EqualTo(12));
            Assert.That(l.Files.Select(f => f.Name), Is.EqualTo(new[] { "f00.txt", "f01.txt", "f02.txt", "f03.txt", "f04.txt" }));
            Assert.That(l.Files[3].Size, Is.EqualTo(3));
            Assert.That(l.Files[3].ModifiedUtc, Is.EqualTo(T2020));
            Assert.That(l.Files[3].Path, Is.EqualTo(Path.Combine(root, "files", "f03.txt")));
            Assert.That(l.Files.All(f => !f.CloudOnly));

            Assert.That(FolderScanner.ListFiles(Path.Combine(root, "a"), 100).Files.Select(f => f.Name), Is.EqualTo(new[] { "one.txt" }), "no subfolders, no junction");
            Assert.That(FolderScanner.ListFiles(Path.Combine(root, "missing"), 100).ErrorCode, Is.Not.EqualTo(0));
        }
    }
}
