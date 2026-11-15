using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace DataTransfer.Core
{
    public enum AccessLevel { StandardUser, LocalAdmin, Unknown }

    /// <summary>One user who has signed in on this PC (has a profile), for the v2 user picker.</summary>
    public sealed class SignedInUser
    {
        public string Sid { get; set; }
        /// <summary>DOMAIN\name, or the profile folder name when the account cannot be looked up.</summary>
        public string Account { get; set; }
        public bool AccountFound { get; set; }
        public string ProfilePath { get; set; }
        public DateTime? LastSignIn { get; set; }
        public bool SignedInNow { get; set; }
        public bool SessionDisconnected { get; set; }
        public AccessLevel Access { get; set; }
        /// <summary>Group that gives admin rights when it is not the account itself, e.g. CORP\Domain Admins.</summary>
        public string AccessVia { get; set; }
        /// <summary>Why the access level is unknown.</summary>
        public string AccessNote { get; set; }

        public string Name => Account.Contains("\\") ? Account.Substring(Account.IndexOf('\\') + 1) : Account;

        public string AccessText
        {
            get
            {
                switch (Access)
                {
                    case AccessLevel.LocalAdmin: return AccessVia == null ? "Local admin" : $"Local admin (via {AccessVia})";
                    case AccessLevel.StandardUser: return "Standard user";
                    default: return "Access unknown" + (AccessNote == null ? "" : $" ({AccessNote})");
                }
            }
        }

        public string SignInText => SignedInNow ? (SessionDisconnected ? "signed in now (disconnected)" : "signed in now")
            : LastSignIn.HasValue ? "last sign-in " + LastSignIn.Value.ToString("yyyy-MM-dd") : "last sign-in unknown";

        public override string ToString() => $"{Account}  -  {AccessText}  -  {SignInText}";
    }

    public sealed class ProfileEntry
    {
        public string Sid;
        public string ProfilePath;
        public DateTime? LoadTime;
    }

    public sealed class GroupMember
    {
        public string Sid;
        public string Name;
        /// <summary>SID_NAME_USE: 1 user, 2 group, 3 domain, 4 alias, 5 well-known group, 6 deleted, 8 unknown.</summary>
        public int Usage;
    }

    public sealed class SessionEntry
    {
        public string Account;
        public bool Disconnected;
    }

    /// <summary>What <see cref="UserDirectory"/> needs from Windows; replaced by a fake in tests.</summary>
    public interface IAccountSource
    {
        IEnumerable<ProfileEntry> Profiles();
        bool ProfileExists(string path);
        DateTime? ProfileFileTime(string profilePath);
        /// <summary>Account name for a SID, or null when it cannot be looked up.</summary>
        string Translate(string sid);
        /// <summary>SID for an account name, or null.</summary>
        string ToSid(string account);
        /// <summary>SID prefix of local accounts (the machine SID).</summary>
        string MachineSid { get; }
        IList<GroupMember> AdministratorsMembers();
        /// <summary>All group SIDs of an account; false with a reason when they cannot be worked out.</summary>
        bool TryGetGroups(string sid, out ISet<string> groups, out string error);
        IEnumerable<SessionEntry> Sessions();
    }

    /// <summary>Lists the users who have signed in on this PC with their access level.</summary>
    public static class UserDirectory
    {
        public const string AdministratorsSid = "S-1-5-32-544";
        /// <summary>Well-known groups that would make every signed-in user an admin if added to Administrators.</summary>
        static readonly Dictionary<string, string> EveryoneLike = new Dictionary<string, string>
        {
            { "S-1-1-0", "Everyone" }, { "S-1-5-4", "INTERACTIVE" }, { "S-1-5-11", "Authenticated Users" }, { "S-1-5-32-545", "Users" },
        };

        /// <summary>Real people only: local and domain accounts (S-1-5-21-…) and Microsoft Entra accounts (S-1-12-1-…).</summary>
        public static bool IsPersonSid(string sid) =>
            sid != null && (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) || sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase));

        public static List<SignedInUser> List(IAccountSource src)
        {
            var admins = SafeList(src.AdministratorsMembers);
            var sessions = SafeList(() => src.Sessions().ToList());
            var sessionSids = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sessions)
            {
                string sid = Safe(() => src.ToSid(s.Account));
                if (sid == null) continue;
                // one connected session is enough for "signed in now (connected)"
                sessionSids[sid] = sessionSids.TryGetValue(sid, out bool disc) ? disc && s.Disconnected : s.Disconnected;
            }

            var result = new List<SignedInUser>();
            foreach (var p in src.Profiles())
            {
                if (!IsPersonSid(p.Sid) || string.IsNullOrEmpty(p.ProfilePath) || !src.ProfileExists(p.ProfilePath)) continue;
                string account = Safe(() => src.Translate(p.Sid));
                var u = new SignedInUser
                {
                    Sid = p.Sid,
                    ProfilePath = p.ProfilePath,
                    Account = account ?? Path.GetFileName(p.ProfilePath.TrimEnd('\\')),
                    AccountFound = account != null,
                    LastSignIn = p.LoadTime ?? Safe(() => src.ProfileFileTime(p.ProfilePath)),
                };
                if (sessionSids.TryGetValue(p.Sid, out bool disconnected)) { u.SignedInNow = true; u.SessionDisconnected = disconnected; }
                Classify(u, admins, src);
                result.Add(u);
            }
            return result.OrderByDescending(u => u.SignedInNow).ThenByDescending(u => u.LastSignIn ?? DateTime.MinValue)
                         .ThenBy(u => u.Account, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static void Classify(SignedInUser u, IList<GroupMember> admins, IAccountSource src)
        {
            if (admins == null) { u.Access = AccessLevel.Unknown; u.AccessNote = "Administrators group cannot be read"; return; }
            if (admins.Any(m => Same(m.Sid, u.Sid))) { u.Access = AccessLevel.LocalAdmin; return; }

            var everyone = admins.FirstOrDefault(m => m.Sid != null && EveryoneLike.ContainsKey(m.Sid));
            if (everyone != null) { u.Access = AccessLevel.LocalAdmin; u.AccessVia = everyone.Name ?? EveryoneLike[everyone.Sid]; return; }

            // groups in Administrators other than local accounts: these can contain the user
            var groupMembers = admins.Where(m => m.Sid != null && m.Usage != 1 && m.Usage != 6).ToList();
            if (groupMembers.Count == 0) { u.Access = AccessLevel.StandardUser; return; }

            ISet<string> groups = null; string error = null; bool ok;
            try { ok = src.TryGetGroups(u.Sid, out groups, out error); }
            catch (Exception ex) { ok = false; error = ex.Message; }
            if (ok && groups != null)
            {
                var via = groupMembers.FirstOrDefault(m => groups.Contains(m.Sid));
                if (via != null) { u.Access = AccessLevel.LocalAdmin; u.AccessVia = via.Name ?? UnnamedGroup(via.Sid); return; }
                if (groups.Contains(AdministratorsSid)) { u.Access = AccessLevel.LocalAdmin; return; }
                u.Access = AccessLevel.StandardUser; return;
            }

            // Membership could not be worked out. A local account can only be in this PC's own groups,
            // and a domain or Entra account can only be in this PC's groups if added directly (checked
            // above), so only the groups that could hold this account leave the answer open.
            string machine = src.MachineSid;
            bool IsLocal(string s) => machine != null && s.StartsWith(machine + "-", StringComparison.OrdinalIgnoreCase);
            bool localUser = IsLocal(u.Sid);
            var candidates = groupMembers.Where(m => localUser ? IsLocal(m.Sid) : !IsLocal(m.Sid)).ToList();
            if (machine != null && candidates.Count == 0) { u.Access = AccessLevel.StandardUser; return; }
            u.Access = AccessLevel.Unknown;
            u.AccessNote = u.Sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase) ? "Microsoft Entra groups cannot be checked offline" : "domain not reachable";
        }

        /// <summary>Text for a group whose name cannot be looked up; a SID is never shown.</summary>
        public static string UnnamedGroup(string sid) =>
            sid != null && sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase) ? "a Microsoft Entra group" : "a group whose name cannot be looked up";

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        static T Safe<T>(Func<T> f) where T : class { try { return f(); } catch { return null; } }
        static DateTime? Safe(Func<DateTime?> f) { try { return f(); } catch { return null; } }
        static IList<T> SafeList<T>(Func<IList<T>> f) { try { return f(); } catch { return null; } }
        static List<T> SafeList<T>(Func<List<T>> f) { try { return f(); } catch { return new List<T>(); } }
    }

    /// <summary>The real Windows calls behind <see cref="IAccountSource"/>. Read-only.</summary>
    public sealed class SystemAccountSource : IAccountSource
    {
        const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

        public IEnumerable<ProfileEntry> Profiles()
        {
            var list = new List<ProfileEntry>();
            using (var k = Registry.LocalMachine.OpenSubKey(ProfileListKey))
            {
                if (k == null) return list;
                foreach (string sid in k.GetSubKeyNames())
                {
                    using (var sk = k.OpenSubKey(sid))
                    {
                        if (sk == null) continue;
                        // ".bak" keys are left over from broken profiles
                        if (sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                        var e = new ProfileEntry { Sid = sid, ProfilePath = Environment.ExpandEnvironmentVariables(sk.GetValue("ProfileImagePath") as string ?? "") };
                        if (sk.GetValue("LocalProfileLoadTimeHigh") is int hi && sk.GetValue("LocalProfileLoadTimeLow") is int lo)
                        {
                            long ft = ((long)(uint)hi << 32) | (uint)lo;
                            if (ft > 0) try { e.LoadTime = DateTime.FromFileTime(ft); } catch (ArgumentOutOfRangeException) { }
                        }
                        list.Add(e);
                    }
                }
            }
            return list;
        }

        public bool ProfileExists(string path) => Directory.Exists(path);

        public DateTime? ProfileFileTime(string profilePath)
        {
            string hive = Path.Combine(profilePath, "NTUSER.DAT");
            return File.Exists(hive) ? File.GetLastWriteTime(hive) : (DateTime?)null;
        }

        public string Translate(string sid)
        {
            try { return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
            catch (Exception ex) when (ex is IdentityNotMappedException || ex is SystemException) { return null; }
        }

        public string ToSid(string account)
        {
            try { return new NTAccount(account).Translate(typeof(SecurityIdentifier)).Value; }
            catch (Exception ex) when (ex is IdentityNotMappedException || ex is SystemException) { return null; }
        }

        string machineSid;
        public string MachineSid
        {
            get
            {
                if (machineSid != null) return machineSid;
                // the account domain of the local security authority is this PC's own SAM
                var attrs = new Native.LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf(typeof(Native.LSA_OBJECT_ATTRIBUTES)) };
                if (Native.LsaOpenPolicy(IntPtr.Zero, ref attrs, Native.POLICY_VIEW_LOCAL_INFORMATION, out IntPtr policy) != 0) return null;
                try
                {
                    if (Native.LsaQueryInformationPolicy(policy, Native.PolicyAccountDomainInformation, out IntPtr buf) != 0) return null;
                    try
                    {
                        IntPtr psid = Marshal.ReadIntPtr(buf, Marshal.SizeOf(typeof(Native.LSA_UNICODE_STRING)));
                        if (psid != IntPtr.Zero) machineSid = new SecurityIdentifier(psid).Value;
                    }
                    finally { Native.LsaFreeMemory(buf); }
                }
                finally { Native.LsaClose(policy); }
                return machineSid;
            }
        }

        public IList<GroupMember> AdministratorsMembers()
        {
            string group = new SecurityIdentifier(UserDirectory.AdministratorsSid).Translate(typeof(NTAccount)).Value;
            group = group.Substring(group.IndexOf('\\') + 1); // the name is localised, e.g. "Administratoren"
            var result = new List<GroupMember>();
            IntPtr resume = IntPtr.Zero;
            int rc;
            do
            {
                rc = Native.NetLocalGroupGetMembers(null, group, 2, out IntPtr buf, -1, out int read, out int total, ref resume);
                if (rc != 0 && rc != 234) throw new Win32Exception(rc);
                try
                {
                    int size = Marshal.SizeOf(typeof(Native.LOCALGROUP_MEMBERS_INFO_2));
                    for (int i = 0; i < read; i++)
                    {
                        var m = (Native.LOCALGROUP_MEMBERS_INFO_2)Marshal.PtrToStructure(buf + i * size, typeof(Native.LOCALGROUP_MEMBERS_INFO_2));
                        result.Add(new GroupMember
                        {
                            Sid = m.lgrmi2_sid == IntPtr.Zero ? null : new SecurityIdentifier(m.lgrmi2_sid).Value,
                            Name = Marshal.PtrToStringUni(m.lgrmi2_domainandname),
                            Usage = m.lgrmi2_sidusage,
                        });
                    }
                }
                finally { if (buf != IntPtr.Zero) Native.NetApiBufferFree(buf); }
            } while (rc == 234); // ERROR_MORE_DATA
            return result;
        }

        public bool TryGetGroups(string sid, out ISet<string> groups, out string error)
        {
            groups = null; error = null;
            if (!Native.AuthzInitializeResourceManager(Native.AUTHZ_RM_FLAG_NO_AUDIT, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, null, out IntPtr rm))
            { error = new Win32Exception().Message; return false; }
            try
            {
                var s = new SecurityIdentifier(sid);
                var bytes = new byte[s.BinaryLength];
                s.GetBinaryForm(bytes, 0);
                if (!Native.AuthzInitializeContextFromSid(0, bytes, rm, IntPtr.Zero, new Native.LUID(), IntPtr.Zero, out IntPtr ctx))
                { error = new Win32Exception().Message; return false; }
                try
                {
                    Native.AuthzGetInformationFromContext(ctx, Native.AuthzContextInfoGroupsSids, 0, out int needed, IntPtr.Zero);
                    if (needed <= 0) { error = new Win32Exception().Message; return false; }
                    IntPtr buf = Marshal.AllocHGlobal(needed);
                    try
                    {
                        if (!Native.AuthzGetInformationFromContext(ctx, Native.AuthzContextInfoGroupsSids, needed, out needed, buf))
                        { error = new Win32Exception().Message; return false; }
                        // TOKEN_GROUPS: DWORD count, then SID_AND_ATTRIBUTES { PSID; DWORD } aligned to pointer size
                        int count = Marshal.ReadInt32(buf);
                        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < count; i++)
                        {
                            IntPtr psid = Marshal.ReadIntPtr(buf, IntPtr.Size + i * 2 * IntPtr.Size);
                            if (psid != IntPtr.Zero) set.Add(new SecurityIdentifier(psid).Value);
                        }
                        groups = set;
                        return true;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { Native.AuthzFreeContext(ctx); }
            }
            finally { Native.AuthzFreeResourceManager(rm); }
        }

        public IEnumerable<SessionEntry> Sessions()
        {
            var list = new List<SessionEntry>();
            if (!Native.WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr info, out int count)) return list;
            try
            {
                int size = Marshal.SizeOf(typeof(Native.WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    var s = (Native.WTS_SESSION_INFO)Marshal.PtrToStructure(info + i * size, typeof(Native.WTS_SESSION_INFO));
                    string user = Query(s.SessionId, 5), domain = Query(s.SessionId, 7);
                    if (string.IsNullOrEmpty(user)) continue;
                    list.Add(new SessionEntry { Account = string.IsNullOrEmpty(domain) ? user : domain + "\\" + user, Disconnected = s.State == 4 });
                }
            }
            finally { Native.WTSFreeMemory(info); }
            return list;
        }

        static string Query(int session, int infoClass)
        {
            if (!Native.WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out IntPtr buf, out _)) return null;
            try { return Marshal.PtrToStringUni(buf); } finally { Native.WTSFreeMemory(buf); }
        }

        static class Native
        {
            public const int AUTHZ_RM_FLAG_NO_AUDIT = 1;
            public const int AuthzContextInfoGroupsSids = 2;

            [StructLayout(LayoutKind.Sequential)]
            public struct LOCALGROUP_MEMBERS_INFO_2 { public IntPtr lgrmi2_sid; public int lgrmi2_sidusage; public IntPtr lgrmi2_domainandname; }

            [StructLayout(LayoutKind.Sequential)]
            public struct LUID { public uint LowPart; public int HighPart; }

            public const int POLICY_VIEW_LOCAL_INFORMATION = 1;
            public const int PolicyAccountDomainInformation = 5;

            [StructLayout(LayoutKind.Sequential)]
            public struct LSA_UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

            [StructLayout(LayoutKind.Sequential)]
            public struct LSA_OBJECT_ATTRIBUTES { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public int Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }

            [DllImport("advapi32.dll")] public static extern int LsaOpenPolicy(IntPtr system, ref LSA_OBJECT_ATTRIBUTES attrs, int access, out IntPtr policy);
            [DllImport("advapi32.dll")] public static extern int LsaQueryInformationPolicy(IntPtr policy, int infoClass, out IntPtr buf);
            [DllImport("advapi32.dll")] public static extern int LsaFreeMemory(IntPtr buf);
            [DllImport("advapi32.dll")] public static extern int LsaClose(IntPtr policy);

            [StructLayout(LayoutKind.Sequential)]
            public struct WTS_SESSION_INFO { public int SessionId; public IntPtr pWinStationName; public int State; }

            [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
            public static extern int NetLocalGroupGetMembers(string server, string group, int level, out IntPtr buf, int prefMax, out int read, out int total, ref IntPtr resume);
            [DllImport("netapi32.dll")] public static extern int NetApiBufferFree(IntPtr buf);

            [DllImport("authz.dll", SetLastError = true)]
            public static extern bool AuthzInitializeResourceManager(int flags, IntPtr access, IntPtr dynGroups, IntPtr freeGroups, string name, out IntPtr rm);
            [DllImport("authz.dll", SetLastError = true)]
            public static extern bool AuthzInitializeContextFromSid(int flags, byte[] sid, IntPtr rm, IntPtr expiration, LUID id, IntPtr dynArgs, out IntPtr ctx);
            [DllImport("authz.dll", SetLastError = true)]
            public static extern bool AuthzGetInformationFromContext(IntPtr ctx, int infoClass, int size, out int needed, IntPtr buf);
            [DllImport("authz.dll")] public static extern bool AuthzFreeContext(IntPtr ctx);
            [DllImport("authz.dll")] public static extern bool AuthzFreeResourceManager(IntPtr rm);

            [DllImport("wtsapi32.dll", SetLastError = true)]
            public static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr info, out int count);
            [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass, out IntPtr buf, out int bytes);
            [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr mem);
        }
    }
}
