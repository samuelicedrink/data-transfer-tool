using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DataTransfer.Core
{
    public sealed class LogEntry
    {
        public bool IsDirectory;
        /// <summary>Robocopy's class column: "New File", "Newer", "Older", "same", "named", "New Dir", ...</summary>
        public string Class;
        public long Size;
        public string Path;
    }

    /// <summary>
    /// Line-by-line parser for English Robocopy logs written with /BYTES /FP (with or without /MT).
    /// Feed it lines as they appear; it keeps running counters, the failure list and the summary.
    /// </summary>
    public sealed class RobocopyLogParser
    {
        static readonly Regex ErrorRx = new Regex(@"^(?<ts>\d{4}/\d\d/\d\d \d\d:\d\d:\d\d) ERROR (?<code>-?\d+) \(0x[0-9A-Fa-f]{8}\) (?<rest>.+)$", RegexOptions.Compiled);
        static readonly Regex FileRx = new Regex(@"^\t\s*(?<cls>\*?[A-Za-z][A-Za-z ]*?)\s*\t\t\s*(?<size>\d+(?:\.\d+)?(?: [kmgt])?)(?: (?<ts>\d{4}/\d\d/\d\d \d\d:\d\d:\d\d))?\t(?<path>.*)$", RegexOptions.Compiled);
        static readonly Regex DirRx = new Regex(@"^\t *(?<cls>\*?[A-Za-z][A-Za-z ]*?) +(?<count>-?\d+)\t(?<path>.*)$", RegexOptions.Compiled);
        static readonly Regex SummaryRx = new Regex(@"^\s*(?<row>Dirs|Files|Bytes)\s*:\s+(?<vals>\d.*)$", RegexOptions.Compiled);
        static readonly Regex ValueRx = new Regex(@"\d+(?:\.\d+)?(?: [kmgt]\b)?", RegexOptions.Compiled);
        static readonly Regex PathStartRx = new Regex(@" (?=[A-Za-z]:\\|\\\\)", RegexOptions.Compiled);
        static readonly Regex TailNoiseRx = new Regex(@"(Waiting \d+ seconds\.\.\.)?( Retrying\.\.\.)?$", RegexOptions.Compiled);

        /// <summary>Classes that mean "this file is being (or would be) copied".</summary>
        static readonly HashSet<string> CopyClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "New File", "Newer", "Older", "Changed", "Modified", "Tweaked" };

        static readonly HashSet<int> DiskFullCodes = new HashSet<int> { 112, 39 };
        /// <summary>Errors that mean the destination itself is gone.</summary>
        static readonly HashSet<int> DestinationGoneCodes = new HashSet<int> { 21, 53, 55, 59, 64, 67, 121, 433, 1167, 1231, 1232, 1256 };
        /// <summary>Errors that may mean it; the engine probes the destination when it sees one.</summary>
        static readonly HashSet<int> DestinationSuspectCodes = new HashSet<int> { 2, 3, 15, 31, 1006, 1117 };

        readonly Dictionary<string, FileFailure> failures = new Dictionary<string, FileFailure>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> recentSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        readonly Queue<string> recentOrder = new Queue<string>();
        FileFailure awaitingMessage;
        bool fatalAwaitingDetail;

        public Action<LogEntry> OnEntry;
        public Action<FileFailure> OnError;

        /// <summary>Files Robocopy has started copying (or would copy, with /L). A retried file counts once.</summary>
        public long FilesStarted { get; private set; }
        public long BytesStarted { get; private set; }
        public string CurrentFolder { get; private set; }
        public string Options { get; private set; }
        public bool HasSummary => Files != null && Bytes != null;
        public bool Ended { get; private set; }
        /// <summary>Text of a start-up refusal such as missing backup rights or an invalid parameter.</summary>
        public string FatalError { get; private set; }
        public int DiskFullErrors { get; private set; }
        public int DestinationSuspectErrors { get; private set; }
        public int ErrorLines { get; private set; }

        /// <summary>Summary rows: Total, Copied, Skipped, Mismatch, Failed, Extras.</summary>
        public long[] Dirs { get; private set; }
        public long[] Files { get; private set; }
        public long[] Bytes { get; private set; }

        public IReadOnlyCollection<FileFailure> Failures => failures.Values;

        public Counts ToCounts()
        {
            var c = new Counts();
            if (!HasSummary) return c;
            c.FilesTotal = Files[0]; c.FilesCopied = Files[1]; c.FilesSkipped = Files[2]; c.FilesFailed = Files[4];
            c.BytesTotal = Bytes[0]; c.BytesCopied = Bytes[1]; c.BytesSkipped = Bytes[2]; c.BytesFailed = Bytes[4];
            return c;
        }

        public void Feed(string line)
        {
            if (line == null) return;
            if (line.Length > 0 && line[0] == '\uFEFF') line = line.Substring(1);
            line = line.TrimEnd('\r');

            Match m;
            if (line.Length > 0 && line[0] == '\t')
            {
                awaitingMessage = null;
                if ((m = FileRx.Match(line)).Success) { File(m); return; }
                if ((m = DirRx.Match(line)).Success) { Dir(m); return; }
                return; // continuation of the header's exclusion lists
            }
            if ((m = ErrorRx.Match(line)).Success) { Error(m); return; }
            if (line.StartsWith("ERROR: RETRY LIMIT EXCEEDED")) { awaitingMessage = null; return; }
            if (line.StartsWith("ERROR : ") || line.StartsWith("ERROR: "))
            {
                FatalError = line.Substring(line.IndexOf(':') + 1).Trim();
                fatalAwaitingDetail = true; awaitingMessage = null; return;
            }
            if (fatalAwaitingDetail)
            {
                fatalAwaitingDetail = false;
                if (line.Trim().Length > 0 && !line.StartsWith("---")) { FatalError += " " + line.Trim(' ', '*'); return; }
            }
            if (awaitingMessage != null)
            {
                var f = awaitingMessage; awaitingMessage = null;
                if (line.Trim().Length > 0 && !line.StartsWith("---")) { f.Message = line.Trim(); return; }
            }
            if ((m = SummaryRx.Match(line)).Success)
            {
                var vals = ValueRx.Matches(m.Groups["vals"].Value).Cast<Match>().Select(v => ParseSize(v.Value)).ToArray();
                if (vals.Length != 6) return;
                switch (m.Groups["row"].Value) { case "Dirs": Dirs = vals; break; case "Files": Files = vals; break; default: Bytes = vals; break; }
                return;
            }
            string t = line.TrimStart();
            if (t.StartsWith("Options :")) Options = t.Substring(9).Trim();
            else if (t.StartsWith("Ended :")) Ended = true;
        }

        void File(Match m)
        {
            var e = new LogEntry
            {
                Class = m.Groups["cls"].Value.Trim(),
                Size = ParseSize(m.Groups["size"].Value),
                Path = TailNoiseRx.Replace(m.Groups["path"].Value, "")
            };
            if (CopyClasses.Contains(e.Class))
            {
                // with /MT a file that is being retried gets a second identical line
                if (failures.TryGetValue(e.Path, out var known)) known.Size = e.Size;
                else
                {
                    FilesStarted++; BytesStarted += e.Size;
                    // remembered briefly so that a failure can report the size of its file
                    recentSizes[e.Path] = e.Size; recentOrder.Enqueue(e.Path);
                    if (recentOrder.Count > 512) recentSizes.Remove(recentOrder.Dequeue());
                }
                int i = e.Path.LastIndexOf('\\');
                if (i > 0) CurrentFolder = e.Path.Substring(0, i);
            }
            OnEntry?.Invoke(e);
        }

        void Dir(Match m)
        {
            var e = new LogEntry { IsDirectory = true, Class = m.Groups["cls"].Value.Trim(), Size = 0, Path = m.Groups["path"].Value };
            if (e.Class == "New Dir" || e.Class == "Existing Dir" || long.Parse(m.Groups["count"].Value) >= 0)
                CurrentFolder = e.Path.TrimEnd('\\');
            OnEntry?.Invoke(e);
        }

        void Error(Match m)
        {
            ErrorLines++;
            int code = int.Parse(m.Groups["code"].Value);
            string rest = m.Groups["rest"].Value, op = rest, path = "";
            var ps = PathStartRx.Match(rest);
            if (ps.Success) { op = rest.Substring(0, ps.Index); path = rest.Substring(ps.Index + 1); }
            bool isDir = op.IndexOf("Directory", StringComparison.OrdinalIgnoreCase) >= 0 || path.EndsWith("\\");
            if (isDir && path.Length > 3) path = path.TrimEnd('\\');

            if (DiskFullCodes.Contains(code)) DiskFullErrors++;
            // negative codes are driver/NTSTATUS-style failures (seen when a virtual disk was detached)
            if (code < 0 || DestinationGoneCodes.Contains(code) || DestinationSuspectCodes.Contains(code)) DestinationSuspectErrors++;

            if (!failures.TryGetValue(path, out var f))
            {
                f = new FileFailure { SourcePath = path };
                if (recentSizes.TryGetValue(path, out long size)) f.Size = size;
                failures[path] = f;
            }
            f.IsDirectory = isDir;
            f.ErrorCode = code;
            f.Operation = op;
            f.Category = Categorize(code);
            f.Message = Win32Message(code);
            f.Time = m.Groups["ts"].Value;
            f.Attempts++;
            awaitingMessage = f;
            OnError?.Invoke(f);
        }

        public static FailureCategory Categorize(int code)
        {
            if (code == 32 || code == 33) return FailureCategory.Locked;
            if (code == 5) return FailureCategory.AccessDenied;
            if (code == 2 || code == 3) return FailureCategory.NotFound;
            if (code == 206 || code == 111) return FailureCategory.PathTooLong;
            if (DiskFullCodes.Contains(code)) return FailureCategory.DiskFull;
            if (DestinationGoneCodes.Contains(code)) return FailureCategory.DestinationUnavailable;
            return FailureCategory.Other;
        }

        static string Win32Message(int code)
        {
            try { return new Win32Exception(code).Message; } catch { return "Error " + code; }
        }

        /// <summary>Exact with /BYTES; without it Robocopy prints "1.7 m" style values, converted approximately.</summary>
        public static long ParseSize(string s)
        {
            s = s.Trim();
            long mult = 1;
            if (s.Length > 2 && s[s.Length - 2] == ' ')
            {
                switch (char.ToLowerInvariant(s[s.Length - 1]))
                {
                    case 'k': mult = 1L << 10; break;
                    case 'm': mult = 1L << 20; break;
                    case 'g': mult = 1L << 30; break;
                    case 't': mult = 1L << 40; break;
                }
                s = s.Substring(0, s.Length - 2);
            }
            return (long)(decimal.Parse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) * mult);
        }

        /// <summary>Parses a complete log held in memory.</summary>
        public static RobocopyLogParser ParseAll(string text)
        {
            var p = new RobocopyLogParser();
            foreach (var line in text.Split('\n')) p.Feed(line);
            return p;
        }
    }
}
