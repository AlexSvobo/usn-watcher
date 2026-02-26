# usn-watcher

A Windows daemon that streams NTFS USN Journal events as NDJSON. Designed for reliable, volume-wide
change telemetry with optional filtering, per-FRN deduplication, and named-pipe broadcasting.

Quick links
- Documentation: [docs/USN_JOURNAL_PRIMER.md](docs/USN_JOURNAL_PRIMER.md)
- Developer notes: [docs/CONTEXT.md](docs/CONTEXT.md)

Requirements
- Windows 10/11
- .NET 8 SDK (for development) or .NET 8 runtime (for running published binaries)
- Administrator privileges to read USN journal volumes

Quick start (developer)
1. Build from source (elevated):
   ```powershell
   cd src\UsnWatcher.Host
   dotnet run -- C --format json
   ```
2. Enable named pipe output (multi-client):
   ```powershell
   dotnet run -- C --format json --pipe
   ```

Pipe client (PowerShell):
```powershell
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'usn-watcher-C', [System.IO.Pipes.PipeDirection]::In)
$pipe.Connect(5000)
$reader = New-Object System.IO.StreamReader($pipe, [System.Text.Encoding]::UTF8)
while (-not $reader.EndOfStream) { $line = $reader.ReadLine(); $line | ConvertFrom-Json | Select timestamp,fileName,reason }
```

Pipe client (Python):
```python
import win32file
pipe = win32file.CreateFile(r'\\.\\pipe\\usn-watcher-C', win32file.GENERIC_READ, 0, None, win32file.OPEN_EXISTING, 0, None)
while True:
      hr, data = win32file.ReadFile(pipe, 4096)
      for line in data.decode('utf-8').splitlines():
            print(line)
```

CLI flags (examples)
- `--format json` — emit NDJSON (one JSON object per line). Recommended for production.
- `--pipe` — enable named-pipe server for broadcasts.
- `--filter '<expr>'` — filter expression (see `docs/FILTER_QUERY_LANGUAGE.md`). Example:
   - `--filter "ext:.cs AND reason:CLOSE"`
- `--filter-log` — print excluded events to stderr for debugging filters.
- `--no-populate` — skip initial FRN cache population (useful for quick runs).
- `--poll-ms <n>` — poll interval in ms (default 250).
- `--verbose` / `-v` — diagnostics and progress logs.

Filtering examples
- Watch for saved C# files:

```powershell
dotnet run -- C --format json --filter "ext:.cs AND reason:CLOSE"
```

- Exclude `node_modules` and only show `.log` growths:

```powershell
dotnet run -- C --format json --filter "NOT path:node_modules AND ext:.log AND reason:DATA_EXTEND"
```
To run the dashboard
dotnet run --project tools/ws-bridge/ws-bridge.csproj
then open usn-dashboard.html or navigate to localhost:3000
**This will not (currently) run the MFT scan and path cache, and instead depends on you having ran the Host prior. 

Notes & Troubleshooting
- Elevated privileges: reading the USN Journal requires Administrator rights. If you see "Access Denied", run your terminal as Administrator.
- If the named pipe client cannot connect, confirm the daemon printed the listening message `"[PIPE] Listening on \\ \\.\\pipe\\usn-watcher-<Drive>"` and that the pipe exists.
- The FRN cache is a performance optimization; a background reconcile runs to catch changes that occurred while the daemon was offline.

Install as a Windows Service (pack & install)
- Publish a single-file executable (see `.github/workflows/release.yml` for CI example).
- Use `scripts/install.ps1` (requires Administrator). The script copies the exe to `C:\usn-watcher` and registers a service that runs the exe with `--service`.

Files created at runtime
- Cursor: `%APPDATA%\usn-watcher\cursor.json` — last saved USN and journal id.
- FRN cache: `%APPDATA%\usn-watcher\cache-<Drive>.bin` — persisted FRN→path cache.

Releases & publishing
- Do not commit built artifacts (`dist/` is ignored). Use GitHub Releases to attach the published single-file executable and optional symbols.
- A GitHub Actions workflow (`.github/workflows/release.yml`) is included to build + attach artifacts when you push a `v*.*.*` tag.

Support
- If you encounter "Access Denied" logs, run the daemon from an Administrator shell.

License: MIT

For developer details and advanced troubleshooting see the `docs/` folder.
