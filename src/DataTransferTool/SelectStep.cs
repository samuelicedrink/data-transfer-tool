using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DataTransfer.Core;

namespace DataTransferTool
{
    /// <summary>Screen 1: lazy folder tree with tri-state check boxes and the "User data" preset.</summary>
    internal sealed class SelectStep : StepControl
    {
        readonly TreeView tree = new TreeView { Name = "folderTree", Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true, ShowLines = true };
        readonly ListBox selectionList = new ListBox { Name = "selectionList", Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
        readonly Label summaryLabel = new Label { Name = "summaryLabel", Dock = DockStyle.Bottom, AutoSize = false, Height = 48, AutoEllipsis = true, ForeColor = Ui.Muted, Padding = new Padding(0, 6, 0, 0) };
        readonly TextBox addPathBox = Ui.TextBox("addPathBox", 300);
        readonly Label messageLabel = new Label { Name = "messageLabel", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        readonly ComboBox userCombo = new ComboBox { Name = "userCombo", DropDownStyle = ComboBoxStyle.DropDownList, Width = 340, Margin = new Padding(3, 4, 3, 3) };
        readonly Label accessLabel = new Label { Name = "accessLabel", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        readonly ToolTip tips = new ToolTip();
        Button userDataButton;

        /// <summary>An entry of the user picker; User is null for "All users".</summary>
        sealed class UserItem
        {
            public SignedInUser User;
            public override string ToString() => User == null ? "All users" : $"{User.Account}  ({User.AccessText})";
        }
        int sourceCount;

        public SelectStep(AppState state) : base(state)
        {
            var userData = userDataButton = Ui.Button("userDataButton", "Tick user data");
            var clear = Ui.Button("clearButton", "Clear");
            var add = Ui.Button("addPathButton", "Tick");
            var browse = Ui.Button("browseButton", "Browse...");
            var options = Ui.Button("optionsButton", "Options...");
            SetUserDataTip();

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 6) };
            bar.Controls.AddRange(new Control[] { userData, clear, Ui.Label("Folder:"), addPathBox, add, browse, options, messageLabel });

            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0) };
            right.Controls.Add(selectionList);
            right.Controls.Add(new Label { Text = "Will be copied (one Robocopy job each):", Dock = DockStyle.Top, AutoSize = true, Font = Ui.BoldFont, Padding = new Padding(0, 0, 0, 4) });
            right.Controls.Add(summaryLabel);

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
            split.Panel1.Controls.Add(tree);
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
            Ui.SplitAt(split, 0.58, 200);

            tree.StateImageList = Ui.CheckImages(this);
            tree.BeforeExpand += (s, e) => Populate(e.Node);
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
        public override string Subtitle => "Tick the folders to copy. Unticked subfolders are left out. Click a box or press Space.";
        public override bool CanGoNext => sourceCount > 0;

        public override void OnEnter()
        {
            if (State.Tree != null) return;
            LoadUsers();
            State.Tree = new FolderTree(FolderTree.FixedDrives());
            tree.BeginUpdate();
            foreach (var r in State.Tree.Roots) tree.Nodes.Add(NewNode(r));
            tree.EndUpdate();
            if (State.Job.Sources.Count > 0) State.Tree.Apply(State.Job.Sources);
            Refresh(null);
        }

        public override bool OnLeaving(bool forward)
        {
            if (!forward) return true;
            var sources = State.Tree.ToSources();
            if (sources.Count == 0) return false;
            bool same = sources.Count == State.Job.Sources.Count && sources.Zip(State.Job.Sources, (a, b) =>
                a.Path == b.Path && a.ExcludedSubfolders.SequenceEqual(b.ExcludedSubfolders)).All(x => x);
            if (!same) { State.Job.Sources = sources; State.Invalidate(); }
            return true;
        }

        // ------------------------------------------------------------ tree

        TreeNode NewNode(FolderNode n)
        {
            var tn = new TreeNode(n.Name) { Tag = n, Name = n.Path };
            if (!n.ChildrenLoaded || n.Children.Count > 0) tn.Nodes.Add(new TreeNode("...")); // placeholder until expanded
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
                tree.BeginUpdate();
                tn.Nodes.Clear();
                foreach (var c in kids) tn.Nodes.Add(NewNode(c));
                if (n.LoadError != null) tn.ToolTipText = "Subfolders cannot be listed: " + n.LoadError;
                RefreshImages(tn.Nodes);
                tree.EndUpdate();
            }
            finally { Cursor = Cursors.Default; }
        }

        void Toggle(TreeNode tn)
        {
            if (!(tn.Tag is FolderNode n)) return;
            State.Tree.Toggle(n);
            Refresh(null);
        }

        void RefreshImages(TreeNodeCollection nodes)
        {
            foreach (TreeNode tn in nodes)
            {
                if (!(tn.Tag is FolderNode n)) continue;
                int i = (int)State.Tree.State(n);
                if (tn.StateImageIndex != i) tn.StateImageIndex = i;
                if (tn.IsExpanded || tn.Nodes.Count > 1) RefreshImages(tn.Nodes);
            }
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
            var user = (userCombo.SelectedItem as UserItem)?.User;
            var profiles = user == null ? State.Profiles
                : new ProfileInfo { ProfilesDirectory = State.Profiles.ProfilesDirectory, ProfileFolders = { user.ProfilePath } };
            var folders = UserDataPreset.Folders(profiles);
            int ticked = folders.Count(State.Tree.Include);
            string usersDir = State.Profiles.ProfilesDirectory.TrimEnd('\\') + "\\";
            int count = folders.Where(f => f.StartsWith(usersDir, StringComparison.OrdinalIgnoreCase))
                                  .Select(f => f.Substring(usersDir.Length).Split('\\')[0])
                                  .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (folders.Count > 0) Reveal(State.Profiles.ProfilesDirectory);
            Refresh(ticked == 0 ? "No user folders found." : user != null ? $"Ticked {ticked} folder(s) of {user.Account}." : $"Ticked {ticked} folder(s) in {count} profile(s).");
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
                selectionList.Items.Add(s.ExcludedSubfolders.Count == 0 ? s.Path : $"{s.Path}   (without {s.ExcludedSubfolders.Count} subfolder{(s.ExcludedSubfolders.Count == 1 ? "" : "s")})");
            selectionList.EndUpdate();
            summaryLabel.Text = sources.Count == 0 ? "Nothing ticked yet." :
                $"{sources.Count} folder job(s). Skipped everywhere: {string.Join(", ", State.Job.Options.ExcludeFiles)}; and {string.Join(", ", State.Job.Options.ExcludeProfileDirs)} in each profile.";
            if (message != null) Message(message, false);
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
            SetUserDataTip();
        }

        void SetUserDataTip()
        {
            var u = (userCombo.SelectedItem as UserItem)?.User;
            tips.SetToolTip(userDataButton, u == null
                ? "Ticks Desktop, Documents, Pictures and Downloads of every user profile on this PC."
                : $"Ticks Desktop, Documents, Pictures and Downloads of {u.Account}.");
        }

        void Message(string text, bool error)
        {
            messageLabel.Text = text;
            messageLabel.ForeColor = error ? Ui.Bad : Ui.Muted;
        }
    }
}
