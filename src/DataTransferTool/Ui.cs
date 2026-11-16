using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;
using DataTransfer.Core;

namespace DataTransferTool
{
    /// <summary>Shared state of one session of the tool.</summary>
    internal sealed class AppState
    {
        public JobStore Store;
        public JobDefinition Job = new JobDefinition();
        public FolderTree Tree;
        /// <summary>Destination password. Memory only.</summary>
        public string Password;
        public ProfileInfo Profiles = ProfileInfo.FromSystem();
        /// <summary>"Save credentials for resume" ticked: DPAPI file written when the copy starts.</summary>
        public bool SaveCredentials;
        /// <summary>The failures CSV written after the last run, if there were failures.</summary>
        public string FailuresCsv;
        public PreflightEngine Preflight;
        /// <summary>Every pre-flight engine of this session; any of them may have stopped OneDrive.</summary>
        public readonly System.Collections.Generic.List<PreflightEngine> AllPreflights = new System.Collections.Generic.List<PreflightEngine>();
        public bool Running;

        /// <summary>Selections, destination or options changed: earlier checks no longer apply.</summary>
        public void Invalidate() => Preflight = null;
    }

    /// <summary>One screen of the wizard.</summary>
    internal abstract class StepControl : UserControl
    {
        protected readonly AppState State;
        public event Action ButtonsChanged;

        protected StepControl(AppState state)
        {
            State = state;
            Dock = DockStyle.Fill;
            Font = Ui.BaseFont;
            Padding = new Padding(12, 8, 12, 4);
        }

        public abstract string Title { get; }
        public abstract string Subtitle { get; }
        public virtual string NextText => "Next >";
        public virtual bool CanGoBack => true;
        public virtual bool CanGoNext => true;
        public virtual void OnEnter() { }
        /// <summary>False keeps the user on this screen (validation).</summary>
        public virtual bool OnLeaving(bool forward) => true;
        protected void RaiseButtonsChanged() => ButtonsChanged?.Invoke();
    }

    internal static class Ui
    {
        public static readonly Font BaseFont = SystemFonts.MessageBoxFont;
        public static readonly Font BoldFont = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
        public static readonly Font TitleFont = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.Size * 1.5f, FontStyle.Regular);
        public static readonly Color Good = Color.FromArgb(0, 122, 51), Warning = Color.FromArgb(176, 96, 0), Bad = Color.FromArgb(192, 0, 0), Muted = SystemColors.GrayText;

        public static Label Label(string text, string name = null, bool bold = false) => new Label
        {
            Name = name ?? "", Text = text, AutoSize = true, Font = bold ? BoldFont : BaseFont, Margin = new Padding(3, 7, 3, 3)
        };

        public static Button Button(string name, string text) => new Button
        {
            Name = name, Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 2, 8, 2), Margin = new Padding(3), UseVisualStyleBackColor = true, MinimumSize = new Size(80, 0)
        };

        public static TextBox TextBox(string name, int width) => new TextBox { Name = name, Width = width, Margin = new Padding(3, 4, 3, 3) };

        /// <summary>Unchecked, checked and mixed check box images for the folder tree, in the system style.</summary>
        public static ImageList CheckImages(Control owner)
        {
            int size;
            using (var g = owner.CreateGraphics()) size = Math.Max(16, (int)Math.Round(16 * g.DpiX / 96f));
            var list = new ImageList { ImageSize = new Size(size, size), ColorDepth = ColorDepth.Depth32Bit };
            foreach (var state in new[] { CheckBoxState.UncheckedNormal, CheckBoxState.CheckedNormal, CheckBoxState.MixedNormal })
            {
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                {
                    var glyph = CheckBoxRenderer.GetGlyphSize(g, state);
                    CheckBoxRenderer.DrawCheckBox(g, new Point((size - glyph.Width) / 2, (size - glyph.Height) / 2), state);
                }
                list.Images.Add(bmp);
            }
            return list;
        }

        /// <summary>
        /// Puts a split container's divider at a fraction of its size once it has a real size.
        /// Setting SplitterDistance on a control that is still small throws, and an exception in a
        /// Load handler is swallowed silently on 64-bit Windows (found in M4 testing).
        /// </summary>
        public static void SplitAt(SplitContainer s, double fraction, int minimum)
        {
            bool done = false;
            void Apply()
            {
                if (done) return;
                int total = s.Orientation == Orientation.Vertical ? s.Width : s.Height;
                int d = Math.Max(minimum, (int)(total * fraction));
                if (d < s.Panel1MinSize || d > total - s.Panel2MinSize - s.SplitterWidth) return;
                s.SplitterDistance = d;
                done = true;
            }
            s.SizeChanged += (a, b) => Apply();
            s.HandleCreated += (a, b) => Apply();
        }

        public static Color ColorOf(CheckStatus s) =>
            s == CheckStatus.Pass ? Good : s == CheckStatus.Warn ? Warning : s == CheckStatus.Fail ? Bad : Muted;

        public static string Size(long bytes) => PreflightEngine.Size(bytes);

        /// <summary>
        /// SmokeTest build only: with DTT_SMOKE_QUIET=1 the windows open on a screen other than the
        /// main one and never take the focus, so the automated UI test can run while someone keeps
        /// using the PC (their typing went into the tool before). Always false in the Release build.
        /// </summary>
#if SMOKETEST
        public static readonly bool SmokeQuiet = Environment.GetEnvironmentVariable("DTT_SMOKE_QUIET") == "1";
#else
        public const bool SmokeQuiet = false;
#endif

        /// <summary>Puts a top-level window in the middle of the first screen that is not the main one.</summary>
        public static void PlaceForSmokeTest(Form f)
        {
            if (!SmokeQuiet) return;
            var screen = Screen.AllScreens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen;
            var wa = screen.WorkingArea;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(wa.X + Math.Max(0, (wa.Width - f.Width) / 2), wa.Y + Math.Max(0, (wa.Height - f.Height) / 2));
        }

        public static string Duration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : t.TotalMinutes >= 1 ? $"{t.Minutes} min {t.Seconds:00} s" : $"{t.Seconds} s";
    }
}
