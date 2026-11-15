using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace DataTransfer.Core
{
    /// <summary>
    /// Saves the job beside the exe, or under %ProgramData% when that folder is read-only.
    /// The file never contains a password.
    /// </summary>
    public sealed class JobStore
    {
        public const string FileName = "DataTransferJob.json";
        public string Directory { get; }
        public string JobFile => Path.Combine(Directory, FileName);
        public string LogDirectory => Path.Combine(Directory, "Logs");

        public JobStore(string directory) { Directory = directory; }

        /// <summary>First writable of: preferred folder (the exe's), %ProgramData%\DataTransferTool.</summary>
        public static JobStore Resolve(string preferredDirectory, string fallbackDirectory = null)
        {
            fallbackDirectory = fallbackDirectory ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DataTransferTool");
            // an existing job file wins, wherever it is
            foreach (var d in new[] { preferredDirectory, fallbackDirectory })
                if (File.Exists(Path.Combine(d, FileName)) && IsWritable(d)) return new JobStore(d);
            if (IsWritable(preferredDirectory)) return new JobStore(preferredDirectory);
            System.IO.Directory.CreateDirectory(fallbackDirectory);
            return new JobStore(fallbackDirectory);
        }

        public static bool IsWritable(string directory)
        {
            try
            {
                if (!System.IO.Directory.Exists(directory)) return false;
                string probe = Path.Combine(directory, ".dtt_write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        static DataContractJsonSerializer Serializer() => new DataContractJsonSerializer(typeof(JobDefinition),
            new DataContractJsonSerializerSettings { DateTimeFormat = new DateTimeFormat("o"), UseSimpleDictionaryFormat = true });

        public void Save(JobDefinition job)
        {
            System.IO.Directory.CreateDirectory(Directory);
            string tmp = JobFile + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, false, true))
                Serializer().WriteObject(w, job);
            // replace in one step so a crash never leaves a half-written job file
            if (File.Exists(JobFile)) File.Replace(tmp, JobFile, null);
            else File.Move(tmp, JobFile);
        }

        public JobDefinition Load()
        {
            if (!File.Exists(JobFile)) return null;
            try
            {
                using (var fs = File.OpenRead(JobFile))
                    return (JobDefinition)Serializer().ReadObject(fs);
            }
            catch (SerializationException) { return null; }
            catch (IOException) { return null; }
        }

        public void Delete()
        {
            if (File.Exists(JobFile)) File.Delete(JobFile);
        }
    }
}
