using System;
using System.Collections.Generic;

namespace UsnWatcher.Core
{
    /// <summary>
    /// A clean, managed representation of a single USN Journal record.
    /// This is what consumers of the library work with — no unsafe code, no structs.
    /// </summary>
    public sealed class UsnRecord
    {
        /// <summary>The monotonically increasing sequence number. Store this as your cursor.</summary>
        public long Usn { get; init; }

        /// <summary>When the change was recorded by the kernel.</summary>
        public DateTime Timestamp { get; init; }

        /// <summary>
        /// Unique identifier for the file/directory on this volume.
        /// Stable for the life of the file. Changes if the file is deleted and recreated.
        /// Equivalent to an inode number.
        /// </summary>
        public ulong FileReferenceNumber { get; init; }

        /// <summary>Reference number of the parent directory. Use to build full paths.</summary>
        public ulong ParentFileReferenceNumber { get; init; }

        /// <summary>
        /// Just the filename, e.g. "Program.cs".
        /// Does NOT include the path. See FullPath for the resolved path.
        /// </summary>
        public string FileName { get; init; } = string.Empty;

        /// <summary>
        /// Full path if resolved, e.g. "C:\Users\dev\project\Program.cs".
        /// May be null if the path resolver hasn't seen the parent yet.
        /// </summary>
        public string? FullPath { get; set; }

        /// <summary>Human-readable list of what changed, e.g. ["DATA_OVERWRITE", "CLOSE"]</summary>
        public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

        /// <summary>Raw bitmask from the kernel, for advanced filtering.</summary>
        public uint ReasonRaw { get; init; }

        /// <summary>True if this record is for a directory, false for a file.</summary>
        public bool IsDirectory { get; init; }

        /// <summary>Raw file attributes bitmask (FILE_ATTRIBUTE_*).</summary>
        public uint FileAttributes { get; init; }

        // ── Convenience Properties ───────────────────────────────────────────────

        public bool IsClose       => (ReasonRaw & (uint)NativeApi.UsnReason.Close) != 0;
        public bool IsCreate      => (ReasonRaw & (uint)NativeApi.UsnReason.FileCreate) != 0;
        public bool IsDelete      => (ReasonRaw & (uint)NativeApi.UsnReason.FileDelete) != 0;
        public bool IsRename      => (ReasonRaw & (uint)(NativeApi.UsnReason.RenameOldName | NativeApi.UsnReason.RenameNewName)) != 0;
        public bool IsDataChange  => (ReasonRaw & (uint)(NativeApi.UsnReason.DataOverwrite | NativeApi.UsnReason.DataExtend | NativeApi.UsnReason.DataTruncation)) != 0;

        public string? Extension => System.IO.Path.GetExtension(FileName);

        /// <summary>When a rename occurs this stores the previous full path (if known).</summary>
        public string? OldPath { get; set; }

        /// <summary>The newly computed full path for rename events.</summary>
        public string? NewPath { get; set; }

        public override string ToString() =>
            $"[{Usn}] {Timestamp:HH:mm:ss.fff} {string.Join("|", Reasons),-30} {FullPath ?? FileName}";
    }
}
