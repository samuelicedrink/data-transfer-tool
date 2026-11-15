using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DataTransfer.Core;

namespace DataTransferTool
{
    internal static class Program
    {
        static JobStore store;

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            store = JobStore.Resolve(AppDomain.CurrentDomain.BaseDirectory);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Crash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);
            Application.Run(new MainForm(store));
        }

        /// <summary>Writes the error to the log folder. Exceptions never carry the destination password.</summary>
        static void Crash(Exception ex)
        {
            string file = null;
            try
            {
                Directory.CreateDirectory(store.LogDirectory);
                file = Path.Combine(store.LogDirectory, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(file, $"{DateTime.Now:O}\r\n{Environment.OSVersion}\r\n{ex}\r\n", Encoding.UTF8);
            }
            catch { }
            MessageBox.Show("The tool hit an unexpected error and has to close.\r\n\r\n" + ex?.Message +
                            (file != null ? "\r\n\r\nDetails: " + file : ""), "Data Transfer Tool", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.Exit(3);
        }
    }
}
