// Console driver for the core library (development and testing aid; the product is the WinForms exe).
//
//   DttCli scan   --dest <folder> --src <folder> [--src <folder>...] [--xd <subfolder>...] [--no-backup]
//   DttCli run    (same options)        start a new job and save it beside the exe
//   DttCli resume                       continue the saved, unfinished job
//   DttCli retry                        retry the failures recorded in the saved job
//   DttCli show                         print the saved job
using System;
using System.Linq;
using System.Threading;
using DataTransfer.Core;

namespace DttCli
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("usage: DttCli scan|run|resume|retry|show [--dest D] [--src S]... [--xd X]... [--no-backup] [--store DIR]"); return 2; }
            string verb = args[0].ToLowerInvariant();
            string storeDir = Value(args, "--store") ?? AppDomain.CurrentDomain.BaseDirectory;
            var store = JobStore.Resolve(storeDir);
            Console.WriteLine($"job file: {store.JobFile}");

            JobDefinition job;
            if (verb == "scan" || verb == "run" || verb == "preflight")
            {
                job = new JobDefinition { DestinationRoot = Value(args, "--dest"), DestinationUserName = Value(args, "--user") };
                if (Values(args, "--only-app").Any()) job.Preflight.KnownApps = Values(args, "--only-app").ToList(); // replaces the list
                foreach (var a in Values(args, "--app")) job.Preflight.KnownApps.Add(a);
                if (Value(args, "--close-timeout") != null) job.Preflight.AppCloseTimeoutSeconds = int.Parse(Value(args, "--close-timeout"));
                foreach (var s in Values(args, "--src")) job.Sources.Add(new SourceSelection { Path = s });
                foreach (var x in Values(args, "--xd"))
                    job.OwnerOf(x)?.ExcludedSubfolders.Add(x);
                if (args.Contains("--no-backup")) job.Options.BackupMode = false;
                if (job.DestinationRoot == null || job.Sources.Count == 0) { Console.WriteLine("need --dest and at least one --src"); return 2; }
            }
            else
            {
                job = store.Load();
                if (job == null) { Console.WriteLine("no saved job"); return 1; }
            }

            if (verb == "show") { Print(job); return 0; }
            if (verb == "preflight") return Preflight(job, store, args);

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; Console.WriteLine("\ncancelling..."); cts.Cancel(); };

            var engine = new CopyEngine(job, store.JobLogDirectory(job));
            engine.Message += m => Console.WriteLine("\n" + m);
            var lastLine = DateTime.MinValue;
            engine.Progress += p =>
            {
                if ((DateTime.UtcNow - lastLine).TotalSeconds < 1 && p.Phase != EnginePhase.Finished) return;
                lastLine = DateTime.UtcNow;
                string text = p.ToString();
                Console.Write("\r" + (text.Length > 118 ? text.Substring(0, 118) : text.PadRight(118)));
            };

            if (verb == "scan")
            {
                var scan = engine.Scan(cts.Token);
                Console.WriteLine();
                if (scan.FatalError != null) { Console.WriteLine("scan failed: " + scan.FatalError); return 1; }
                Console.WriteLine($"to copy: {scan.Totals.FilesCopied} files, {scan.Totals.BytesCopied} bytes; already there or excluded: {scan.Totals.FilesSkipped} files; unreadable folders: {scan.UnreadableFolders}");
                return 0;
            }

            engine.SaveState = store.Save;
            if (verb == "run" || verb == "resume")
            {
                if (verb == "resume" && !job.IsUnfinished) { Console.WriteLine($"saved job is {job.State}; nothing to resume"); return 1; }
                Console.WriteLine("scanning...");
                var scan = engine.Scan(cts.Token);
                if (scan.Cancelled) return 1;
                if (scan.FatalError != null) { Console.WriteLine("\nscan failed: " + scan.FatalError); return 1; }
                engine = new CopyEngine(job, store.JobLogDirectory(job)) { SaveState = store.Save, ExpectedFiles = scan.Totals.FilesCopied, ExpectedBytes = scan.Totals.BytesCopied };
                engine.Message += m => Console.WriteLine("\n" + m);
                engine.Progress += p =>
                {
                    if ((DateTime.UtcNow - lastLine).TotalSeconds < 1 && p.Phase != EnginePhase.Finished) return;
                    lastLine = DateTime.UtcNow;
                    string text = p.ToString();
                    Console.Write("\r" + (text.Length > 118 ? text.Substring(0, 118) : text.PadRight(118)));
                };
                var outcome = engine.Run(cts.Token);
                Console.WriteLine("\noutcome: " + outcome);
                Print(job);
                return outcome == JobOutcome.Completed ? 0 : 1;
            }
            if (verb == "retry")
            {
                var outcome = engine.RetryFailed(cts.Token);
                Console.WriteLine("\noutcome: " + outcome);
                Print(job);
                return outcome == JobOutcome.Completed ? 0 : 1;
            }
            Console.WriteLine("unknown command " + verb);
            return 2;
        }

        //   DttCli preflight --dest D --src S... [--user U] [--fix apps|sessions]... [--ack-all]
        //                    [--app NAME]... [--close-timeout SECONDS] [--restart-onedrive]
        // Checks only look. Applications are closed only when --fix is given.
        static int Preflight(JobDefinition job, JobStore store, string[] args)
        {
            var pf = new PreflightEngine(job, store.JobLogDirectory(job));
            if (!string.IsNullOrEmpty(job.DestinationUserName))
            {
                Console.Write($"Password for {job.DestinationUserName} (not shown, not stored): ");
                pf.DestinationPassword = ReadPassword();
            }
            pf.Activity += a => Console.Write("\r" + (a.Length > 118 ? a.Substring(0, 118) : a.PadRight(118)));
            pf.RunAll(CancellationToken.None);
            foreach (var id in Values(args, "--fix"))
            {
                if (pf[id].FixLabel == null) { Console.WriteLine($"\nno fix available for '{id}' right now"); continue; }
                Console.WriteLine($"\napplying fix: {pf[id].FixLabel}");
                pf.ApplyFix(id, CancellationToken.None);
            }
            if (args.Contains("--ack-all")) foreach (var r in pf.Results) pf.Acknowledge(r.Id);

            Console.WriteLine("\r" + new string(' ', 118));
            foreach (var r in pf.Results)
            {
                Console.WriteLine(r + (r.Blocking ? "  (blocking)" : r.Acknowledged ? "  (acknowledged)" : "") + (r.FixLabel != null ? $"  [fix: {r.FixLabel}]" : ""));
                foreach (var d in r.Details) Console.WriteLine("        " + d);
            }
            Console.WriteLine();
            Console.WriteLine(pf.CanRun ? "RUN ENABLED: every check passed or was acknowledged."
                                        : "RUN DISABLED: " + string.Join(", ", pf.Results.Where(r => !r.AllowsRun).Select(r => r.Id + (r.Blocking ? " (blocking)" : " (needs acknowledgement)"))));
            if (pf.OneDriveWasStopped && args.Contains("--restart-onedrive"))
            {
                foreach (var n in pf.RestartOneDrive()) Console.WriteLine(n);
                Console.WriteLine("OneDrive restart requested.");
            }
            return pf.CanRun ? 0 : 1;
        }

        static string ReadPassword()
        {
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                var k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Enter) break;
                if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
                else if (k.KeyChar != '\0') sb.Append(k.KeyChar);
            }
            Console.WriteLine();
            return sb.ToString();
        }

        static void Print(JobDefinition job)
        {
            Console.WriteLine($"job {job.Id}: {job.State}, destination {job.DestinationRoot}");
            foreach (var s in job.Sources)
                Console.WriteLine($"  {s.Status,-20} {s.Path}: {s.Totals}{(s.CountsApproximate ? " (approximate)" : "")}");
            foreach (var f in job.AllFailures().Take(50)) Console.WriteLine("  FAILED " + f);
        }

        static string Value(string[] args, string name) => Values(args, name).FirstOrDefault();
        static System.Collections.Generic.IEnumerable<string> Values(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) yield return args[i + 1];
        }
    }
}
