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
    /// <summary>
    /// V2-M3 relevance score and Advice. No Windows accounts are created (owner, 2026-11-15): file
    /// owners are simulated with made-up SIDs on a generated tree; owner reading itself is checked
    /// read-only on files this test creates.
    /// </summary>
    [TestFixture]
    public class RelevanceTests
    {
        const string AnnaSid = "S-1-5-21-1000-2000-3000-1001", BobSid = "S-1-5-21-1000-2000-3000-1002";
        string root, users, anna, bob;
        RelevanceTarget annaT, bobT;

        [SetUp]
        public void Make()
        {
            root = Path.Combine(Path.GetTempPath(), "dtt_rel_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            users = Path.Combine(root, "Users");
            anna = Path.Combine(users, "anna");
            bob = Path.Combine(users, "bob");
            Write(Path.Combine(anna, "Pictures", "holiday.jpg"), 1000);
            Write(Path.Combine(anna, "Downloads", "setup.exe"), 300);
            Write(Path.Combine(anna, "AppData", "Roaming", "Adobe", "Adobe Photoshop 2024", "Presets", "brush.abr"), 200);
            Write(Path.Combine(anna, "AppData", "Local", "Temp", "x.tmp"), 500);
            Write(Path.Combine(anna, "AppData", "Local", "Kakao", "KakaoTalk", "users", "chat.edb"), 400);
            Write(Path.Combine(anna, "AppData", "Roaming", "Microsoft", "Teams", "settings.json"), 100);
            Write(Path.Combine(bob, "Documents", "bob.docx"), 700);
            Write(Path.Combine(users, "Public", "Documents", "shared.pdf"), 600);
            annaT = new RelevanceTarget { Sid = AnnaSid, Name = "anna", ProfilePath = anna };
            bobT = new RelevanceTarget { Sid = BobSid, Name = "bob", ProfilePath = bob };
        }

        [TearDown]
        public void Remove() { try { Directory.Delete(root, true); } catch { } }

        static void Write(string path, int size)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[size]);
        }

        RelevanceClassifier Classifier() => new RelevanceClassifier(Directory.GetDirectories(users), readSecurity: false)
        {
            // simulated owners: each profile's files belong to its user, everything else to Administrators
            OwnerOverride = p => PathUtil.IsSameOrUnder(p, anna) ? AnnaSid : PathUtil.IsSameOrUnder(p, bob) ? BobSid : UserDirectory.AdministratorsSid,
            FolderAclOverride = d => string.Equals(PathUtil.Trim(d), anna, StringComparison.OrdinalIgnoreCase) ? new[] { AnnaSid, "S-1-5-18" } : null
        };

        RelevanceResult File1(RelevanceClassifier c, string path, RelevanceTarget t)
        {
            var key = c.KeyFor(c.ForFolder(Path.GetDirectoryName(path)), path, Path.GetFileName(path));
            return c.Evaluate(new[] { new RelevanceGroup { Key = key, Bytes = new FileInfo(path).Length, Count = 1 } }, t);
        }

        [Test]
        public void OwnPicture_IsLikely_AndAnotherUsersDocument_IsProbablyNot()
        {
            var c = Classifier();
            var pic = File1(c, Path.Combine(anna, "Pictures", "holiday.jpg"), annaT);
            Assert.That(pic.Score, Is.EqualTo(100), "50 + owner 35 + profile 15 + picture 10, clamped");
            Assert.That(pic.Advice, Does.StartWith("Likely anna's: owned by anna, in anna's profile, pictures"));
            Assert.That(pic.Weak, Is.False);

            var doc = File1(c, Path.Combine(bob, "Documents", "bob.docx"), annaT);
            Assert.That(doc.Score, Is.EqualTo(0), "50 - other owner 40 - other profile 30 + document 10, clamped");
            Assert.That(doc.Advice, Does.StartWith("Probably not anna's: owned by another user, in bob's profile"));

            // the same file for bob
            Assert.That(File1(c, Path.Combine(bob, "Documents", "bob.docx"), bobT).Score, Is.EqualTo(100));
            Assert.That(File1(c, Path.Combine(anna, "Pictures", "holiday.jpg"), bobT).Score, Is.EqualTo(0));
        }

        [Test]
        public void Downloads_AreNotPenalised_ButCachesAre()
        {
            var c = Classifier();
            Assert.That(File1(c, Path.Combine(anna, "Downloads", "setup.exe"), annaT).Score, Is.EqualTo(100), "a downloaded installer is the user's file (owner, 2026-11-15)");
            var tmp = File1(c, Path.Combine(anna, "AppData", "Local", "Temp", "x.tmp"), annaT);
            Assert.That(tmp.Score, Is.EqualTo(70), "50 + 35 + 15 - cache 30");
            Assert.That(tmp.Advice, Does.StartWith("Review: ").And.Contain("program or cache files"));
        }

        [Test]
        public void KnownApps_AreNamed_WithTheirNotes()
        {
            var c = Classifier();
            var ps = File1(c, Path.Combine(anna, "AppData", "Roaming", "Adobe", "Adobe Photoshop 2024", "Presets", "brush.abr"), annaT);
            Assert.That(ps.Advice, Does.Contain("Photoshop settings"));
            var kakao = File1(c, Path.Combine(anna, "AppData", "Local", "Kakao", "KakaoTalk", "users", "chat.edb"), annaT);
            Assert.That(kakao.Advice, Does.Contain("KakaoTalk chats (backup only, will not open on another PC)"));

            // Teams is named but not scored up: its chats are in Microsoft 365
            var teamsKey = c.KeyFor(c.ForFolder(Path.Combine(anna, "AppData", "Roaming", "Microsoft", "Teams")), Path.Combine(anna, "AppData", "Roaming", "Microsoft", "Teams", "settings.json"), "settings.json");
            Assert.That(c.Score(teamsKey, annaT), Is.EqualTo(100), "owner and profile only");
            Assert.That(c.AppOf(teamsKey).Boost, Is.False);
            Assert.That(c.Evaluate(new[] { new RelevanceGroup { Key = teamsKey, Bytes = 100, Count = 1 } }, annaT).Advice, Does.Contain("nothing to copy"));

            // with bob picked, anna's Photoshop presets are still named, but scored down
            var psBob = File1(c, Path.Combine(anna, "AppData", "Roaming", "Adobe", "Adobe Photoshop 2024", "Presets", "brush.abr"), bobT);
            Assert.That(psBob.Score, Is.EqualTo(0));
            Assert.That(psBob.Advice, Does.Contain("Photoshop settings"));
        }

        [Test]
        public void AdministratorsOwnedFiles_AreAWeakEstimate_AndPublicIsNobodysProfile()
        {
            var c = Classifier();
            var shared = File1(c, Path.Combine(users, "Public", "Documents", "shared.pdf"), annaT);
            Assert.That(shared.Score, Is.EqualTo(35), "50 - Administrators 25 + document 10; Public is not another user's profile");
            Assert.That(shared.Weak);
            Assert.That(shared.Advice, Does.StartWith("Probably not anna's").And.Contain("weak estimate: owned by Administrators"));

            var noOwner = new RelevanceClassifier(Directory.GetDirectories(users), readSecurity: false) { OwnerOverride = _ => null };
            var fat = File1(noOwner, Path.Combine(anna, "Pictures", "holiday.jpg"), annaT);
            Assert.That(fat.Score, Is.EqualTo(75), "no owner: profile 15 + picture 10");
            Assert.That(fat.Advice, Does.Contain("no owner recorded"));
        }

        [Test]
        public void ProgramFiles_ArePrograms_AndFolderPermissionsAddALittle()
        {
            var c = Classifier();
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var key = c.KeyFor(c.ForFolder(Path.Combine(pf, "SomeApp")), Path.Combine(pf, "SomeApp", "app.dll"), "app.dll");
            var r = c.Evaluate(new[] { new RelevanceGroup { Key = key, Bytes = 10, Count = 1 } }, annaT);
            Assert.That(r.Score, Is.EqualTo(0), "50 - Administrators 25 - system area 20 - program 30");
            Assert.That(r.Advice, Does.StartWith("Program files: "));

            // a file directly in anna's profile folder, whose permissions name her
            Write(Path.Combine(anna, "notes.bin"), 10);
            var top = File1(c, Path.Combine(anna, "notes.bin"), annaT);
            Assert.That(top.Advice, Does.Contain("folder permissions name anna"));
            var bobView = File1(c, Path.Combine(anna, "notes.bin"), bobT);
            Assert.That(bobView.Advice, Does.Not.Contain("folder permissions"));
        }

        [Test]
        public void FolderScore_WeightsGroupsBySize()
        {
            var c = Classifier();
            using (var s = new FolderScanner(useBackupPrivilege: false, classifier: c))
            {
                s.Request(users);
                var sw = Stopwatch.StartNew();
                FolderSummary sum;
                while (!s.TryGet(users, out sum) && sw.Elapsed.TotalSeconds < 20) Thread.Sleep(20);
                Assert.That(sum, Is.Not.Null);
                Assert.That(sum.Groups.Sum(g => g.Count), Is.EqualTo(8));
                Assert.That(sum.Groups.Sum(g => g.Bytes), Is.EqualTo(1000 + 300 + 200 + 500 + 400 + 100 + 700 + 600));

                // anna's profile for anna: 2000 bytes at 100 and 500 bytes (Temp) at 70
                Assert.That(s.TryGet(anna, out var a));
                var forAnna = c.Evaluate(a.Groups, annaT);
                Assert.That(forAnna.Score, Is.EqualTo((int)Math.Round((2000 * 100 + 500 * 70) / 2500.0)));
                Assert.That(forAnna.Advice, Does.StartWith("Likely anna's").And.Contain("KakaoTalk chats").And.Contain("Photoshop settings"));
                // and for bob: all of it scored down
                Assert.That(c.Evaluate(a.Groups, bobT).Score, Is.LessThan(RelevanceWeights.Review));

                // the files listed under an opened folder get their key too
                var listed = FolderScanner.ListFiles(Path.Combine(anna, "Pictures"), 10, c);
                Assert.That(c.Evaluate(listed.Files[0].AsGroups(), annaT).Score, Is.EqualTo(100));
            }
        }

        // ---------------------------------------------------------------- real system, read-only

        [Test]
        public void ThisPc_OwnerOfANewFile_IsTheCurrentUserOrAdministrators()
        {
            string f = Path.Combine(root, "mine.txt");
            Write(f, 1);
            string owner = Security.Owner(f);
            string me = WindowsIdentity.GetCurrent().User.Value;
            // an elevated administrator's new files may be owned by Administrators (Windows default owner policy)
            Assert.That(owner, Is.EqualTo(me).Or.EqualTo(UserDirectory.AdministratorsSid));

            var real = new RelevanceClassifier(Directory.GetDirectories(users));
            var key = real.KeyFor(real.ForFolder(root), f, "mine.txt");
            Assert.That(real.OwnerOf(key), Is.EqualTo(owner));
            Assert.That(Security.Owner(Path.Combine(root, "missing.txt")), Is.Null);
        }

        [Test]
        public void ThisPc_ExplicitPermissions_AreReadFromAFolder()
        {
            string d = Path.Combine(root, "acl");
            Directory.CreateDirectory(d);
            Assert.That(Security.ExplicitAllowSids(d), Is.Empty, "a new folder only inherits");
            using (var p = Process.Start(new ProcessStartInfo("icacls", $"\"{d}\" /grant *S-1-5-32-545:(R)") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
                p.WaitForExit(10000);
            Assert.That(Security.ExplicitAllowSids(d), Does.Contain("S-1-5-32-545"));
        }
    }
}
