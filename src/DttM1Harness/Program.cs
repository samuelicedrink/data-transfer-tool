// M1 investigation harness. Generates its own test trees and runs Robocopy
// against them; it never touches real user data.
//
//   DttM1Harness all <workRoot>                 run every local test under <workRoot>
//   DttM1Harness onedrive <oneDriveDir> <work>  cloud-only test in a generated folder
//
// Each test prints PASS / FAIL / INFO lines. Logs are kept in <workRoot>\logs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DttM1Harness
{
    internal sealed class RoboResult
    {
        public int Exit;
        public string LogPath;
        public string Log = "";
        public string StdOut = "";
        public double Seconds;
        public bool Killed;
        // Dirs/Files/Bytes -> Total, Copied, Skipped, Mismatch, Failed, Extras
        public Dictionary<string, long[]> Summary = new Dictionary<string, long[]>();
        public long Get(string row, int col) => Summary.TryGetValue(row, out var v) ? v[col] : -1;
        public string Short()
        {
            string f(string k) => Summary.TryGetValue(k, out var v) ? string.Join("/", v) : "n/a";
            return $"exit={Exit} {Seconds:0.0}s files(total/copied/skipped/mismatch/failed/extras)={f("Files")} dirs={f("Dirs")} bytes={f("Bytes")}";
        }
    }

    internal static class Program
    {
        const string SpecFlags = "/E /B /R:1 /W:2 /XJ /COPY:DAT /DCOPY:DAT /FFT /MT:8 /NP";
        const string Excl = "/XD AppData /XF desktop.ini thumbs.db ~$* *.tmp";
        const int Total = 0, Copied = 1, Skipped = 2, Mismatch = 3, Failed = 4, Extras = 5;

        static string Root, Logs, BaseFlags;
        static bool Elevated;
        static int fails;
        static readonly StringBuilder Report = new StringBuilder();

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length < 2) { Console.WriteLine("usage: all <workRoot> | onedrive <oneDriveDir> <workRoot>"); return 2; }
            Elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            BaseFlags = Elevated ? SpecFlags : SpecFlags.Replace("/B ", "");
            Root = Path.GetFullPath(args[args.Length - 1]);
            Logs = Path.Combine(Root, Elevated ? "logs_elevated" : "logs");
            Directory.CreateDirectory(Logs);
            Say($"# M1 harness  elevated={Elevated}  os={Environment.OSVersion.Version}  robocopy={FileVersionInfo.GetVersionInfo(RoboExe).ProductVersion}");
            Say($"# base flags: {BaseFlags}");
            try
            {
                if (args[0] == "all") RunAll();
                else if (args[0] == "live") T_LiveProgress();
                else if (args[0] == "onedrive") OneDrive(Path.GetFullPath(args[1]));
                else { Console.WriteLine("unknown command"); return 2; }
            }
            catch (Exception ex) { Fail("harness crashed: " + ex); }
            Say($"# done, {fails} FAIL line(s)");
            File.WriteAllText(Path.Combine(Logs, "_report.txt"), Report.ToString(), Encoding.UTF8);
            return fails == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ helpers

        static string RoboExe => Path.Combine(Environment.SystemDirectory, "robocopy.exe");
        static string LP(string p) => p.StartsWith(@"\\?\") ? p : @"\\?\" + p;
        static void Say(string s) { Console.WriteLine(s); Report.AppendLine(s); }
        static void Head(string s) { Say(""); Say("== " + s); }
        static void Info(string s) => Say("  INFO " + s);
        static void Pass(string s) => Say("  PASS " + s);
        static void Fail(string s) { fails++; Say("  FAIL " + s); }
        static void Check(bool ok, string what) { if (ok) Pass(what); else Fail(what); }
        static string LogPath(string name) => Path.Combine(Logs, name + ".log");

        static RoboResult Robo(string name, string src, string dst, string flags, Action<Process> during = null, bool unilog = true)
        {
            var r = new RoboResult { LogPath = LogPath(name) };
            if (File.Exists(r.LogPath)) File.Delete(r.LogPath);
            string args = $"\"{src}\" \"{dst}\" {flags}" + (unilog ? $" /UNILOG:\"{r.LogPath}\"" : "");
            var psi = new ProcessStartInfo(RoboExe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            var sw = Stopwatch.StartNew();
            using (var p = Process.Start(psi))
            {
                var so = p.StandardOutput.ReadToEndAsync();
                during?.Invoke(p);
                p.WaitForExit();
                r.Seconds = sw.Elapsed.TotalSeconds;
                r.Exit = p.ExitCode;
                r.StdOut = so.Result;
            }
            if (unilog && File.Exists(r.LogPath)) r.Log = File.ReadAllText(r.LogPath, Encoding.Unicode);
            else if (!unilog) { r.Log = r.StdOut; File.WriteAllText(r.LogPath, r.StdOut, Encoding.Unicode); }
            foreach (Match m in Regex.Matches(r.Log, @"^\s*(Dirs|Files|Bytes)\s*:\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$", RegexOptions.Multiline))
                r.Summary[m.Groups[1].Value] = Enumerable.Range(2, 6).Select(i => long.Parse(m.Groups[i].Value)).ToArray();
            return r;
        }

        static int Cmd(string exe, string args, out string output)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                var e = p.StandardError.ReadToEndAsync();
                output = p.StandardOutput.ReadToEnd() + e.Result;
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        static void WriteRandom(string path, long size, Random rng)
        {
            Directory.CreateDirectory(LP(Path.GetDirectoryName(path)));
            var buf = new byte[1 << 20];
            using (var fs = new FileStream(LP(path), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                long left = size;
                while (left > 0)
                {
                    rng.NextBytes(buf);
                    int n = (int)Math.Min(left, buf.Length);
                    fs.Write(buf, 0, n);
                    left -= n;
                }
            }
        }

        static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(LP(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
                return BitConverter.ToString(sha.ComputeHash(fs));
        }

        static void Wipe(string dir)
        {
            if (!Directory.Exists(LP(dir))) return;
            // Robocopy mirror of an empty folder removes long paths and junctions reliably.
            string empty = Path.Combine(Root, "_empty");
            Directory.CreateDirectory(empty);
            Cmd(RoboExe, $"\"{empty}\" \"{dir}\" /MIR /R:0 /W:0 /NJH /NJS /NFL /NDL /NP", out _);
            try { Directory.Delete(LP(dir), true); } catch { }
        }

        static bool IsExcludedFile(string name)
        {
            string n = name.ToLowerInvariant();
            return n == "desktop.ini" || n == "thumbs.db" || n.StartsWith("~$") || n.EndsWith(".tmp");
        }

        /// Walks src (not following reparse points) and yields relative paths.
        static void Walk(string dir, string rel, List<string> dirs, List<string> files)
        {
            foreach (var d in Directory.GetDirectories(LP(dir)))
            {
                string name = Path.GetFileName(d);
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                string r = rel.Length == 0 ? name : rel + "\\" + name;
                dirs.Add(r);
                Walk(Path.Combine(dir, name), r, dirs, files);
            }
            foreach (var f in Directory.GetFiles(LP(dir)))
                files.Add(rel.Length == 0 ? Path.GetFileName(f) : rel + "\\" + Path.GetFileName(f));
        }

        /// Compares dst with src: content hash, size, timestamps, attributes.
        static bool Verify(string label, string src, string dst, bool exclusions, bool quiet = false, bool ctimeAsInfo = false)
        {
            var dirs = new List<string>(); var files = new List<string>();
            Walk(src, "", dirs, files);
            var problems = new List<string>();
            int okFiles = 0, okDirs = 0, exclOk = 0, ctimeDiffs = 0;
            foreach (var rel in dirs)
            {
                bool excl = exclusions && rel.Split('\\').Any(s => s.Equals("AppData", StringComparison.OrdinalIgnoreCase));
                string s = LP(Path.Combine(src, rel)), d = LP(Path.Combine(dst, rel));
                if (excl) { if (Directory.Exists(d)) problems.Add("excluded dir present: " + rel); else exclOk++; continue; }
                if (!Directory.Exists(d)) { problems.Add("dir missing: " + rel); continue; }
                if (Directory.GetLastWriteTimeUtc(s) != Directory.GetLastWriteTimeUtc(d)) problems.Add("dir mtime differs: " + rel);
                else if (Directory.GetCreationTimeUtc(s) != Directory.GetCreationTimeUtc(d)) problems.Add("dir ctime differs: " + rel);
                else okDirs++;
            }
            foreach (var rel in files)
            {
                bool excl = exclusions && (IsExcludedFile(Path.GetFileName(rel)) || rel.Split('\\').Any(s => s.Equals("AppData", StringComparison.OrdinalIgnoreCase)));
                string s = LP(Path.Combine(src, rel)), d = LP(Path.Combine(dst, rel));
                if (excl) { if (File.Exists(d)) problems.Add("excluded file present: " + rel); else exclOk++; continue; }
                if (!File.Exists(d)) { problems.Add("file missing: " + rel); continue; }
                var si = new FileInfo(s); var di = new FileInfo(d);
                if (si.Length != di.Length) problems.Add($"size differs: {rel} {si.Length} vs {di.Length}");
                else if (Hash(s) != Hash(d)) problems.Add("CONTENT differs: " + rel);
                else if (si.LastWriteTimeUtc != di.LastWriteTimeUtc) problems.Add("file mtime differs: " + rel);
                else if (si.CreationTimeUtc != di.CreationTimeUtc) { if (ctimeAsInfo) { ctimeDiffs++; okFiles++; } else problems.Add("file ctime differs: " + rel); }
                else if ((si.Attributes & ~FileAttributes.Archive) != (di.Attributes & ~FileAttributes.Archive)) problems.Add($"attributes differ: {rel} {si.Attributes} vs {di.Attributes}");
                else okFiles++;
            }
            bool ok = problems.Count == 0;
            if (ctimeDiffs > 0) Info($"{label}: {ctimeDiffs} file(s) have the right content, size and modified-time but a created-time of the copy moment (kill landed between writing data and stamping created-time)");
            if (!quiet || !ok)
            {
                string msg = $"{label}: {okFiles} files + {okDirs} dirs identical (hash, size, times, attrs), {exclOk} excluded items absent, {problems.Count} problem(s)";
                if (ok) Pass(msg); else Fail(msg);
                foreach (var g in problems.GroupBy(p => p.Substring(0, p.IndexOf(':'))))
                    Say($"       {g.Count()} x {g.Key}, e.g. {Trunc(g.First(), 150)}");
            }
            return ok;
        }

        static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";

        // ------------------------------------------------------------------ tree generation

        static string GenMainTree()
        {
            string src = Path.Combine(Root, "src_main");
            Wipe(src);
            var rng = new Random(1);
            string docs = Path.Combine(src, "Docs");
            string[] ext = { ".docx", ".xlsx", ".pdf", ".txt", ".pptx" };
            for (int i = 0; i < 200; i++) WriteRandom(Path.Combine(docs, $"file_{i:000}{ext[i % ext.Length]}"), rng.Next(1, 50000), rng);
            WriteRandom(Path.Combine(docs, "zero_bytes.txt"), 0, rng);
            WriteRandom(Path.Combine(docs, "Été & Co (2024)", "résumé – final version.docx"), 12345, rng);
            WriteRandom(Path.Combine(docs, "日本語フォルダ", "報告書 ①.txt"), 2345, rng);
            WriteRandom(Path.Combine(docs, "Sub", "report one.docx"), 4000, rng);
            WriteRandom(Path.Combine(docs, "Sub", "AppData", "nested_appdata.dat"), 100, rng);
            // default exclusions
            WriteRandom(Path.Combine(docs, "desktop.ini"), 100, rng);
            WriteRandom(Path.Combine(docs, "Thumbs.db"), 100, rng);
            WriteRandom(Path.Combine(docs, "~$budget.xlsx"), 100, rng);
            WriteRandom(Path.Combine(docs, "scratch.tmp"), 100, rng);
            WriteRandom(Path.Combine(src, "AppData", "Local", "cache.dat"), 5000, rng);
            Directory.CreateDirectory(Path.Combine(docs, "EmptyFolder"));
            for (int i = 0; i < 20; i++) WriteRandom(Path.Combine(src, "Pictures", $"img_{i:00}.jpg"), rng.Next(1 << 20, 3 << 20), rng);
            // long path: > 400 characters
            string deep = Path.Combine(src, "Deep");
            for (int i = 0; i < 10; i++) deep = Path.Combine(deep, $"level{i:00}_" + new string('x', 32));
            WriteRandom(Path.Combine(deep, "deep file.txt"), 7777, rng);
            WriteRandom(Path.Combine(deep, new string('n', 200) + ".txt"), 888, rng);
            // attributes
            string ro = Path.Combine(docs, "readonly.txt"); WriteRandom(ro, 500, rng); File.SetAttributes(ro, FileAttributes.ReadOnly | FileAttributes.Archive);
            string hid = Path.Combine(docs, "hidden.txt"); WriteRandom(hid, 500, rng); File.SetAttributes(hid, FileAttributes.Hidden | FileAttributes.Archive);
            // junction that must not be followed (/XJ)
            Cmd("cmd.exe", $"/c mklink /J \"{Path.Combine(docs, "JunctionToPictures")}\" \"{Path.Combine(src, "Pictures")}\"", out _);
            // distinctive timestamps (odd second, old creation time)
            string f1 = Path.Combine(docs, "file_001.xlsx");
            File.SetCreationTimeUtc(f1, new DateTime(2017, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(f1, new DateTime(2020, 2, 2, 2, 2, 3, 123, DateTimeKind.Utc));
            string sub = Path.Combine(docs, "Sub");
            Directory.SetCreationTimeUtc(sub, new DateTime(2018, 5, 6, 7, 8, 9, DateTimeKind.Utc));
            Directory.SetLastWriteTimeUtc(sub, new DateTime(2019, 3, 4, 5, 6, 7, DateTimeKind.Utc));
            return src;
        }

        // ------------------------------------------------------------------ tests

        static void RunAll()
        {
            Head("Generate main tree");
            string src = GenMainTree();
            var dirs = new List<string>(); var files = new List<string>(); Walk(src, "", dirs, files);
            Info($"{files.Count} files, {dirs.Count} dirs, longest path {files.Max(f => Path.Combine(src, f).Length)} chars");

            T_SpecFlagsVerbatim(src);
            T_LogVariants(src);
            string dst = T_FullCopy(src);
            T_SkipIdentical(src, dst);
            T_Fft(src, dst);
            T_LongDestination(src);
            T_Errors();
            T_KillResume();
            T_LiveProgress();
        }

        static void T_SpecFlagsVerbatim(string src)
        {
            Head("T1 spec flag set verbatim (includes /B)");
            string dst = Path.Combine(Root, "dst_spec"); Wipe(dst);
            var r = Robo("t1_spec_verbatim", src, dst, SpecFlags + " " + Excl);
            Info(r.Short());
            if (!Elevated)
            {
                bool refused = r.Log.Contains("Backup and Restore Files user rights");
                Check(refused && r.Exit == 16, "not elevated: /B refused with exit 16 and 'You do not have the Backup and Restore Files user rights'");
                Info("=> /B needs elevation; all following tests in this run omit /B");
            }
            else
            {
                Check(r.Exit == 1, "elevated: spec flags verbatim copy exits 1 (all copied)");
                Verify("spec verbatim copy", src, dst, true);
            }
            // spec flags, no /BYTES: capture the human-readable summary for the parser
            Info("summary lines without /BYTES:");
            foreach (var l in r.Log.Split('\n').Where(l => Regex.IsMatch(l, @"^\s*(Dirs|Files|Bytes|Times|Speed|Ended)\s*:"))) Say("       " + l.TrimEnd());
        }

        static void T_LogVariants(string src)
        {
            Head("T2 log format variants (/L listings against an empty destination)");
            string dst = Path.Combine(Root, "dst_nonexistent");
            var v = new (string name, string flags)[]
            {
                ("t2_L_spec",            BaseFlags + " " + Excl + " /L"),
                ("t2_L_bytes_fp",        BaseFlags + " " + Excl + " /L /BYTES /FP"),
                ("t2_L_nomt_bytes_fp",   BaseFlags.Replace(" /MT:8", "") + " " + Excl + " /L /BYTES /FP"),
                ("t2_L_bytes_fp_ndl",    BaseFlags + " " + Excl + " /L /BYTES /FP /NDL /NJH"),
                ("t2_L_v_ts",            BaseFlags + " " + Excl + " /L /BYTES /FP /V /TS"),
                ("t2_L_nfl_ndl",         BaseFlags + " " + Excl + " /L /BYTES /NFL /NDL"),
            };
            foreach (var (name, flags) in v)
            {
                var r = Robo(name, src, dst, flags);
                Info($"{name}: {r.Short()}  loglines={r.Log.Split('\n').Length} stdoutChars={r.StdOut.Length}");
            }
            Check(!Directory.Exists(dst), "/L created nothing at the destination");
        }

        static string T_FullCopy(string src)
        {
            Head("T3 full copy with the working flag set + default exclusions");
            string dst = Path.Combine(Root, "dst_main"); Wipe(dst);
            var r = Robo("t3_copy_mt", src, dst, BaseFlags + " " + Excl + " /BYTES /FP");
            Info(r.Short());
            Check(r.Exit == 1, "exit code 1 (files copied, no failures)");
            Verify("copy /MT:8", src, dst, true);
            Check(!Directory.Exists(Path.Combine(dst, "Docs", "JunctionToPictures")) || !Directory.EnumerateFileSystemEntries(Path.Combine(dst, "Docs", "JunctionToPictures")).Any(),
                "/XJ: junction target not copied through the junction");
            Info("junction entry at destination exists as: " + (Directory.Exists(Path.Combine(dst, "Docs", "JunctionToPictures")) ? "empty directory" : "nothing"));
            Check(Directory.Exists(Path.Combine(dst, "Docs", "EmptyFolder")), "/E: empty folder created");
            Check(r.StdOut.Trim().Length < 200, $"/UNILOG without /TEE: stdout is quiet ({r.StdOut.Trim().Length} chars)");

            string dst2 = Path.Combine(Root, "dst_main_nomt"); Wipe(dst2);
            var r2 = Robo("t3_copy_nomt", src, dst2, BaseFlags.Replace(" /MT:8", "") + " " + Excl + " /BYTES /FP");
            Info("no /MT: " + r2.Short());
            Verify("copy without /MT", src, dst2, true);

            // /COPY:DAT vs default and /DCOPY:DAT vs /DCOPY:DA, to see what each flag buys
            string dst3 = Path.Combine(Root, "dst_main_dcopy_da"); Wipe(dst3);
            Robo("t3_copy_dcopy_da", src, dst3, BaseFlags.Replace("/DCOPY:DAT", "/DCOPY:DA") + " " + Excl + " /BYTES /FP");
            string sSub = Path.Combine(src, "Docs", "Sub"), dSub = Path.Combine(dst3, "Docs", "Sub");
            Info($"/DCOPY:DA (no T): folder modified-time kept={Directory.GetLastWriteTimeUtc(dSub) == Directory.GetLastWriteTimeUtc(sSub)}, folder created-time kept={Directory.GetCreationTimeUtc(dSub) == Directory.GetCreationTimeUtc(sSub)}");
            Check(Directory.GetCreationTimeUtc(dSub) != Directory.GetCreationTimeUtc(sSub), "/DCOPY:DA (without T) loses the folder created-time, so the T in /DCOPY:DAT is doing real work");
            Wipe(dst2); Wipe(dst3);
            return dst;
        }

        static void T_SkipIdentical(string src, string dst)
        {
            Head("T4 skip-if-identical");
            var r = Robo("t4_rerun_identical", src, dst, BaseFlags + " " + Excl + " /BYTES /FP");
            Info(r.Short());
            Check(r.Exit == 0 && r.Get("Files", Copied) == 0 && r.Get("Files", Skipped) == r.Get("Files", Total), "rerun: exit 0, 0 copied, every file skipped");

            // same size + newer time, different size, and a new file
            var rng = new Random(7);
            string a = Path.Combine(src, "Docs", "file_010.docx"); long len = new FileInfo(a).Length;
            WriteRandom(a, len, rng);                                              // same size, new mtime
            WriteRandom(Path.Combine(src, "Docs", "file_011.xlsx"), 60000, rng);    // different size
            WriteRandom(Path.Combine(src, "Docs", "brand new.txt"), 321, rng);      // new
            var r2 = Robo("t4_rerun_3_changed", src, dst, BaseFlags + " " + Excl + " /BYTES /FP");
            Info(r2.Short());
            Check(r2.Exit == 1 && r2.Get("Files", Copied) == 3, "after changing 2 files and adding 1: exactly 3 copied");
            Verify("after incremental run", src, dst, true, quiet: true);

            // same size AND same timestamp but different content: robocopy cannot see it (by design)
            string b = Path.Combine(src, "Docs", "file_012.pdf"); var t = File.GetLastWriteTimeUtc(b); long l2 = new FileInfo(b).Length;
            WriteRandom(b, l2, rng); File.SetLastWriteTimeUtc(b, t);
            var r3 = Robo("t4_rerun_same_size_time", src, dst, BaseFlags + " " + Excl + " /BYTES /FP");
            Info($"content changed but size+mtime kept: copied={r3.Get("Files", Copied)} (0 means robocopy treats it as identical, as the spec asks)");
            File.Copy(b, Path.Combine(dst, "Docs", "file_012.pdf"), true); // put back in sync for later tests
            File.SetLastWriteTimeUtc(Path.Combine(dst, "Docs", "file_012.pdf"), t);
        }

        static void T_Fft(string src, string dst)
        {
            Head("T5 /FFT (2-second timestamp tolerance)");
            string d = Path.Combine(dst, "Docs", "file_002.pdf"); var orig = File.GetLastWriteTimeUtc(d);
            string with = BaseFlags + " " + Excl + " /BYTES /FP /L";
            string without = BaseFlags.Replace(" /FFT", "") + " " + Excl + " /BYTES /FP /L";
            File.SetLastWriteTimeUtc(d, orig.AddSeconds(1));
            long cWith = Robo("t5_fft_1s_with", src, dst, with).Get("Files", Copied);
            long cWithout = Robo("t5_fft_1s_without", src, dst, without).Get("Files", Copied);
            Check(cWith == 0 && cWithout == 1, $"destination time off by 1 s: with /FFT would copy {cWith}, without /FFT would copy {cWithout}");
            File.SetLastWriteTimeUtc(d, orig.AddSeconds(3));
            long c3 = Robo("t5_fft_3s_with", src, dst, with).Get("Files", Copied);
            Check(c3 == 1, $"destination time off by 3 s: with /FFT would copy {c3}");
            File.SetLastWriteTimeUtc(d, orig);
        }

        static void T_LongDestination(string src)
        {
            Head("T6 long paths (source > 400 chars, destination base > 260 chars)");
            string dstBase = Path.Combine(Root, "dst_long");
            Wipe(dstBase);
            string dst = dstBase;
            for (int i = 0; i < 6; i++) dst = Path.Combine(dst, $"longdest{i}_" + new string('y', 40));
            Info($"destination base length {dst.Length}");
            var r = Robo("t6_long_paths", src, dst, BaseFlags + " " + Excl + " /BYTES /FP");
            Info(r.Short());
            Check(r.Exit == 1, "exit code 1");
            Verify("long destination", src, dst, true);
            var files = new List<string>(); Walk(dst, "", new List<string>(), files);
            Info($"longest destination path written: {files.Max(f => Path.Combine(dst, f).Length)} chars");
            // does this process (longPathAware manifest) handle long paths WITHOUT the \\?\ prefix?
            string probe = Path.Combine(dst, "Deep", "probe.txt");
            try { File.WriteAllText(probe, "x"); File.Delete(probe); Info(@"plain (no \\?\) .NET file IO on a >260 path works on this PC (LongPathsEnabled=1 here; not guaranteed on other PCs)"); }
            catch (Exception ex) { Info(@"plain (no \\?\) .NET file IO on a >260 path FAILS: " + ex.GetType().Name); }
            Wipe(dstBase);
        }

        static void T_Errors()
        {
            Head("T7 per-file errors: locked file, access-denied file, unreadable folder");
            string src = Path.Combine(Root, "src_err"), dst = Path.Combine(Root, "dst_err");
            string denied = Path.Combine(src, "denied.txt"), deniedDir = Path.Combine(src, "DeniedDir");
            if (Directory.Exists(src)) { Cmd("icacls.exe", $"\"{denied}\" /remove:d *S-1-1-0", out _); Cmd("icacls.exe", $"\"{deniedDir}\" /remove:d *S-1-1-0", out _); }
            Wipe(src); Wipe(dst);
            var rng = new Random(3);
            WriteRandom(Path.Combine(src, "a ok.txt"), 1000, rng);
            WriteRandom(Path.Combine(src, "locked.docx"), 2000, rng);
            WriteRandom(denied, 3000, rng);
            WriteRandom(Path.Combine(deniedDir, "inside.txt"), 4000, rng);
            WriteRandom(Path.Combine(src, "ok", "b ok.txt"), 5000, rng);
            Cmd("icacls.exe", $"\"{denied}\" /deny *S-1-1-0:(R)", out _);
            Cmd("icacls.exe", $"\"{deniedDir}\" /deny *S-1-1-0:(OI)(CI)(RX)", out _);
            RoboResult r, l;
            using (new FileStream(Path.Combine(src, "locked.docx"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                l = Robo("t7_errors_L", src, dst, BaseFlags + " /BYTES /FP /L");
                r = Robo("t7_errors_copy", src, dst, BaseFlags + " /BYTES /FP");
            }
            Info("/L scan : " + l.Short());
            Info("copy    : " + r.Short());
            Info("error lines in the copy log:");
            foreach (var line in r.Log.Split('\n').Where(x => x.Contains("ERROR") || x.Contains("denied") || x.Contains("being used"))) Say("       " + Trunc(line.TrimEnd(), 170));
            Check((r.Exit & 8) != 0 && r.Exit < 16, $"exit code has the 'some files failed' bit (8) and is not fatal: {r.Exit}");
            Check(File.Exists(Path.Combine(dst, "a ok.txt")) && File.Exists(Path.Combine(dst, "ok", "b ok.txt")), "healthy files were still copied; no prompt, no hang");
            Info($"locked.docx copied={File.Exists(Path.Combine(dst, "locked.docx"))}, denied.txt copied={File.Exists(Path.Combine(dst, "denied.txt"))}, DeniedDir\\inside.txt copied={File.Exists(Path.Combine(dst, "DeniedDir", "inside.txt"))}  (elevated /B is expected to copy the two ACL-denied ones)");
            Info($"elapsed {r.Seconds:0.0}s with /R:1 /W:2");

            Cmd("icacls.exe", $"\"{denied}\" /remove:d *S-1-1-0", out _);
            Cmd("icacls.exe", $"\"{deniedDir}\" /remove:d *S-1-1-0", out _);
            var r2 = Robo("t7_errors_retry", src, dst, BaseFlags + " /BYTES /FP");
            Info("retry after unlock: " + r2.Short());
            // ACLs differ on purpose (COPY:DAT has no S), so only content is compared here
            Check(r2.Exit <= 1 && Verify("retry pass", src, dst, false, quiet: true), "second pass picks up exactly the previously failed files; tree now identical");
        }

        static void T_KillResume()
        {
            Head("T8 resume after a killed run");
            string src = Path.Combine(Root, "src_big"), dst = Path.Combine(Root, "dst_big");
            Wipe(dst);
            if (!Directory.Exists(src))
            {
                var rng = new Random(5);
                for (int i = 0; i < 300; i++)
                    WriteRandom(Path.Combine(src, $"dir{i / 50}", $"f_{i:000}.bin"), i % 30 == 7 ? 200L << 20 : 1L << 20, rng);
            }
            var files = new List<string>(); Walk(src, "", new List<string>(), files);
            long total = files.Sum(f => new FileInfo(Path.Combine(src, f)).Length);
            Info($"source: {files.Count} files, {total >> 20} MB");

            var lens = new List<long>(); string tailErr = null; int dstCountAtKill = 0;
            var r = Robo("t8_killed_run", src, dst, BaseFlags + " /BYTES /FP", p =>
            {
                while (!p.HasExited)
                {
                    try { using (var fs = new FileStream(LogPath("t8_killed_run"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) lens.Add(fs.Length); }
                    catch (Exception ex) { tailErr = ex.Message; }
                    try { dstCountAtKill = Directory.Exists(dst) ? Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories).Count() : 0; } catch { }
                    if (dstCountAtKill >= 100) { try { p.Kill(); } catch { } break; }
                    Thread.Sleep(40);
                }
            });
            r.Killed = r.Exit == -1;
            Check(r.Killed, $"robocopy killed mid-run (exit {r.Exit}) with ~{dstCountAtKill} of {files.Count} files at the destination");
            int loggedLines = Regex.Matches(r.Log, @"^\s+New File", RegexOptions.Multiline).Count;
            Info($"killed-run log lists {loggedLines} file line(s) while {dstCountAtKill} files were already at the destination: with /MT the log falls behind while a large file is in flight, and the unflushed tail is lost on a kill");
            Info($"killed-run log has a summary block: {r.Summary.Count > 0}");

            // classify what the kill left behind
            int same = 0, missing = 0, partialSmaller = 0, sameSizeBadContent = 0, sameSizeBadContentSameTime = 0;
            var partialNotes = new List<string>();
            foreach (var f in files)
            {
                var s = new FileInfo(Path.Combine(src, f)); var d = new FileInfo(Path.Combine(dst, f));
                if (!d.Exists) { missing++; continue; }
                bool timeEq = Math.Abs((s.LastWriteTimeUtc - d.LastWriteTimeUtc).TotalSeconds) <= 2;
                if (d.Length != s.Length) { partialSmaller++; partialNotes.Add($"{f}: size {d.Length}/{s.Length}, mtime {d.LastWriteTimeUtc:u}, attrs {d.Attributes}"); }
                else if (Hash(s.FullName) != Hash(d.FullName))
                {
                    sameSizeBadContent++; if (timeEq) sameSizeBadContentSameTime++;
                    partialNotes.Add($"{f}: FULL SIZE but content incomplete, mtime {d.LastWriteTimeUtc:u} (source {s.LastWriteTimeUtc:u}), attrs {d.Attributes}");
                }
                else if (timeEq) same++;
                else partialNotes.Add($"{f}: content complete but mtime {d.LastWriteTimeUtc:u} not yet set");
            }
            Info($"after kill: {same} complete, {missing} not started, {partialSmaller} wrong size, {sameSizeBadContent} full-size-but-incomplete");
            foreach (var n in partialNotes.Take(10)) Say("       " + n);
            Check(sameSizeBadContentSameTime == 0, "no partial file has BOTH the final size and the final timestamp (so a rerun cannot mistake a partial file for a finished one)");

            var r2 = Robo("t8_resume_run", src, dst, BaseFlags + " /BYTES /FP");
            Info("resume run: " + r2.Short());
            Check(r2.Get("Files", Skipped) >= same - 1 && r2.Get("Files", Skipped) > 0, $"resume skipped the {same} files that were already complete");
            Verify("after resume", src, dst, false, ctimeAsInfo: true);
            var r3 = Robo("t8_third_run", src, dst, BaseFlags + " /BYTES /FP");
            Check(r3.Exit == 0 && r3.Get("Files", Copied) == 0, "third run has nothing left to do (exit 0)");
            Wipe(dst);
        }

        /// How quickly does per-file output become visible to a watching process?
        static void T_LiveProgress()
        {
            Head("T9 live progress: when does per-file output reach a watcher?");
            string src = Path.Combine(Root, "src_many"), dst = Path.Combine(Root, "dst_many");
            if (!Directory.Exists(src))
            {
                var rng = new Random(11);
                for (int i = 0; i < 6000; i++) WriteRandom(Path.Combine(src, $"d{i / 500:00}", $"m_{i:0000}.dat"), 100000, rng);
            }
            var variants = new (string name, string flags, bool unilog)[]
            {
                ("t9_live_unilog_mt",      BaseFlags + " /BYTES", true),
                ("t9_live_unilog_tee_mt",  BaseFlags + " /BYTES /TEE", true),
                ("t9_live_stdout_mt",      BaseFlags + " /BYTES", false),
                ("t9_live_unilog_nomt",    BaseFlags.Replace(" /MT:8", "") + " /BYTES /FP", true),
                ("t9_live_stdout_nomt",    BaseFlags.Replace(" /MT:8", "") + " /BYTES /FP", false),
            };
            foreach (var (name, flags, unilog) in variants)
            {
                Wipe(dst);
                string log = LogPath(name);
                if (File.Exists(log)) File.Delete(log);
                var psi = new ProcessStartInfo(RoboExe, $"\"{src}\" \"{dst}\" {flags}" + (unilog ? $" /UNILOG:\"{log}\"" : ""))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                var samples = new List<(double t, long logLen, int stdoutLines, int dstFiles)>();
                int lines = 0; var sw = Stopwatch.StartNew();
                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null && e.Data.Contains("New File")) Interlocked.Increment(ref lines); };
                    p.Start(); p.BeginOutputReadLine();
                    while (!p.HasExited)
                    {
                        long len = -1;
                        try { if (unilog) using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) len = fs.Length; } catch { }
                        int n = 0; try { if (Directory.Exists(dst)) n = Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories).Count(); } catch { }
                        samples.Add((sw.Elapsed.TotalSeconds, len, lines, n));
                        Thread.Sleep(50);
                    }
                    p.WaitForExit();
                    long finalLen = unilog && File.Exists(log) ? new FileInfo(log).Length : -1;
                    Info($"{name}: {sw.Elapsed.TotalSeconds:0.0}s exit={p.ExitCode} finalLog={finalLen} finalStdoutFileLines={lines}");
                }
                int step = Math.Max(1, samples.Count / 6);
                for (int i = 0; i < samples.Count; i += step)
                    Say($"       t={samples[i].t,5:0.00}s  filesAtDest={samples[i].dstFiles,5}  logBytes={samples[i].logLen,9}  stdoutFileLines={samples[i].stdoutLines,5}");
            }
            Wipe(dst);
        }

        // ------------------------------------------------------------------ OneDrive

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFileAttributesW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetCompressedFileSizeW(string path, out uint high);

        static string AttrText(uint a)
        {
            var names = new (uint bit, string n)[] {
                (0x1,"READONLY"),(0x2,"HIDDEN"),(0x4,"SYSTEM"),(0x20,"ARCHIVE"),(0x80,"NORMAL"),(0x200,"SPARSE"),(0x400,"REPARSE_POINT"),
                (0x800,"COMPRESSED"),(0x1000,"OFFLINE"),(0x2000,"NOT_CONTENT_INDEXED"),(0x4000,"ENCRYPTED"),(0x80000,"PINNED"),
                (0x100000,"UNPINNED"),(0x40000,"RECALL_ON_OPEN"),(0x400000,"RECALL_ON_DATA_ACCESS") };
            return $"0x{a:X8} " + string.Join("|", names.Where(x => (a & x.bit) != 0).Select(x => x.n));
        }
        static long OnDisk(string p) { uint lo = GetCompressedFileSizeW(p, out uint hi); return ((long)hi << 32) | lo; }

        static void OneDrive(string oneDriveDir)
        {
            Head("T9 cloud-only OneDrive files (generated test folder inside the sync root)");
            if (!Directory.Exists(oneDriveDir)) { Fail("OneDrive folder not found"); return; }
            string src = Path.Combine(oneDriveDir, "_DTT_M1_TEST_DELETE_ME");
            string dst = Path.Combine(Root, "dst_onedrive");
            Wipe(dst);
            if (Directory.Exists(src)) Directory.Delete(src, true);
            var rng = new Random(9);
            string local = Path.Combine(src, "local_1.bin"), pinned = Path.Combine(src, "pinned_2.bin");
            var cloud = Enumerable.Range(3, 4).Select(i => Path.Combine(src, "sub", $"cloud_{i}.bin")).ToArray();
            WriteRandom(local, 200000, rng); WriteRandom(pinned, 200000, rng);
            foreach (var c in cloud) WriteRandom(c, 300000, rng);
            Action<string> dump = title =>
            {
                Info(title);
                foreach (var f in new[] { local, pinned }.Concat(cloud))
                    Say($"       {Path.GetFileName(f),-13} size={new FileInfo(f).Length,7} onDisk={OnDisk(f),7} {AttrText(GetFileAttributesW(f))}");
            };
            dump("just created:");
            Cmd("attrib.exe", $"+P -U \"{pinned}\"", out _);

            // ask OneDrive to dehydrate ("Free up space") once uploaded; retry until it takes effect
            var sw = Stopwatch.StartNew(); bool done = false;
            while (sw.Elapsed.TotalSeconds < 360 && !done)
            {
                foreach (var c in cloud) Cmd("attrib.exe", $"+U -P \"{c}\"", out _);
                Thread.Sleep(5000);
                done = cloud.All(c => (GetFileAttributesW(c) & 0x400000) != 0 || OnDisk(c) == 0);
            }
            dump($"after 'attrib +U -P' on cloud_*.bin, waited {sw.Elapsed.TotalSeconds:0}s, dehydrated={done}:");
            if (!done) { Fail("OneDrive did not dehydrate the test files in time (sync paused or Files On-Demand off?); cannot conclude"); return; }

            uint ca = GetFileAttributesW(cloud[0]);
            Info($"cloud-only file carries OFFLINE(0x1000)={(ca & 0x1000) != 0}, RECALL_ON_DATA_ACCESS(0x400000)={(ca & 0x400000) != 0}, UNPINNED={(ca & 0x100000) != 0}, REPARSE_POINT={(ca & 0x400) != 0}");
            Info("attrib.exe shows: " + RunAttrib(cloud[0]));

            string f = BaseFlags + " /BYTES /FP";
            var lAll = Robo("t9_od_L_no_exclusion", src, dst, f + " /L");
            var lXao = Robo("t9_od_L_xa_o", src, dst, f + " /L /XA:O");
            Info("/L without /XA:O : " + lAll.Short());
            Info("/L with    /XA:O : " + lXao.Short());
            Check(lAll.Get("Files", Total) == 6, "/L without exclusion lists all 6 files");
            bool xaWorks = lXao.Get("Files", Copied) == 2;
            Check(xaWorks, $"/XA:O excludes exactly the 4 cloud-only files (would copy {lXao.Get("Files", Copied)}, skipped {lXao.Get("Files", Skipped)})");
            Check(cloud.All(c => OnDisk(c) == 0), "/L scans did not hydrate (download) any cloud-only file");

            var c1 = Robo("t9_od_copy_xa_o", src, dst, f + " /XA:O");
            Info("copy with /XA:O  : " + c1.Short());
            Check(File.Exists(Path.Combine(dst, "local_1.bin")) && File.Exists(Path.Combine(dst, "pinned_2.bin")), "locally available + always-keep files were copied");
            Check(!cloud.Any(c => File.Exists(Path.Combine(dst, "sub", Path.GetFileName(c)))), "no cloud-only file was copied");
            Check(cloud.All(c => OnDisk(c) == 0), "copy with /XA:O did not hydrate any cloud-only file");
            if (File.Exists(Path.Combine(dst, "local_1.bin")))
                Info("attributes of a copied local file at the destination: " + AttrText(GetFileAttributesW(Path.Combine(dst, "local_1.bin"))));

            // what happens with no exclusion: one generated cloud-only file, copied for real
            var c2 = Robo("t9_od_copy_one_cloud_no_exclusion", Path.Combine(src, "sub"), Path.Combine(dst, "sub_noexcl"), f + " /R:0 \"cloud_3.bin\"");
            Info("copy of ONE cloud-only file without exclusion: " + c2.Short());
            Info($"   copied={File.Exists(Path.Combine(dst, "sub_noexcl", "cloud_3.bin"))}; source now onDisk={OnDisk(cloud[0])} {AttrText(GetFileAttributesW(cloud[0]))}");
            foreach (var line in c2.Log.Split('\n').Where(x => x.Contains("ERROR") || x.Contains("cloud"))) Say("       " + Trunc(line.TrimEnd(), 170));

            try { Directory.Delete(src, true); Info("test folder removed from OneDrive"); } catch (Exception ex) { Info("could not remove test folder: " + ex.Message); }
            Wipe(dst);
        }

        static string RunAttrib(string path)
        {
            Cmd("attrib.exe", $"\"{path}\"", out string o);
            o = o.Trim(); int i = o.IndexOf(":\\", StringComparison.Ordinal);
            return i > 1 ? o.Substring(0, i - 1).Trim() : o;
        }
    }
}
