using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DataTransfer.Core;
using NUnit.Framework;

namespace DataTransfer.Core.Tests
{
    [TestFixture]
    public class FolderTreeTests
    {
        // an in-memory folder structure; nothing on disk
        static readonly string[] Folders =
        {
            @"C:\Users", @"C:\Users\Anna", @"C:\Users\Anna\Documents", @"C:\Users\Anna\Documents\Old",
            @"C:\Users\Anna\Documents\Old\Keep", @"C:\Users\Anna\Documents\Work", @"C:\Users\Anna\Pictures",
            @"C:\Users\Bob", @"C:\Users\Bob\Desktop", @"C:\Data", @"C:\Data\Projects", @"D:\Archive"
        };

        static int listCalls;
        static IEnumerable<string> List(string folder)
        {
            listCalls++;
            string f = folder.TrimEnd('\\');
            return Folders.Where(p => string.Equals(Path.GetDirectoryName(p)?.TrimEnd('\\'), f, StringComparison.OrdinalIgnoreCase));
        }

        static FolderTree Tree() => new FolderTree(new[] { @"C:\", @"D:\" }, List);

        [Test]
        public void Children_AreLoadedOnlyWhenAsked()
        {
            listCalls = 0;
            var t = Tree();
            Assert.That(listCalls, Is.EqualTo(0));
            Assert.That(t.Roots[0].ChildrenLoaded, Is.False);
            var kids = t.EnsureChildren(t.Roots[0]);
            Assert.That(kids.Select(k => k.Name), Is.EqualTo(new[] { "Users", "Data" }));
            t.EnsureChildren(t.Roots[0]);
            Assert.That(listCalls, Is.EqualTo(1), "loaded once");
            Assert.That(kids[0].ChildrenLoaded, Is.False);
        }

        [Test]
        public void TickingAFolder_TicksEverythingBelow_AndParentsShowMixed()
        {
            var t = Tree();
            Assert.That(t.Include(@"C:\Users\Anna\Documents"));
            var docs = t.Find(@"C:\Users\Anna\Documents");
            Assert.That(t.State(docs), Is.EqualTo(TriState.Checked));
            Assert.That(t.State(t.Find(@"C:\Users\Anna")), Is.EqualTo(TriState.Mixed));
            Assert.That(t.State(t.Roots[0]), Is.EqualTo(TriState.Mixed));
            Assert.That(t.State(t.Roots[1]), Is.EqualTo(TriState.Unchecked));
            // children loaded later inherit the tick
            Assert.That(t.EnsureChildren(docs).All(c => c.Included));
            Assert.That(t.State(t.Find(@"C:\Users\Anna\Documents\Old\Keep")), Is.EqualTo(TriState.Checked));
        }

        [Test]
        public void UntickingASubfolder_MakesItAnExclusion_AndTheParentMixed()
        {
            var t = Tree();
            t.Include(@"C:\Users\Anna\Documents");
            t.Toggle(t.Find(@"C:\Users\Anna\Documents\Old"));
            Assert.That(t.State(t.Find(@"C:\Users\Anna\Documents")), Is.EqualTo(TriState.Mixed));
            var s = t.ToSources().Single();
            Assert.That(s.Path, Is.EqualTo(@"C:\Users\Anna\Documents"));
            Assert.That(s.ExcludedSubfolders, Is.EqualTo(new[] { @"C:\Users\Anna\Documents\Old" }));
        }

        [Test]
        public void ClickingAMixedFolder_TicksAll_ClickingAgainUnticksAll()
        {
            var t = Tree();
            t.Include(@"C:\Users\Anna\Documents");
            t.Exclude(@"C:\Users\Anna\Documents\Old");
            var docs = t.Find(@"C:\Users\Anna\Documents");
            t.Toggle(docs);
            Assert.That(t.State(docs), Is.EqualTo(TriState.Checked));
            Assert.That(t.ToSources().Single().ExcludedSubfolders, Is.Empty);
            t.Toggle(docs);
            Assert.That(t.State(docs), Is.EqualTo(TriState.Unchecked));
            Assert.That(t.ToSources(), Is.Empty);
        }

        [Test]
        public void NestedIncludeInsideAnExclusion_IsItsOwnJob()
        {
            var t = Tree();
            t.Include(@"C:\Users\Anna");
            t.Exclude(@"C:\Users\Anna\Documents\Old");
            t.Include(@"C:\Users\Anna\Documents\Old\Keep");
            var s = t.ToSources();
            Assert.That(s.Select(x => x.Path), Is.EqualTo(new[] { @"C:\Users\Anna", @"C:\Users\Anna\Documents\Old\Keep" }));
            Assert.That(s[0].ExcludedSubfolders, Is.EqualTo(new[] { @"C:\Users\Anna\Documents\Old" }));
            Assert.That(s[1].ExcludedSubfolders, Is.Empty);
        }

        [Test]
        public void SiblingsAndDrives_GiveOneJobPerTickedTopLevelFolder()
        {
            var t = Tree();
            t.Include(@"C:\Users\Anna\Pictures");
            t.Include(@"C:\Users\Bob");
            t.Include(@"D:\");
            Assert.That(t.ToSources().Select(x => x.Path), Is.EqualTo(new[] { @"C:\Users\Anna\Pictures", @"C:\Users\Bob", @"D:\" }));
            t.Include(@"C:\Users");
            Assert.That(t.ToSources().Select(x => x.Path), Is.EqualTo(new[] { @"C:\Users", @"D:\" }), "ticking the parent merges the jobs");
        }

        [Test]
        public void TickingEverythingUnderAFolderByHand_StillCopiesItsOwnFilesOnlyIfItIsTicked()
        {
            var t = Tree();
            t.EnsureChildren(t.Find(@"C:\Users\Anna"));
            t.Include(@"C:\Users\Anna\Documents");
            t.Include(@"C:\Users\Anna\Pictures");
            // all children ticked, but Anna itself was not: it shows Mixed and its loose files are not copied
            Assert.That(t.State(t.Find(@"C:\Users\Anna")), Is.EqualTo(TriState.Mixed));
            Assert.That(t.ToSources().Select(x => x.Path), Is.EqualTo(new[] { @"C:\Users\Anna\Documents", @"C:\Users\Anna\Pictures" }));
        }

        [Test]
        public void Apply_RestoresASavedSelection()
        {
            var t = Tree();
            t.Include(@"C:\Users\Anna");
            t.Exclude(@"C:\Users\Anna\Documents\Old");
            t.Include(@"C:\Users\Anna\Documents\Old\Keep");
            t.Include(@"D:\Archive");
            var saved = t.ToSources();

            var t2 = Tree();
            t2.Include(@"C:\Data"); // replaced by Apply
            t2.Apply(saved);
            var again = t2.ToSources();
            Assert.That(again.Select(s => s.Path), Is.EqualTo(saved.Select(s => s.Path)));
            Assert.That(again.SelectMany(s => s.ExcludedSubfolders), Is.EqualTo(saved.SelectMany(s => s.ExcludedSubfolders)));
        }

        [Test]
        public void UnknownPath_IsRejected()
        {
            var t = Tree();
            Assert.That(t.Include(@"C:\Nope"), Is.False);
            Assert.That(t.Include(@"Q:\"), Is.False);
            Assert.That(t.ToSources(), Is.Empty);
        }

        [Test]
        public void RealFileSystem_SkipsJunctions_AndReportsUnreadableFolders()
        {
            string root = Path.Combine(Path.GetTempPath(), "dtt_tree_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(root, "b"));
            Directory.CreateDirectory(Path.Combine(root, "a"));
            try
            {
                using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(root, "j")}\" \"{Path.Combine(root, "a")}\"") { UseShellExecute = false, CreateNoWindow = true }))
                    p.WaitForExit();
                Assert.That(FolderTree.ListFileSystem(root).Select(Path.GetFileName), Is.EqualTo(new[] { "a", "b" }));

                var t = new FolderTree(new[] { Path.Combine(root, "missing") });
                Assert.That(t.EnsureChildren(t.Roots[0]), Is.Empty);
                Assert.That(t.Roots[0].LoadError, Is.Not.Null);
            }
            finally
            {
                if (Directory.Exists(Path.Combine(root, "j"))) Directory.Delete(Path.Combine(root, "j"), false); // the link only
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void UserDataPreset_TicksTheFourFoldersOfEveryRealProfile_IncludingOneDriveBackup()
        {
            string users = Path.Combine(Path.GetTempPath(), "dtt_users_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            foreach (var d in new[] { @"Anna\Desktop", @"Anna\Documents", @"Anna\Pictures", @"Anna\Downloads", @"Anna\Music", @"Anna\AppData",
                                      @"Bob\Documents", @"Bob\OneDrive - Contoso\Desktop", @"Bob\OneDrive - Contoso\Documents", @"Bob\OneDrive - Contoso\Other",
                                      @"Public\Documents", @"Default\Desktop" })
                Directory.CreateDirectory(Path.Combine(users, d));
            try
            {
                var profiles = new ProfileInfo { ProfilesDirectory = users, ProfileFolders = Directory.GetDirectories(users).ToList() };
                var found = UserDataPreset.Folders(profiles).Select(p => p.Substring(users.Length + 1));
                Assert.That(found, Is.EqualTo(new[] { @"Anna\Desktop", @"Anna\Documents", @"Anna\Pictures", @"Anna\Downloads",
                                                       @"Bob\Documents", @"Bob\OneDrive - Contoso\Desktop", @"Bob\OneDrive - Contoso\Documents" }));
            }
            finally { Directory.Delete(users, true); }
        }
    }
}
