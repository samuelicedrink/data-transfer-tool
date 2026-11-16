// End-to-end UI test of DataTransferTool (SmokeTest build). It clicks through the wizard like a
// technician would, using generated folders only, and never presses a pre-flight "fix" button, so
// no real application is closed.
//
//   DttUiSmoke <tool exe> <screenshot dir> <work dir> <small source> <large source> <many-files source>
//
// Scenario A: preset, clear, tick a folder, untick a subfolder, options (backup mode off),
//             destination test, acknowledge warnings, copy both sources, verify the copy.
// Scenario B: start a copy and cancel it with the two-click Cancel button.
// Scenario C: start a copy and close the window: it must cancel and exit without any dialog.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

static class Program
{
    static string exe, shots, work, small, large, many;
    static int fails;
    static Process proc;
    static AutomationElement win;

    static int Main(string[] a)
    {
        if (a.Length < 6) { Console.WriteLine("usage: DttUiSmoke <exe> <shots> <work> <small source> <large source> <many-files source>"); return 2; }
        exe = Path.GetFullPath(a[0]); shots = a[1]; work = a[2]; small = a[3]; large = a[4]; many = a[5];
        Directory.CreateDirectory(shots);
        // DTT_SMOKE_QUIET=1: the tool runs on the second screen without taking the focus (SmokeTest
        // build), and this watcher checks that it never does, so the PC can be used meanwhile
        bool quiet = Environment.GetEnvironmentVariable("DTT_SMOKE_QUIET") == "1";
        int focusTaken = 0;
        var watching = new CancellationTokenSource();
        if (quiet)
            new Thread(() =>
            {
                while (!watching.IsCancellationRequested)
                {
                    var p = proc;
                    GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                    if (p != null && pid == (uint)p.Id) Interlocked.Increment(ref focusTaken);
                    Thread.Sleep(100);
                }
            }) { IsBackground = true }.Start();
        try
        {
            string only = Environment.GetEnvironmentVariable("DTT_SMOKE_ONLY") ?? "ABCDEF"; // D resumes the job C leaves behind
            if (only.Contains("A")) ScenarioA();
            if (only.Contains("B")) ScenarioB();
            if (only.Contains("C")) ScenarioC();
            if (only.Contains("D")) ScenarioD();
            if (only.Contains("E")) ScenarioE();
            if (only.Contains("F")) ScenarioF();
        }
        catch (Exception ex) { Fail("crashed: " + ex); Shot("error"); }
        finally { try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { } }
        watching.Cancel();
        if (quiet) Check(focusTaken == 0, $"the tool never took the focus from the person using the PC ({focusTaken} check(s) found it in front)");
        Console.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILURE(S)");
        return fails == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------ scenarios

    static void ScenarioA()
    {
        Console.WriteLine("== A: full wizard and copy");
        string dest = Path.Combine(work, "ui_dest_a");
        Wipe(dest);
        Launch();
        Check(Text("titleLabel") == "Select folders", "starts on the Select screen");
        Check(!Id("nextButton").Current.IsEnabled, "Next is disabled while nothing is ticked");

        Invoke(Id("userDataButton"));
        Thread.Sleep(800);
        string presetMsg = Text("messageLabel");
        Check(presetMsg.StartsWith("Ticked ") && Items("selectionList").Count > 0, $"User data preset ticks folders: \"{presetMsg}\", {Items("selectionList").Count} job(s)");
        Check(Items("selectionList").All(i => UserFolderNames.Any(n => i.Contains("\\" + n))), "preset jobs are only Desktop/Documents/Pictures/Downloads folders");
        Shot("1-select-preset");
        Invoke(Id("clearButton"));
        Thread.Sleep(300);
        Check(Items("selectionList").Count == 0, "Clear unticks everything");

        TickFolder(small);
        TickFolder(large);
        // untick <small>\Docs\Sub in the tree with the keyboard, as a user would
        var smallItem = TreeItem(Id("folderTree"), Path.GetFileName(small));
        Expand(smallItem);
        var docs = TreeItem(smallItem, "Docs");
        Expand(docs);
        var sub = TreeItem(docs, "Sub");
        ((SelectionItemPattern)sub.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Thread.Sleep(300);
        PressSpace(Id("folderTree"));
        Thread.Sleep(500);
        var jobs = Items("selectionList");
        Check(jobs.Count == 2 && jobs.Any(j => j.StartsWith(small) && j.Contains("without 1 subfolder")) && jobs.Any(j => j == large),
              "two jobs; the small one without its unticked subfolder: " + string.Join(" | ", jobs));
        Shot("2-select");

        OptionsBackupOff(screenshot: true);

        Next();
        Check(Text("titleLabel") == "Choose the destination", "Destination screen");
        SetValue(Id("localPathBox"), dest);
        Invoke(Id("testButton"));
        Check(WaitFor(() => Text("resultLabel").StartsWith("OK:"), 30), "destination test passes: " + First(Text("resultLabel")));
        Check(Text("layoutLabel").Contains(Path.Combine(dest, Environment.MachineName, small.Substring(0, 1))), "layout example shows <Dest>\\<COMPUTERNAME>\\<drive>");
        Shot("3-destination");
        Next();

        Preflight(screenshot: true);
        Next();
        Check(Text("titleLabel") == "Copying" || Text("titleLabel") == "Copy finished", "Run screen");
        Check(!Id("backButton").Current.IsEnabled, "Back is disabled during the run");
        Thread.Sleep(2500);
        Shot("6-running");
        Check(WaitFor(() => Id("nextButton").Current.IsEnabled, 900), "copy finishes");
        Thread.Sleep(500);
        Shot("7-finished");
        string status = Text("statusLabel");
        Console.WriteLine("   status: " + status + " | files: " + Text("filesLabel"));
        Check(status == "Finished: everything was copied.", "outcome shown");
        Check(NoOtherWindows(), "no dialog appeared during the run");

        VerifyCopy(dest);
        string jobFile = Path.Combine(Path.GetDirectoryName(exe), "DataTransferJob.json");
        string json = File.Exists(jobFile) ? File.ReadAllText(jobFile) : "";
        Check(json.Contains("\"State\":2") || json.Contains("\"State\": 2"), "saved job beside the exe says Completed");
        Check(!json.ToLowerInvariant().Contains("password"), "saved job contains no password field");

        Next();
        Check(Text("titleLabel") == "Report", "Report screen");
        Check(Text("outcomeLabel") == "Everything was copied.", "report outcome: " + Text("outcomeLabel"));
        var rows = ReportRows("totalsList");
        Check(rows.Count == 3 && rows[2][0].StartsWith("Total") && rows[2][1].StartsWith("529 ") && rows[2][3] == "0", "totals per folder job and overall: " + string.Join(" | ", rows.Select(r => string.Join(" / ", r))));
        Check(Text("failuresLabel") == "No files failed." && !Id("retryButton").Current.IsEnabled, "no failures, Retry failed disabled");
        Shot("8-report");
        Invoke(Id("nextButton")); // Close
        Check(proc.WaitForExit(15000), "Close exits the tool");
        Check(!File.Exists(jobFile) && Directory.GetFiles(Path.Combine(Path.GetDirectoryName(exe), "Logs"), "job_*.json").Length > 0,
              "the finished job is moved to the log folder, so the next launch starts clean");
        Wipe(dest);
    }

    static void ScenarioB()
    {
        Console.WriteLine("== B: two-click Cancel");
        string dest = Path.Combine(work, "ui_dest_b");
        Wipe(dest);
        Launch();
        TickFolder(Tiny());
        OptionsBackupOff(screenshot: false);
        Next();
        SetValue(Id("localPathBox"), dest);
        Next();
        Preflight(screenshot: false);
        Next();
        var cancel = Id("cancelButton");
        var next = Id("nextButton");
        Thread.Sleep(1000);
        Check(!next.Current.IsEnabled, "copy is running when Cancel is clicked");
        Shot("6b-running");
        // how fast does the tool's own window answer while the copy runs? (WM_NULL round trip)
        var lat = new List<long>();
        var probe = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            var one = Stopwatch.StartNew();
            SendMessageTimeout(proc.MainWindowHandle, 0, IntPtr.Zero, IntPtr.Zero, 2 /* ABORTIFHUNG */, 10000, out _);
            lat.Add(one.ElapsedMilliseconds);
            Thread.Sleep(100);
        }
        Console.WriteLine($"   window response during copy: {string.Join(", ", lat)} ms");
        var uiaOne = Stopwatch.StartNew(); string dummy = next.Current.Name;
        Console.WriteLine($"   one UI Automation property read: {uiaOne.ElapsedMilliseconds} ms");
        // UI Automation's Invoke on a background window waits about 4 s before clicking (measured),
        // longer than Cancel stays armed; the button's own click message arrives at once, like a real click
        Click(cancel);
        Thread.Sleep(400);
        Check(cancel.Current.Name == "Click again to cancel", "first click only arms Cancel");
        Check(next.Current.IsEnabled == false, "still running after one click");
        Click(cancel);
        Check(WaitFor(() => Id("nextButton").Current.IsEnabled, 60), "second click cancels");
        Shot("8-cancelled");
        Check(Text("statusLabel").StartsWith("Cancelled."), "status: " + Text("statusLabel"));
        Check(Process.GetProcessesByName("robocopy").Length == 0, "no Robocopy left running");
        Check(NoOtherWindows(), "no dialog appeared");

        Next();
        Check(Text("outcomeLabel").StartsWith("The copy was cancelled."), "report says cancelled");
        var cont = Id("continueButton");
        Check(!cont.Current.IsOffscreen && cont.Current.IsEnabled, "Continue copy is offered");
        Shot("9-report-cancelled");
        Click(cont);
        Check(WaitFor(() => Text("titleLabel") == "Copying" || Text("titleLabel") == "Copy finished", 10), "Continue copy goes back to the Run screen");
        Check(WaitFor(() => Id("nextButton").Current.IsEnabled, 300), "continued copy finishes");
        Check(Text("statusLabel") == "Finished: everything was copied.", "status: " + Text("statusLabel"));
        Next();
        Check(Text("outcomeLabel") == "Everything was copied.", "report after continuing: " + Text("outcomeLabel"));
        int n = Directory.GetFiles(Path.Combine(dest, Environment.MachineName), "*", SearchOption.AllDirectories).Length;
        Check(n == 40000, $"all 40,000 files at the destination after continuing ({n})");
        Invoke(Id("nextButton"));
        Check(proc.WaitForExit(15000), "tool exits");
        Wipe(dest);
    }

    /// <summary>Relaunch after scenario C left a cancelled job: the tool offers to resume it.</summary>
    static void ScenarioD()
    {
        Console.WriteLine("== D: resume offered at start-up");
        string dest = Path.Combine(work, "ui_dest_c");
        string jobFile = Path.Combine(Path.GetDirectoryName(exe), "DataTransferJob.json");
        Check(File.Exists(jobFile), "a cancelled job is saved beside the exe");
        Launch(keepJob: true);
        AutomationElement dlg = null;
        Check(WaitFor(() => (dlg = win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ResumeForm"))
                                ?? AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "ResumeForm"))) != null, 15),
              "resume is offered");
        if (dlg == null) return;
        string info = Id("resumeInfo", dlg).Current.Name;
        Console.WriteLine("   " + info.Replace("\r\n", "\r\n   "));
        Check(info.Contains("it was cancelled") && info.Contains("src_tiny") && info.Contains(dest), "the offer describes the job");
        Shot("10-resume", dlg);
        Invoke(Id("resumeButton", dlg));
        Check(WaitFor(() => Text("titleLabel") == "Pre-flight checks", 15), "resume goes straight to the pre-flight checks");
        Preflight(screenshot: false);
        Next();
        Check(WaitFor(() => Id("nextButton").Current.IsEnabled, 300), "resumed copy finishes");
        Check(Text("statusLabel") == "Finished: everything was copied.", "status: " + Text("statusLabel"));
        Next();
        Check(Text("outcomeLabel") == "Everything was copied.", "report: " + Text("outcomeLabel"));
        var total = ReportRows("totalsList").Last();
        Console.WriteLine("   totals: " + string.Join(" / ", total));
        int n = Directory.GetFiles(Path.Combine(dest, Environment.MachineName), "*", SearchOption.AllDirectories).Length;
        Check(n == 40000, $"all 40,000 files at the destination after resuming ({n})");
        Invoke(Id("nextButton"));
        Check(proc.WaitForExit(15000), "tool exits");
        Check(!File.Exists(jobFile), "the finished job no longer waits to be resumed");
        Wipe(dest);
    }

    /// <summary>One file stays locked: it is reported with its reason and in the CSV; Retry failed copies it once released.</summary>
    static void ScenarioE()
    {
        Console.WriteLine("== E: failure report, CSV and Retry failed");
        string src = Path.Combine(work, "src_lock"), dest = Path.Combine(work, "ui_dest_e");
        Wipe(dest);
        Directory.CreateDirectory(src);
        var rng = new Random(4);
        for (int i = 0; i < 20; i++) { var b = new byte[3000]; rng.NextBytes(b); File.WriteAllBytes(Path.Combine(src, $"doc {i}.txt"), b); }
        string locked = Path.Combine(src, "locked report.docx");
        File.WriteAllBytes(locked, Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
        var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            Launch();
            TickFolder(src);
            OptionsBackupOff(screenshot: false);
            Next();
            SetValue(Id("localPathBox"), dest);
            Next();
            Preflight(screenshot: false);
            Next();
            Check(WaitFor(() => Id("nextButton").Current.IsEnabled, 120), "copy finishes (main pass, wait, locked-file retry)");
            Check(Text("statusLabel") == "Finished with 1 file(s) not copied.", "status: " + Text("statusLabel"));
            Next();
            var fails = ReportRows("failuresList");
            Check(fails.Count == 1 && fails[0][0] == locked && fails[0][1] == "File was open in another program" && fails[0][2].Contains("error 32"),
                  "the locked file is listed with its reason: " + string.Join(" | ", fails.Select(r => string.Join(" / ", r))));
            string csvText = Text("csvLabel");
            string csv = csvText.StartsWith("CSV: ") ? csvText.Substring(5) : "";
            var lines = File.Exists(csv) ? File.ReadAllLines(csv) : new string[0];
            Check(lines.Length == 2 && lines[1].Contains(locked) && lines[1].Contains("File was open in another program") && lines[1].Contains("\"32\""),
                  "failures CSV written to the log folder with the reason: " + Path.GetFileName(csv));
            Check(Id("retryButton").Current.IsEnabled, "Retry failed is offered");
            Shot("11-report-failure");

            hold.Dispose(); hold = null; // the user closes the document
            Click(Id("retryButton"));
            Check(WaitFor(() => Text("outcomeLabel") == "Everything was copied.", 60), "after Retry failed: " + Text("outcomeLabel"));
            Check(Text("failuresLabel") == "No files failed." && Text("reportActivityLabel").StartsWith("Retry finished: 1 of 1 now copied"), "report updated: " + Text("reportActivityLabel"));
            string copied = Path.Combine(dest, Environment.MachineName, src.Substring(0, 1).ToUpperInvariant(), src.Substring(3), "locked report.docx");
            Check(File.Exists(copied) && Hash(copied) == Hash(locked), "the retried file is at the destination, identical");
            Check(!File.Exists(csv) && Text("csvLabel") == "", "the CSV is removed once nothing is failing");
            Shot("12-report-after-retry");
            Invoke(Id("nextButton"));
            Check(proc.WaitForExit(15000), "tool exits");
        }
        finally { hold?.Dispose(); }
        Wipe(dest);
    }

    /// <summary>
    /// Resume of a network job, with and without saved credentials. The share does not exist, so the
    /// copy never starts; what is tested is how the saved credentials are used on resume.
    /// </summary>
    static void ScenarioF()
    {
        Console.WriteLine("== F: resume with and without saved credentials");
        string dir = Path.GetDirectoryName(exe);
        string jobFile = Path.Combine(dir, "DataTransferJob.json"), credFile = Path.Combine(dir, "DataTransferJob.credentials");
        const string id = "uitestf00001", user = @"TESTPC\nobody", password = "Ui-Test-Secret-9!";
        string json = "{\"Id\":\"" + id + "\",\"ComputerName\":\"" + Environment.MachineName + "\",\"DestinationRoot\":\"\\\\\\\\127.0.0.1\\\\dtt-no-such-share\\\\Export\"," +
                      "\"DestinationUserName\":\"TESTPC\\\\nobody\",\"State\":3,\"Options\":{\"BackupMode\":false,\"ExcludeCloudOnly\":true,\"Threads\":8,\"Retries\":1,\"WaitSeconds\":2," +
                      "\"ExcludeFiles\":[\"desktop.ini\"],\"ExcludeProfileDirs\":[\"AppData\"],\"ExcludeDirNames\":[],\"LockedRetryDelaySeconds\":10,\"DestinationPollSeconds\":3}," +
                      "\"Sources\":[{\"Path\":\"" + small.Replace("\\", "\\\\") + "\",\"Status\":0,\"ExcludedSubfolders\":[],\"Failures\":[]}]}";
        File.WriteAllText(jobFile, json);
        // the same DPAPI format the tool writes: user \0 password, CurrentUser scope, bound to the job id
        File.WriteAllBytes(credFile, System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(user + "\0" + password), System.Text.Encoding.UTF8.GetBytes("DataTransferTool/" + id),
            System.Security.Cryptography.DataProtectionScope.CurrentUser));
        try
        {
            // 1. credentials saved: no password prompt, straight to the checks
            Launch(keepJob: true);
            var dlg = WaitForDialog("ResumeForm");
            Check(dlg != null && Id("resumeInfo", dlg).Current.Name.Contains("Credentials:   saved for resume"), "the offer says the credentials are saved");
            if (dlg == null) return;
            Invoke(Id("resumeButton", dlg));
            Check(WaitFor(() => Text("titleLabel") == "Pre-flight checks", 15), "with saved credentials, resume skips the password prompt");
            Check(WaitFor(() => Id("rerunButton").Current.IsEnabled, 120), "checks finish");
            var dest = Rows().First(r => r[1].StartsWith("Destination"));
            Check(dest[0] == "FAIL (must fix)" && dest[2].Contains(@"Could not connect to \\127.0.0.1\dtt-no-such-share as " + user),
                  "the saved user name was used to connect: " + First(dest[2]));
            Check(!AllText().Contains(password), "the password is not shown anywhere in the window");
            ((WindowPattern)win.GetCurrentPattern(WindowPattern.Pattern)).Close();
            Check(proc.WaitForExit(15000), "tool exits");
            Check(File.Exists(credFile) && File.Exists(jobFile), "job and saved credentials are kept for the next resume");
            Check(!File.ReadAllText(jobFile).Contains(password), "the job file does not contain the password");

            // 2. no saved credentials: the tool asks for the password on the Destination screen
            File.Delete(credFile);
            Launch(keepJob: true);
            dlg = WaitForDialog("ResumeForm");
            Check(dlg != null && Id("resumeInfo", dlg).Current.Name.Contains("not saved - you will be asked for the password"), "the offer says the password will be needed");
            if (dlg == null) return;
            Invoke(Id("resumeButton", dlg));
            Check(WaitFor(() => Text("titleLabel") == "Choose the destination", 15), "without saved credentials, resume stops on the Destination screen");
            Check(Text("noticeLabel") == "Enter the password for " + user + " to resume.", "and asks for the password: " + Text("noticeLabel"));
            Check(Value(Id("uncPathBox")) == @"\\127.0.0.1\dtt-no-such-share\Export" && Value(Id("userBox")) == user, "share and user name are filled in");
            Shot("13-resume-needs-password");
            ((WindowPattern)win.GetCurrentPattern(WindowPattern.Pattern)).Close();
            Check(proc.WaitForExit(15000), "tool exits");
            Check(!File.Exists(credFile), "nothing is saved when the box was not ticked");
        }
        finally
        {
            foreach (var f in new[] { jobFile, credFile }) if (File.Exists(f)) File.Delete(f);
        }
    }

    static AutomationElement WaitForDialog(string id)
    {
        AutomationElement dlg = null;
        WaitFor(() => (dlg = win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id))
                             ?? AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, id))) != null, 15);
        return dlg;
    }

    /// <summary>Every name and value in the window, to check that a secret is not displayed.</summary>
    static string AllText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (AutomationElement e in win.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            try
            {
                sb.Append(e.Current.Name).Append('\n');
                if (e.TryGetCurrentPattern(ValuePattern.Pattern, out object v)) sb.Append(((ValuePattern)v).Current.Value).Append('\n');
                if (e.TryGetCurrentPattern(TextPattern.Pattern, out object t)) sb.Append(((TextPattern)t).DocumentRange.GetText(-1)).Append('\n');
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
        }
        return sb.ToString();
    }

    /// <summary>The cells of each row of a details-view list.</summary>
    static List<string[]> ReportRows(string listId) =>
        Id(listId).FindAll(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)))
        .Cast<AutomationElement>().Select(RowOf).ToList();

    static void ScenarioC()
    {
        Console.WriteLine("== C: closing the window during the run");
        string dest = Path.Combine(work, "ui_dest_c");
        Wipe(dest);
        Launch();
        TickFolder(Tiny());
        OptionsBackupOff(screenshot: false);
        Next();
        SetValue(Id("localPathBox"), dest);
        Next();
        Preflight(screenshot: false);
        Next();
        Thread.Sleep(1000);
        Check(!Id("nextButton").Current.IsEnabled, "copy is running when the window is closed");
        var sw = Stopwatch.StartNew();
        ((WindowPattern)win.GetCurrentPattern(WindowPattern.Pattern)).Close();
        bool exited = proc.WaitForExit(30000);
        Check(exited, $"window close cancels the copy and exits without asking ({sw.Elapsed.TotalSeconds:0.0} s)");
        Check(Process.GetProcessesByName("robocopy").Length == 0, "no Robocopy left running");
        string json = File.ReadAllText(Path.Combine(Path.GetDirectoryName(exe), "DataTransferJob.json"));
        Check(json.Contains("\"State\":3") || json.Contains("\"State\": 3"), "saved job says Cancelled (can be resumed)");
        // the partial copy and the job stay for scenario D, which resumes them
    }

    // ------------------------------------------------------------ wizard helpers

    static readonly string[] UserFolderNames = { "Desktop", "Documents", "Pictures", "Downloads" };

    /// <summary>
    /// 40,000 files of 512 bytes (about 20 MB). Robocopy's cost is per file, so this takes several
    /// seconds even when large files copy from cache in a moment; the cancel scenarios need that.
    /// </summary>
    static string Tiny()
    {
        string dir = Path.Combine(work, "src_tiny");
        if (Directory.Exists(dir) && Directory.GetDirectories(dir).Length == 40) return dir;
        var data = new byte[512];
        new Random(8).NextBytes(data);
        for (int d = 0; d < 40; d++)
        {
            string sub = Path.Combine(dir, $"t{d:00}");
            Directory.CreateDirectory(sub);
            for (int i = 0; i < 1000; i++) File.WriteAllBytes(Path.Combine(sub, $"n_{i:0000}.txt"), data);
        }
        return dir;
    }

    static void Launch(bool keepJob = false)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe))) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
        string dir = Path.GetDirectoryName(exe);
        if (!keepJob)
            foreach (var f in new[] { "DataTransferJob.json", "DataTransferJob.credentials" })
                if (File.Exists(Path.Combine(dir, f))) File.Delete(Path.Combine(dir, f));
        proc = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
        proc.WaitForInputIdle(20000);
        for (int i = 0; i < 100 && proc.MainWindowHandle == IntPtr.Zero; i++) { Thread.Sleep(100); proc.Refresh(); }
        win = AutomationElement.FromHandle(proc.MainWindowHandle);
        if (!keepJob) WaitFor(() => Text("titleLabel") == "Select folders", 15);
    }

    static void TickFolder(string path)
    {
        SetValue(Id("addPathBox"), path);
        Invoke(Id("addPathButton"));
        Thread.Sleep(600);
        Check(Text("messageLabel") == "Ticked " + path, "ticked " + path);
    }

    static void OptionsBackupOff(bool screenshot)
    {
        var opener = Task.Run(() => Invoke(Id("optionsButton"))); // opens a modal dialog
        AutomationElement dlg = null;
        WaitFor(() => (dlg = win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "OptionsForm"))
                             ?? AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "OptionsForm"))) != null, 10);
        Check(dlg != null, "Options dialog opens");
        if (dlg == null) return;
        var backup = Id("backupCheck", dlg);
        var toggle = (TogglePattern)backup.GetCurrentPattern(TogglePattern.Pattern);
        Check(toggle.Current.ToggleState == ToggleState.On, "backup mode (/B) is on by default");
        // the smoke test is not elevated, and /B needs elevation
        toggle.Toggle();
        Check(Value(Id("excludeFilesBox", dlg)).Replace("\r\n", ",") == "desktop.ini,thumbs.db,~$*,*.tmp", "default file exclusions are listed and editable");
        Check(Value(Id("knownAppsBox", dlg)).Contains("WINWORD"), "known applications list is editable");
        if (screenshot) Shot("4-options", dlg);
        Invoke(Id("okButton", dlg));
        opener.Wait(5000);
    }

    static void Preflight(bool screenshot)
    {
        Check(Text("titleLabel") == "Pre-flight checks", "Pre-flight screen");
        Thread.Sleep(500);
        Check(WaitFor(() => Id("rerunButton").Current.IsEnabled, 300), "checks finish");
        var rows = Rows();
        foreach (var r in rows) Console.WriteLine($"   {r[0],-16} {r[1],-42} {First(r.Length > 2 ? r[2] : "")}");
        Check(rows.Count == 10, "ten checks listed");
        Check(rows.All(r => r[0] != "..." && r[0] != "not run"), "every check ran");
        Check(!rows.Any(r => r[0].Contains("must fix")), "no blocking failure");
        if (screenshot) Shot("5a-preflight");
        Check(!Id("nextButton").Current.IsEnabled || rows.All(r => r[0] == "PASS"), "Start copy stays disabled while warnings are open");
        for (int guard = 0; guard < 12; guard++)
        {
            var open = Rows().FirstOrDefault(r => (r[0].StartsWith("WARN") || r[0].StartsWith("FAIL")) && !r[0].Contains("acknowledged"));
            if (open == null) break;
            var item = ListItems().First(li => RowOf(li)[1] == open[1]);
            ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Thread.Sleep(300);
            Check(Id("ackButton").Current.IsEnabled, "Acknowledge is offered for: " + open[1]);
            Invoke(Id("ackButton")); // never the fix button: it would close real applications
            Thread.Sleep(300);
        }
        Check(Id("nextButton").Current.IsEnabled && Text("nextButton") == "Start copy", "Start copy is enabled once everything is acknowledged");
        if (screenshot) Shot("5b-preflight-acknowledged");
    }

    static void Next()
    {
        string before = Text("titleLabel");
        var next = Id("nextButton");
        if (!WaitFor(() => next.Current.IsEnabled, 10)) throw new InvalidOperationException("Next is disabled on: " + before);
        // a screen that starts work at once disables Next inside the click; the proxy then reports an error
        try { Invoke(next); } catch (InvalidOperationException) { }
        if (!WaitFor(() => Text("titleLabel") != before, 10)) throw new InvalidOperationException("Next did not leave: " + before);
        Thread.Sleep(500);
    }

    // ------------------------------------------------------------ verification

    static void VerifyCopy(string dest)
    {
        var job = new { Computer = Environment.MachineName };
        int smallOk = 0, largeOk = 0, problems = 0;
        string Dst(string src) => Path.Combine(dest, job.Computer, src.Substring(0, 1).ToUpperInvariant(), src.Substring(3));
        string[] excludedNames = { "desktop.ini", "thumbs.db" };
        foreach (var f in Walk(small))
        {
            string rel = f.Substring(small.Length + 1), name = Path.GetFileName(f).ToLowerInvariant();
            bool excluded = rel.StartsWith(@"Docs\Sub\", StringComparison.OrdinalIgnoreCase) || excludedNames.Contains(name) || name.StartsWith("~$") || name.EndsWith(".tmp");
            bool there = File.Exists(@"\\?\" + Dst(f));
            if (excluded) { if (there) { problems++; Console.WriteLine("   excluded file was copied: " + rel); } continue; }
            if (!there || Hash(f) != Hash(Dst(f))) { problems++; Console.WriteLine("   missing or different: " + rel); continue; }
            smallOk++;
        }
        foreach (var f in Walk(large))
        {
            var s = new FileInfo(@"\\?\" + f); var d = new FileInfo(@"\\?\" + Dst(f));
            if (!d.Exists || d.Length != s.Length || d.LastWriteTimeUtc != s.LastWriteTimeUtc) { problems++; continue; }
            largeOk++;
        }
        Check(problems == 0 && smallOk > 100 && largeOk > 100, $"copy verified: {smallOk} small-tree files by hash, {largeOk} large-tree files by size and time, {problems} problem(s); unticked subfolder and default exclusions absent");
    }

    static IEnumerable<string> Walk(string dir)
    {
        foreach (var f in Directory.GetFiles(@"\\?\" + dir)) yield return f.Substring(4);
        foreach (var d in Directory.GetDirectories(@"\\?\" + dir))
            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0)
                foreach (var f in Walk(d.Substring(4))) yield return f;
    }

    static string Hash(string path)
    {
        using (var sha = SHA256.Create()) using (var fs = File.OpenRead(@"\\?\" + path)) return Convert.ToBase64String(sha.ComputeHash(fs));
    }

    static void Wipe(string dir)
    {
        if (!Directory.Exists(dir)) return;
        string empty = Path.Combine(work, "_empty_ui");
        Directory.CreateDirectory(empty);
        using (var p = Process.Start(new ProcessStartInfo("robocopy.exe", $"\"{empty}\" \"{dir}\" /MIR /NJH /NJS /NFL /NDL /NP") { UseShellExecute = false, CreateNoWindow = true })) p.WaitForExit();
        try { Directory.Delete(dir, true); } catch { }
    }

    // ------------------------------------------------------------ UI Automation helpers

    static AutomationElement Id(string id, AutomationElement scope = null)
    {
        AutomationElement e = null;
        WaitFor(() => (e = (scope ?? win).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id))) != null, 10);
        if (e == null) throw new InvalidOperationException("control not found: " + id);
        return e;
    }

    static string Text(string id) { try { return Id(id).Current.Name ?? ""; } catch (InvalidOperationException) { return ""; } }
    static string Value(AutomationElement e) =>
        e.TryGetCurrentPattern(ValuePattern.Pattern, out object v) ? ((ValuePattern)v).Current.Value
        : ((TextPattern)e.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1).TrimEnd('\r', '\n'); // multi-line boxes
    static void SetValue(AutomationElement e, string v) => ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).SetValue(v);
    static void Invoke(AutomationElement e) => ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    static void Expand(AutomationElement e)
    {
        ((ExpandCollapsePattern)e.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        Thread.Sleep(400);
    }

    static AutomationElement TreeItem(AutomationElement scope, string name)
    {
        AutomationElement e = null;
        WaitFor(() => (e = scope.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem), new PropertyCondition(AutomationElement.NameProperty, name)))) != null, 10);
        if (e == null) throw new InvalidOperationException("tree item not found: " + name);
        return e;
    }

    static List<string> Items(string listId) =>
        Id(listId).FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                  .Cast<AutomationElement>().Select(i => i.Current.Name).ToList();

    // a ListView in Details view exposes its rows as data items
    static List<AutomationElement> ListItems() =>
        Id("checksList").FindAll(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem))).Cast<AutomationElement>().ToList();

    /// <summary>Status, check, result of one pre-flight row.</summary>
    static string[] RowOf(AutomationElement item)
    {
        var cells = item.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>().Select(c => c.Current.Name).ToList();
        if (cells.Count == 0) cells.Add(item.Current.Name);
        while (cells.Count < 3) cells.Add("");
        return cells.ToArray();
    }

    static List<string[]> Rows() => ListItems().Select(RowOf).ToList();

    static bool NoOtherWindows() =>
        AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, proc.Id)).Count <= 1 &&
        win.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)).Count == 0;

    static bool WaitFor(Func<bool> cond, double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            try { if (cond()) return true; } catch (ElementNotAvailableException) { } catch (InvalidOperationException) { }
            Thread.Sleep(200);
        }
        return false;
    }

    // The test never brings the tool to the front and never sends keystrokes through the desktop,
    // so it cannot type into, or cover, whatever the person at the PC is using.
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

    /// <summary>BM_CLICK straight to the button's window.</summary>
    static void Click(AutomationElement button) => PostMessage(new IntPtr(button.Current.NativeWindowHandle), 0x00F5, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Space key delivered to one control's window only.</summary>
    static void PressSpace(AutomationElement control)
    {
        var h = new IntPtr(control.Current.NativeWindowHandle);
        PostMessage(h, 0x0100 /* WM_KEYDOWN */, new IntPtr(0x20), new IntPtr(0x00390001));
        PostMessage(h, 0x0101 /* WM_KEYUP */, new IntPtr(0x20), new IntPtr(unchecked((int)0xC0390001)));
    }

    /// <summary>Captures the window itself, even when other windows are in front of it.</summary>
    static void Shot(string name, AutomationElement element = null)
    {
        try
        {
            var h = new IntPtr((element ?? win).Current.NativeWindowHandle);
            GetWindowRect(h, out RECT r);
            using (var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try { PrintWindow(h, hdc, 2 /* PW_RENDERFULLCONTENT */); } finally { g.ReleaseHdc(hdc); }
                }
                bmp.Save(Path.Combine(shots, name + ".png"), ImageFormat.Png);
            }
        }
        catch (Exception ex) { Console.WriteLine("   (screenshot failed: " + ex.Message + ")"); }
    }

    static string First(string s) { s = s ?? ""; int i = s.IndexOfAny(new[] { '\r', '\n' }); s = i >= 0 ? s.Substring(0, i) : s; return s.Length > 110 ? s.Substring(0, 110) + "..." : s; }

    static void Check(bool ok, string what)
    {
        if (!ok) fails++;
        Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
    }

    static void Fail(string what) { fails++; Console.WriteLine("  FAIL " + what); }
}
