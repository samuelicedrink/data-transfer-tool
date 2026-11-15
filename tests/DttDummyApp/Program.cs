using System;
using System.Linq;
using System.Windows.Forms;

// DttDummyApp [--stubborn] [--title TEXT]
// Shows a window. With --stubborn it refuses to close when asked (like an app showing "Save changes?").
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool stubborn = args.Contains("--stubborn");
        int t = Array.IndexOf(args, "--title");
        var form = new Form { Text = t >= 0 && t + 1 < args.Length ? args[t + 1] : "Dummy document", Width = 320, Height = 120, ShowInTaskbar = true };
        form.Controls.Add(new Label { Text = stubborn ? "Refuses to close" : "Closes when asked", Dock = DockStyle.Fill });
        form.FormClosing += (s, e) => { if (stubborn) e.Cancel = true; };
        Application.Run(form);
    }
}
