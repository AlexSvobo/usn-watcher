using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace UsnWatcher.Core
{
    /// <summary>
    /// Reads records from the NTFS USN Journal on a given volume.
    ///
    /// Typical usage:
    ///   using var volume = VolumeHandle.Open("C");
    ///   var reader = new UsnJournalReader(volume);
    ///   reader.Initialize();
    ///
    ///   // Store reader.NextUsn as your cursor. On restart, pass it to SetCursor().
    ///   foreach (var record in reader.ReadBatch())
    ///       Console.WriteLine(record);
    /// </summary>
    public sealed class UsnJournalReader
    {
        private readonly VolumeHandle _volume;

        // Journal metadata, populated by Initialize()
        private ulong _journalId;
        private long  _nextUsn;

        // Read buffer. 64KB is a good balance — fits ~1000 records per call.
        private const int BUFFER_SIZE = 65536;

        public long NextUsn => _nextUsn;
        public ulong JournalId => _journalId;

        /// <summary>True after Initialize() succeeds.</summary>
        public bool IsReady { get; private set; }

        public UsnJournalReader(VolumeHandle volume)
        {
            _volume = volume ?? throw new ArgumentNullException(nameof(volume));
        }

        /// <summary>
        /// Queries journal metadata and sets the cursor to the current tail (live events only).
        /// Call once before ReadBatch().
        /// </summary>
        public void Initialize()
        {
            QueryJournalData(out var data);
            _journalId = data.UsnJournalID;
            _nextUsn   = data.NextUsn;
            IsReady    = true;

            Console.WriteLine($"[USN] Journal ID: 0x{_journalId:X16}");
            Console.WriteLine($"[USN] First USN:  {data.FirstUsn}");
            Console.WriteLine($"[USN] Next USN:   {data.NextUsn}");
            Console.WriteLine($"[USN] Max Size:   {data.MaximumSize / (1024 * 1024)} MB");
        }

        /// <summary>
        /// Resumes from a previously stored cursor. Use this on restart.
        /// If the cursor is older than FirstUsn, events were missed — this method returns false.
        /// </summary>
        public bool SetCursor(long storedUsn)
        {
            QueryJournalData(out var data);
            _journalId = data.UsnJournalID;

            if (storedUsn < data.FirstUsn)
            {
                Console.Error.WriteLine($"[USN] WARNING: Stored USN {storedUsn} is older than journal FirstUsn {data.FirstUsn}. Events were missed.");
                _nextUsn = data.FirstUsn; // Start from oldest available
                IsReady = true;
                return false; // Caller should handle the "missed events" case
            }

            _nextUsn = storedUsn;
            IsReady = true;
            return true;
        }

        /// <summary>
        /// Reads a batch of available records since the last read.
        /// Returns an empty enumerable if no new records are available.
        ///
        /// Call this in a loop:
        ///   while (true) {
        ///       foreach (var r in reader.ReadBatch()) Process(r);
        ///       await Task.Delay(500); // Poll interval
        ///   }
        /// </summary>
        public IEnumerable<UsnRecord> ReadBatch(uint reasonMask = 0xFFFFFFFF)
        {
            if (!IsReady)
                throw new InvalidOperationException("Call Initialize() or SetCursor() first.");

            IntPtr buffer = Marshal.AllocHGlobal(BUFFER_SIZE);
            try
            {
                // ── Build the input struct ──────────────────────────────────────────
                var readData = new NativeApi.READ_USN_JOURNAL_DATA
                {
                    StartUsn          = _nextUsn,
                    ReasonMask        = reasonMask,
                    ReturnOnlyOnClose = 0,   // Get all events, not just on-close
                    Timeout           = 0,   // Non-blocking — return immediately if no data
                    BytesToWaitFor    = 0,
                    UsnJournalID      = _journalId
                };

                uint readDataSize = (uint)Marshal.SizeOf<NativeApi.READ_USN_JOURNAL_DATA>();
                IntPtr readDataPtr = Marshal.AllocHGlobal((int)readDataSize);
                try
                {
                    Marshal.StructureToPtr(readData, readDataPtr, false);

                    bool success = NativeApi.DeviceIoControl(
                        _volume.Handle,
                        NativeApi.FSCTL_READ_USN_JOURNAL,
                        readDataPtr, readDataSize,
                        buffer, BUFFER_SIZE,
                        out uint bytesReturned,
                        IntPtr.Zero
                    );

                    if (!success)
                    {
                        int err = Marshal.GetLastWin32Error();

                        if (err == NativeApi.ERROR_HANDLE_EOF)
                            yield break; // No new records, try again later

                        if (err == NativeApi.ERROR_JOURNAL_ENTRY_DELETED)
                        {
                            // Journal wrapped — we missed events
                            // Reset to current tail
                            QueryJournalData(out var freshData);
                            _nextUsn = freshData.NextUsn;
                            throw new UsnJournalWrappedException(
                                "USN Journal wrapped. Events were missed. Cursor reset to current tail."
                            );
                        }

                        throw new InvalidOperationException(
                            $"FSCTL_READ_USN_JOURNAL failed: {NativeApi.GetWin32ErrorMessage(err)}"
                        );
                    }

                    // ── Parse the output buffer ─────────────────────────────────────
                    // First 8 bytes = next USN to read from
                    // Remaining bytes = packed USN_RECORD structs
                    if (bytesReturned < 8) yield break;

                    _nextUsn = Marshal.ReadInt64(buffer, 0); // Update cursor

                    int offset = 8; // Skip the 8-byte next-USN prefix
                    while (offset < bytesReturned)
                    {
                        var record = ParseRecord(buffer + offset);
                        if (record != null) yield return record;

                        // Advance by RecordLength — it's the first field of USN_RECORD
                        uint recordLength = (uint)Marshal.ReadInt32(buffer + offset);
                        if (recordLength < 60 || recordLength > BUFFER_SIZE)
                            break; // Corrupt record — bail out

                        offset += (int)recordLength;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(readDataPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // ── Private Helpers ─────────────────────────────────────────────────────────

        private void QueryJournalData(out NativeApi.USN_JOURNAL_DATA data)
        {
            bool success = NativeApi.DeviceIoControl(
                _volume.Handle,
                NativeApi.FSCTL_QUERY_USN_JOURNAL,
                IntPtr.Zero, 0,
                out data,
                (uint)Marshal.SizeOf<NativeApi.USN_JOURNAL_DATA>(),
                out _,
                IntPtr.Zero
            );

            if (!success)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"FSCTL_QUERY_USN_JOURNAL failed: {NativeApi.GetWin32ErrorMessage(err)}"
                );
            }
        }

        private static UsnRecord? ParseRecord(IntPtr recordPtr)
        {
            var raw = Marshal.PtrToStructure<NativeApi.USN_RECORD>(recordPtr);

            if (raw.MajorVersion != 2) return null; // We only handle v2
            if (raw.RecordLength < 60) return null;  // Sanity check

            // Decode the filename (UTF-16 LE, no null terminator)
            // FileNameOffset is relative to the START of the record
            string fileName = string.Empty;
            if (raw.FileNameLength > 0 && raw.FileNameOffset >= 60)
            {
                byte[] nameBytes = new byte[raw.FileNameLength];
                Marshal.Copy(recordPtr + raw.FileNameOffset, nameBytes, 0, raw.FileNameLength);
                fileName = Encoding.Unicode.GetString(nameBytes);
            }

            // Decode timestamp from FILETIME
            var timestamp = DateTime.FromFileTimeUtc(raw.TimeStamp).ToLocalTime();

            // Decode reason flags
            var reasons = DecodeReasons(raw.Reason);

            // Check if directory
            const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
            bool isDirectory = (raw.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;

            return new UsnRecord
            {
                Usn                      = raw.Usn,
                Timestamp                = timestamp,
                FileReferenceNumber      = raw.FileReferenceNumber,
                ParentFileReferenceNumber= raw.ParentFileReferenceNumber,
                FileName                 = fileName,
                Reasons                  = reasons,
                ReasonRaw                = raw.Reason,
                IsDirectory              = isDirectory,
                FileAttributes           = raw.FileAttributes,
            };
        }

        private static List<string> DecodeReasons(uint reason)
        {
            var result = new List<string>();
            foreach (NativeApi.UsnReason flag in Enum.GetValues<NativeApi.UsnReason>())
            {
                if ((reason & (uint)flag) != 0)
                    result.Add(flag.ToString().ToUpperInvariant());
            }
            return result;
        }
    }

    public sealed class UsnJournalWrappedException : Exception
    {
        public UsnJournalWrappedException(string message) : base(message) { }
    }
}
