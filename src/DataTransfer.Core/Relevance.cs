using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace DataTransfer.Core
{
    /// <summary>
    /// The relevance weights (docs/SPEC_V2.md section 3), in one place so they can be tuned after
    /// the manual tests. A file starts at Start; the adjustments that apply are added and the
    /// result is clamped to 0..100.
    /// </summary>
    public static class RelevanceWeights
    {
        public const int Start = 50;
        public const int OwnerIsUser = 35, OwnerIsOtherUser = -40, OwnerIsSystem = -25;
        public const int InUserProfile = 15, InOtherProfile = -30, InSystemArea = -20;
        public const int UserContent = 10, ProgramOrCache = -30, KnownApp = 20, FolderNamesUser = 5;
        public const int Likely = 75, Review = 40;
    }

    public enum FileKind : byte { Other = 0, UserContent = 1, ProgramOrCache = 2 }

    /// <summary>A known place where an app keeps user data (settings, presets, local history).</summary>
    public sealed class KnownApp
    {
        public string Name;
        /// <summary>Folder relative to a profile; segments may hold * and ?.</summary>
        public string Folder;
        /// <summary>Optional file name pattern; null = every file below Folder.</summary>
        public string Files;
        /// <summary>False: named in Advice but not scored up (e.g. data kept in the cloud).</summary>
        public bool Boost = true;
        /// <summary>Extra words for Advice, e.g. "backup only, will not open on another PC".</summary>
        public string Note;

        internal Regex[] segments;
        internal Regex files;
    }

    /// <summary>
    /// Files that share every fact the score depends on, added up. A folder keeps one entry per
    /// combination found inside it, so its score for any user is worked out without a rescan.
    /// </summary>
    public struct RelevanceGroup
    {
        public long Key, Bytes, Count;
    }

    /// <summary>Who the score is for. Built from the user picked on the Select step.</summary>
    public sealed class RelevanceTarget
    {
        public string Sid;
        /// <summary>Short account name for Advice, e.g. "anna".</summary>
        public string Name;
        public string ProfilePath;

        public static RelevanceTarget From(SignedInUser u) => u == null ? null : new RelevanceTarget
        {
            Sid = u.Sid, ProfilePath = u.ProfilePath,
            Name = (u.Account ?? "").Contains("\\") ? u.Account.Substring(u.Account.LastIndexOf('\\') + 1) : u.Account
        };
    }

    public sealed class RelevanceResult
    {
        public int Score;
        public string Advice;
        /// <summary>The estimate rests on weak facts (no owner on the drive, files owned by Administrators).</summary>
        public bool Weak;
    }

    /// <summary>
    /// Classifies files for the relevance score: owner, which profile they are in, system areas,
    /// file kind, known-app locations and the folder's explicit permissions. Read-only.
    /// </summary>
    public sealed class RelevanceClassifier
    {
        // ---------------------------------------------------------------- tables

        static readonly HashSet<string> UserContentExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // pictures: all of them (owner, 2026-11-15)
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".webp", ".avif", ".svg",
            ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".raf", ".srw", ".pef", ".psd", ".psb", ".xcf", ".kra", ".ico",
            // video and audio
            ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".m4v", ".mts", ".m2ts", ".3gp", ".webm", ".flv", ".mpg", ".mpeg", ".vob",
            ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma", ".aif", ".aiff", ".opus", ".mid", ".midi",
            // documents
            ".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm", ".xls", ".xlsx", ".xlsm", ".xlsb", ".csv", ".ppt", ".pptx", ".pptm",
            ".pdf", ".txt", ".rtf", ".odt", ".ods", ".odp", ".md", ".one", ".onepkg", ".vsd", ".vsdx", ".pub", ".epub", ".pages", ".numbers", ".key", ".xps",
            // archives
            ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".iso",
            // project files
            ".prproj", ".aep", ".aet", ".ai", ".indd", ".idml", ".blend", ".dwg", ".dxf", ".als", ".flp", ".sketch", ".fig", ".xd", ".skp",
            ".max", ".c4d", ".ma", ".mb", ".fla", ".veg", ".drp", ".aup", ".aup3", ".band", ".cpr", ".ptx", ".sesx", ".mogrt", ".fcpbundle",
            ".lrcat", ".cdr", ".afphoto", ".afdesign", ".mlt", ".kdenlive", ".rpp", ".ipynb",
            // mailboxes and messages
            ".pst", ".msg", ".eml", ".mbox"
        };

        static readonly HashSet<string> ProgramExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".sys", ".msi", ".msp", ".cab", ".ocx", ".drv", ".mui", ".cat", ".manifest", ".pdb", ".lib", ".obj", ".pyc", ".pyd", ".node", ".winmd"
        };

        static readonly HashSet<string> CacheDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", "Temp", "Tmp", "Cache", "Caches", "INetCache", "Code Cache", "GPUCache", "ShaderCache", "DawnCache", "GrShaderCache",
            "__pycache__", "Crashpad", "CrashDumps", "Service Worker", "CachedData", "Temporary Internet Files"
        };

        /// <summary>Profiles that belong to nobody in particular.</summary>
        static readonly string[] SharedProfiles = { "Public", "Default", "Default User", "All Users", "defaultuser0" };

        /// <summary>First version of the catalog (SPEC_V2 section 3). Every path is confirmed in manual test T21.</summary>
        public static readonly List<KnownApp> DefaultCatalog = new List<KnownApp>
        {
            new KnownApp { Name = "Photoshop settings", Folder = @"AppData\Roaming\Adobe\Adobe Photoshop *" },
            new KnownApp { Name = "Illustrator settings", Folder = @"AppData\Roaming\Adobe\Adobe Illustrator *" },
            new KnownApp { Name = "Premiere Pro projects and presets", Folder = @"Documents\Adobe\Premiere Pro" },
            new KnownApp { Name = "Premiere Pro settings", Folder = @"AppData\Roaming\Adobe\Premiere Pro" },
            new KnownApp { Name = "After Effects settings", Folder = @"Documents\Adobe\After Effects *" },
            new KnownApp { Name = "After Effects settings", Folder = @"AppData\Roaming\Adobe\After Effects" },
            new KnownApp { Name = "Office templates", Folder = @"AppData\Roaming\Microsoft\Templates" },
            new KnownApp { Name = "Office templates", Folder = @"Documents\Custom Office Templates" },
            new KnownApp { Name = "Outlook signatures", Folder = @"AppData\Roaming\Microsoft\Signatures" },
            new KnownApp { Name = "Office dictionaries", Folder = @"AppData\Roaming\Microsoft\UProof" },
            new KnownApp { Name = "Outlook mailbox", Folder = @"AppData\Local\Microsoft\Outlook", Files = "*.pst" },
            new KnownApp { Name = "Outlook mailbox", Folder = @"Documents\Outlook Files", Files = "*.pst" },
            new KnownApp { Name = "Chrome bookmarks", Folder = @"AppData\Local\Google\Chrome\User Data\*", Files = "Bookmarks" },
            new KnownApp { Name = "Edge bookmarks", Folder = @"AppData\Local\Microsoft\Edge\User Data\*", Files = "Bookmarks" },
            new KnownApp { Name = "Firefox bookmarks", Folder = @"AppData\Roaming\Mozilla\Firefox\Profiles\*", Files = "places.sqlite" },
            new KnownApp { Name = "Sticky Notes", Folder = @"AppData\Local\Packages\Microsoft.MicrosoftStickyNotes_*\LocalState" },
            new KnownApp { Name = "saved games", Folder = @"Saved Games" },
            new KnownApp { Name = "saved games", Folder = @"Documents\My Games" },
            new KnownApp { Name = "OBS settings", Folder = @"AppData\Roaming\obs-studio" },
            new KnownApp { Name = "VS Code settings", Folder = @"AppData\Roaming\Code\User" },
            new KnownApp { Name = "Notepad++ settings", Folder = @"AppData\Roaming\Notepad++" },
            new KnownApp { Name = "KakaoTalk chats", Folder = @"AppData\Local\Kakao\KakaoTalk", Note = "backup only, will not open on another PC" },
            new KnownApp { Name = "LINE chats", Folder = @"AppData\Local\LINE\Data", Note = "backup only, will not open on another PC" },
            new KnownApp { Name = "WeChat files", Folder = @"Documents\WeChat Files" },
            new KnownApp { Name = "WeChat files", Folder = @"Documents\xwechat_files" },
            new KnownApp { Name = "Teams", Folder = @"AppData\Roaming\Microsoft\Teams", Boost = false, Note = "chats are stored in Microsoft 365, nothing to copy" },
            new KnownApp { Name = "Teams", Folder = @"AppData\Local\Packages\MSTeams_*", Boost = false, Note = "chats are stored in Microsoft 365, nothing to copy" },
        };

        // ---------------------------------------------------------------- state

        List<string> profiles;                   // profile folders, index = profile number
        bool[] shared;
        readonly string[] systemAreas;
        readonly string[] installAreas;
        public IReadOnlyList<KnownApp> Catalog { get; }
        readonly bool readSecurity;

        readonly ConcurrentDictionary<string, int> sidIds = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<int, string> sids = new ConcurrentDictionary<int, string>();
        readonly ConcurrentDictionary<string, int> aclIds = new ConcurrentDictionary<string, int>();
        readonly ConcurrentDictionary<int, string[]> acls = new ConcurrentDictionary<int, string[]>();
        int nextSid, nextAcl;

        /// <param name="profileFolders">Every folder in the profiles directory (as ProfileInfo lists them).</param>
        /// <param name="readSecurity">False in tests that give owners through <see cref="OwnerOverride"/>.</param>
        public RelevanceClassifier(IEnumerable<string> profileFolders, IEnumerable<KnownApp> catalog = null, bool readSecurity = true)
        {
            profiles = profileFolders.Select(PathUtil.Trim).ToList();
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string root = System.IO.Path.GetPathRoot(win);
            systemAreas = new[] { win, pf, pf86, System.IO.Path.Combine(root, "Program Files"), System.IO.Path.Combine(root, "Program Files (x86)"), System.IO.Path.Combine(pd, "Package Cache") }
                .Where(s => !string.IsNullOrEmpty(s)).Select(PathUtil.Trim).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            installAreas = systemAreas.Concat(new[] { PathUtil.Trim(pd) }).ToArray();
            Catalog = (catalog ?? DefaultCatalog).Take(254).ToList();
            if (profiles.Count > 1000) profiles = profiles.Take(1000).ToList();
            shared = profiles.Select(p => SharedProfiles.Contains(System.IO.Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)).ToArray();
            foreach (var a in Catalog)
            {
                a.segments = a.Folder.Split('\\').Select(Wildcard).ToArray();
                a.files = a.Files == null ? null : Wildcard(a.Files);
            }
            this.readSecurity = readSecurity;
            sidIds[""] = 0; sids[0] = null; // id 0 = owner unknown
            aclIds[""] = 0; acls[0] = new string[0];
        }

        /// <summary>Tests: owner of a file by path, instead of reading it from the file system.</summary>
        public Func<string, string> OwnerOverride;
        /// <summary>Tests: explicit permission entries of a folder, instead of reading them.</summary>
        public Func<string, IEnumerable<string>> FolderAclOverride;

        static Regex Wildcard(string pattern) =>
            new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // ---------------------------------------------------------------- key packing
        // owner 24 bits | profile 10 | area 1 | kind 2 | app 8 | acl 16

        static long Pack(int owner, int profile, bool system, FileKind kind, int app, int acl) =>
            ((long)(owner & 0xFFFFFF) << 37) | ((long)((profile + 1) & 0x3FF) << 27) | ((system ? 1L : 0) << 26) |
            ((long)kind << 24) | ((long)((app + 1) & 0xFF) << 16) | (long)(acl & 0xFFFF);

        public string OwnerOf(long key) => sids.TryGetValue((int)((key >> 37) & 0xFFFFFF), out var s) ? s : null;
        static int ProfileOf(long key) => (int)((key >> 27) & 0x3FF) - 1;
        static bool SystemOf(long key) => ((key >> 26) & 1) != 0;
        public static FileKind KindOf(long key) => (FileKind)((key >> 24) & 3);
        public KnownApp AppOf(long key) { int a = (int)((key >> 16) & 0xFF) - 1; return a >= 0 && a < Catalog.Count ? Catalog[a] : null; }
        string[] AclOf(long key) => acls.TryGetValue((int)(key & 0xFFFF), out var a) ? a : new string[0];

        int SidId(string sid)
        {
            if (string.IsNullOrEmpty(sid)) return 0;
            int got = sidIds.GetOrAdd(sid, s => { int id = System.Threading.Interlocked.Increment(ref nextSid); if (id <= 0xFFFFFF) sids[id] = s; return id; });
            return got <= 0xFFFFFF ? got : 0; // more owners than the key holds: treated as unknown
        }

        // ---------------------------------------------------------------- per folder and per file

        /// <summary>Facts shared by every file directly in one folder.</summary>
        public sealed class FolderFacts
        {
            internal int Profile = -1, Acl;
            internal bool System, Install, Cache;
            internal string[] RelSegments;   // folder relative to its profile, null when not in a profile
        }

        public FolderFacts ForFolder(string dir)
        {
            dir = PathUtil.Trim(dir);
            var f = new FolderFacts
            {
                System = systemAreas.Any(a => PathUtil.IsSameOrUnder(dir, a)),
                Install = installAreas.Any(a => PathUtil.IsSameOrUnder(dir, a)),
            };
            for (int i = 0; i < profiles.Count; i++)
                if (PathUtil.IsSameOrUnder(dir, profiles[i]))
                {
                    f.Profile = i;
                    string rel = dir.Length > profiles[i].Length ? dir.Substring(profiles[i].Length).Trim('\\') : "";
                    f.RelSegments = rel.Length == 0 ? new string[0] : rel.Split('\\');
                    if (f.RelSegments.Length > 0 && f.RelSegments[0].Equals("AppData", StringComparison.OrdinalIgnoreCase)) f.Install = true;
                    break;
                }
            // inside a profile only the folders below it count (the profile path itself is not a cache)
            f.Cache = (f.RelSegments ?? dir.Split('\\')).Any(CacheDirs.Contains);
            var aclSids = (FolderAclOverride != null ? FolderAclOverride(dir) : readSecurity ? Security.ExplicitAllowSids(dir) : null)?.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
            if (aclSids != null && aclSids.Length > 0)
            {
                string k = string.Join(";", aclSids);
                f.Acl = aclIds.GetOrAdd(k, _ => { int id = System.Threading.Interlocked.Increment(ref nextAcl); if (id <= 0xFFFF) acls[id] = aclSids; return id; });
                if (f.Acl > 0xFFFF) f.Acl = 0; // more permission sets than the key holds: ignored
            }
            return f;
        }

        /// <summary>The group key of one file directly in a folder described by <paramref name="folder"/>.</summary>
        public long KeyFor(FolderFacts folder, string fullPath, string name)
        {
            string ext = System.IO.Path.GetExtension(name);
            var kind = folder.Cache || (folder.Install && ProgramExt.Contains(ext)) ? FileKind.ProgramOrCache
                     : UserContentExt.Contains(ext) ? FileKind.UserContent : FileKind.Other;
            int app = -1;
            if (folder.RelSegments != null)
                for (int i = 0; i < Catalog.Count && app < 0; i++)
                {
                    var a = Catalog[i];
                    if (a.segments.Length > folder.RelSegments.Length) continue;
                    bool match = true;
                    for (int s = 0; s < a.segments.Length && match; s++) match = a.segments[s].IsMatch(folder.RelSegments[s]);
                    if (match && (a.files == null || a.files.IsMatch(name))) app = i;
                }
            string owner = OwnerOverride != null ? OwnerOverride(fullPath) : readSecurity ? Security.Owner(fullPath) : null;
            return Pack(SidId(owner), folder.Profile, folder.System, kind, app, folder.Acl);
        }

        // ---------------------------------------------------------------- scoring

        int ProfileIndexOf(RelevanceTarget t) =>
            t?.ProfilePath == null ? -1 : profiles.FindIndex(p => string.Equals(p, PathUtil.Trim(t.ProfilePath), StringComparison.OrdinalIgnoreCase));

        static bool IsPerson(string sid) => sid != null && ((sid.StartsWith("S-1-5-21-") && !sid.EndsWith("-500")) || sid.StartsWith("S-1-12-1-"));
        static bool IsSystemOwner(string sid) => sid == "S-1-5-18" || sid == UserDirectory.AdministratorsSid || sid.StartsWith("S-1-5-80-") /* TrustedInstaller and services */;

        /// <summary>Score of one group of files for the target user.</summary>
        public int Score(long key, RelevanceTarget t) => Score(key, t, ProfileIndexOf(t), null);

        int Score(long key, RelevanceTarget t, int userProfile, List<string> reasons)
        {
            int score = RelevanceWeights.Start;
            string owner = OwnerOf(key);
            if (owner != null)
            {
                if (string.Equals(owner, t.Sid, StringComparison.OrdinalIgnoreCase)) { score += RelevanceWeights.OwnerIsUser; reasons?.Add($"owned by {t.Name}"); }
                else if (IsSystemOwner(owner)) { score += RelevanceWeights.OwnerIsSystem; reasons?.Add(owner == UserDirectory.AdministratorsSid ? "owned by Administrators" : "owned by Windows"); }
                else if (IsPerson(owner)) { score += RelevanceWeights.OwnerIsOtherUser; reasons?.Add("owned by another user"); }
            }
            int p = ProfileOf(key);
            if (p >= 0 && p == userProfile) { score += RelevanceWeights.InUserProfile; reasons?.Add($"in {t.Name}'s profile"); }
            else if (p >= 0 && !shared[p]) { score += RelevanceWeights.InOtherProfile; reasons?.Add($"in {System.IO.Path.GetFileName(profiles[p])}'s profile"); }
            if (SystemOf(key)) { score += RelevanceWeights.InSystemArea; reasons?.Add("in Windows or Program Files"); }
            var kind = KindOf(key);
            if (kind == FileKind.UserContent) { score += RelevanceWeights.UserContent; reasons?.Add("pictures, media, documents or projects"); }
            else if (kind == FileKind.ProgramOrCache) { score += RelevanceWeights.ProgramOrCache; reasons?.Add("program or cache files"); }
            var app = AppOf(key);
            if (app != null) { if (app.Boost) score += RelevanceWeights.KnownApp; reasons?.Add(app.Name + (app.Note != null ? " (" + app.Note + ")" : "")); }
            if (t.Sid != null && AclOf(key).Contains(t.Sid, StringComparer.OrdinalIgnoreCase)) { score += RelevanceWeights.FolderNamesUser; reasons?.Add($"folder permissions name {t.Name}"); }
            return Math.Max(0, Math.Min(100, score));
        }

        /// <summary>What is left of a folder's totals after taking out parts of it (unticked subfolders).</summary>
        public static RelevanceGroup[] Subtract(IEnumerable<RelevanceGroup> all, IEnumerable<RelevanceGroup> minus)
        {
            var d = all.ToDictionary(g => g.Key);
            foreach (var m in minus)
                if (d.TryGetValue(m.Key, out var g))
                    d[m.Key] = new RelevanceGroup { Key = g.Key, Bytes = Math.Max(0, g.Bytes - m.Bytes), Count = Math.Max(0, g.Count - m.Count) };
            return d.Values.Where(g => g.Count > 0).ToArray();
        }

        /// <summary>
        /// Score and Advice for a folder (size-weighted average of its groups; by count when all
        /// files are empty) or a single file (one group). Null when there is nothing to score.
        /// </summary>
        public RelevanceResult Evaluate(IReadOnlyList<RelevanceGroup> groups, RelevanceTarget t)
        {
            if (t == null || groups == null || groups.Count == 0) return null;
            int userProfile = ProfileIndexOf(t);
            long bytes = groups.Sum(g => g.Bytes), count = groups.Sum(g => g.Count);
            if (count == 0) return null;
            double sum = 0;
            foreach (var g in groups) sum += Score(g.Key, t, userProfile, null) * (double)(bytes > 0 ? g.Bytes : g.Count);
            int score = (int)Math.Round(sum / (bytes > 0 ? bytes : count));

            // the largest group explains the score; known apps anywhere inside are named
            var main = groups.OrderByDescending(g => bytes > 0 ? g.Bytes : g.Count).First();
            var reasons = new List<string>();
            Score(main.Key, t, userProfile, reasons);
            var apps = groups.Select(g => AppOf(g.Key)).Where(a => a != null).Select(a => a.Name + (a.Note != null ? " (" + a.Note + ")" : ""))
                             .Distinct().Where(a => !reasons.Contains(a)).ToList();

            double weakShare = groups.Where(g => { var o = OwnerOf(g.Key); return o == null || o == UserDirectory.AdministratorsSid; })
                                     .Sum(g => (double)(bytes > 0 ? g.Bytes : g.Count)) / (bytes > 0 ? bytes : count);
            bool weak = weakShare > 0.5;
            string band = score >= RelevanceWeights.Likely ? $"Likely {t.Name}'s"
                        : score >= RelevanceWeights.Review ? "Review"
                        : KindOf(main.Key) == FileKind.ProgramOrCache || SystemOf(main.Key) ? "Program files"
                        : $"Probably not {t.Name}'s";
            string advice = band + (reasons.Count > 0 ? ": " + string.Join(", ", reasons.Take(4)) : "");
            if (apps.Count > 0) advice += "; contains " + string.Join(", ", apps.Take(3)) + (apps.Count > 3 ? $" and {apps.Count - 3} more" : "");
            if (weak) advice += OwnerOf(main.Key) == null ? "; weak estimate: no owner recorded (FAT/exFAT drive or unreadable)" : "; weak estimate: owned by Administrators, maybe copied in by an admin";
            return new RelevanceResult { Score = score, Advice = advice, Weak = weak };
        }
    }

    /// <summary>Owner and explicit permissions of files and folders, read without opening the file's data.</summary>
    public static class Security
    {
        const int SE_FILE_OBJECT = 1;
        const uint OWNER_SECURITY_INFORMATION = 1, DACL_SECURITY_INFORMATION = 4;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetNamedSecurityInfoW(string name, int type, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr sd);
        [DllImport("advapi32.dll")] static extern uint GetSecurityDescriptorLength(IntPtr sd);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

        /// <summary>Owner SID, or null when the drive keeps no owner (FAT/exFAT) or it cannot be read.</summary>
        public static string Owner(string path)
        {
            if (GetNamedSecurityInfoW(PathUtil.Long(path), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, out IntPtr owner, out _, out _, out _, out IntPtr sd) != 0) return null;
            try { return owner == IntPtr.Zero ? null : new SecurityIdentifier(owner).Value; }
            finally { LocalFree(sd); }
        }

        /// <summary>SIDs given access by entries set on this folder itself (not inherited).</summary>
        public static List<string> ExplicitAllowSids(string path)
        {
            var list = new List<string>();
            if (GetNamedSecurityInfoW(PathUtil.Long(path), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION, out _, out _, out IntPtr dacl, out _, out IntPtr sd) != 0) return list;
            try
            {
                if (dacl == IntPtr.Zero) return list;
                var bytes = new byte[GetSecurityDescriptorLength(sd)];
                Marshal.Copy(sd, bytes, 0, bytes.Length);
                var raw = new RawSecurityDescriptor(bytes, 0);
                if (raw.DiscretionaryAcl == null) return list;
                foreach (var ace in raw.DiscretionaryAcl.OfType<CommonAce>())
                    if (ace.AceQualifier == AceQualifier.AccessAllowed && (ace.AceFlags & AceFlags.Inherited) == 0) list.Add(ace.SecurityIdentifier.Value);
            }
            catch (ArgumentException) { }
            finally { LocalFree(sd); }
            return list;
        }
    }
}
