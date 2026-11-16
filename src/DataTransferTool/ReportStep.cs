using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DataTransfer.Core;

namespace DataTransferTool
{
    /// <summary>Screen 5: totals, the files that were not copied with reasons, the CSV, and Retry failed.</summary>
    internal sealed class ReportStep : StepControl
    {
        readonly Label outcomeLabel = new Label { Name = "outcomeLabel", AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(3, 2, 3, 8) };
        readonly ListView totals = new ListView { Name = "totalsList", Dock = DockStyle.Top, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Height = 130 };
        readonly ListView failures = new ListView { Name = "failuresList", Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        readonly Label scoredForLabel = new Label { Name = "scoredForLabel", Dock = DockStyle.Top, AutoSize = true, ForeColor = Ui.Muted, Padding = new Padding(0, 4, 0, 0) };
        readonly Label failuresLabel = new Label { Name = "failuresLabel", Dock = DockStyle.Top, AutoSize = true, Font = Ui.BoldFont, Padding = new Padding(0, 10, 0, 4) };
        readonly Label csvLabel = new Label { Name = "csvLabel", AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(8, 8, 3, 3) };
        readonly Label activityLabel = new Label { Name = "reportActivityLabel", AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(8, 8, 3, 3) };
        readonly ProgressBar retryBar = new ProgressBar { Name = "retryBar", Width = 160, Style = ProgressBarStyle.Marquee, Visible = false, Margin = new Padding(8, 8, 3, 3) };
        readonly Button retryButton = Ui.Button("retryButton", "Retry failed");
        readonly Button continueButton = Ui.Button("continueButton", "Continue copy");
        readonly Button openCsvButton = Ui.Button("openCsvButton", "Open CSV");
        readonly Button saveCsvButton = Ui.Button("saveCsvButton", "Save CSV as...");
        readonly Button logsButton = Ui.Button("reportLogsButton", "Open log folder");
        bool busy;

        /// <summary>Set by the main form: run the copy again from where it stopped.</summary>
        public Action ContinueRequested;

        public ReportStep(AppState state) : base(state)
        {
            totals.Columns.Add("Folder job", 330);
            totals.Columns.Add("Copied", 150);
            totals.Columns.Add("Skipped", 150);
            totals.Columns.Add("Failed", 120);
            totals.Columns.Add("Status", 160);
            totals.Columns.Add("Relevant", 110);
            failures.Columns.Add("File or folder not copied", 470);
            failures.Columns.Add("Reason", 230);
            failures.Columns.Add("Details", 360);

            var top = new Panel { Dock = DockStyle.Top, AutoSize = true };
            outcomeLabel.Dock = DockStyle.Top;
            top.Controls.Add(scoredForLabel);
            top.Controls.Add(totals);
            top.Controls.Add(outcomeLabel);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(0, 6, 0, 0) };
            bar.Controls.AddRange(new Control[] { retryButton, continueButton, openCsvButton, saveCsvButton, logsButton, retryBar, activityLabel, csvLabel });

            Controls.Add(failures);
            Controls.Add(failuresLabel);
            Controls.Add(top);
            Controls.Add(bar);

            retryButton.Click += async (s, e) => await Retry();
            continueButton.Click += (s, e) => ContinueRequested?.Invoke();
            openCsvButton.Click += (s, e) => { if (State.FailuresCsv != null) try { Process.Start(new ProcessStartInfo(State.FailuresCsv) { UseShellExecute = true }); } catch (Exception) { } };
            saveCsvButton.Click += (s, e) =>
            {
                using (var d = new SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = Path.GetFileName(State.FailuresCsv ?? "failures.csv"), OverwritePrompt = true })
                    if (d.ShowDialog(this) == DialogResult.OK)
                        try { FailureCsv.Write(State.Job, d.FileName); activityLabel.Text = "Saved " + d.FileName; }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { activityLabel.Text = "Could not save: " + ex.Message; }
            };
            logsButton.Click += (s, e) => { try { Process.Start("explorer.exe", "\"" + State.Store.LogDirectory + "\""); } catch (Exception) { } };
        }

        public override string Title => "Report";
        public override string Subtitle => $"Destination: {State.Job.DestinationRoot}\\{State.Job.ComputerName}";
        public override bool CanGoBack => false;
        public override bool CanGoNext => !busy;
        public override string NextText => "Close";

        public override void OnEnter() => Fill();

