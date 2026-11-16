using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DataTransfer.Core;

namespace DataTransferTool
{
    /// <summary>
    /// Screen 1: lazy folder tree with tri-state check boxes and the "User data" preset. v2: the
    /// tree is a tree-grid with Modified and Size columns, opened folders also list their files,
    /// and sizes come from a background scan.
    /// </summary>
    internal sealed class SelectStep : StepControl
    {
        /// <summary>Files listed under one folder; the rest are counted in a "more files" line.</summary>
        const int MaxFilesShown = 1000;
        const string Waiting = "…";

        readonly TreeGrid tree = new TreeGrid { Name = "folderTree", Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true, ShowLines = true };
        readonly Label detailLabel = new Label { Name = "detailLabel", Dock = DockStyle.Bottom, AutoSize = false, Height = Ui.BaseFont.Height * 3 + 10, AutoEllipsis = true, ForeColor = Ui.Muted, Padding = new Padding(0, 4, 0, 0), UseMnemonic = false };
        readonly Timer scanTimer = new Timer { Interval = 300 };
        /// <summary>Folder rows still showing "…".</summary>
        readonly System.Collections.Generic.HashSet<TreeNode> waiting = new System.Collections.Generic.HashSet<TreeNode>();
        FolderScanner scanner;
        RelevanceClassifier classifier;
        /// <summary>Whose data relevance is estimated for; null = All users (no estimate).</summary>
        RelevanceTarget target;
        readonly ListBox selectionList = new ListBox { Name = "selectionList", Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
        readonly Label summaryLabel = new Label { Name = "summaryLabel", Dock = DockStyle.Bottom, AutoSize = false, Height = 48, AutoEllipsis = true, ForeColor = Ui.Muted, Padding = new Padding(0, 6, 0, 0) };
        readonly TextBox addPathBox = Ui.TextBox("addPathBox", 300);
        readonly Label messageLabel = new Label { Name = "messageLabel", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        readonly ComboBox userCombo = new ComboBox { Name = "userCombo", DropDownStyle = ComboBoxStyle.DropDownList, Width = 340, Margin = new Padding(3, 4, 3, 3) };
        readonly Label accessLabel = new Label { Name = "accessLabel", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        readonly ToolTip tips = new ToolTip();

        /// <summary>An entry of the user picker; User is null for "All users".</summary>
        sealed class UserItem
        {
            public SignedInUser User;
            public override string ToString() => User == null ? "All users" : $"{User.Account}  ({User.AccessText})";
        }
        int sourceCount;

        public SelectStep(AppState state) : base(state)
        {
            var userData = Ui.Button("userDataButton", "Tick user data");
            var clear = Ui.Button("clearButton", "Clear");
            var add = Ui.Button("addPathButton", "Tick");
            var browse = Ui.Button("browseButton", "Browse...");
            var options = Ui.Button("optionsButton", "Options...");
            tips.SetToolTip(userData, "Ticks Desktop, Documents, Pictures and Downloads of every user profile on this PC.");
            tips.SetToolTip(userCombo, "Whose data this is. Used to estimate how relevant each folder is to that user; it does not tick anything.");

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 6) };
            bar.Controls.AddRange(new Control[] { userData, clear, Ui.Label("Folder:"), addPathBox, add, browse, options, messageLabel });

            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0) };
            right.Controls.Add(selectionList);
            right.Controls.Add(new Label { Text = "Will be copied (one Robocopy job each):", Dock = DockStyle.Top, AutoSize = true, Font = Ui.BoldFont, Padding = new Padding(0, 0, 0, 4) });
            right.Controls.Add(summaryLabel);

