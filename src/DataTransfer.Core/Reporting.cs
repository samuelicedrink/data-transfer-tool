using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DataTransfer.Core
{
    /// <summary>The failure list as CSV, for the report screen and the log folder. Never contains credentials.</summary>
    public static class FailureCsv
    {
        public static readonly string[] Header =
            { "Source path", "Destination path", "Type", "Reason", "Error code", "Windows message", "Size (bytes)", "Attempts", "Time", "Log file", "Log line" };

        public static string Reason(FailureCategory c)
        {
            switch (c)
            {
                case FailureCategory.Locked: return "File was open in another program";
                case FailureCategory.AccessDenied: return "Access denied";
                case FailureCategory.NotFound: return "Removed or renamed during the copy";
                case FailureCategory.PathTooLong: return "Path too long for the destination";
                case FailureCategory.DiskFull: return "Destination full";
                case FailureCategory.DestinationUnavailable: return "Destination unreachable";
                default: return "Other error";
            }
        }

        public static void Write(JobDefinition job, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", Header.Select(Cell)));
            foreach (var f in job.AllFailures().OrderBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                string dest;
                try { dest = job.DestinationFor(f.SourcePath); } catch (ArgumentException) { dest = ""; }
                sb.AppendLine(string.Join(",", new[]
                {
                    f.SourcePath, dest, f.IsDirectory ? "Folder" : "File", Reason(f.Category), f.ErrorCode.ToString(),
                    f.Message ?? "", f.IsDirectory ? "" : f.Size.ToString(), f.Attempts.ToString(), f.Time ?? "",
                    f.LogFile ?? "", f.LogLine > 0 ? f.LogLine.ToString() : ""
                }.Select(Cell)));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            // UTF-8 with BOM so Excel shows accented and non-Latin file names correctly
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        /// <summary>Quotes a cell; a leading =, +, - or @ is neutralised so Excel does not run it as a formula.</summary>
        public static string Cell(string value)
        {
            value = value ?? "";
            if (value.Length > 0 && "=+-@".IndexOf(value[0]) >= 0 && !IsNumber(value)) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        static bool IsNumber(string s) => long.TryParse(s, out _);
    }

    /// <summary>
    /// Destination credentials for resume, encrypted with DPAPI (CurrentUser scope): only the
    /// Windows account that saved them, on this PC, can read them back. Saved only when the
    /// technician ticks "Save credentials for resume". Never written in plain text or to logs.
    /// </summary>
    public sealed class CredentialStore
    {
        public const string FileName = "DataTransferJob.credentials";
        readonly string file;

        public CredentialStore(string directory) { file = Path.Combine(directory, FileName); }

        public string FilePath => file;
        public bool Exists => File.Exists(file);

        /// <param name="jobId">Bound into the encryption, so the file only opens for the same job.</param>
        public void Save(string jobId, string userName, string password)
        {
            byte[] plain = Encoding.UTF8.GetBytes((userName ?? "") + "\0" + (password ?? ""));
            try
            {
                byte[] sealedBytes = ProtectedData.Protect(plain, Entropy(jobId), DataProtectionScope.CurrentUser);
                string tmp = file + ".tmp";
                File.WriteAllBytes(tmp, sealedBytes);
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        /// <summary>False when there is nothing saved, or it belongs to another job, user or PC.</summary>
        public bool TryLoad(string jobId, out string userName, out string password)
        {
            userName = password = null;
            if (!File.Exists(file)) return false;
            byte[] plain = null;
            try
            {
                plain = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy(jobId), DataProtectionScope.CurrentUser);
                string s = Encoding.UTF8.GetString(plain);
                int i = s.IndexOf('\0');
                if (i < 0) return false;
                userName = s.Substring(0, i);
                password = s.Substring(i + 1);
                return true;
            }
            catch (CryptographicException) { return false; }
            catch (IOException) { return false; }
            finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
        }

        public void Delete()
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        static byte[] Entropy(string jobId) => Encoding.UTF8.GetBytes("DataTransferTool/" + (jobId ?? ""));
    }
}