        void Fill()
        {
            var job = State.Job;
            var t = job.Totals();
            bool approx = job.Sources.Any(s => s.CountsApproximate);
            switch (job.State)
            {
                case JobState.Completed when t.FilesFailed == 0 && !job.AllFailures().Any():
                    outcomeLabel.Text = "Everything was copied."; outcomeLabel.ForeColor = Ui.Good; break;
                case JobState.Completed:
                    outcomeLabel.Text = $"Copy finished, but {job.AllFailures().Count()} item(s) were not copied. See below; Retry failed tries them again."; outcomeLabel.ForeColor = Ui.Warning; break;
                case JobState.Cancelled:
                    outcomeLabel.Text = "The copy was cancelled. Continue copy carries on where it stopped (already copied files are skipped)."; outcomeLabel.ForeColor = Ui.Warning; break;
                case JobState.DestinationFull:
                    outcomeLabel.Text = "The destination is full. Free some space or choose another destination, then Continue copy."; outcomeLabel.ForeColor = Ui.Bad; break;
                default:
                    outcomeLabel.Text = "The copy stopped: " + (job.LastMessage ?? "unknown error"); outcomeLabel.ForeColor = Ui.Bad; break;
            }

            totals.BeginUpdate();
            totals.Items.Clear();
            foreach (var s in job.Sources)
                totals.Items.Add(Row(s.Describe(), s.Totals, s.Failures.Count, StatusText(s.Status),
                    s.Relevance.HasValue ? s.Relevance + " %" + (s.RelevanceWeak ? " (weak)" : "") : ""));
            scoredForLabel.Text = ScoredFor(job);
            var all = Row("Total" + (approx ? "  (approximate after an interruption)" : ""), t, job.AllFailures().Count(), "", "");
            all.Font = Ui.BoldFont;
            totals.Items.Add(all);
            totals.EndUpdate();
            totals.Height = Math.Min(260, (job.Sources.Count + 2) * (totals.Font.Height + 8) + 12);

            var list = job.AllFailures().OrderBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase).ToList();
            failuresLabel.Text = list.Count == 0 ? "No files failed." : $"Not copied ({list.Count}{(list.Count > 1000 ? ", first 1,000 shown; all are in the CSV" : "")}):";
            failures.BeginUpdate();
            failures.Items.Clear();
            foreach (var f in list.Take(1000))
                failures.Items.Add(new ListViewItem(new[] { f.SourcePath + (f.IsDirectory ? "\\" : ""), FailureCsv.Reason(f.Category), $"{f.Message} (error {f.ErrorCode})" + (f.LogFile != null ? $"; log {f.LogFile}, line {f.LogLine}" : "") }));
            failures.EndUpdate();

            RunStep.WriteFailuresCsv(State);
            csvLabel.Text = State.FailuresCsv == null ? "" : "CSV: " + State.FailuresCsv;
            openCsvButton.Enabled = saveCsvButton.Enabled = State.FailuresCsv != null && !busy;
            retryButton.Enabled = list.Count > 0 && !busy && !job.IsUnfinished;
            continueButton.Visible = job.IsUnfinished;
            continueButton.Enabled = !busy;
            RaiseButtonsChanged();
        }

        /// <summary>Who the Relevant column was estimated for. The job keeps only the SID; the account name is looked up here and a SID is never shown.</summary>
        static string lookedUpSid, lookedUpAccount;

        static string ScoredFor(JobDefinition job)
        {
            if (string.IsNullOrEmpty(job.SelectedUserSid)) return "No user was picked on the Select screen, so relevance was not estimated.";
            // looked up once: a domain account can take seconds when the domain is not reachable
            if (lookedUpSid != job.SelectedUserSid)
            {
                lookedUpSid = job.SelectedUserSid; lookedUpAccount = null;
                try { lookedUpAccount = new SystemAccountSource().Translate(job.SelectedUserSid); } catch (Exception) { }
            }
            string account = lookedUpAccount;
            string who = account ?? "a user whose account can no longer be looked up";
            return job.Sources.Any(s => s.Relevance.HasValue)
                ? $"Relevant: how likely each job's files are {who}'s, as estimated on the Select screen. An estimate, not a check."
                : $"The user picked on the Select screen was {who}; the relevance numbers were not ready when the copy was set up.";
        }

        static ListViewItem Row(string name, Counts c, int failed, string status, string relevance) => new ListViewItem(new[]
        {
            name, $"{c.FilesCopied:N0} ({Ui.Size(c.BytesCopied)})", $"{c.FilesSkipped:N0} ({Ui.Size(c.BytesSkipped)})", $"{failed:N0}", status, relevance
        });

        static string StatusText(SourceStatus s) =>
            s == SourceStatus.Completed ? "Done" : s == SourceStatus.CompletedWithErrors ? "Done, with failures" :
            s == SourceStatus.Pending ? "Not finished" : s == SourceStatus.Running ? "Interrupted" : "Failed";

        async Task Retry()
        {
            busy = true;
            retryButton.Enabled = continueButton.Enabled = false;
            retryBar.Visible = true;
            int before = State.Job.AllFailures().Count();
            activityLabel.Text = $"Retrying {before} item(s)...";
            RaiseButtonsChanged();
            var engine = new CopyEngine(State.Job, State.Store.LogDirectory)
            {
                Profiles = State.Profiles,
                SaveState = j => { try { State.Store.Save(j); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { } }
            };
            engine.Message += m => { if (IsHandleCreated) BeginInvoke((Action)(() => activityLabel.Text = m)); };
            State.Running = true;
            JobOutcome outcome;
            try { outcome = await Task.Run(() => engine.RetryFailed(CancellationToken.None)); }
            catch (Exception ex) { outcome = JobOutcome.Failed; State.Job.LastMessage = ex.Message; }
            finally { State.Running = false; busy = false; retryBar.Visible = false; }
            int after = State.Job.AllFailures().Count();
            Fill();
            activityLabel.Text = $"Retry finished: {before - after} of {before} now copied, {after} still failing.";
        }
    }
}
