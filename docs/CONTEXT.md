# USN Watcher — Developer Context

> **READ THIS FIRST.** This file is the authoritative reference for contributors and maintainers working on this project.

---

## What This Project Is

**USN Watcher** is a Windows filesystem event streaming daemon built on the NTFS USN (Update Sequence Number) Journal — a low-level kernel feature that has existed since Windows 2000 but has almost no modern tooling built on top of it.

The USN Journal is a circular buffer maintained by the NTFS driver. Every file system change on an NTFS volume — every write, rename, delete, attribute change, security change — is logged as a `USN_RECORD` entry with a monotonically increasing sequence number. We read this journal in real time and expose it as a clean, programmable event stream.

**The analogy:** rsync optimized the network link. We are optimizing the local event loop — turning a kernel log that requires ugly `DeviceIoControl` calls into something a developer can subscribe to in one line.

---

## The Core Problem We Are Solving

Windows developers who want to watch for file changes currently have two options:

1. `FileSystemWatcher` (.NET) / `ReadDirectoryChangesW` (Win32) — watches a specific directory, misses events under load, requires a path upfront, has a buffer that overflows silently.
2. Third-party tools like Everything (voidtools) — not programmable, not embeddable, not an API.

The USN Journal solves all of these problems because:
- It is **volume-wide** — one journal per drive, covers everything
- It is **kernel-maintained** — you cannot miss events; they are written by the NTFS driver itself
- It is **persistent** — you can read events that happened while your daemon was offline, by storing the last-read USN
- It is **efficient** — one `DeviceIoControl` call can return thousands of events in a single batch

---

## Tech Stack

| Layer | Choice | Why |
|---|---|---|
| Language | C# (.NET 8) | Best P/Invoke ergonomics, async/await, runs on any Windows 10/11 IdeaPad with no extra install |
| Framework | .NET 8 Console / Worker Service | Zero overhead, runs as a Windows Service or standalone |
| API surface | Named pipe + stdout JSON | Simple, language-agnostic, easy to consume from any tool |
| Persistence | SQLite (optional) | Store last USN cursor so we survive restarts |
| Tests | xUnit | Standard .NET test framework |

---

## Project Structure

```
usn-watcher/
├── docs/CONTEXT.md        ← Developer context and contributor notes.
├── PLAN.md                ← Current build plan and milestone tracker
├── README.md              ← User-facing docs
├── usn-watcher.sln        ← Solution file
│
├── src/
│   ├── UsnWatcher.Core/   ← The kernel-facing layer. All Win32 P/Invoke lives here.
│   │   ├── NativeApi.cs           Raw DeviceIoControl declarations
│   │   ├── UsnJournalReader.cs    Main reader loop
│   │   ├── UsnRecord.cs           Managed record struct
│   │   └── VolumeHandle.cs        Safe volume handle management
│   │
│   ├── UsnWatcher.Stream/ ← The event stream / pub-sub layer
│   │   ├── EventBus.cs            In-process pub-sub
│   │   ├── FilterEngine.cs        Path/extension/reason filtering
│   │   └── JsonSerializer.cs      Serialize records to JSON
│   │
│   └── UsnWatcher.Host/   ← The runnable daemon
│       ├── Program.cs             Entry point
│       ├── WorkerService.cs       Background service loop
│       └── PipeServer.cs          Named pipe output
│
├── examples/
│   ├── watch_extensions.ps1       PowerShell: watch for .cs file changes
│   ├── watch_process_output.py    Python: consume the named pipe stream
│   └── webhook_trigger.sh         Bash: trigger webhook on file events
│
└── docs/
    ├── USN_JOURNAL_PRIMER.md      Deep dive on the Windows kernel API
    ├── ARCHITECTURE.md            Design decisions
    └── FILTER_QUERY_LANGUAGE.md   The filter DSL spec
```

---

## The Windows API — Everything You Need to Know

### Step 1: Open a Volume Handle

