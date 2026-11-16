using System;
using System.Drawing;
using System.Windows.Forms;
using DataTransfer.Core;

namespace DataTransferTool
{
    /// <summary>The wizard: Select, Destination, Pre-flight, Run.</summary>
    internal sealed class MainForm : Form
    {
        readonly AppState state;
        readonly StepControl[] steps;
        readonly Panel content = new Panel { Dock = DockStyle.Fill };
        readonly Label titleLabel = new Label { Name = "titleLabel", AutoSize = true, Font = Ui.TitleFont, Margin = new Padding(0) };
        readonly Label subtitleLabel = new Label { Name = "subtitleLabel", AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(0, 4, 0, 0) };
        readonly Label stepLabel = new Label { Name = "stepLabel", AutoSize = true, ForeColor = Ui.Muted, Anchor = AnchorStyles.Left, Margin = new Padding(12, 10, 3, 3) };
        readonly Button backButton = Ui.Button("backButton", "< Back");
        readonly Button nextButton = Ui.Button("nextButton", "Next >");
        readonly RunStep runStep;
        readonly DestinationStep destinationStep;
        int current = -1;
        const int SelectIndex = 0, DestinationIndex = 1, PreflightIndex = 2, RunIndex = 3;

        public MainForm(JobStore store)
        {
            state = new AppState { Store = store };
            var v = typeof(MainForm).Assembly.GetName().Version;
            Text = $"Data Transfer Tool v{v.Major}.{v.Minor}.{v.Build} - {Environment.MachineName}";
            Name = "MainForm";
            Font = Ui.BaseFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            // v2: wider for the Modified, Size, Relevant and Advice columns, within the screen
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(1280, wa.Width - 40), Math.Min(740, wa.Height - 40));
            MinimumSize = new Size(800, 560);

            runStep = new RunStep(state) { CloseRequested = () => BeginInvoke((Action)Close) };
            destinationStep = new DestinationStep(state);
            var report = new ReportStep(state);
            report.ContinueRequested = () => { runStep.Reset(); Go(RunIndex, forward: false); };
            steps = new StepControl[] { new SelectStep(state), destinationStep, new PreflightStep(state), runStep, report };
            foreach (var s in steps) s.ButtonsChanged += UpdateButtons;

            var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(14, 12, 14, 8), BackColor = SystemColors.Window, WrapContents = false };
            header.Controls.Add(titleLabel);
            header.Controls.Add(subtitleLabel);
            var rule = new Label { Dock = DockStyle.Top, Height = 1, BackColor = SystemColors.ControlDark };

            var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(8, 6, 8, 8) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Controls.Add(stepLabel, 0, 0);
            footer.Controls.Add(backButton, 1, 0);
            footer.Controls.Add(nextButton, 2, 0);
            var rule2 = new Label { Dock = DockStyle.Bottom, Height = 1, BackColor = SystemColors.ControlDark };

            Controls.Add(content);
            Controls.Add(rule);
            Controls.Add(header);
            Controls.Add(rule2);
            Controls.Add(footer);
            AcceptButton = null;

            backButton.Click += (s, e) => Go(current - 1, forward: false);
            nextButton.Click += (s, e) => Next();
            Shown += (s, e) => Start();
            FormClosing += OnClosing;
        }

        /// <summary>Offers to resume an unfinished job; otherwise starts on the Select screen.</summary>
        void Start()
        {
            JobDefinition saved = null;
            try { saved = state.Store.Load(); } catch (Exception) { }
            if (saved == null || !saved.IsUnfinished)
            {
                // a finished job from an earlier session is kept as a record in the log folder
                if (saved != null) try { state.Store.Archive(saved.Id); } catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { }
                Go(SelectIndex, forward: true);
                return;
            }

            var creds = state.Store.Credentials;
            string user = null, password = null;
            bool credsOk = saved.DestinationUserName != null && creds.TryLoad(saved.Id, out user, out password) && user == saved.DestinationUserName;
            DialogResult answer;
            using (var f = new ResumeForm(saved, credsOk)) answer = f.ShowDialog(this);
            if (answer != DialogResult.Yes)
            {
                try { state.Store.Archive(saved.Id); } catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { }
                Go(SelectIndex, forward: true);
                return;
            }

            // resume: same job, same selections and destination; checks run again because the PC may have changed
            state.Job = saved;
            if (credsOk) { state.Password = password; state.SaveCredentials = true; }
            Go(SelectIndex, forward: true);
            Go(DestinationIndex, forward: true);
            if (saved.DestinationUserName != null && state.Password == null)
            {
                destinationStep.ShowNotice($"Enter the password for {saved.DestinationUserName} to resume.");
                return;
            }
            Go(PreflightIndex, forward: true);
        }

        void Next()
        {
            if (current == steps.Length - 1)
            {
                // a finished job is archived to the log folder so the next launch starts clean
                if (state.Job.State == JobState.Completed)
                    try { state.Store.Archive(state.Job.Id); } catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { }
                Close();
                return;
            }
            Go(current + 1, forward: true);
        }

        void Go(int index, bool forward)
        {
            if (index < 0 || index >= steps.Length) return;
            if (current >= 0 && !steps[current].OnLeaving(forward)) return;
            current = index;
            var step = steps[current];
            content.SuspendLayout();
            content.Controls.Clear();
            content.Controls.Add(step);
            content.ResumeLayout();
            titleLabel.Text = step.Title;
            subtitleLabel.Text = step.Subtitle;
            stepLabel.Text = $"Step {current + 1} of {steps.Length}";
            step.OnEnter();
            UpdateButtons();
        }

        void UpdateButtons()
        {
            if (current < 0) return;
            var step = steps[current];
            backButton.Enabled = current > 0 && step.CanGoBack;
            nextButton.Text = step.NextText;
            nextButton.Enabled = step.CanGoNext;
            titleLabel.Text = step.Title;
            subtitleLabel.Text = step.Subtitle;
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            // closing the window during a run cancels it cleanly instead of asking anything
            if (state.Running)
            {
                e.Cancel = true;
                runStep.CancelAndClose();
                return;
            }
            foreach (var pf in state.AllPreflights) pf.RestartOneDrive();
            // credentials stay on disk only for a job that can still be resumed, and only when asked for
            if (!state.Job.IsUnfinished || !state.SaveCredentials) state.Store.Credentials.Delete();
            if (NetworkConnection.IsUnc(state.Job.DestinationRoot) && !string.IsNullOrEmpty(state.Job.DestinationUserName))
                NetworkConnection.Disconnect(state.Job.DestinationRoot);
        }
    }
}
