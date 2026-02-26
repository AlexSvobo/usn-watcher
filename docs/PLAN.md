# USN Watcher — Build Plan

Track progress here. Check off items as you complete them.

---

## Milestone 1 — Raw Reader
**Done when:** USN records print to the console when you touch a file.

- [x] `dotnet build` succeeds with no errors
- [x] Running as Admin, console shows journal metadata (ID, First USN, Next USN)
- [x] Touching a file (save in Notepad, etc.) produces a new line in the console
- [x] Ctrl+C stops cleanly and prints the final USN cursor
- [x] At least one `CLOSE` event visible per file save


## Project Closed

Completed: 2026-02-25

What was built (summary):
1. Install .NET 8 SDK: https://dotnet.microsoft.com/download
 **Notes:**
 > When implementing serialization, prefer `System.Text.Json` and keep outputs stable for consumers.
5. `cd src/UsnWatcher.Host && dotnet run -- C`
---

## Milestone 2 — Clean JSON Output
**Done when:** Every event is a single-line JSON object on stdout.
- [x] Create `src/UsnWatcher.Stream/JsonSerializer.cs`
- [x] Serialize `UsnRecord` to NDJSON (one JSON object per line)
- [x] Output matches the schema in `docs/CONTEXT.md`
- [x] Pipe works: `usn-watcher C | ConvertFrom-Json` in PowerShell
- [x] Add `--format` flag: `raw` (current) or `json`


 **Notes:**
 > `PathResolver` should populate quickly (background) and keep the cache synchronized on CREATE/RENAME/DELETE events.

  - Use `FSCTL_ENUM_USN_DATA` to walk the MFT efficiently
  - Or use a recursive `Directory.EnumerateFiles` as a simpler starting point
 - [x] Cache is updated as events come in (CREATE adds, DELETE removes, RENAME updates)
 - [x] `UsnRecord.FullPath` is set by a `PathResolver` class before output
Implement a `PathResolver` that builds and maintains a `Dictionary<ulong, string>` mapping FileReferenceNumber to full path. The initial population may scan the filesystem and subsequent USN events (CREATE, RENAME, DELETE) should keep the cache synchronized.

---

## Milestone 4 — Filter Engine
**Done when:** You can run `usn-watcher C --filter "ext:.cs reason:CLOSE"`

- [x] Implement `FilterEngine.cs` in `src/UsnWatcher.Stream/`
- [x] Support: `ext:`, `path:`, `reason:`, `name:`
- [x] Support: `AND`, `OR`, `NOT` operators
- [x] Parse from command-line `--filter` argument
path:src/                            # Only files under src/
NOT path:node_modules/               # Exclude node_modules
ext:.log AND reason:DATA_EXTEND      # Log files that grew
```


- [x] On startup, check stored cursor against journal `FirstUsn`
- [x] Detect journal recreation (JournalId mismatch)
- [x] Emit a `{"type":"CURSOR_RESET","reason":"..."}` event when events are missed
- [x] Named pipe server: `\\.\pipe\usn-watcher` — any language can connect
- [x] WebSocket server on `ws://localhost:9876` — browser/web apps can subscribe
- [x] HTTP webhook: POST matching events to a configured URL
- [x] Each channel is opt-in via config

---

## Ideas For After Milestone 6

- **Query language:** SQL-like syntax, `SELECT * FROM events WHERE ext = '.cs' AND reason = 'CLOSE'`
- **Windows Service install:** `usn-watcher install` registers as a Windows Service
- **Event deduplication:** Collapse multiple rapid events on the same file into one
- **Volume support:** Watch multiple drives simultaneously
- **VSCode extension:** Surface USN events in the VSCode status bar / Problems panel
- **Webhook templates:** Configurable payload templates (Slack, Teams, custom)
- **File content snapshots:** Optionally capture file content at change time (async)
