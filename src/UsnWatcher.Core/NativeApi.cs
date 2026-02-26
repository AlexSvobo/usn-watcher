using System;
using System.Runtime.InteropServices;

namespace UsnWatcher.Core
{
    /// <summary>
    /// All Win32 P/Invoke declarations needed to read the NTFS USN Journal.
    /// This is the ugliest part of the project — everything above this layer is clean C#.
    /// </summary>
    internal static class NativeApi
    {
        // ── File Access Flags ───────────────────────────────────────────────────────
        internal const uint GENERIC_READ      = 0x80000000;
        internal const uint FILE_SHARE_READ   = 0x00000001;
        internal const uint FILE_SHARE_WRITE  = 0x00000002;
        internal const uint OPEN_EXISTING     = 3;
        internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        // ── IOCTL Control Codes ─────────────────────────────────────────────────────
        internal const uint FSCTL_QUERY_USN_JOURNAL  = 0x000900F4;
        internal const uint FSCTL_READ_USN_JOURNAL   = 0x000900BB;
        internal const uint FSCTL_CREATE_USN_JOURNAL = 0x000900E7;
        internal const uint FSCTL_ENUM_USN_DATA       = 0x000900B3; // Used for startup scan

        // MFT enumeration input struct for FSCTL_ENUM_USN_DATA
        [StructLayout(LayoutKind.Sequential)]
        internal struct MFT_ENUM_DATA
        {
            public ulong StartFileReferenceNumber;
            public long LowUsn;
            public long HighUsn;
        }

        // ── Win32 Error Codes ───────────────────────────────────────────────────────
        internal const int ERROR_JOURNAL_NOT_ACTIVE    = 0x49B;   // Journal is disabled
        internal const int ERROR_JOURNAL_ENTRY_DELETED = 0x49D;   // Your cursor USN was overwritten
        internal const int ERROR_HANDLE_EOF            = 38;       // No more data right now (non-blocking read)

        // ── USN Reason Flags ────────────────────────────────────────────────────────
        [Flags]
        internal enum UsnReason : uint
        {
            DataOverwrite           = 0x00000001,
            DataExtend              = 0x00000002,
            DataTruncation          = 0x00000004,
            NamedDataOverwrite      = 0x00000010,
            NamedDataExtend         = 0x00000020,
            NamedDataTruncation     = 0x00000040,
            FileCreate              = 0x00000100,
            FileDelete              = 0x00000200,
            EaChange                = 0x00000400,
            SecurityChange          = 0x00000800,
            RenameOldName           = 0x00001000,
            RenameNewName           = 0x00002000,
            IndexableChange         = 0x00004000,
            BasicInfoChange         = 0x00008000,
            HardLinkChange          = 0x00010000,
            CompressionChange       = 0x00020000,
            EncryptionChange        = 0x00040000,
            ObjectIdChange          = 0x00080000,
            ReparsePointChange      = 0x00100000,
            StreamChange            = 0x00200000,
            TransactedChange        = 0x00400000,
            IntegrityChange         = 0x00800000,
            Close                   = 0x80000000, // ← Most useful: file handle closed
        }

        // ── Structs ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returned by FSCTL_QUERY_USN_JOURNAL.
        /// Contains journal metadata and the current tail USN.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct USN_JOURNAL_DATA
        {
            public ulong UsnJournalID;   // Unique ID — changes if journal is recreated
            public long  FirstUsn;       // Oldest USN in the journal (it's circular)
            public long  NextUsn;        // Next USN to be written — your live "tail"
            public long  LowestValidUsn;
            public long  MaxUsn;
            public ulong MaximumSize;    // Max journal size in bytes
            public ulong AllocationDelta;
        }

        /// <summary>
        /// Input to FSCTL_READ_USN_JOURNAL.
        /// Set StartUsn to your cursor. Set ReasonMask to filter what you get back.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct READ_USN_JOURNAL_DATA
        {
            public long  StartUsn;          // Read from here. Update after each call.
            public uint  ReasonMask;         // Bitmask of UsnReason flags. 0xFFFFFFFF = everything.
            public uint  ReturnOnlyOnClose;  // 1 = only return records with CLOSE flag set
            public ulong Timeout;            // 0 = non-blocking. >0 = wait up to N 100ns intervals.
            public ulong BytesToWaitFor;     // Min bytes to accumulate before returning
            public ulong UsnJournalID;       // Must match the journal's current ID
        }

        /// <summary>
        /// A single USN journal record. VARIABLE LENGTH — do NOT use sizeof() to walk a buffer.
        /// Always use record.RecordLength to advance to the next record.
        ///
        /// Layout:
        ///   [fixed header: 60 bytes]
        ///   [FileName: FileNameLength bytes of UTF-16 LE, NO null terminator]
        ///   [padding to 8-byte alignment]
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct USN_RECORD
        {
            public uint   RecordLength;              // Total size including filename and padding
            public ushort MajorVersion;              // Should be 2
            public ushort MinorVersion;              // Should be 0
            public ulong  FileReferenceNumber;       // Unique file ID (like an inode number)
            public ulong  ParentFileReferenceNumber; // Parent directory's reference number
            public long   Usn;                       // The sequence number
            public long   TimeStamp;                 // Windows FILETIME (100ns intervals since 1601-01-01)
            public uint   Reason;                    // Bitmask of what happened (UsnReason flags)
            public uint   SourceInfo;                // Usually 0
            public uint   SecurityId;
            public uint   FileAttributes;            // FILE_ATTRIBUTE_* flags
            public ushort FileNameLength;            // In BYTES (divide by 2 for char count)
            public ushort FileNameOffset;            // Offset in bytes from start of record to FileName
            // FileName follows here as UTF-16 LE — use Marshal to read it
        }

        // ── P/Invoke Declarations ───────────────────────────────────────────────────

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        internal static extern IntPtr CreateFile(
            string lpFileName,
            uint   dwDesiredAccess,
            uint   dwShareMode,
            IntPtr lpSecurityAttributes,
            uint   dwCreationDisposition,
            uint   dwFlagsAndAttributes,
            IntPtr hTemplateFile
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// The main syscall for all journal operations.
        /// Used with FSCTL_QUERY_USN_JOURNAL and FSCTL_READ_USN_JOURNAL.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint   dwIoControlCode,
            IntPtr lpInBuffer,
            uint   nInBufferSize,
            IntPtr lpOutBuffer,
            uint   nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped
        );

        // Overload for fixed struct input (QUERY)
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint   dwIoControlCode,
            IntPtr lpInBuffer,
            uint   nInBufferSize,
            out USN_JOURNAL_DATA lpOutBuffer,
            uint   nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped
        );

        // ── Helpers ─────────────────────────────────────────────────────────────────

        internal static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        /// <summary>Throws a Win32Exception with the last error code if handle is invalid.</summary>
        internal static void CheckHandle(IntPtr handle, string context)
        {
            if (handle == INVALID_HANDLE_VALUE || handle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"{context} failed with Win32 error {err}: {GetWin32ErrorMessage(err)}"
                );
            }
        }

        internal static string GetWin32ErrorMessage(int errorCode) =>
            errorCode switch
            {
                5       => "Access Denied — run as Administrator",
                0x49B   => "Journal not active — enable it first with FSCTL_CREATE_USN_JOURNAL",
                0x49D   => "Journal entry deleted — cursor USN was overwritten, journal wrapped",
                38      => "No more data (EOF) — poll again",
                _       => new System.ComponentModel.Win32Exception(errorCode).Message
            };
    }
}