To read the USN Journal, you need a handle to the **volume** (e.g. `C:\`), not a file.

```csharp
// You MUST run as Administrator to open a volume handle
IntPtr handle = CreateFile(
    @"\\.\C:",           // Volume path — note the \\.\ prefix
    GENERIC_READ,
    FILE_SHARE_READ | FILE_SHARE_WRITE,
    IntPtr.Zero,
    OPEN_EXISTING,
    0,
    IntPtr.Zero
);
```

**Critical:** This requires the process to run as Administrator. Plan accordingly.

### Step 2: Query Journal Metadata

Before reading, get the journal ID and current USN (your "cursor").

```csharp
// FSCTL_QUERY_USN_JOURNAL = 0x000900f4
DeviceIoControl(
    volumeHandle,
    FSCTL_QUERY_USN_JOURNAL,
    IntPtr.Zero, 0,
    out USN_JOURNAL_DATA journalData,
    sizeof(USN_JOURNAL_DATA),
    out bytesReturned,
    IntPtr.Zero
);
```

The `USN_JOURNAL_DATA` struct gives you:
- `UsnJournalID` — unique ID for this journal instance (changes if the journal is recreated)
- `FirstUsn` — oldest USN available (journal is circular, old entries are overwritten)
- `NextUsn` — the next USN that will be written (your starting point for live tailing)

### Step 3: Read Records in a Loop

This is the core loop. `FSCTL_READ_USN_JOURNAL` is the money call.

```csharp
var readData = new READ_USN_JOURNAL_DATA
{
    StartUsn = lastReadUsn,           // Where we left off
    ReasonMask = 0xFFFFFFFF,          // All reasons (or filter here)
    ReturnOnlyOnClose = 0,            // Get events as they happen
    Timeout = 0,                      // Non-blocking
    BytesToWaitFor = 0,               // Don't wait for data
    UsnJournalID = journalId
};

// FSCTL_READ_USN_JOURNAL = 0x000900bb
DeviceIoControl(
    volumeHandle,
    FSCTL_READ_USN_JOURNAL,
    ref readData, sizeof(READ_USN_JOURNAL_DATA),
    buffer, BUFFER_SIZE,
    out bytesReturned,
    IntPtr.Zero
);

// First 8 bytes of the output buffer = next USN to read from
// Remaining bytes = packed USN_RECORD structs
```

### Key Structs

```csharp
[StructLayout(LayoutKind.Sequential)]
struct USN_RECORD
{
    public uint RecordLength;       // Variable length — records are NOT fixed size
    public ushort MajorVersion;     // Should be 2
    public ushort MinorVersion;
    public ulong FileReferenceNumber;    // Unique file identifier (inode equivalent)
    public ulong ParentFileReferenceNumber;  // Parent directory's reference number
    public long Usn;                // The sequence number
    public long TimeStamp;          // FILETIME
    public uint Reason;             // What happened (see USN_REASON flags below)
    public uint SourceInfo;
    public uint SecurityId;
    public uint FileAttributes;
    public ushort FileNameLength;   // In BYTES, not characters
    public ushort FileNameOffset;   // Offset from start of record to filename
    // WCHAR FileName[] follows — variable length, no null terminator
}
```

**IMPORTANT:** Records are variable length. To walk the buffer you MUST use `RecordLength` to advance the pointer:
```csharp
int offset = 8; // Skip the 8-byte "next USN" prefix
while (offset < bytesReturned)
{
    var record = Marshal.PtrToStructure<USN_RECORD>(buffer + offset);
    // ... process record ...
    offset += (int)record.RecordLength;
}
```

### USN Reason Flags (What Happened)

These are bitmask values in `USN_RECORD.Reason`:

```csharp
// The ones you almost always care about:
const uint USN_REASON_DATA_OVERWRITE      = 0x00000001; // File data changed
const uint USN_REASON_DATA_EXTEND         = 0x00000002; // File grew
const uint USN_REASON_DATA_TRUNCATION     = 0x00000004; // File shrunk
const uint USN_REASON_FILE_CREATE         = 0x00000100; // New file created
const uint USN_REASON_FILE_DELETE         = 0x00000200; // File deleted
const uint USN_REASON_RENAME_OLD_NAME     = 0x00001000; // Pre-rename name
const uint USN_REASON_RENAME_NEW_NAME     = 0x00002000; // Post-rename name
const uint USN_REASON_CLOSE               = 0x80000000; // File handle closed (batched changes flushed)

// Less common but useful:
const uint USN_REASON_NAMED_DATA_OVERWRITE = 0x00000010; // Alternate data stream changed
const uint USN_REASON_EA_CHANGE           = 0x00000400; // Extended attributes changed  
const uint USN_REASON_SECURITY_CHANGE     = 0x00000800; // Permissions changed
const uint USN_REASON_HARD_LINK_CHANGE    = 0x00004000; // Hard link added/removed
const uint USN_REASON_COMPRESSION_CHANGE  = 0x00020000; // Compression toggled
const uint USN_REASON_OBJECT_ID_CHANGE    = 0x00080000;
```

**Key insight:** A single file operation may generate MULTIPLE records. A "save" in VS Code generates: `DATA_OVERWRITE`, `DATA_TRUNCATION` (if the file got shorter), and finally `CLOSE`. If you only care about "file was saved and closed," filter on `USN_REASON_CLOSE`.

### Getting the Full Path from a FileReferenceNumber

This is the hardest part. The USN record gives you `FileName` (just the filename, not the full path) and `ParentFileReferenceNumber`. To get the full path, you need to walk up the parent chain using `FSCTL_READ_FILE_USN_DATA` or open the file by its reference number:

```csharp
// Open a file by its FileReferenceNumber (not by path)
const ulong FILE_FLAG_OPEN_BY_FILE_ID = 0x00000010;

var fileId = record.FileReferenceNumber;
// Construct a special path: \\.\C:\[fileId as 8-byte-little-endian]
// Then use NtCreateFile or the OpenById pattern
```

**Simpler approach for MVP:** Build a cache. Subscribe to `FILE_CREATE` and `RENAME_NEW_NAME` events and maintain an in-memory `Dictionary<ulong, string>` mapping `FileReferenceNumber → FullPath`. Start by scanning the directory tree on startup to populate it, then keep it updated. Most production USN tools do exactly this.

---

## The Event Output Schema (JSON)

Every event emitted by the daemon should be a JSON object on a single line (NDJSON format):

```json
{
  "usn": 1234567890,
  "timestamp": "2026-02-25T14:32:11.443Z",
  "fileReferenceNumber": "0x0002000000001234",
  "parentReferenceNumber": "0x0001000000000100",
  "fileName": "Program.cs",
  "fullPath": "C:\\Users\\dev\\project\\src\\Program.cs",
  "reason": ["DATA_OVERWRITE", "CLOSE"],
  "reasonRaw": 2147483649,
  "isDirectory": false,
  "attributes": ["ARCHIVE"]
}
```

---

## Build Milestones

### Milestone 1 — Raw Reader (DONE WHEN: you can see USN records printed to console)
- Open volume handle
- Query journal metadata
- Read records in a loop
- Print raw struct values to console
- Handle the "next USN" cursor correctly

### Milestone 2 — Managed Records (DONE WHEN: you can see clean JSON on stdout)
- Map raw USN_RECORD to a managed C# class
- Decode filename from the variable-length buffer correctly
- Serialize to JSON (use System.Text.Json)
- Decode Reason flags to string array
- Implement polling loop with configurable interval

### Milestone 3 — Path Resolution (DONE WHEN: fullPath is populated in JSON output)
- Scan volume on startup to build FileReferenceNumber → Path cache
- Update cache on CREATE/RENAME/DELETE events
- Handle the case where a file's parent isn't in the cache yet

### Milestone 4 — Filter Engine (DONE WHEN: you can filter by extension, path prefix, reason)
- Implement filter DSL: `extension:.cs`, `path:src/`, `reason:CLOSE`
- Combine filters with AND/OR
- Accept filter config from command line or JSON config file

### Milestone 5 — Stable Cursor (DONE WHEN: daemon survives restart without missing events)
- Persist last-read USN to a local file or SQLite
- On startup, read from persisted USN rather than `NextUsn`
- Detect journal wrap (when `FirstUsn > lastReadUsn`)
- Handle journal recreation (UsnJournalID changed)

### Milestone 6 — Output Channels (DONE WHEN: other programs can consume the stream)
- Named pipe server (`\\.\pipe\usn-watcher`)
- WebSocket server (for browser/web app consumers)
- HTTP webhook POST on matching events

---

## Common Pitfalls and How to Avoid Them

**1. The buffer offset bug**
The first 8 bytes of the `FSCTL_READ_USN_JOURNAL` output buffer are the next USN, NOT a record. If you try to parse a `USN_RECORD` at offset 0 you get garbage. Always skip 8 bytes first.

**2. Variable record length**
`USN_RECORD` is variable length. The `FileName` is appended after the fixed-size header. `RecordLength` is padded to 8-byte alignment. Do NOT use `sizeof(USN_RECORD)` to advance through the buffer.

**3. FileName encoding**
`FileNameLength` is in **bytes**, not characters. The filename is UTF-16 LE (Windows native). To decode: `Encoding.Unicode.GetString(buffer, fileNameOffset, fileNameLength)`.

**4. Administrator requirement**
Opening `\\.\C:` requires running as Administrator. The application must either:
- Be launched from an elevated terminal
- Or use a Windows Service (which runs as SYSTEM)
Build a clear error message for when this fails.

**5. Journal can be disabled**
NTFS USN Journal can be disabled (rare, but possible on custom Windows installs). Always check the return value of `FSCTL_QUERY_USN_JOURNAL`. Error code `ERROR_JOURNAL_NOT_ACTIVE` (0x49B) means it's off. You can enable it with `FSCTL_CREATE_USN_JOURNAL`.

**6. The journal wraps**
The journal is circular. If your daemon is offline long enough, `FirstUsn` will advance past your last-read USN. You need to detect this and either start fresh from `FirstUsn`, or signal to the consumer that events were missed.

---

## Useful References

- Microsoft Docs: [Change Journals](https://learn.microsoft.com/en-us/windows/win32/fileio/change-journals)
- Microsoft Docs: [USN_RECORD_V2 structure](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2)
- Microsoft Docs: [FSCTL_READ_USN_JOURNAL](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_read_usn_journal)
- IOCTL codes reference: `winioctl.h` in the Windows SDK
- voidtools Everything SDK (they've solved many of these problems): https://www.voidtools.com/support/everything/sdk/

---

## How to Contribute / Developer Notes

For contributors and maintainers:

- Start by reading this file and the `README.md` for high-level design and operation.
- When working on a specific area, open the related source file and run the project locally:

```powershell
cd src/UsnWatcher.Host
dotnet run -- C
```

- Run `dotnet build` in the solution root to validate compilation.
- Use the `examples/` scripts to test common integrations (PowerShell/Python consumers).
- When making changes, prefer small, reviewable commits and update `PLAN.md` or `docs/` with any design changes.

If you need help or context about a particular file, include that file and a concise description of the problem when opening an issue or pull request.