            // tree-grid: names on the left, columns on the right
            tree.Columns.Add(new GridColumn { Header = "Modified", Width = TextRenderer.MeasureText("2026-12-31 23:59", Font).Width + 16 });
            tree.Columns.Add(new GridColumn { Header = "Size", Width = TextRenderer.MeasureText("1023.99 MB+", Font).Width + 16, Align = HorizontalAlignment.Right });
            tree.Columns.Add(new GridColumn { Header = "Relevant", Width = TextRenderer.MeasureText("Relevant", Ui.BoldFont).Width + 16, Align = HorizontalAlignment.Right });
            tree.Columns.Add(new GridColumn { Header = "Advice", Width = 160, Share = 0.28 });
            tree.CellText = CellText;
            tree.CellColor = CellColor;
            tree.NodeColor = tn => tn.Tag is FileItem f && f.CloudOnly || tn.Tag is string ? Ui.Muted : Color.Empty;
            var grid = new Panel { Name = "gridPanel", Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
            grid.Controls.Add(tree);
            grid.Controls.Add(new TreeGridHeader(tree, "Folder / file"));
            var left = new Panel { Dock = DockStyle.Fill };
            left.Controls.Add(grid);
            left.Controls.Add(detailLabel);

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(right);

            // top left: whose data is being exported
            var userRow = new FlowLayoutPanel { Name = "userRow", Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 4) };
            userRow.Controls.AddRange(new Control[] { Ui.Label("User:", bold: true), userCombo, accessLabel });
            userCombo.Items.Add("Loading users...");
            userCombo.SelectedIndex = 0;
            userCombo.Enabled = false;
            userCombo.SelectedIndexChanged += (s, e) => UserChanged();

            Controls.Add(split);
            Controls.Add(bar);
            Controls.Add(userRow);
            Ui.SplitAt(split, 0.66, 200);

