using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DataTransfer.Core
{
    public enum TriState { Unchecked, Checked, Mixed }

    public sealed class FolderNode
    {
        internal List<FolderNode> children;

        public string Path { get; internal set; }
        public string Name { get; internal set; }
        public FolderNode Parent { get; internal set; }
        /// <summary>The folder's own files (and, by default, everything below it) are copied.</summary>
        public bool Included { get; internal set; }
        public bool ChildrenLoaded => children != null;
        public IReadOnlyList<FolderNode> Children => (IReadOnlyList<FolderNode>)children ?? Array.Empty<FolderNode>();
        /// <summary>Set when the subfolders could not be listed.</summary>
        public string LoadError { get; internal set; }

        public override string ToString() => Path;
    }

    /// <summary>
    /// Folder-level selection with tri-state display, loaded lazily. The model:
    /// every node has its own Included flag; ticking a node sets it on the node and everything
    /// below; a node shows Checked when it and all its loaded descendants are included, Unchecked
    /// when none are, Mixed otherwise. A ticked node whose parent is not ticked is one Robocopy
    /// job; unticked nodes inside it become /XD.
    /// </summary>
    public sealed class FolderTree
    {
        readonly Func<string, IEnumerable<string>> listSubfolders;
        public List<FolderNode> Roots { get; } = new List<FolderNode>();

        /// <param name="roots">Usually the fixed drives ("C:\").</param>
        /// <param name="listSubfolders">Full paths of a folder's subfolders; default lists the file system.</param>
        public FolderTree(IEnumerable<string> roots, Func<string, IEnumerable<string>> listSubfolders = null)
        {
            this.listSubfolders = listSubfolders ?? ListFileSystem;
            foreach (var r in roots) Roots.Add(new FolderNode { Path = r, Name = r.TrimEnd('\\') });
        }

        public static IEnumerable<string> FixedDrives() =>
            DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName);

        /// <summary>
        /// Subfolders, skipping junctions and symbolic links (Robocopy /XJ does not follow them,
        /// and profiles contain compatibility junctions such as "My Documents").
        /// </summary>
        public static IEnumerable<string> ListFileSystem(string folder)
        {
            var list = new List<string>();
            foreach (var d in new DirectoryInfo(PathUtil.Long(folder)).EnumerateDirectories())
            {
                if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                list.Add(System.IO.Path.Combine(folder, d.Name));
            }
            return list.OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<FolderNode> EnsureChildren(FolderNode node)
        {
            if (node.children != null) return node.children;
            node.children = new List<FolderNode>();
            try
            {
                foreach (var p in listSubfolders(node.Path))
                    node.children.Add(new FolderNode { Path = p, Name = System.IO.Path.GetFileName(p.TrimEnd('\\')), Parent = node, Included = node.Included });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { node.LoadError = ex.Message; }
            return node.children;
        }

        public TriState State(FolderNode node)
        {
            if (node.children == null || node.children.Count == 0) return node.Included ? TriState.Checked : TriState.Unchecked;
            bool all = node.Included, none = !node.Included;
            foreach (var c in node.children)
            {
                var s = State(c);
                if (s != TriState.Checked) all = false;
                if (s != TriState.Unchecked) none = false;
                if (!all && !none) return TriState.Mixed;
            }
            return all ? TriState.Checked : none ? TriState.Unchecked : TriState.Mixed;
        }

        /// <summary>Click on a node: Checked becomes Unchecked; Unchecked and Mixed become Checked.</summary>
        public void Toggle(FolderNode node) => SetSubtree(node, State(node) != TriState.Checked);

        public void SetSubtree(FolderNode node, bool included)
        {
            node.Included = included;
            if (node.children != null) foreach (var c in node.children) SetSubtree(c, included);
        }

        public void Clear()
        {
            foreach (var r in Roots) SetSubtree(r, false);
        }

        /// <summary>Finds the node for a path, loading the folders on the way. Null if it does not exist.</summary>
        public FolderNode Find(string path)
        {
            string target = PathUtil.Trim(System.IO.Path.GetFullPath(path));
            var node = Roots.FirstOrDefault(r => PathUtil.IsSameOrUnder(target, r.Path));
            while (node != null && !string.Equals(PathUtil.Trim(node.Path), target, StringComparison.OrdinalIgnoreCase))
                node = EnsureChildren(node).FirstOrDefault(c => PathUtil.IsSameOrUnder(target, c.Path));
            return node;
        }

        /// <summary>Ticks a folder and everything below it. False if the folder is not in the tree.</summary>
        public bool Include(string path)
        {
            var n = Find(path);
            if (n == null) return false;
            SetSubtree(n, true);
            return true;
        }

        public bool Exclude(string path)
        {
            var n = Find(path);
            if (n == null) return false;
            SetSubtree(n, false);
            return true;
        }

        /// <summary>The Robocopy jobs this selection stands for.</summary>
        public List<SourceSelection> ToSources()
        {
            var list = new List<SourceSelection>();
            foreach (var r in Roots) Collect(r, null, list);
            return list;
        }

        static void Collect(FolderNode node, SourceSelection current, List<SourceSelection> list)
        {
            if (node.Included && current == null)
            {
                current = new SourceSelection { Path = PathUtil.Trim(node.Path) };
                list.Add(current);
            }
            else if (!node.Included && current != null)
            {
                current.ExcludedSubfolders.Add(PathUtil.Trim(node.Path));
                current = null; // an included folder further down is a job of its own
            }
            if (node.children != null) foreach (var c in node.children) Collect(c, current, list);
        }

        /// <summary>Restores a saved selection (for resume).</summary>
        public void Apply(IEnumerable<SourceSelection> sources)
        {
            Clear();
            var all = sources.ToList();
            // outer folders first so that nested jobs and exclusions land on top
            foreach (var s in all.OrderBy(s => s.Path.Length)) Include(s.Path);
            foreach (var x in all.SelectMany(s => s.ExcludedSubfolders).OrderBy(x => x.Length)) Exclude(x);
            foreach (var s in all.Where(s => all.Any(o => o != s && PathUtil.IsSameOrUnder(s.Path, o.Path)))) Include(s.Path);
        }
    }

    /// <summary>The "User data" preset: Desktop, Documents, Pictures and Downloads of every profile.</summary>
    public static class UserDataPreset
    {
        public static readonly string[] FolderNames = { "Desktop", "Documents", "Pictures", "Downloads" };
        static readonly string[] SkipProfiles = { "Public", "Default", "Default User", "All Users", "defaultuser0" };

        /// <summary>
        /// Existing preset folders. When OneDrive backs up a profile's folders they live in
        /// "OneDrive*\Desktop" etc. inside the profile, so those are ticked too.
        /// </summary>
        public static List<string> Folders(ProfileInfo profiles)
        {
            var result = new List<string>();
            foreach (var profile in profiles.ProfileFolders.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(profile.TrimEnd('\\'));
                if (SkipProfiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    if ((File.GetAttributes(profile) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (var f in FolderNames)
                    {
                        string p = Path.Combine(profile, f);
                        if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0) result.Add(p);
                    }
                    foreach (var od in Directory.GetDirectories(profile, "OneDrive*"))
                    {
                        if ((File.GetAttributes(od) & FileAttributes.ReparsePoint) != 0) continue;
                        foreach (var f in FolderNames)
                        {
                            string p = Path.Combine(od, f);
                            if (Directory.Exists(p)) result.Add(p);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            return result;
        }
    }
}
