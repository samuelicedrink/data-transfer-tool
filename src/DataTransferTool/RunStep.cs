using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DataTransfer.Core;

namespace DataTransferTool
{
    /// <summary>Screen 4: progress and Cancel. Never shows a dialog while the copy runs.</summary>
    internal sealed class RunStep : StepControl
    {
        readonly Label statusLabel = new Label { Name = "statusLabel", AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(3, 4, 3, 8) };
        readonly ProgressBar bar = new ProgressBar { Name = "progressBar", Dock = DockStyle.Top, Height = 22, Maximum = 1000, Style = ProgressBarStyle.Continuous };
        readonly Label jobLabel = Value("jobLabel"), filesLabel = Value("filesLabel"), bytesLabel = Value("bytesLabel"), folderLabel = Value("folderLabel"),
                       speedLabel = Value("speedLabel"), etaLabel = Value("etaLabel"), elapsedLabel = Value("elapsedLabel");
        readonly ListBox messages = new ListBox { Name = "messagesList", Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
        readonly Button cancelButton = Ui.Button("cancelButton", "Cancel");
        readonly Button logsButton = Ui.Button("logsButton", "Open log folder");
        readonly System.Windows.Forms.Timer cancelArm = new System.Windows.Forms.Timer { Interval = 5000 };
        CancellationTokenSource cts;
        bool started, finished, closeWhenDone;

        public Action CloseRequested;

        static Label Value(string name) => new Label { Name = name, AutoSize = true, AutoEllipsis = false, Margin = new Padding(3, 4, 3, 4), MaximumSize = new Size(760, 0) };

        public RunStep(AppState state) : base(state)
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 8, 0, 8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            void Row(string caption, Label value) { grid.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(3, 4, 12, 4) }); grid.Controls.Add(value); }
            Row("Folder job", jobLabel);
            Row("Files", filesLabel);
            Row("Data", bytesLabel);
            Row("Current folder", folderLabel);
            Row("Speed", speedLabel);
            Row("Time left", etaLabel);
            Row("Elapsed", elapsedLabel);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(logsButton);

            var top = new Panel { Dock = DockStyle.Top, AutoSize = true };
            top.Controls.Add(grid);
            top.Controls.Add(bar);
            top.Controls.Add(statusLabel);
            statusLabel.Dock = DockStyle.Top;

            Controls.Add(messages);
            Controls.Add(new Label { Text = "Activity", Dock = DockStyle.Top, AutoSize = true, ForeColor = Ui.Muted, Padding = new Padding(0, 4, 0, 2) });
            Controls.Add(top);
            Controls.Add(buttons);

