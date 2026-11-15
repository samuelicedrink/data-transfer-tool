using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using DataTransfer.Core;
using NUnit.Framework;

namespace DataTransfer.Core.Tests
{
    [TestFixture]
    public class UserAccountsTests
    {
        // made-up SIDs: machine, a domain and Microsoft Entra
        const string M = "S-1-5-21-1000-2000-3000";
        const string D = "S-1-5-21-7000-8000-9000";
        const string Anna = M + "-1001", Bob = M + "-1002", Carl = D + "-1105", Dina = D + "-1106", Eve = "S-1-12-1-11-22-33-44", Old = M + "-1009";
        const string DomainAdmins = D + "-512", EntraAdmins = "S-1-12-1-99-98-97-96";

        sealed class Fake : IAccountSource
        {
            public List<ProfileEntry> ProfileList = new List<ProfileEntry>();
            public HashSet<string> Missing = new HashSet<string>();
            public Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<GroupMember> Admins = new List<GroupMember>();
            public Dictionary<string, ISet<string>> Groups = new Dictionary<string, ISet<string>>();
            public List<SessionEntry> SessionList = new List<SessionEntry>();
            public bool AdminsFail;

            public IEnumerable<ProfileEntry> Profiles() => ProfileList;
            public bool ProfileExists(string path) => !Missing.Contains(path);
            public DateTime? ProfileFileTime(string p) => new DateTime(2020, 1, 1);
            public string Translate(string sid) => Names.TryGetValue(sid, out var n) ? n : null;
            public string ToSid(string account) => Names.FirstOrDefault(kv => string.Equals(kv.Value, account, StringComparison.OrdinalIgnoreCase)).Key;
            public string MachineSid => M;
            public IList<GroupMember> AdministratorsMembers() { if (AdminsFail) throw new UnauthorizedAccessException(); return Admins; }
            public bool TryGetGroups(string sid, out ISet<string> groups, out string error)
            {
                error = null;
                if (Groups.TryGetValue(sid, out groups)) return true;
                error = "The specified domain either does not exist or could not be contacted.";
                return false;
            }
            public IEnumerable<SessionEntry> Sessions() => SessionList;

