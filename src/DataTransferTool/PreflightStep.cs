using System;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DataTransfer.Core;

namespace DataTransferTool
{
    /// <summary>Screen 3: every check with pass/warn/fail, a fix button where one exists, and acknowledgement.</summary>
    internal sealed class PreflightStep : StepControl
    {
        readonly ListView list = new ListView
        {
            Name = "checksList", Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable
        };
        readonly TextBox details = new TextBox { Name = "detailsBox", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = SystemColors.Window };
        readonly Button rerunButton = Ui.Button("rerunButton", "Run checks again");
        readonly Button fixButton = Ui.Button("fixButton", "Fix");
        readonly Button ackButton = Ui.Button("ackButton", "Acknowledge");
        readonly Label activityLabel = new Label { Name = "activityLabel", AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(8, 8, 3, 3) };
        CancellationTokenSource cts;
        bool busy;

        public PreflightStep(AppState state) : base(state)
        {
            list.Columns.Add("Status", 150);
            list.Columns.Add("Check", 270);
            list.Columns.Add("Result", 520);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(0, 6, 0, 0) };
            bar.Controls.AddRange(new Control[] { rerunButton, fixButton, ackButton, activityLabel });

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
            split.Panel1.Controls.Add(list);
            split.Panel2.Controls.Add(details);
            Controls.Add(split);
            Controls.Add(bar);
            Ui.SplitAt(split, 0.52, 120);
            list.Resize += (s, e) => list.Columns[2].Width = Math.Max(200, list.ClientSize.Width - list.Columns[0].Width - list.Columns[1].Width - 4);

            list.SelectedIndexChanged += (s, e) => ShowSelected();
            rerunButton.Click += (s, e) => RunChecks();
            ackButton.Click += (s, e) =>
            {
                var r = Selected();
                if (r != null && State.Preflight.Acknowledge(r.Id)) { UpdateItem(r); ShowSelected(); RaiseButtonsChanged(); SelectNextOpen(); }
            };
            fixButton.Click += async (s, e) =>
            {
                var r = Selected();
                if (r?.FixLabel == null) return;
                await Busy("Fixing: " + r.FixLabel, ct => State.Preflight.ApplyFix(r.Id, ct));
            };
        }

        public override string Title => "Pre-flight checks";
        public override string Subtitle => busy ? "Checking this PC, the selected folders and the destination..." :
            State.Preflight == null ? "" :
            State.Preflight.CanRun ? "All checks passed or were acknowledged. The copy can start." :
            "Fix or acknowledge every warning and failure to enable Start copy. Failures marked \"must fix\" cannot be acknowledged.";
        public override string NextText => "Start copy";
        public override bool CanGoNext => !busy && State.Preflight != null && State.Preflight.CanRun;
        public override bool CanGoBack => !busy;

        public override void OnEnter()
        {
            if (State.Preflight == null) RunChecks();
            else { Fill(); RaiseButtonsChanged(); }
        }

        public override bool OnLeaving(bool forward)
        {
            if (busy) return false;
            return true;
        }

        void RunChecks()
        {
            var pf = new PreflightEngine(State.Job, State.Store.LogDirectory) { Profiles = State.Profiles, DestinationPassword = State.Password };
            pf.CheckCompleted += r => BeginInvoke((Action)(() => { UpdateItem(r); if (list.SelectedItems.Count == 0) SelectNextOpen(); }));
            pf.Activity += a => BeginInvoke((Action)(() => activityLabel.Text = a));
            State.Preflight = pf;
            State.AllPreflights.Add(pf);
            Fill();
            _ = Busy("Running checks", ct => { pf.RunAll(ct); return null; });
        }

        async Task Busy(string what, Func<CancellationToken, CheckResult> work)
        {
            busy = true;
            cts = new CancellationTokenSource();
            foreach (var b in new[] { rerunButton, fixButton, ackButton }) b.Enabled = false;
            activityLabel.Text = what + "...";
            RaiseButtonsChanged();
            try
            {
                var r = await Task.Run(() => work(cts.Token));
                if (r != null) UpdateItem(r);
            }
            finally
            {
                busy = false;
                activityLabel.Text = State.Preflight.CanRun ? "Ready." :
                    $"{State.Preflight.Results.Count(x => !x.AllowsRun)} item(s) still need attention.";
                rerunButton.Enabled = true;
                ShowSelected();
                if (list.SelectedItems.Count == 0 || Selected()?.AllowsRun == true) SelectNextOpen();
                RaiseButtonsChanged();
            }
        }

        // ------------------------------------------------------------ list

        void Fill()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var r in State.Preflight.Results)
            {
                var item = new ListViewItem(new[] { "", r.Title, "" }) { Name = r.Id, UseItemStyleForSubItems = false };
                list.Items.Add(item);
                UpdateItem(r);
            }
            list.EndUpdate();
        }

        void UpdateItem(CheckResult r)
        {
            var item = list.Items[r.Id];
            if (item == null) return;
            string status = r.Status == CheckStatus.NotRun ? (busy ? "..." : "not run") :
                            r.Status == CheckStatus.Pass ? "PASS" :
                            r.Blocking ? "FAIL (must fix)" :
                            (r.Status == CheckStatus.Warn ? "WARN" : "FAIL") + (r.Acknowledged ? " (acknowledged)" : "");
            item.SubItems[0].Text = status;
            item.SubItems[0].ForeColor = r.Acknowledged ? Ui.Muted : Ui.ColorOf(r.Status);
            item.SubItems[0].Font = Ui.BoldFont;
            item.SubItems[2].Text = r.Summary ?? "";
            if (Selected()?.Id == r.Id) ShowSelected();
            RaiseButtonsChanged();
        }

        CheckResult Selected() =>
            list.SelectedItems.Count == 0 || State.Preflight == null ? null : State.Preflight[list.SelectedItems[0].Name];

        void SelectNextOpen()
        {
            if (State.Preflight == null) return;
            var open = State.Preflight.Results.FirstOrDefault(r => r.Status != CheckStatus.NotRun && !r.AllowsRun);
            if (open == null) return;
            var item = list.Items[open.Id];
            item.Selected = true;
            item.Focused = true;
            item.EnsureVisible();
        }

        void ShowSelected()
        {
            var r = Selected();
            if (r == null) { details.Text = ""; fixButton.Visible = false; ackButton.Enabled = false; return; }
            var sb = new StringBuilder();
            sb.AppendLine(r.Title).AppendLine(r.Summary ?? "");
            if (r.Details.Count > 0) { sb.AppendLine(); foreach (var d in r.Details) sb.AppendLine(d); }
            if (r.Blocking) sb.AppendLine().AppendLine("This must be fixed before the copy can start. Fix it, then press Run checks again.");
            else if (r.Status == CheckStatus.Warn || r.Status == CheckStatus.Fail)
                sb.AppendLine().AppendLine(r.Acknowledged ? "Acknowledged." : "Press Acknowledge to continue anyway" + (r.FixLabel != null ? ", or use the fix button." : "."));
            details.Text = sb.ToString();
            fixButton.Visible = r.FixLabel != null;
            fixButton.Text = r.FixLabel ?? "Fix";
            fixButton.Enabled = !busy && r.FixLabel != null;
            ackButton.Enabled = !busy && !r.Blocking && !r.Acknowledged && (r.Status == CheckStatus.Warn || r.Status == CheckStatus.Fail);
        }
    }
}
