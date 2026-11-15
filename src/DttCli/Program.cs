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
            if (verb == "scan" || verb == "run")
            {
                job = new JobDefinition { DestinationRoot = Value(args, "--dest") };
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

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; Console.WriteLine("\ncancelling..."); cts.Cancel(); };

            var engine = new CopyEngine(job, store.LogDirectory);
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
                engine = new CopyEngine(job, store.LogDirectory) { SaveState = store.Save, ExpectedFiles = scan.Totals.FilesCopied, ExpectedBytes = scan.Totals.BytesCopied };
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