            public void Add(string sid, string name, string folder, DateTime? load = null)
            {
                ProfileList.Add(new ProfileEntry { Sid = sid, ProfilePath = @"C:\Users\" + folder, LoadTime = load });
                if (name != null) Names[sid] = name;
            }
        }

        static Fake Pc()
        {
            var f = new Fake();
            f.ProfileList.Add(new ProfileEntry { Sid = "S-1-5-18", ProfilePath = @"C:\Windows\system32\config\systemprofile" });
            f.ProfileList.Add(new ProfileEntry { Sid = "S-1-5-19", ProfilePath = @"C:\Windows\ServiceProfiles\LocalService" });
            f.Add(Anna, @"PC01\anna", "anna", new DateTime(2026, 11, 10));
            f.Add(Bob, @"PC01\bob", "bob", new DateTime(2026, 11, 1));
            f.Add(Carl, @"CORP\carl", "carl", new DateTime(2026, 10, 1));
            f.Add(Dina, @"CORP\dina", "dina", new DateTime(2026, 9, 1));
            f.Add(Eve, @"AzureAD\EveSmith", "EveSmith");
            f.Admins.Add(new GroupMember { Sid = M + "-500", Name = @"PC01\Administrator", Usage = 1 });
            f.Admins.Add(new GroupMember { Sid = Anna, Name = @"PC01\anna", Usage = 1 });
            f.Admins.Add(new GroupMember { Sid = DomainAdmins, Name = @"CORP\Domain Admins", Usage = 2 });
            f.Groups[Bob] = new HashSet<string> { "S-1-5-32-545", M + "-513" };
            f.Groups[Carl] = new HashSet<string> { DomainAdmins, D + "-513" };
            f.Groups[Dina] = new HashSet<string> { D + "-513" };
            return f;
        }

        static SignedInUser Get(List<SignedInUser> l, string sid) => l.Single(u => u.Sid == sid);

        [Test]
        public void ListsPeopleOnly_WithAccessLevel()
        {
            var list = UserDirectory.List(Pc());
            Assert.That(list.Select(u => u.Sid), Is.EquivalentTo(new[] { Anna, Bob, Carl, Dina, Eve }), "system profiles left out");
            Assert.That(Get(list, Anna).AccessText, Is.EqualTo("Local admin"));
            Assert.That(Get(list, Bob).AccessText, Is.EqualTo("Standard user"));
            Assert.That(Get(list, Carl).AccessText, Is.EqualTo(@"Local admin (via CORP\Domain Admins)"));
            Assert.That(Get(list, Dina).AccessText, Is.EqualTo("Standard user"));
        }

        [Test]
        public void DomainNotReachable_IsUnknown_NeverAGuess()
        {
            var f = Pc();
            f.Groups.Remove(Carl);
            var carl = Get(UserDirectory.List(f), Carl);
            Assert.That(carl.Access, Is.EqualTo(AccessLevel.Unknown));
            Assert.That(carl.AccessText, Is.EqualTo("Access unknown (domain not reachable)"));
        }

        [Test]
        public void LocalUser_CannotBeInDomainGroups_SoUnreachableDomainStillGivesAnAnswer()
        {
            var f = Pc();
            f.Groups.Remove(Bob);
            Assert.That(Get(UserDirectory.List(f), Bob).Access, Is.EqualTo(AccessLevel.StandardUser));
        }

        [Test]
        public void EntraAccount_WithEntraGroupInAdministrators_IsUnknownOffline()
        {
            var f = Pc();
            f.Admins.Add(new GroupMember { Sid = EntraAdmins, Name = null, Usage = 5 });
            var eve = Get(UserDirectory.List(f), Eve);
            Assert.That(eve.Access, Is.EqualTo(AccessLevel.Unknown));
            Assert.That(eve.AccessNote, Does.Contain("Microsoft Entra"));
            f.Groups[Eve] = new HashSet<string> { EntraAdmins };
            Assert.That(Get(UserDirectory.List(f), Eve).AccessText, Is.EqualTo("Local admin (via a Microsoft Entra group)"), "a SID is never shown");
        }

        [Test]
        public void EveryoneInAdministrators_MakesEveryUserAnAdmin()
        {
            var f = Pc();
            f.Admins.Add(new GroupMember { Sid = "S-1-5-4", Name = @"NT AUTHORITY\INTERACTIVE", Usage = 5 });
            Assert.That(UserDirectory.List(f).All(u => u.Access == AccessLevel.LocalAdmin));
            Assert.That(Get(UserDirectory.List(f), Bob).AccessVia, Is.EqualTo(@"NT AUTHORITY\INTERACTIVE"));
        }

        [Test]
        public void AdministratorsUnreadable_IsUnknownForEveryone()
        {
            var f = Pc();
            f.AdminsFail = true;
            Assert.That(UserDirectory.List(f).All(u => u.Access == AccessLevel.Unknown && u.AccessNote == "Administrators group cannot be read"));
        }

        [Test]
        public void SessionsAndOrder_SignedInFirstThenMostRecent()
        {
            var f = Pc();
            f.SessionList.Add(new SessionEntry { Account = @"CORP\dina", Disconnected = true });
            f.SessionList.Add(new SessionEntry { Account = @"PC01\bob" });
            var list = UserDirectory.List(f);
            Assert.That(list.Select(u => u.Sid).Take(2), Is.EquivalentTo(new[] { Bob, Dina }));
            Assert.That(list[2].Sid, Is.EqualTo(Anna), "then by last sign-in");
            Assert.That(Get(list, Dina).SignInText, Is.EqualTo("signed in now (disconnected)"));
            Assert.That(Get(list, Bob).SignInText, Is.EqualTo("signed in now"));
            Assert.That(Get(list, Anna).SignInText, Is.EqualTo("last sign-in 2026-11-10"));
            Assert.That(Get(list, Eve).SignInText, Is.EqualTo("last sign-in 2020-01-01"), "falls back to the profile's NTUSER.DAT time");
        }

        [Test]
        public void DeletedAccountOrMissingFolder()
        {
            var f = Pc();
            f.Add(Old, null, "olduser");
            f.Missing.Add(@"C:\Users\bob");
            var list = UserDirectory.List(f);
            Assert.That(list.Any(u => u.Sid == Bob), Is.False, "profile folder gone: not listed");
            var old = Get(list, Old);
            Assert.That(old.Account, Is.EqualTo("olduser"));
            Assert.That(old.AccountFound, Is.False);
            Assert.That(list.All(u => !u.ToString().Contains("S-1-")), "no SID in any entry");
        }

        // ---------------------------------------------------------------- real system, read-only

        [Test]
        public void ThisPc_ListsTheCurrentUser_AsSignedIn_WithTheRightAccess()
        {
            var src = new SystemAccountSource();
            var me = WindowsIdentity.GetCurrent();
            Assert.That(src.MachineSid, Does.StartWith("S-1-5-21-"));

            var list = UserDirectory.List(src);
            var mine = list.SingleOrDefault(u => u.Sid == me.User.Value);
            Assert.That(mine, Is.Not.Null, "current user listed");
            Assert.That(mine.SignedInNow, "current user is signed in");
            Assert.That(mine.Account, Is.EqualTo(me.Name).IgnoreCase);

            // expected answer from Windows itself: Administrators appears in the user's token
            // (as a deny-only group when not elevated)
            bool adminInToken = TokenHasAdministrators();
            Assert.That(mine.Access, Is.EqualTo(adminInToken ? AccessLevel.LocalAdmin : AccessLevel.StandardUser), mine.AccessText);
            TestContext.WriteLine($"{list.Count} user(s); current user: {mine.AccessText}, {mine.SignInText}");
        }

        [Test]
        public void ThisPc_AuthzGroups_MatchTheCurrentToken()
        {
            var src = new SystemAccountSource();
            var me = WindowsIdentity.GetCurrent();
            Assert.That(src.TryGetGroups(me.User.Value, out var groups, out string error), error);
            Assert.That(groups.Contains(UserDirectory.AdministratorsSid), Is.EqualTo(TokenHasAdministrators()));
            Assert.That(groups.Contains("S-1-5-32-545"), "BUILTIN\\Users");
        }

        static bool TokenHasAdministrators()
        {
            // whoami lists deny-only groups too, which WindowsIdentity.Groups does not
            var psi = new ProcessStartInfo("whoami", "/groups /fo csv /nh") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using (var p = Process.Start(psi)) return p.StandardOutput.ReadToEnd().Contains("\"" + UserDirectory.AdministratorsSid + "\"");
        }
    }
}
