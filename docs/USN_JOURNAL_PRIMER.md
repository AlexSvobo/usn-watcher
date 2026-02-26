# USN Journal — Technical Primer

A reference document for understanding the Windows kernel internals behind this project.
Keep this open while working on the low-level parts.

---

## What Is the USN Journal?

USN stands for **Update Sequence Number**. It's a 64-bit integer that the NTFS driver
increments monotonically every time it writes an entry to the change journal. Think of it
as a log sequence number (LSN) in a database transaction log — it only ever goes up.

The journal itself is a file stored at the root of each NTFS volume, in the hidden system
directory `$Extend\$UsnJrnl`. You can't open it directly; you interact with it through
`DeviceIoControl` FSCTL calls.

---

## The Journal Is Circular

The journal has a configured maximum size (default is 32MB on most systems). When it fills
up, the oldest entries are discarded. The `FirstUsn` field in `USN_JOURNAL_DATA` tracks the
oldest available entry. If your stored cursor falls below `FirstUsn`, you've missed events.

This is the same problem that Kafka handles with retention policies. Our solution (Milestone 5)
is to store the cursor and detect the "wrap" condition on startup.

---

## The MFT Connection

Every file on an NTFS volume has an entry in the **Master File Table (MFT)**. The MFT is the
"directory" of all files — their names, sizes, timestamps, and crucially, their
**File Reference Numbers**.

A File Reference Number (FRN) is a 64-bit integer structured as:
- Low 48 bits: MFT record number (unique index within the volume)
- High 16 bits: sequence number (increments when a record is reused after deletion)

This is why you can have two files with the same name but different FRNs — the path isn't the
identity, the FRN is. When you rename a file, the FRN stays the same but the FileName in the
journal changes.

The USN record gives you:
- `FileReferenceNumber` — the FRN of the file that changed
- `ParentFileReferenceNumber` — the FRN of its parent directory

To reconstruct the full path, you walk up the parent chain using the FRN-to-path cache.

---

## Why ReadDirectoryChangesW Is Worse

`ReadDirectoryChangesW` is the "official" Win32 API for watching a directory. Its problems:

1. **Per-directory:** You need one handle per watched directory.
2. **Buffer overflow:** If events accumulate faster than you consume them, you get
   `ERROR_NOTIFY_ENUM_DIR` — a signal that you missed events with no way to recover.
3. **No history:** It only gives you events that happen after you start watching.
4. **Path-bound:** You have to know the path upfront; you can't watch "everything."

The USN Journal solves all four problems. The tradeoff is that it requires Admin.

---

## FSCTL Call Reference

### FSCTL_QUERY_USN_JOURNAL (0x000900F4)
**Input:** None  
**Output:** `USN_JOURNAL_DATA`  
**Use:** Get journal metadata and your starting cursor.

### FSCTL_READ_USN_JOURNAL (0x000900BB)
**Input:** `READ_USN_JOURNAL_DATA`  
**Output:** `[8-byte next USN][USN_RECORD_V2 records...]`  
**Use:** The core read loop. Call repeatedly with `StartUsn` = last returned NextUsn.

### FSCTL_CREATE_USN_JOURNAL (0x000900E7)
**Input:** `CREATE_USN_JOURNAL_DATA` (MaximumSize, AllocationDelta)  
**Output:** None  
**Use:** Enable the journal if it's disabled, or resize it.

### FSCTL_ENUM_USN_DATA (0x000900B3)
**Input:** `MFT_ENUM_DATA` (StartFileReferenceNumber, LowUsn, HighUsn)  
**Output:** `[8-byte next FRN][USN_RECORD_V2 records...]`  
**Use:** Enumerate ALL files on the volume to build the initial path cache.
This is the efficient way — it reads the MFT directly rather than walking directories.

---

## Record Version Notes

There are three versions of USN records (V2, V3, V4). We use V2, which is the most common
and works on all Windows 10/11 systems without special configuration.

- **V2** (MajorVersion=2): Standard. FRNs are 64-bit. FileName is UTF-16. This is what we use.
- **V3** (MajorVersion=3): 128-bit FRNs (ReFS support). More complex, not needed.
- **V4** (MajorVersion=4): Range-based records for very large files. Rare.

Always check `MajorVersion == 2` when parsing records, as the struct layout differs.

---

## Performance Characteristics

On a modern NVMe drive, the USN Journal can be read at the speed of the drive itself —
you're not paying the overhead of a filesystem scan. A single `FSCTL_READ_USN_JOURNAL` call
with a 64KB buffer returns roughly 1000 records.

At 250ms poll interval, you can comfortably handle 4000 events/second without the journal
outpacing your reader. For extremely write-heavy workloads (e.g. a compiler build), you may
want to drop to 50ms or use the `BytesToWaitFor` field to switch to a blocking/event-driven
read instead of polling.

Blocking read (set `Timeout` and `BytesToWaitFor` to non-zero values):
- The kernel holds the call until `BytesToWaitFor` bytes accumulate in the journal
- More efficient than polling; adds latency proportional to how quiet the filesystem is

---

## Security Considerations

Opening a volume handle is an Admin-only operation. This is a deliberate security boundary —
you can see every file operation on the volume, including system files, temp files, and browser
history. Any tool built on this should:

1. Be explicit about requiring Admin
2. Not store or transmit full paths without user consent
3. Filter out sensitive paths (e.g. `\Users\*\AppData`) by default
