using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DataTransfer.Core;

namespace DataTransferTool
{
    /// <summary>The editable lists and copy settings: exclusions, known applications, Robocopy options.</summary>
    internal sealed class OptionsForm : Form
    {
        readonly JobDefinition job;
        readonly TextBox filesBox = List("excludeFilesBox"), profileDirsBox = List("excludeProfileDirsBox"), dirNamesBox = List("excludeDirNamesBox"), appsBox = List("knownAppsBox");
        readonly NumericUpDown timeout = Number("closeTimeoutBox", 1, 600), threads = Number("threadsBox", 1, 32), retries = Number("retriesBox", 0, 10), wait = Number("waitBox", 0, 60);
        readonly CheckBox backup = new CheckBox { Name = "backupCheck", AutoSize = true, Text = "Backup mode (/B): read files regardless of their permissions. Needs administrator." };
        readonly CheckBox cloud = new CheckBox { Name = "cloudCheck", AutoSize = true, Text = "Skip cloud-only OneDrive files (/XA:O) instead of downloading them." };

        static TextBox List(string name) => new TextBox { Name = name, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Dock = DockStyle.Fill, Height = 110 };
        static NumericUpDown Number(string name, int min, int max) => new NumericUpDown { Name = name, Minimum = min, Maximum = max, Width = 80 };

        protected override bool ShowWithoutActivation => Ui.SmokeQuiet;

        public OptionsForm(JobDefinition job)
        {
            this.job = job;
            Name = "OptionsForm";
            Text = "Options";
            Font = Ui.BaseFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            Size = new Size(820, 660);
            MinimumSize = new Size(640, 520);

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoScroll = true };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(Ui.Label("Skip these files (one pattern per line, * and ? allowed):", bold: true), 0, 0);
            grid.Controls.Add(Ui.Label("Skip these folders directly inside each user profile:", bold: true), 1, 0);
            grid.Controls.Add(filesBox, 0, 1);
            grid.Controls.Add(profileDirsBox, 1, 1);
            grid.Controls.Add(Ui.Label("Skip folders with these names anywhere:", bold: true), 0, 2);
            grid.Controls.Add(Ui.Label("Applications to close before copying (process names):", bold: true), 1, 2);
            grid.Controls.Add(dirNamesBox, 0, 3);
            grid.Controls.Add(appsBox, 1, 3);

            var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            FlowLayoutPanel Pair(string caption, Control c) { var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false }; f.Controls.Add(Ui.Label(caption)); f.Controls.Add(c); return f; }
            settings.Controls.Add(Pair("Seconds an application gets to close before it is force-closed:", timeout));
            var numbers = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            numbers.Controls.Add(Pair("Copy threads (/MT):", threads));
            numbers.Controls.Add(Pair("Retries per file (/R):", retries));
            numbers.Controls.Add(Pair("Seconds between retries (/W):", wait));
            settings.Controls.Add(numbers);
            settings.Controls.Add(backup);
            settings.Controls.Add(cloud);
            grid.Controls.Add(settings, 0, 4);
            grid.SetColumnSpan(settings, 2);

            var ok = Ui.Button("okButton", "OK");
            var cancel = Ui.Button("cancelOptionsButton", "Cancel");
            var reset = Ui.Button("resetButton", "Reset to defaults");
            ok.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            buttons.Controls.AddRange(new Control[] { cancel, ok, reset });
            Controls.Add(grid);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;

            Fill(job.Options, job.Preflight);
            reset.Click += (s, e) => Fill(new CopyOptions(), new PreflightOptions());
            ok.Click += (s, e) => Save();
        }

        void Fill(CopyOptions o, PreflightOptions p)
        {
            filesBox.Text = string.Join("\r\n", o.ExcludeFiles);
            profileDirsBox.Text = string.Join("\r\n", o.ExcludeProfileDirs);
            dirNamesBox.Text = string.Join("\r\n", o.ExcludeDirNames);
            appsBox.Text = string.Join("\r\n", p.KnownApps);
            timeout.Value = Math.Max(timeout.Minimum, Math.Min(timeout.Maximum, p.AppCloseTimeoutSeconds));
            threads.Value = Math.Max(threads.Minimum, Math.Min(threads.Maximum, o.Threads));
            retries.Value = Math.Max(retries.Minimum, Math.Min(retries.Maximum, o.Retries));
            wait.Value = Math.Max(wait.Minimum, Math.Min(wait.Maximum, o.WaitSeconds));
            backup.Checked = o.BackupMode;
            cloud.Checked = o.ExcludeCloudOnly;
        }

        static System.Collections.Generic.List<string> Lines(TextBox t) =>
            t.Lines.Select(l => l.Trim()).Where(l => l.Length > 0 && l.IndexOf('"') < 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        void Save()
        {
            var o = job.Options;
            o.ExcludeFiles = Lines(filesBox);
            o.ExcludeProfileDirs = Lines(profileDirsBox);
            o.ExcludeDirNames = Lines(dirNamesBox);
            o.Threads = (int)threads.Value;
            o.Retries = (int)retries.Value;
            o.WaitSeconds = (int)wait.Value;
            o.BackupMode = backup.Checked;
            o.ExcludeCloudOnly = cloud.Checked;
            job.Preflight.KnownApps = Lines(appsBox);
            job.Preflight.AppCloseTimeoutSeconds = (int)timeout.Value;
        }
    }
}
