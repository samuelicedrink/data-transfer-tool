using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DataTransfer.Core
{
    /// <summary>Turns a job into Robocopy command lines. No I/O apart from what ProfileInfo was given.</summary>
    public static class RobocopyCommandBuilder
    {
        /// <summary>Robocopy's own limit is 32,767 characters; stay well below it.</summary>
        public const int MaxArgumentLength = 30000;

        /// <summary>Flags common to every invocation (main pass, scan, retry).</summary>
        public static string BaseFlags(CopyOptions o, bool recurse)
        {
            var sb = new StringBuilder();
            if (recurse) sb.Append("/E ");
            if (o.BackupMode) sb.Append("/B ");
            sb.Append($"/R:{o.Retries} /W:{o.WaitSeconds} /XJ /COPY:DAT /DCOPY:DAT /FFT /MT:{o.Threads} /NP /BYTES /FP");
            if (o.ExcludeCloudOnly) sb.Append(" /XA:O");
            if (!o.KeepHiddenAttributes) sb.Append(" /A-:SH"); // copied files are not Hidden or System; folders are handled by Unhide
            return sb.ToString();
        }

        /// <summary>Main pass (or /L scan) for one ticked top-level folder.</summary>
        public static string Build(JobDefinition job, SourceSelection source, string logPath, bool listOnly, ProfileInfo profiles)
            => BuildTree(job, source, source.Path, logPath, listOnly, profiles);

        /// <summary>Same as Build but for a subfolder of a source (used to retry a failed folder).</summary>
        public static string BuildTree(JobDefinition job, SourceSelection source, string folder, string logPath, bool listOnly, ProfileInfo profiles)
        {
            var o = job.Options;
            var sb = new StringBuilder();
            sb.Append(Quote(folder)).Append(' ').Append(Quote(job.DestinationFor(folder))).Append(' ');
            sb.Append(BaseFlags(o, recurse: true));
            if (listOnly) sb.Append(" /L");

            var xd = new List<string>();
            xd.AddRange(source.ExcludedSubfolders.Where(x => PathUtil.IsSameOrUnder(x, folder) || PathUtil.IsSameOrUnder(folder, x)));
            xd.AddRange(ProfileExclusions(folder, o.ExcludeProfileDirs, profiles));
            // never copy the destination into itself
            if (PathUtil.IsSameOrUnder(job.DestinationRoot, folder)) xd.Add(job.DestinationRoot);
            xd.AddRange(o.ExcludeDirNames.Where(n => !string.IsNullOrWhiteSpace(n)));
            xd = xd.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (xd.Count > 0) sb.Append(" /XD ").Append(string.Join(" ", xd.Select(Quote)));

            var xf = o.ExcludeFiles.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (xf.Count > 0) sb.Append(" /XF ").Append(string.Join(" ", xf.Select(Quote)));

            sb.Append(" /UNILOG+:").Append(Quote(logPath));
            if (sb.Length > MaxArgumentLength)
                throw new InvalidOperationException($"Too many exclusions for one Robocopy command ({sb.Length} characters). Untick fewer subfolders under {folder}.");
            return sb.ToString();
        }

        /// <summary>
        /// Retry of named files in one folder (no recursion). Returns one command per chunk so
        /// that no command line exceeds the limit. Add the log file with WithLog.
        /// </summary>
        public static List<string> BuildFileRetry(JobDefinition job, string sourceFolder, IEnumerable<string> fileNames)
        {
            var result = new List<string>();
            string head = Quote(sourceFolder) + " " + Quote(job.DestinationFor(sourceFolder));
            string flags = " " + BaseFlags(job.Options, recurse: false);
            var chunk = new StringBuilder();
            Action flush = () =>
            {
                if (chunk.Length == 0) return;
                result.Add(head + chunk + flags);
                chunk.Clear();
            };
            foreach (var name in fileNames)
            {
                string q = " " + Quote(name);
                if (head.Length + chunk.Length + q.Length + flags.Length + 600 > MaxArgumentLength) flush();
                chunk.Append(q);
            }
            flush();
            return result;
        }

        public static string WithLog(string command, string logPath) => command + " /UNILOG+:" + Quote(logPath);

        /// <summary>
        /// Profile-level exclusions (AppData by default) as full paths, so that a folder that merely
        /// has the same name somewhere inside Documents is still copied.
        /// </summary>
        public static IEnumerable<string> ProfileExclusions(string folder, IEnumerable<string> names, ProfileInfo profiles)
        {
            if (profiles == null || string.IsNullOrEmpty(profiles.ProfilesDirectory)) yield break;
            foreach (var profile in profiles.ProfileFolders)
            {
                // the profile is the folder itself or lies beneath it
                if (!PathUtil.IsSameOrUnder(profile, folder)) continue;
                foreach (var n in names.Where(n => !string.IsNullOrWhiteSpace(n)))
                    yield return Path.Combine(profile, n);
            }
        }

        /// <summary>
        /// Quotes a path or pattern for Robocopy. A trailing backslash before the closing quote
        /// would escape the quote, so it is removed; a drive root keeps it and is doubled.
        /// </summary>
        public static string Quote(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (s.IndexOf('"') >= 0) throw new ArgumentException("Path contains a quote: " + s);
            string t = s.TrimEnd('\\');
            if (t.Length == 2 && t[1] == ':') return "\"" + t + "\\\\\"";   // "C:\\" -> C:\
            return "\"" + t + "\"";
        }
    }
}
