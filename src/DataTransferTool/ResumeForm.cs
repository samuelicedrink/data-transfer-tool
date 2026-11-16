using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DataTransfer.Core;

namespace DataTransferTool
{
    /// <summary>Shown at start-up when the saved job did not finish: resume it, or start a new job.</summary>
    internal sealed class ResumeForm : Form
    {
        protected override bool ShowWithoutActivation => Ui.SmokeQuiet;

        public ResumeForm(JobDefinition job, bool credentialsSaved)
        {
            Name = "ResumeForm";
            Text = "Unfinished job";
            Font = Ui.BaseFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(16);

            var t = job.Totals();
            string why = job.State == JobState.DestinationFull ? "it stopped because the destination was full"
                       : job.State == JobState.Running ? "the tool was closed or the PC restarted during the copy"
                       : "it was cancelled";
            string started = job.StartedUtc.HasValue ? job.StartedUtc.Value.ToLocalTime().ToString("g") : "-";
            var lines = new[]
            {
                $"A job on this stick did not finish: {why}.",
                "",
                $"Computer:      {job.ComputerName}" + (string.Equals(job.ComputerName, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? "" : "   (NOT this PC)"),
                $"Started:       {started}",
                $"Destination:   {job.DestinationRoot}" + (job.DestinationUserName != null ? $"   as {job.DestinationUserName}" : ""),
                $"Folders:       {string.Join(", ", job.Sources.Select(s => s.Path).Take(4))}{(job.Sources.Count > 4 ? $" and {job.Sources.Count - 4} more" : "")}",
                $"Done so far:   {job.Sources.Count(s => s.Status == SourceStatus.Completed || s.Status == SourceStatus.CompletedWithErrors)} of {job.Sources.Count} folder jobs, about {t.FilesCopied:N0} files ({Ui.Size(t.BytesCopied)}) copied",
                job.DestinationUserName == null ? null : credentialsSaved ? "Credentials:   saved for resume" : "Credentials:   not saved - you will be asked for the password",
                "",
                "Resume skips files that are already at the destination and continues with the rest."
            };
            var info = new Label { Name = "resumeInfo", AutoSize = true, Text = string.Join("\r\n", lines.Where(l => l != null)), Font = new Font(FontFamily.GenericMonospace, Ui.BaseFont.Size) };

            var resume = Ui.Button("resumeButton", "Resume this job");
            var fresh = Ui.Button("newJobButton", "Start a new job");
            resume.DialogResult = DialogResult.Yes;
            fresh.DialogResult = DialogResult.No;
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
            buttons.Controls.Add(fresh);
            buttons.Controls.Add(resume);
            new ToolTip().SetToolTip(fresh, "The unfinished job is moved to the log folder and its saved credentials are deleted.");

            var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            layout.Controls.Add(info);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            AcceptButton = resume;
            CancelButton = fresh;
        }
    }
}
