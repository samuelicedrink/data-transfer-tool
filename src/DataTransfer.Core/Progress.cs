using System;
using System.Collections.Generic;

namespace DataTransfer.Core
{
    public enum EnginePhase { Starting, Scanning, Copying, PausedDestinationUnavailable, WaitingToRetryLocked, RetryingFailed, Finished }

    public sealed class ProgressSnapshot
    {
        public EnginePhase Phase;
        public int SourceIndex;          // 1-based
        public int SourceCount;
        public string CurrentSource;
        public string CurrentFolder;
        /// <summary>Files Robocopy has started on in this session (from the log).</summary>
        public long FilesProcessed;
        public long FilesFailed;
        /// <summary>Bytes written in this session, measured as the drop in destination free space.</summary>
        public long BytesCopied;
        /// <summary>Sum of the sizes of files started, from the log. Runs ahead of BytesCopied.</summary>
        public long BytesFromLog;
        public bool BytesFromFreeSpace;
        public long? TotalFiles;
        public long? TotalBytes;
        public double BytesPerSecond;
        public TimeSpan Elapsed;
        public TimeSpan? Eta;
        public string Note;

        public override string ToString()
        {
            string eta = Eta.HasValue ? Eta.Value.ToString(@"h\:mm\:ss") : "?";
            return $"{Phase} [{SourceIndex}/{SourceCount}] files {FilesProcessed}{(TotalFiles.HasValue ? "/" + TotalFiles : "")} failed {FilesFailed} " +
                   $"bytes {BytesCopied}{(TotalBytes.HasValue ? "/" + TotalBytes : "")} {BytesPerSecond / 1048576.0:0.0} MB/s eta {eta} | {CurrentFolder}";
        }
    }

    /// <summary>
    /// Transfer rate over a sliding window. Copies stall for seconds at a time (seen in M1), so the
    /// rate is an average over the window rather than the latest sample, and a pause resets it.
    /// </summary>
    public sealed class RateEstimator
    {
        readonly TimeSpan window;
        readonly Queue<KeyValuePair<TimeSpan, long>> samples = new Queue<KeyValuePair<TimeSpan, long>>();

        public RateEstimator(TimeSpan? window = null) { this.window = window ?? TimeSpan.FromSeconds(30); }

        public void Add(TimeSpan time, long bytes)
        {
            samples.Enqueue(new KeyValuePair<TimeSpan, long>(time, bytes));
            while (samples.Count > 2 && time - samples.Peek().Key > window) samples.Dequeue();
        }

        public void Reset() => samples.Clear();

        public double BytesPerSecond
        {
            get
            {
                if (samples.Count < 2) return 0;
                KeyValuePair<TimeSpan, long> first = samples.Peek(), last = default(KeyValuePair<TimeSpan, long>);
                foreach (var s in samples) last = s;
                double dt = (last.Key - first.Key).TotalSeconds;
                return dt <= 0 || last.Value <= first.Value ? 0 : (last.Value - first.Value) / dt;
            }
        }

        /// <summary>Null when there is no measurable rate yet or nothing is known about the total.</summary>
        public TimeSpan? Eta(long done, long? total)
        {
            double rate = BytesPerSecond;
            if (!total.HasValue || rate <= 0) return null;
            long left = total.Value - done;
            if (left <= 0) return TimeSpan.Zero;
            double seconds = left / rate;
            return seconds > 30 * 24 * 3600 ? (TimeSpan?)null : TimeSpan.FromSeconds(seconds);
        }
    }
}
