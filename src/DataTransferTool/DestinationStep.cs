using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DataTransfer.Core;

namespace DataTransferTool
{
    /// <summary>Screen 2: a local or USB folder, or a network share with its own credentials.</summary>
    internal sealed class DestinationStep : StepControl
    {
        readonly RadioButton localRadio = new RadioButton { Name = "localRadio", Text = "Local folder or USB drive", AutoSize = true, Checked = true, Font = Ui.BoldFont, Margin = new Padding(3, 6, 3, 3) };
        readonly RadioButton networkRadio = new RadioButton { Name = "networkRadio", Text = "Network share", AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(3, 14, 3, 3) };
        readonly TextBox localPathBox = Ui.TextBox("localPathBox", 420);
        readonly TextBox uncPathBox = Ui.TextBox("uncPathBox", 420);
        readonly TextBox userBox = Ui.TextBox("userBox", 260);
        readonly TextBox passwordBox = new TextBox { Name = "passwordBox", Width = 260, UseSystemPasswordChar = true, Margin = new Padding(3, 4, 3, 3) };
        readonly CheckBox saveCredsCheck = new CheckBox { Name = "saveCredsCheck", AutoSize = true, Text = "Save credentials for resume (encrypted for this Windows account on this PC)", Margin = new Padding(3, 6, 3, 3) };
        readonly Label noticeLabel = new Label { Name = "noticeLabel", AutoSize = true, ForeColor = Ui.Warning, Font = Ui.BoldFont, Margin = new Padding(3, 6, 3, 3) };
        readonly Button browseButton = Ui.Button("browseDestButton", "Browse...");
        readonly Button testButton = Ui.Button("testButton", "Test destination");
        readonly Label resultLabel = new Label { Name = "resultLabel", AutoSize = true, MaximumSize = new System.Drawing.Size(820, 0), Margin = new Padding(3, 10, 3, 3) };
        readonly Label layoutLabel = new Label { Name = "layoutLabel", AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new System.Drawing.Size(820, 0), Margin = new Padding(3, 12, 3, 3) };
        bool testing;

        public DestinationStep(AppState state) : base(state)
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int row = 0;
            void Add(Control a, Control b = null, Control c = null)
            {
                grid.Controls.Add(a, 0, row);
                if (b != null) grid.Controls.Add(b, 1, row);
                if (c != null) grid.Controls.Add(c, 2, row);
                row++;
            }
            grid.Controls.Add(localRadio, 0, row); grid.SetColumnSpan(localRadio, 3); row++;
            Add(Ui.Label("Folder:"), localPathBox, browseButton);
            grid.Controls.Add(networkRadio, 0, row); grid.SetColumnSpan(networkRadio, 3); row++;
            Add(Ui.Label("Share path:"), uncPathBox, Ui.Label(@"\\server\share\folder", null) );
            Add(Ui.Label("User name:"), userBox, Ui.Label(@"DOMAIN\name or name@domain. Empty: connect as the account running this tool."));
            Add(Ui.Label("Password:"), passwordBox, Ui.Label("Kept in memory only, unless saved below."));
            ((Label)grid.GetControlFromPosition(2, 3)).ForeColor = Ui.Muted;
            ((Label)grid.GetControlFromPosition(2, 4)).ForeColor = Ui.Muted;
            ((Label)grid.GetControlFromPosition(2, 5)).ForeColor = Ui.Muted;
            grid.Controls.Add(saveCredsCheck, 1, row); grid.SetColumnSpan(saveCredsCheck, 2); row++;
            grid.Controls.Add(noticeLabel, 0, row); grid.SetColumnSpan(noticeLabel, 3); row++;
            saveCredsCheck.CheckedChanged += (s, e) => State.SaveCredentials = saveCredsCheck.Checked;

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
            bottom.Controls.Add(testButton);
            bottom.Controls.Add(resultLabel);
            bottom.Controls.Add(layoutLabel);
            bottom.Controls.Add(new Label
            {
                AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new System.Drawing.Size(820, 0), Margin = new Padding(3, 12, 3, 3),
                Text = "This tool runs as administrator, so drive letters mapped by the signed-in user are not visible here. Use the share path and its credentials instead."
            });

            Controls.Add(bottom);
            Controls.Add(grid);