            // two clicks instead of a confirmation dialog
            cancelButton.Click += (s, e) =>
            {
                if (cts == null || cts.IsCancellationRequested) return;
                if (!cancelArm.Enabled) { cancelButton.Text = "Click again to cancel"; cancelArm.Start(); return; }
                cancelArm.Stop();
                Cancel();
            };
            cancelArm.Tick += (s, e) => { cancelArm.Stop(); if (!cts.IsCancellationRequested) cancelButton.Text = "Cancel"; };
            logsButton.Click += (s, e) => { try { Process.Start("explorer.exe", "\"" + State.Store.LogDirectory + "\""); } catch (Exception) { } };
        }

        public override string Title => finished ? "Copy finished" : "Copying";
        public override string Subtitle => finished ? "" : "The copy runs without asking anything. Locked files are retried at the end; a lost destination is waited for.";
        public override bool CanGoBack => false;
        public override bool CanGoNext => finished;
        public override string NextText => "Close";

        public override void OnEnter()
        {
            if (started) return;
            started = true;
            Start();
        }

        /// <summary>Window closed during the run: cancel without asking, then close.</summary>
        public void CancelAndClose()
        {
            closeWhenDone = true;
            Cancel();
        }

        void Cancel()
        {
            if (cts == null || cts.IsCancellationRequested) return;
            cts.Cancel();
            cancelButton.Text = "Cancelling...";
            cancelButton.Enabled = false;
            Add("Cancel requested; stopping Robocopy.");
        }

        void Start()
        {
            var job = State.Job;
            var scan = State.Preflight?.Scan;
            cts = new CancellationTokenSource();
            State.Running = true;
            var engine = new CopyEngine(job, State.Store.LogDirectory)
            {
                Profiles = State.Profiles,
                SaveState = j => { try { State.Store.Save(j); } catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { } },
                ExpectedFiles = scan?.Totals.FilesCopied,
                ExpectedBytes = scan?.Totals.BytesCopied
            };
            engine.Progress += p => { if (IsHandleCreated) BeginInvoke((Action)(() => Show(p))); };
            engine.Message += m => { if (IsHandleCreated) BeginInvoke((Action)(() => Add(m))); };
            Add($"Job {job.Id}: {job.Sources.Count} folder job(s) to {job.DestinationRoot}. Logs: {State.Store.LogDirectory}");
            statusLabel.Text = "Starting...";

            Task.Run(() =>
            {
                try { return (outcome: engine.Run(cts.Token), error: (Exception)null); }
                catch (Exception ex) { return (outcome: JobOutcome.Failed, error: ex); }
            }).ContinueWith(t => BeginInvoke((Action)(() => Finished(t.Result.outcome, t.Result.error))));
        }

        void Show(ProgressSnapshot p)
        {
            if (finished) return;
            switch (p.Phase)
            {
                case EnginePhase.Copying: statusLabel.Text = "Copying"; statusLabel.ForeColor = SystemColors.ControlText; break;
                case EnginePhase.PausedDestinationUnavailable:
                    statusLabel.Text = $"Destination unreachable - waiting for it to return (checking every {State.Job.Options.DestinationPollSeconds} s)";
                    statusLabel.ForeColor = Ui.Warning; break;
                case EnginePhase.WaitingToRetryLocked: statusLabel.Text = "Main pass done - waiting before retrying locked files"; statusLabel.ForeColor = SystemColors.ControlText; break;
                case EnginePhase.RetryingFailed: statusLabel.Text = "Retrying files that were locked"; statusLabel.ForeColor = SystemColors.ControlText; break;
                case EnginePhase.Finished: return;
            }
            jobLabel.Text = p.CurrentSource == null ? "" : $"{p.SourceIndex} of {p.SourceCount}: {p.CurrentSource}";
            filesLabel.Text = $"{p.FilesProcessed:N0}" + (p.TotalFiles.HasValue ? $" of {p.TotalFiles.Value:N0}" : "") + (p.FilesFailed > 0 ? $"   ({p.FilesFailed:N0} failed so far)" : "");
            double fraction = p.TotalBytes.HasValue && p.TotalBytes.Value > 0 ? Math.Min(1, (double)p.BytesCopied / p.TotalBytes.Value) : 0;
            bytesLabel.Text = Ui.Size(p.BytesCopied) + (p.TotalBytes.HasValue ? $" of {Ui.Size(p.TotalBytes.Value)}  ({fraction:P0})" : "");
            bar.Value = (int)(fraction * 1000);
            if (p.CurrentFolder != null) folderLabel.Text = p.CurrentFolder.Length <= 110 ? p.CurrentFolder : "..." + p.CurrentFolder.Substring(p.CurrentFolder.Length - 107);
            speedLabel.Text = p.BytesPerSecond > 0 ? $"{p.BytesPerSecond / 1048576.0:0.0} MB/s" : "-";
            etaLabel.Text = p.Phase != EnginePhase.Copying ? "-" : p.Eta.HasValue ? Ui.Duration(p.Eta.Value) : "calculating...";
            elapsedLabel.Text = Ui.Duration(p.Elapsed);
        }

        void Add(string text)
        {
            messages.Items.Add($"{DateTime.Now:HH:mm:ss}  {text}");
            messages.TopIndex = Math.Max(0, messages.Items.Count - 1);
        }

        void Finished(JobOutcome outcome, Exception error)
        {
            finished = true;
            State.Running = false;
            cancelArm.Stop();
            cancelButton.Visible = false;
            foreach (var pf in State.AllPreflights)
                foreach (var n in pf.RestartOneDrive()) Add(n);
            if (State.AllPreflights.Any(p => p.OneDriveWasStopped)) Add("OneDrive started again.");

            var t = State.Job.Totals();
            string headline;
            switch (outcome)
            {
                case JobOutcome.Completed: headline = "Finished: everything was copied."; statusLabel.ForeColor = Ui.Good; break;
                case JobOutcome.CompletedWithErrors: headline = $"Finished with {t.FilesFailed:N0} file(s) not copied."; statusLabel.ForeColor = Ui.Warning; break;
                case JobOutcome.Cancelled: headline = "Cancelled. Run the tool again to continue where it stopped."; statusLabel.ForeColor = Ui.Warning; break;
                case JobOutcome.DestinationFull: headline = "Stopped: the destination is full."; statusLabel.ForeColor = Ui.Bad; break;
                default: headline = "Stopped: " + (error?.Message ?? State.Job.LastMessage ?? "the copy could not run."); statusLabel.ForeColor = Ui.Bad; break;
            }
            statusLabel.Text = headline;
            bar.Value = outcome == JobOutcome.Completed || outcome == JobOutcome.CompletedWithErrors ? 1000 : bar.Value;
            etaLabel.Text = "-";
            speedLabel.Text = "-";
            filesLabel.Text = $"{t.FilesCopied:N0} copied, {t.FilesSkipped:N0} skipped (already there or excluded), {t.FilesFailed:N0} failed" +
                              (State.Job.Sources.Any(s => s.CountsApproximate) ? "  (approximate after an interruption)" : "");
            bytesLabel.Text = $"{Ui.Size(t.BytesCopied)} copied";
            Add(headline);
            foreach (var f in State.Job.AllFailures().Take(200)) Add($"Not copied: {f.SourcePath} - {f.Message} ({f.Category})");
            if (error != null) Add("Error: " + error);
            RaiseButtonsChanged();
            if (closeWhenDone) CloseRequested?.Invoke();
        }
    }
}
