using System;
using System.IO;
using System.Linq;
using System.Text;
using DataTransfer.Core;
using NUnit.Framework;

namespace DataTransfer.Core.Tests
{
    [TestFixture]
    public class ReportingTests
    {
        string dir;

        [SetUp] public void SetUp() { dir = Path.Combine(Path.GetTempPath(), "dtt_rep_" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(dir); }
        [TearDown] public void TearDown() { try { Directory.Delete(dir, true); } catch { } }

        const string Password = "Pa55w0rd-Ünïcødé!";

        [Test]
        public void Credentials_RoundTrip_AndAreNotReadableOnDisk()
        {
            var store = new CredentialStore(dir);
            Assert.That(store.TryLoad("job1", out _, out _), Is.False, "nothing saved yet");
            store.Save("job1", @"CORP\tech", Password);
            Assert.That(store.Exists);

            Assert.That(store.TryLoad("job1", out string user, out string pw));
            Assert.That(user, Is.EqualTo(@"CORP\tech"));
            Assert.That(pw, Is.EqualTo(Password));

            byte[] raw = File.ReadAllBytes(store.FilePath);
            foreach (var enc in new Encoding[] { Encoding.UTF8, Encoding.Unicode, Encoding.ASCII })
            {
                string text = enc.GetString(raw);
                Assert.That(text, Does.Not.Contain("Pa55w0rd").And.Not.Contain("tech"), "plain text not in the file (" + enc.WebName + ")");
            }
            Assert.That(Convert.ToBase64String(raw), Does.Not.Contain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Password))));
        }

        [Test]
        public void Credentials_AreBoundToTheJob()
        {
            var store = new CredentialStore(dir);
            store.Save("job1", "u", Password);
            Assert.That(store.TryLoad("job2", out _, out _), Is.False, "another job cannot open them");
            store.Save("job1", "u2", "other");
            Assert.That(store.TryLoad("job1", out string u, out string p) && u == "u2" && p == "other", "save replaces");
            store.Delete();
            Assert.That(store.Exists, Is.False);
            Assert.That(store.TryLoad("job1", out _, out _), Is.False);
        }

        [Test]
        public void Credentials_CorruptFile_IsIgnored()
        {
            var store = new CredentialStore(dir);
            File.WriteAllBytes(store.FilePath, new byte[] { 1, 2, 3, 4 });
            Assert.That(store.TryLoad("job1", out _, out _), Is.False);
        }

        [Test]
        public void JobFile_NeverContainsThePassword_EvenWhenCredentialsAreSaved()
        {
            var job = new JobDefinition { DestinationRoot = @"\\nas\share", DestinationUserName = @"CORP\tech" };
            var js = new JobStore(dir);
            js.Save(job);
            js.Credentials.Save(job.Id, job.DestinationUserName, Password);
            Assert.That(File.ReadAllText(js.JobFile), Does.Not.Contain("Pa55w0rd"));
            Assert.That(js.Credentials.TryLoad(job.Id, out _, out string pw) && pw == Password);
        }

        [Test]
        public void Archive_MovesTheJobToTheLogFolder_AndDeletesCredentials()
        {
            var job = new JobDefinition { DestinationRoot = @"E:\x" };
            var js = new JobStore(dir);
            js.Save(job);
            js.Credentials.Save(job.Id, "u", "p");
            string archived = js.Archive(job.Id);
            Assert.That(File.Exists(js.JobFile), Is.False);
            Assert.That(js.Credentials.Exists, Is.False);
            Assert.That(archived, Does.StartWith(js.LogDirectory));
            Assert.That(File.ReadAllText(archived), Does.Contain(job.Id));
            Assert.That(js.Archive(job.Id), Is.Null, "nothing left to archive");
        }

        [Test]
        public void FailureCsv_HasAReadableRowPerFailure()
        {
            var job = new JobDefinition { ComputerName = "PC01", DestinationRoot = @"E:\Export" };
            var s = new SourceSelection { Path = @"C:\Users\Anna\Documents" };
            s.Failures.Add(new FileFailure { SourcePath = @"C:\Users\Anna\Documents\Budget, final.xlsx", ErrorCode = 32, Category = FailureCategory.Locked, Message = "Held by \"EXCEL\", try later.", Size = 1234, Attempts = 2, Time = "2026/11/15 10:00:00" });
            s.Failures.Add(new FileFailure { SourcePath = @"C:\Users\Anna\Documents\Private", IsDirectory = true, ErrorCode = 5, Category = FailureCategory.AccessDenied, Message = "Access is denied.", Attempts = 2 });
            s.Failures.Add(new FileFailure { SourcePath = @"C:\Users\Anna\Documents\=cmd.txt", ErrorCode = 2, Category = FailureCategory.NotFound, Message = "x" });
            job.Sources.Add(s);

            string path = Path.Combine(dir, "sub", "failures.csv");
            FailureCsv.Write(job, path);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.That(bytes.Take(3), Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }), "UTF-8 BOM for Excel");
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            Assert.That(lines.Length, Is.EqualTo(4));
            Assert.That(lines[0], Does.StartWith("\"Source path\",\"Destination path\",\"Type\",\"Reason\""));
            string budget = lines.Single(l => l.Contains("Budget"));
            Assert.That(budget, Does.StartWith("\"C:\\Users\\Anna\\Documents\\Budget, final.xlsx\","), "a comma stays inside its quoted cell");
            Assert.That(budget, Does.Contain("\"E:\\Export\\PC01\\C\\Users\\Anna\\Documents\\Budget, final.xlsx\""), "destination path in the export layout");
            Assert.That(budget, Does.Contain("\"Held by \"\"EXCEL\"\", try later.\""), "quotes doubled");
            Assert.That(budget, Does.Contain("\"File\",\"File was open in another program\",\"32\""));
            Assert.That(budget, Does.Contain("\"1234\",\"2\",\"2026/11/15 10:00:00\""));
            Assert.That(lines.Single(l => l.Contains("Private")), Does.Contain("\"Folder\",\"Access denied\",\"5\""));
            Assert.That(lines.Single(l => l.Contains("cmd.txt")), Does.StartWith("\"C:\\"), "a path cell is never a formula");
            Assert.That(FailureCsv.Cell("=1+1"), Is.EqualTo("\"'=1+1\""));
            Assert.That(FailureCsv.Cell("-5"), Is.EqualTo("\"-5\""), "negative numbers stay numbers");
        }
    }
}
