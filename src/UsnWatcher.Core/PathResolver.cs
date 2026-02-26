using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace UsnWatcher.Core
{
    /// <summary>
    /// Maintains a simple in-memory map of FileReferenceNumber -> full path.
    /// MVP behavior:
    ///  - Populate(cache) by recursively walking a volume root with Directory APIs
    ///  - Resolve(UsnRecord) sets record.FullPath when possible
    ///  - Update cache on create/rename/delete records
    ///
    /// This is intentionally simple and tolerant of IO errors.
    /// </summary>
    public sealed class PathResolver
    {
        private readonly Dictionary<ulong, string> _map = new();
        private readonly object _lock = new();
        private readonly Dictionary<ulong, string> _pendingRenames = new();

        public PathResolver() { }

        /// <summary>
        /// Attempt to load a serialized FRN->path cache from AppData.
        /// Returns true if a fresh cache (younger than 24 hours) was loaded.
        /// </summary>
        public bool TryLoadCache(string root, Action<string>? progress = null)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentNullException(nameof(root));
            char drive = char.ToUpperInvariant(root[0]);

            try
            {
                var app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var dir = Path.Combine(app, "usn-watcher");
                var file = Path.Combine(dir, $"cache-{drive}.bin");

                if (!File.Exists(file)) return false;

                var last = File.GetLastWriteTimeUtc(file);
                if ((DateTime.UtcNow - last) > TimeSpan.FromHours(24))
                {
                    progress?.Invoke($"Cache file is older than 24h ({last:o}), skipping");
                    return false;
                }

                using var fs = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);
                int count = br.ReadInt32();

                var loaded = new Dictionary<ulong, string>();
                for (int i = 0; i < count; i++)
                {
                    var key = br.ReadUInt64();
                    var value = br.ReadString();
                    loaded[key] = value;
                }

                lock (_lock)
                {
                    _map.Clear();
                    foreach (var kv in loaded) _map[kv.Key] = kv.Value;
                }

                progress?.Invoke($"Loaded cache: {_map.Count:N0} entries from {file}");
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    // If the cache appears corrupt, remove it and fall back to full scan
                    var app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    var dir = Path.Combine(app, "usn-watcher");
                    var file = Path.Combine(dir, $"cache-{char.ToUpperInvariant(root[0])}.bin");
                    if (File.Exists(file)) File.Delete(file);
                }
                catch { }

                progress?.Invoke($"Failed to load cache: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Persist the current FRN->path cache to AppData (best-effort).
        /// </summary>
        public void SaveCache(string root, Action<string>? progress = null)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentNullException(nameof(root));
            char drive = char.ToUpperInvariant(root[0]);

            try
            {
                var app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var dir = Path.Combine(app, "usn-watcher");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"cache-{drive}.bin");

                // Snapshot
                KeyValuePair<ulong, string>[] entries;
                lock (_lock)
                {
                    entries = new KeyValuePair<ulong, string>[_map.Count];
                    int i = 0;
                    foreach (var kv in _map)
                    {
                        entries[i++] = kv;
                    }
                }

                using var fs = File.Open(file, FileMode.Create, FileAccess.Write, FileShare.None);
                using var bw = new BinaryWriter(fs);
                bw.Write(entries.Length);
                foreach (var kv in entries)
                {
                    bw.Write(kv.Key);
                    bw.Write(kv.Value ?? string.Empty);
                }

                progress?.Invoke($"Saved cache: {entries.Length:N0} entries to {file}");
            }
            catch (Exception ex)
            {
                progress?.Invoke($"Failed to save cache: {ex.Message}");
            }
        }

        /// <summary>Read-only snapshot of the current cache.</summary>
        public IReadOnlyDictionary<ulong, string> Cache => _map;

        /// <summary>
        /// Populate the cache by walking the filesystem starting at <paramref name="root"/>.
        /// This is best-effort: permission errors are ignored.
        /// </summary>
        public void Populate(string root, Action<string>? progress = null)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentNullException(nameof(root));

            // Expect root like "C:\\" or "C:\". Extract drive letter.
            char drive = char.ToUpperInvariant(root[0]);
            string volumePath = $"\\\\.\\{drive}:";

            const int BUFFER_SIZE = 65536;
            IntPtr buffer = Marshal.AllocHGlobal(BUFFER_SIZE);
            IntPtr inPtr = IntPtr.Zero;
            IntPtr volHandle = IntPtr.Zero;
            try
            {
                volHandle = NativeApi.CreateFile(volumePath, NativeApi.GENERIC_READ,
                    NativeApi.FILE_SHARE_READ | NativeApi.FILE_SHARE_WRITE,
                    IntPtr.Zero, NativeApi.OPEN_EXISTING, 0, IntPtr.Zero);

                if (volHandle == NativeApi.INVALID_HANDLE_VALUE || volHandle == IntPtr.Zero)
                    throw new InvalidOperationException($"Failed to open volume {volumePath}");

                var mftInput = new NativeApi.MFT_ENUM_DATA
                {
                    StartFileReferenceNumber = 0,
                    LowUsn = 0,
                    HighUsn = long.MaxValue
                };

                uint inSize = (uint)Marshal.SizeOf<NativeApi.MFT_ENUM_DATA>();
                inPtr = Marshal.AllocHGlobal((int)inSize);
                Marshal.StructureToPtr(mftInput, inPtr, false);

                var entries = new Dictionary<ulong, (string fileName, ulong parent)>();

                while (true)
                {
                    bool ok = NativeApi.DeviceIoControl(
                        volHandle,
                        NativeApi.FSCTL_ENUM_USN_DATA,
                        inPtr, inSize,
                        buffer, BUFFER_SIZE,
                        out uint bytesReturned,
                        IntPtr.Zero
                    );

                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == NativeApi.ERROR_HANDLE_EOF)
                            break; // enumeration finished
                        throw new InvalidOperationException($"FSCTL_ENUM_USN_DATA failed: {NativeApi.GetWin32ErrorMessage(err)}");
                    }

                    if (bytesReturned < 8) break;

                    // First 8 bytes are the next StartFileReferenceNumber
                    ulong nextStart = (ulong)Marshal.ReadInt64(buffer, 0);
                    int offset = 8;

                    while (offset < bytesReturned)
                    {
                        var raw = Marshal.PtrToStructure<NativeApi.USN_RECORD>(buffer + offset);
                        if (raw.MajorVersion != 2) break;
                        if (raw.RecordLength < 60 || raw.RecordLength > BUFFER_SIZE) break;

                        string name = string.Empty;
                        if (raw.FileNameLength > 0 && raw.FileNameOffset >= 60)
                        {
                            byte[] nameBytes = new byte[raw.FileNameLength];
                            Marshal.Copy(buffer + offset + raw.FileNameOffset, nameBytes, 0, raw.FileNameLength);
                            name = System.Text.Encoding.Unicode.GetString(nameBytes);
                        }

                        entries[raw.FileReferenceNumber] = (name, raw.ParentFileReferenceNumber);

                        offset += (int)raw.RecordLength;
                    }

                    // Prepare next input (start FRN)
                    mftInput.StartFileReferenceNumber = nextStart;
                    Marshal.StructureToPtr(mftInput, inPtr, false);

                    if (progress != null && (entries.Count % 1000) == 0)
                        progress.Invoke($"Indexed {entries.Count:N0} MFT entries. NextStart: 0x{mftInput.StartFileReferenceNumber:X16}");
                }

                progress?.Invoke($"MFT scan complete: collected {entries.Count:N0} entries. Resolving paths...");

                // Resolve full paths by walking parent FRNs to the root (FRN 5)
                foreach (var kv in entries)
                {
                    var frn = kv.Key;
                    if (_map.ContainsKey(frn)) continue; // already resolved

                    var parts = new List<string>();
                    ulong cur = frn;
                    int guard = 0;
                    while (guard++ < 1024)
                    {
                        if (!entries.TryGetValue(cur, out var info)) break;
                        if (string.IsNullOrEmpty(info.fileName)) break;
                        parts.Add(info.fileName);
                        if (info.parent == 5 || info.parent == 0) { cur = info.parent; break; }
                        if (info.parent == cur) break;
                        cur = info.parent;
                    }

                    parts.Reverse();
                    if (parts.Count > 0)
                    {
                        string full = $"{drive}:\\" + string.Join("\\", parts);
                        lock (_lock)
                        {
                            _map[frn] = full;
                        }
                    }
                }

                progress?.Invoke($"Resolved paths: {_map.Count:N0} entries.");

                // After populate completes, try to reconcile any pending renames that failed earlier
                try
                {
                    int reconciled = 0;
                    lock (_lock)
                    {
                        var pendingKeys = new List<ulong>(_pendingRenames.Keys);
                        foreach (var pfrn in pendingKeys)
                        {
                            if (_map.ContainsKey(pfrn))
                            {
                                _pendingRenames.Remove(pfrn);
                                continue;
                            }

                            if (!entries.TryGetValue(pfrn, out var info)) continue;

                            // If parent is root or present in cache, synthesize
                            if (info.parent == 5 || info.parent == 0)
                            {
                                var full = $"{drive}:\\" + (string.IsNullOrEmpty(info.fileName) ? string.Empty : info.fileName);
                                _map[pfrn] = full;
                                _pendingRenames.Remove(pfrn);
                                reconciled++;
                                continue;
                            }

                            if (_map.TryGetValue(info.parent, out var parentPath))
                            {
                                var full = System.IO.Path.Combine(parentPath, info.fileName);
                                _map[pfrn] = full;
                                _pendingRenames.Remove(pfrn);
                                reconciled++;
                            }
                        }
                    }
                    progress?.Invoke($"Reconciled pending renames: {reconciled:N0} entries.");
                }
                catch
                {
                    // Best-effort: ignore reconciliation failures
                }
            }
            finally
            {
                if (inPtr != IntPtr.Zero) Marshal.FreeHGlobal(inPtr);
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (volHandle != IntPtr.Zero && volHandle != NativeApi.INVALID_HANDLE_VALUE)
                    NativeApi.CloseHandle(volHandle);
            }
        }

        /// <summary>Attempts to set record.FullPath using the cache. Returns true if set.</summary>
        public bool Resolve(UsnRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            lock (_lock)
            {
                if (_map.TryGetValue(record.FileReferenceNumber, out var path))
                {
                    record.FullPath = path;
                    return true;
                }

                // Try to synthesize from parent mapping + filename
                if (_map.TryGetValue(record.ParentFileReferenceNumber, out var parent))
                {
                    var candidate = System.IO.Path.Combine(parent, record.FileName ?? string.Empty);
                    record.FullPath = candidate;
                    // Add synthesized mapping for future
                    _map[record.FileReferenceNumber] = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Update the cache based on the incoming USN record. Handles create, delete, and rename-new-name.
        /// Call this when processing records to keep the cache in sync.
        /// </summary>
        public bool Update(UsnRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            lock (_lock)
            {
                // Check for explicit delete
                if (record.IsDelete)
                {
                    _map.Remove(record.FileReferenceNumber);
                    return true;
                }

                // RENAME_OLD_NAME — remember the old path for the FRN
                if (record.Reasons != null && record.Reasons.Contains("RENAMEOLDNAME"))
                {
                    // Try to find the current path for this FRN
                    if (_map.TryGetValue(record.FileReferenceNumber, out var existing))
                    {
                        _pendingRenames[record.FileReferenceNumber] = existing;
                        return true;
                    }

                    // As a fallback, synthesize from parent + filename
                    if (_map.TryGetValue(record.ParentFileReferenceNumber, out var parentForOld))
                    {
                        var synth = System.IO.Path.Combine(parentForOld, record.FileName ?? string.Empty);
                        _pendingRenames[record.FileReferenceNumber] = synth;
                        return true;
                    }

                    // Could not determine old path
                    Console.Error.WriteLine($"[PATH] Could not record old name for FRN 0x{record.FileReferenceNumber:X16}: path not in cache (RENAMEOLDNAME)");
                    return false;
                }

                // RENAME_NEW_NAME — update FRN -> new full path and attach old/new on record
                if (record.Reasons != null && record.Reasons.Contains("RENAMENEWNAME"))
                {
                    // Compute new path using parent if possible
                    if (_map.TryGetValue(record.ParentFileReferenceNumber, out var parent))
                    {
                        var full = System.IO.Path.Combine(parent, record.FileName ?? string.Empty);

                        // Retrieve any pending old path
                        if (_pendingRenames.TryGetValue(record.FileReferenceNumber, out var old))
                        {
                            record.OldPath = old;
                            _pendingRenames.Remove(record.FileReferenceNumber);
                        }

                        _map[record.FileReferenceNumber] = full;
                        record.FullPath = full;
                        record.NewPath = full;
                        return true;
                    }

                    // If parent missing, still attempt to use pending old and synthesize new without parent
                    if (_pendingRenames.TryGetValue(record.FileReferenceNumber, out var oldNoParent))
                    {
                        record.OldPath = oldNoParent;
                        _pendingRenames.Remove(record.FileReferenceNumber);
                        // best-effort new path = filename only
                        var best = record.FileName ?? string.Empty;
                        record.NewPath = best;
                        _map[record.FileReferenceNumber] = best;
                        return true;
                    }

                    Console.Error.WriteLine($"[PATH] Failed to update FRN 0x{record.FileReferenceNumber:X16}: parent FRN 0x{record.ParentFileReferenceNumber:X16} not in cache (RENAMENEWNAME)");
                    return false;
                }

                // FILE_CREATE — add mapping if we can synthesize a path
                if (record.IsCreate)
                {
                    if (_map.TryGetValue(record.ParentFileReferenceNumber, out var parent))
                    {
                        var full = System.IO.Path.Combine(parent, record.FileName ?? string.Empty);
                        _map[record.FileReferenceNumber] = full;
                        record.FullPath = full;
                        return true;
                    }
                }
                return false;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private bool TryAddPath(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return false;

                if (TryGetFileReferenceNumber(path, out var frn))
                {
                    // Normalize path
                    var full = System.IO.Path.GetFullPath(path);
                    if (!_map.ContainsKey(frn))
                    {
                        _map[frn] = full;
                        return true;
                    }
                }
            }
            catch
            {
                // Ignore entry failures
            }

            return false;
        }

        private static bool TryGetFileReferenceNumber(string path, out ulong fileReferenceNumber)
        {
            fileReferenceNumber = 0;
            IntPtr handle = IntPtr.Zero;
            try
            {
                // Open file or directory. Use backup semantics to open directories.
                uint access = NativeApi.GENERIC_READ;
                uint share = NativeApi.FILE_SHARE_READ | NativeApi.FILE_SHARE_WRITE;
                uint flags = NativeApi.FILE_FLAG_BACKUP_SEMANTICS;

                handle = NativeApi.CreateFile(path, access, share, IntPtr.Zero, NativeApi.OPEN_EXISTING, flags, IntPtr.Zero);
                if (handle == NativeApi.INVALID_HANDLE_VALUE || handle == IntPtr.Zero)
                    return false;

                if (!GetFileInformationByHandle(handle, out var info))
                    return false;

                fileReferenceNumber = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                return true;
            }
            finally
            {
                if (handle != IntPtr.Zero && handle != NativeApi.INVALID_HANDLE_VALUE)
                    NativeApi.CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(IntPtr hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public FILETIME CreationTime;
            public FILETIME LastAccessTime;
            public FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}
