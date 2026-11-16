using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace DataTransfer.Core
{
    public sealed class UnhideResult
    {
        /// <summary>Files and folders whose Hidden or System attribute was removed.</summary>
        public long Cleared;
        /// <summary>Items that are still hidden because the attribute could not be changed.</summary>
        public long Failed;
        public string FirstError;
        public bool Cancelled;
    }

    /// <summary>
    /// Makes copied files and folders visible at the destination (v1.0.3, owner 2026-11-15).
    /// Robocopy gives a destination folder the attributes of its source folder, so a copy of a
    /// whole drive ("C:\") arrives Hidden + System like the drive root, and Explorer does not show
    /// it even with "Hidden items" switched on. /A-:SH only covers files; folders, and anything
    /// already hidden from an earlier run, are cleared here. Only attributes at the destination
    /// are changed, never anything at the source.
    /// </summary>
    public static class Unhide
    {
        const uint AttrHidden = 0x2, AttrSystem = 0x4, AttrDirectory = 0x10, AttrReparsePoint = 0x400, Invalid = 0xFFFFFFFF;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetFileAttributesW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetFileAttributesW(string path, uint attributes);

        /// <summary>Clears Hidden and System on one file or folder. True when it is visible afterwards.</summary>
        public static bool Clear(string path, UnhideResult result = null)
        {
            uint a = GetFileAttributesW(PathUtil.Long(path));
            if (a == Invalid) return false;
            return Clear(path, a, result);
        }

        static bool Clear(string path, uint attributes, UnhideResult result)
        {
            if ((attributes & (AttrHidden | AttrSystem)) == 0) return true;
            uint wanted = attributes & ~(AttrHidden | AttrSystem);
            if ((wanted & ~AttrDirectory) == 0 && (attributes & AttrDirectory) == 0) wanted = 0x80; // FILE_ATTRIBUTE_NORMAL: a file needs at least one
            if (SetFileAttributesW(PathUtil.Long(path), wanted))
            {
                if (result != null) result.Cleared++;
                return true;
            }
            if (result != null)
            {
                result.Failed++;
                if (result.FirstError == null) result.FirstError = $"{path}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            }
            return false;
        }

        /// <summary>
        /// The folder itself and every folder above it up to, but not including, <paramref name="stopAt"/>
        /// (the destination the technician chose). Cheap; done even when a copy is cancelled.
        /// </summary>
        public static UnhideResult ClearChain(string folder, string stopAt)
        {
            var r = new UnhideResult();
            string p = PathUtil.Trim(folder), stop = PathUtil.Trim(stopAt);
            while (!string.IsNullOrEmpty(p) && PathUtil.IsSameOrUnder(p, stop) && !string.Equals(p, stop, StringComparison.OrdinalIgnoreCase))
            {
                Clear(p, r);
                p = System.IO.Path.GetDirectoryName(p);
            }
            return r;
        }

        /// <summary>The folder and everything below it. Junctions are cleared but not entered.</summary>
        public static UnhideResult ClearTree(string folder, CancellationToken ct, UnhideResult result = null)
        {
            var r = result ?? new UnhideResult();
            IntPtr buffer = Marshal.AllocHGlobal(SourceWalker.BufferSize);
            try
            {
                Clear(folder, r);
                var stack = new Stack<string>();
                stack.Push(PathUtil.Trim(folder));
                while (stack.Count > 0)
                {
                    if (ct.IsCancellationRequested) { r.Cancelled = true; return r; }
                    string dir = stack.Pop();
                    SourceWalker.Enumerate(dir, buffer, err => { }, (name, attrs, size, tag, written) =>
                    {
                        string full = dir.EndsWith("\\") ? dir + name : dir + "\\" + name;
                        Clear(full, attrs, r);
                        if ((attrs & AttrDirectory) != 0 && (attrs & AttrReparsePoint) == 0) stack.Push(full);
                    });
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return r;
        }
    }
}