            localRadio.CheckedChanged += (s, e) => UpdateEnabled();
            networkRadio.CheckedChanged += (s, e) => UpdateEnabled();
            foreach (var t in new[] { localPathBox, uncPathBox, userBox, passwordBox }) t.TextChanged += (s, e) => { resultLabel.Text = ""; noticeLabel.Text = ""; UpdateLayoutText(); RaiseButtonsChanged(); };
            userBox.TextChanged += (s, e) =>
            {
                bool can = networkRadio.Checked && userBox.Text.Trim().Length > 0;
                saveCredsCheck.Enabled = can;
                if (!can) saveCredsCheck.Checked = false;
            };
            browseButton.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Destination folder", ShowNewFolderButton = true })
                    if (d.ShowDialog(this) == DialogResult.OK) localPathBox.Text = d.SelectedPath;
            };
            testButton.Click += async (s, e) => await Test();
        }

        public override string Title => "Choose the destination";
        public override string Subtitle => $"Files are copied to <destination>\\{State.Job.ComputerName}\\<drive>\\<original path>.";
        public override bool CanGoNext => !testing && Target().Length > 0;
        public override bool CanGoBack => !testing;

        public override void OnEnter()
        {
            var job = State.Job;
            if (!string.IsNullOrEmpty(job.DestinationRoot))
            {
                bool unc = NetworkConnection.IsUnc(job.DestinationRoot);
                (unc ? uncPathBox : localPathBox).Text = job.DestinationRoot;
                networkRadio.Checked = unc; localRadio.Checked = !unc;
                userBox.Text = job.DestinationUserName ?? "";
                passwordBox.Text = State.Password ?? "";
                saveCredsCheck.Checked = State.SaveCredentials;
            }
            else if (localPathBox.Text.Length == 0)
            {
                // suggest the first removable drive
                var usb = DriveInfo.GetDrives().FirstOrDefault(d => d.DriveType == DriveType.Removable && d.IsReady);
                if (usb != null) localPathBox.Text = Path.Combine(usb.RootDirectory.FullName, "DataExport");
            }
            UpdateEnabled();
        }

        public override bool OnLeaving(bool forward)
        {
            if (!forward) { Store(); return true; }
            string error = Validate(Target());
            if (error != null) { Show(CheckStatus.Fail, error); return false; }
            Store();
            return true;
        }

        string Target() => (networkRadio.Checked ? uncPathBox.Text : localPathBox.Text).Trim().Trim('"');

        static string Validate(string path)
        {
            if (path.Length == 0) return "Enter a destination.";
            if (NetworkConnection.IsUnc(path))
                return NetworkConnection.ShareRoot(path) == null ? @"A network destination must be \\server\share or a folder below it." : null;
            if (path.Length < 3 || path[1] != ':' || path[2] != '\\' || !char.IsLetter(path[0]))
                return @"Enter a full folder path such as E:\DataExport, or choose Network share.";
            return null;
        }

        void Store()
        {
            string target = Target();
            string user = networkRadio.Checked ? userBox.Text.Trim() : "";
            string password = networkRadio.Checked ? passwordBox.Text : null;
            var job = State.Job;
            if (job.DestinationRoot != target || (job.DestinationUserName ?? "") != user || State.Password != password) State.Invalidate();
            job.DestinationRoot = target.Length == 0 ? null : (target.Length == 3 ? target : target.TrimEnd('\\'));
            job.DestinationUserName = user.Length == 0 ? null : user;
            State.Password = string.IsNullOrEmpty(password) ? null : password;
        }

        async Task Test()
        {
            string error = Validate(Target());
            if (error != null) { Show(CheckStatus.Fail, error); return; }
            Store();
            testing = true; testButton.Enabled = false; RaiseButtonsChanged();
            Show(CheckStatus.NotRun, "Testing...");
            try
            {
                var pf = new PreflightEngine(State.Job, State.Store.LogDirectory) { Profiles = State.Profiles, DestinationPassword = State.Password };
                var r = await Task.Run(() => pf.Run(PreflightEngine.Destination, CancellationToken.None));
                Show(r.Status, (r.Status == CheckStatus.Pass ? "OK: " : "") + r.Summary + (r.Details.Count > 0 ? "\r\n" + string.Join("\r\n", r.Details) : ""));
            }
            finally { testing = false; testButton.Enabled = true; RaiseButtonsChanged(); }
        }

        void Show(CheckStatus status, string text)
        {
            resultLabel.ForeColor = Ui.ColorOf(status);
            resultLabel.Text = text;
        }

        /// <summary>Shown when a resumed job needs its password typed again.</summary>
        public void ShowNotice(string text) => noticeLabel.Text = text;

        void UpdateEnabled()
        {
            localPathBox.Enabled = browseButton.Enabled = localRadio.Checked;
            uncPathBox.Enabled = userBox.Enabled = passwordBox.Enabled = networkRadio.Checked;
            // only a network destination with its own user name has credentials to save
            saveCredsCheck.Enabled = networkRadio.Checked && userBox.Text.Trim().Length > 0;
            if (!saveCredsCheck.Enabled) saveCredsCheck.Checked = false;
            resultLabel.Text = "";
            UpdateLayoutText();
            RaiseButtonsChanged();
        }

        void UpdateLayoutText()
        {
            string target = Target();
            var first = State.Job.Sources.FirstOrDefault();
            if (target.Length == 0 || first == null || Validate(target) != null) { layoutLabel.Text = ""; return; }
            var preview = new JobDefinition { ComputerName = State.Job.ComputerName, DestinationRoot = target };
            layoutLabel.Text = $"Example: {first.Path}  ->  {preview.DestinationFor(first.Path)}";
        }
    }
}