            tree.StateImageList = Ui.CheckImages(this);
            tree.BeforeExpand += (s, e) => Populate(e.Node);
            tree.AfterSelect += (s, e) => ShowDetail();
            scanTimer.Tick += (s, e) => ScanProgress();
            tree.NodeMouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && tree.HitTest(e.Location).Location == TreeViewHitTestLocations.StateImage) Toggle(e.Node);
            };
            tree.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Space && tree.SelectedNode != null) { Toggle(tree.SelectedNode); e.Handled = e.SuppressKeyPress = true; }
            };

            userData.Click += (s, e) => TickUserData();
            clear.Click += (s, e) => { State.Tree.Clear(); Refresh(""); };
            add.Click += (s, e) => TickPath(addPathBox.Text);
            addPathBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { TickPath(addPathBox.Text); e.SuppressKeyPress = true; } };
            browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Folder to copy", ShowNewFolderButton = false })
                    if (d.ShowDialog(this) == DialogResult.OK) { addPathBox.Text = d.SelectedPath; TickPath(d.SelectedPath); }
            };
            options.Click += (s, e) =>
            {
                using (var f = new OptionsForm(State.Job))
                    if (f.ShowDialog(this) == DialogResult.OK) { State.Invalidate(); Message("Options saved.", false); }
            };
        }

        public override string Title => "Select folders";
        public override string Subtitle => "Tick the folders and files to copy. Unticked subfolders and files are left out. Click a box or press Space.";
        public override bool CanGoNext => sourceCount > 0;

        public override void OnEnter()
        {
            if (State.Tree != null) return;
            LoadUsers();
            classifier = new RelevanceClassifier(State.Profiles.ProfileFolders);
            scanner = new FolderScanner(State.Job.Options.BackupMode, classifier);
            scanTimer.Start();
            State.Tree = new FolderTree(FolderTree.FixedDrives())
            {
                // each profile's AppData (Options) shows unticked when the profile is ticked, as it is not copied
                LeaveUnticked = path => RobocopyCommandBuilder.ProfileExclusions(Path.GetDirectoryName(path.TrimEnd('\\')) ?? path, State.Job.Options.ExcludeProfileDirs, State.Profiles)
                                        .Any(x => string.Equals(PathUtil.Trim(x), PathUtil.Trim(path), StringComparison.OrdinalIgnoreCase))
            };
            tree.BeginUpdate();
            foreach (var r in State.Tree.Roots) tree.Nodes.Add(NewNode(r));
            tree.EndUpdate();
            RequestScan(tree.Nodes);
            if (State.Job.Sources.Count > 0) State.Tree.Apply(State.Job.Sources);
            Refresh(null);
        }

        public override bool OnLeaving(bool forward)
        {
            if (!forward) return true;
            var sources = State.Tree.ToSources();
            if (sources.Count == 0) return false;
            bool same = sources.Count == State.Job.Sources.Count && sources.Zip(State.Job.Sources, (a, b) =>
                a.Path == b.Path && a.ExcludedSubfolders.SequenceEqual(b.ExcludedSubfolders) &&
                a.ExcludedFiles.SequenceEqual(b.ExcludedFiles) && a.Files.SequenceEqual(b.Files)).All(x => x);
            if (!same) { State.Job.Sources = sources; State.Invalidate(); }
            ScoreSources();
            return true;
        }

        // ------------------------------------------------------------ tree

        TreeNode NewNode(FolderNode n)
        {
            var tn = new TreeNode(n.Name) { Tag = n, Name = n.Path };
            tn.Nodes.Add(new TreeNode("...")); // placeholder until expanded: the folder may hold files even without subfolders
            return tn;
        }

        void Populate(TreeNode tn)
        {
            // only a node that still shows its placeholder needs its children
            if (!(tn.Tag is FolderNode n) || tn.Nodes.Count != 1 || tn.Nodes[0].Tag != null) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                var kids = State.Tree.EnsureChildren(n);
                var files = FolderScanner.ListFiles(n.Path, MaxFilesShown, classifier);
                tree.BeginUpdate();
                tn.Nodes.Clear();
                foreach (var c in kids) tn.Nodes.Add(NewNode(c));
                foreach (var f in files.Files) tn.Nodes.Add(new TreeNode(f.Name) { Tag = f, Name = f.Path });
                int more = files.TotalFiles - files.Files.Count;
                if (more > 0) tn.Nodes.Add(new TreeNode($"... and {more:N0} more file(s), not listed here") { Tag = "more" });
                if (n.LoadError != null) tn.ToolTipText = "Subfolders cannot be listed: " + n.LoadError;
                RefreshImages(tn.Nodes);
                tree.EndUpdate();
                RequestScan(tn.Nodes);
            }
            finally { Cursor = Cursors.Default; }
        }

        void Toggle(TreeNode tn)
        {
            if (tn.Tag is FileItem file && tn.Parent?.Tag is FolderNode folder)
            {
                State.Tree.ToggleFile(folder, file.Name);
                Refresh(null);
                return;
            }
            if (!(tn.Tag is FolderNode n)) return;
            State.Tree.Toggle(n);
            Refresh(null);
        }

        void RefreshImages(TreeNodeCollection nodes)
        {
            foreach (TreeNode tn in nodes)
            {
                if (tn.Tag is FileItem file)
                {
                    int fi = tn.Parent?.Tag is FolderNode p && State.Tree.IsFileIncluded(p, file.Name) ? 1 : 0;
                    if (tn.StateImageIndex != fi) tn.StateImageIndex = fi;
                    continue;
                }
                if (!(tn.Tag is FolderNode n)) continue;
                int i = (int)State.Tree.State(n);
                if (tn.StateImageIndex != i) tn.StateImageIndex = i;
                if (!(tn.Nodes.Count == 1 && tn.Nodes[0].Tag == null)) RefreshImages(tn.Nodes); // skip only a bare placeholder
            }
        }

        // ------------------------------------------------------------ columns

        void RequestScan(TreeNodeCollection nodes)
        {
            var paths = new System.Collections.Generic.List<string>();
            foreach (TreeNode tn in nodes)
                if (tn.Tag is FolderNode n && !scanner.TryGet(n.Path, out _)) { waiting.Add(tn); paths.Add(n.Path); }
            scanner.Request(paths);
        }

        // columns: 0 Modified, 1 Size, 2 Relevant, 3 Advice
        string CellText(TreeNode tn, int column)
        {
            if (tn.Tag is FileItem f)
            {
                if (column == 0) return Date(f.ModifiedUtc);
                if (column == 1) return GridSize(f.Size);
                var fr = Relevance(tn);
                return fr == null ? "" : column == 2 ? fr.Score + " %" : fr.Advice;
            }
            if (!(tn.Tag is FolderNode n)) return null;
            if (scanner == null || !scanner.TryGet(n.Path, out var s)) return column >= 2 && target == null ? "" : Waiting;
            if (column == 0) return Date(s.NewestUtc);
            if (column == 1) return GridSize(s.Bytes) + (s.UnreadableFolders > 0 ? "+" : "");
            var r = Relevance(tn);
            return r == null ? "" : column == 2 ? r.Score + " %" : r.Advice;
        }

        /// <summary>Score and Advice of a row for the picked user; null with All users or before the numbers arrive.</summary>
        RelevanceResult Relevance(TreeNode tn)
        {
            if (target == null || classifier == null) return null;
            if (tn.Tag is FileItem f) return classifier.Evaluate(f.AsGroups(), target);
            if (tn.Tag is FolderNode n && scanner.TryGet(n.Path, out var s)) return classifier.Evaluate(s.Groups, target);
            return null;
        }

        Color CellColor(TreeNode tn, int column)
        {
            if (column < 2) return Color.Empty;
            var r = Relevance(tn);
            if (r == null) return Color.Empty;
            if (r.Weak) return Ui.Warning;
            return r.Score >= RelevanceWeights.Likely ? Ui.Good : r.Score < RelevanceWeights.Review ? Ui.Muted : Color.Empty;
        }

        static bool Matches(string pattern, string name) =>
            !string.IsNullOrWhiteSpace(pattern) && System.Text.RegularExpressions.Regex.IsMatch(name,
                "^" + System.Text.RegularExpressions.Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        static string Date(DateTime? utc) => utc.HasValue ? utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";

        static string GridSize(long bytes) => bytes < 1024 ? bytes + " B" : Ui.Size(bytes);

        /// <summary>Timer: repaints rows whose numbers have arrived.</summary>
        void ScanProgress()
        {
            if (waiting.Count == 0 || scanner == null) return;
            foreach (var tn in waiting.ToList())
            {
                if (tn.TreeView == null) { waiting.Remove(tn); continue; }
                if (!scanner.TryGet(((FolderNode)tn.Tag).Path, out _)) continue;
                waiting.Remove(tn);
                tree.InvalidateRow(tn);
                if (tn == tree.SelectedNode) ShowDetail();
            }
        }

        /// <summary>The line under the tree: facts about the selected folder or file.</summary>
        void ShowDetail()
        {
            var tn = tree.SelectedNode;
            string text = "";
            if (tn?.Tag is FileItem f)
            {
                text = $"{f.Path}: {Ui.Size(f.Size)}, modified {Date(f.ModifiedUtc)}." +
                       (f.CloudOnly ? " Cloud-only (OneDrive keeps it online): counted, but not copied." : "") +
                       (tn.Parent?.Tag is FolderNode p && !p.Included && State.Tree.IsFileIncluded(p, f.Name) ? " Ticked on its own: copied without the rest of its folder." : "");
                string rule = State.Job.Options.ExcludeFiles.FirstOrDefault(x => Matches(x, f.Name));
                if (rule != null) text += $" Not copied even when ticked: it matches the exclusion \"{rule}\" (Options).";
                text += RelevanceLine(tn);
            }
            else if (tn?.Tag is FolderNode n)
            {
                if (scanner == null || !scanner.TryGet(n.Path, out var s)) text = $"{PathUtil.Trim(n.Path)}: counting files {Waiting}";
                else
                {
                    text = $"{PathUtil.Trim(n.Path)}: {s.Files:N0} file(s), {Ui.Size(s.Bytes)}" + (s.NewestUtc.HasValue ? $", newest {Date(s.NewestUtc)}." : ".");
                    if (s.CloudFiles > 0) text += $" {s.CloudFiles:N0} cloud-only file(s) ({Ui.Size(s.CloudBytes)}) are counted but not copied.";
                    if (s.UnreadableFolders > 0) text += $" {s.UnreadableFolders:N0} folder(s) inside cannot be read, so the size is at least this.";
                    text += RelevanceLine(tn);
                }
            }
            else if (tn?.Tag is string) text = "Only the first " + MaxFilesShown.ToString("N0") + " files of a folder are listed; all of them are counted in its size and copied with it.";
            detailLabel.Text = text;
        }

        /// <summary>Keeps each job's relevance for the report. Jobs whose numbers are not ready stay without one.</summary>
        void ScoreSources()
        {
            foreach (var s in State.Job.Sources)
            {
                s.Relevance = null; s.RelevanceWeak = false;
                if (target == null) continue;
                RelevanceGroup[] groups = null;
                if (s.FilesOnly)
                {
                    var wanted = new System.Collections.Generic.HashSet<string>(s.Files, StringComparer.OrdinalIgnoreCase);
                    groups = FolderScanner.ListFiles(s.Path, int.MaxValue, classifier).Files.Where(f => wanted.Contains(f.Name)).SelectMany(f => f.AsGroups()).ToArray();
                }
                else if (scanner.TryGet(s.Path, out var sum) && sum.Groups != null)
                {
                    groups = sum.Groups;
                    foreach (var x in s.ExcludedSubfolders)
                        if (scanner.TryGet(x, out var part) && part.Groups != null) groups = RelevanceClassifier.Subtract(groups, part.Groups);
                }
                var r = classifier.Evaluate(groups, target);
                if (r != null) { s.Relevance = r.Score; s.RelevanceWeak = r.Weak; }
            }
        }

        string RelevanceLine(TreeNode tn)
        {
            if (target == null) return "\r\nPick a user at the top left to estimate how relevant each item is to them.";
            var r = Relevance(tn);
            return r == null ? "" : $"\r\nRelevant to {target.Name}: {r.Score} %. {r.Advice}.";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { scanTimer.Dispose(); scanner?.Dispose(); tips.Dispose(); }
            base.Dispose(disposing);
        }

        /// <summary>Expands the tree down to a folder and selects it.</summary>
        void Reveal(string path)
        {
            var nodes = tree.Nodes;
            TreeNode found = null;
            while (true)
            {
                TreeNode next = null;
                foreach (TreeNode tn in nodes)
                    if (tn.Tag is FolderNode n && PathUtil.IsSameOrUnder(path, n.Path)) { next = tn; break; }
                if (next == null) break;
                found = next;
                if (string.Equals(PathUtil.Trim(((FolderNode)next.Tag).Path), PathUtil.Trim(path), StringComparison.OrdinalIgnoreCase)) break;
                Populate(next);
                next.Expand();
                nodes = next.Nodes;
            }
            if (found != null) { tree.SelectedNode = found; found.EnsureVisible(); }
        }

        // ------------------------------------------------------------ actions

        void TickPath(string text)
        {
            string path = (text ?? "").Trim().Trim('"');
            if (path.Length == 0) return;
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { Message("Not a valid folder path.", true); return; }
            if (!State.Tree.Include(full)) { Message("Folder not found on a fixed drive of this PC: " + full, true); return; }
            Reveal(full);
            Refresh("Ticked " + full);
        }

        void TickUserData()
        {
            var folders = UserDataPreset.Folders(State.Profiles);
            int ticked = folders.Count(State.Tree.Include);
            string usersDir = State.Profiles.ProfilesDirectory.TrimEnd('\\') + "\\";
            int count = folders.Where(f => f.StartsWith(usersDir, StringComparison.OrdinalIgnoreCase))
                                  .Select(f => f.Substring(usersDir.Length).Split('\\')[0])
                                  .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (folders.Count > 0) Reveal(State.Profiles.ProfilesDirectory);
            Refresh(ticked == 0 ? "No user folders found." : $"Ticked {ticked} folder(s) in {count} profile(s).");
        }

        void Refresh(string message)
        {
            tree.BeginUpdate();
            RefreshImages(tree.Nodes);
            tree.EndUpdate();
            var sources = State.Tree.ToSources();
            sourceCount = sources.Count;
            selectionList.BeginUpdate();
            selectionList.Items.Clear();
            foreach (var s in sources)
                selectionList.Items.Add(s.Describe());
            selectionList.EndUpdate();
            summaryLabel.Text = sources.Count == 0 ? "Nothing ticked yet." :
                $"{sources.Count} folder job(s). Skipped everywhere: {string.Join(", ", State.Job.Options.ExcludeFiles)}; and {string.Join(", ", State.Job.Options.ExcludeProfileDirs)} in each profile.";
            if (message != null) Message(message, false);
            ShowDetail();
            RaiseButtonsChanged();
        }

        // ------------------------------------------------------------ user picker

        void LoadUsers()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try { return UserDirectory.List(new SystemAccountSource()); }
                catch { return new System.Collections.Generic.List<SignedInUser>(); }
            }).ContinueWith(t =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)(() => ShowUsers(t.Result)));
            });
        }

        void ShowUsers(System.Collections.Generic.List<SignedInUser> users)
        {
            userCombo.BeginUpdate();
            userCombo.Items.Clear();
            userCombo.Items.Add(new UserItem());
            foreach (var u in users) userCombo.Items.Add(new UserItem { User = u });
            userCombo.EndUpdate();
            int pick = 0;
            for (int i = 1; i < userCombo.Items.Count; i++)
                if (((UserItem)userCombo.Items[i]).User.Sid == State.Job.SelectedUserSid) pick = i;
            userCombo.Enabled = true;
            userCombo.SelectedIndex = pick;
            using (var g = userCombo.CreateGraphics())
                userCombo.DropDownWidth = Math.Max(userCombo.Width, userCombo.Items.Cast<object>().Max(o => (int)g.MeasureString(o.ToString(), userCombo.Font).Width) + 30);
            UserChanged();
        }

        void UserChanged()
        {
            if (!(userCombo.SelectedItem is UserItem item)) { accessLabel.Text = ""; return; }
            var u = item.User;
            State.Job.SelectedUserSid = u?.Sid;
            // only the Relevant and Advice columns change; nothing is ticked (owner, 2026-11-15)
            target = RelevanceTarget.From(u);
            tree.Invalidate();
            ShowDetail();
            if (u == null)
            {
                int n = userCombo.Items.Count - 1;
                accessLabel.Text = n == 0 ? "No user profiles found." : $"{n} user(s) have signed in on this PC.";
                accessLabel.ForeColor = Ui.Muted;
                tips.SetToolTip(accessLabel, null);
            }
            else
            {
                accessLabel.Text = $"{u.AccessText}  -  {u.SignInText}";
                accessLabel.ForeColor = u.Access == AccessLevel.Unknown ? Ui.Warning : SystemColors.ControlText;
                tips.SetToolTip(accessLabel, "Profile folder: " + u.ProfilePath + (u.AccountFound ? "" : "\r\nThe account no longer exists or cannot be looked up."));
            }
        }

        void Message(string text, bool error)
        {
            messageLabel.Text = text;
            messageLabel.ForeColor = error ? Ui.Bad : Ui.Muted;
        }
    }
}
