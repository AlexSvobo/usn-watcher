using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using UsnWatcher.Core;
using UsnWatcher.Host;

/// <summary>
/// USN Watcher — Entry Point
///
/// Run this from an ELEVATED (Administrator) terminal:
///   dotnet run -- C
///
/// You should immediately see filesystem events from your C: drive scrolling by.
/// Every time you save a file, open a browser, or change anything on C:, an event appears.
///
/// This is Milestone 1: raw events to console.
/// Milestone 2 adds JSON output. See docs/CONTEXT.md for the roadmap.
/// </summary>

using UsnWatcher.Stream;



// Subcommands: install / uninstall
var argv = args ?? Array.Empty<string>();
if (argv.Length > 0)
{
    if (string.Equals(argv[0], "install", StringComparison.OrdinalIgnoreCase))
    {
        InstallService();
        return;
    }

    if (string.Equals(argv[0], "uninstall", StringComparison.OrdinalIgnoreCase))
    {
        UninstallService();
        return;
    }
}

// Local helpers for install/uninstall
void InstallService()
{
    try
    {
        var entry = Path.Combine(AppContext.BaseDirectory, "usn-watcher.exe");
        if (string.IsNullOrEmpty(entry) || !File.Exists(entry))
        {
            Console.Error.WriteLine("Cannot determine current executable path.");
            return;
        }

        // Install into a path without spaces to avoid complex quoting
        var destDir = "C:\\usn-watcher";
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, "usn-watcher.exe");
        File.Copy(entry, dest, true);

        // Build sc.exe arguments with an outer quoted binPath value that contains an inner-quoted executable path
        var installPath = dest; // e.g. C:\usn-watcher\usn-watcher.exe
        var binPath = $"\"{installPath}\" --service"; // yields: "C:\\usn-watcher\\usn-watcher.exe" --service
        var scCreateArgs = $"create usn-watcher binPath= \"{binPath}\" start= auto obj= LocalSystem";
        var psi = new ProcessStartInfo("sc.exe", scCreateArgs)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var p = Process.Start(psi);
        if (p != null)
        {
            using (p)
            {
                p.WaitForExit();
                var outp = p.StandardOutput?.ReadToEnd() ?? string.Empty;
                var err = p.StandardError?.ReadToEnd() ?? string.Empty;
                if (p.ExitCode != 0) Console.Error.WriteLine($"sc create failed: {err}\n{outp}");
            }
        }
        else
        {
            Console.Error.WriteLine("sc create failed: could not start sc.exe");
        }

        // Start the service
        var psiStart = new ProcessStartInfo("sc.exe", "start usn-watcher") { UseShellExecute = false, CreateNoWindow = true };
        var p2 = Process.Start(psiStart);
        if (p2 != null)
        {
            using (p2) { p2.WaitForExit(); }
        }
        else
        {
            Console.Error.WriteLine("sc start failed: could not start sc.exe");
        }

        Console.WriteLine("Service 'usn-watcher' installed and started.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Install failed: {ex.Message}");
    }
}

void UninstallService()
{
    try
    {
        try
        {
            using var sc = new ServiceController("usn-watcher");
            try
            {
                if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                }
            }
            catch { /* ignore if cannot stop */ }
        }
        catch { /* ignore if service not present */ }

        var psi = new ProcessStartInfo("sc.exe", "delete usn-watcher") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        var p = Process.Start(psi);
        if (p != null)
        {
            using (p)
            {
                p.WaitForExit();
                var outp = p.StandardOutput?.ReadToEnd() ?? string.Empty;
                var err = p.StandardError?.ReadToEnd() ?? string.Empty;
                if (p.ExitCode != 0) Console.Error.WriteLine($"sc delete failed: {err}\n{outp}");
            }
        }
        else
        {
            Console.Error.WriteLine("sc delete failed: could not start sc.exe");
        }

        Console.WriteLine("Service 'usn-watcher' uninstalled.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Uninstall failed: {ex.Message}");
    }
}

// Parse command-line arguments (robust: accept flags anywhere)
string volumeLetter = "C";
int pollMs = 250;
string format = "table"; // or "json"
bool verbose = false;
bool noPopulate = false;
string? filter = null;
bool filterLog = false;
bool enablePipe = false;
bool serviceMode = false;

for (int i = 0; i < argv.Length; i++)
{
    var a = argv[i];
    if (string.IsNullOrWhiteSpace(a)) continue;

    if (a.StartsWith("--"))
    {
        if (a.StartsWith("--format"))
        {
            var parts = a.Split('=', 2);
            if (parts.Length == 2) format = parts[1];
            else if (i + 1 < argv.Length) format = argv[++i];
        }
        else if (a.StartsWith("--poll-ms") || a.StartsWith("--poll"))
        {
            var parts = a.Split('=', 2);
            var val = parts.Length == 2 ? parts[1] : (i + 1 < argv.Length ? argv[++i] : null);
            if (int.TryParse(val, out var p)) pollMs = p;
        }
        else if (a == "--verbose" || a == "-v")
        {
            verbose = true;
        }
        else if (a == "--no-populate")
        {
            noPopulate = true;
        }
        else if (a.StartsWith("--filter"))
        {
            var parts = a.Split('=', 2);
            if (parts.Length == 2) filter = parts[1];
            else if (i + 1 < argv.Length) filter = argv[++i];
        }
        else if (a == "--service")
        {
            serviceMode = true;
        }
        else if (a == "--filter-log")
        {
            filterLog = true;
        }
        else if (a == "--pipe")
        {
            enablePipe = true;
        }
        else if (a.StartsWith("--start-usn"))
        {
            // left unhandled here; reader.Initialize handles start-usn if implemented
            if (a.Contains("=")) { /* noop */ }
        }
    }
    else
    {
        // first non-flag arg = volume letter, optional second non-flag arg = poll ms
        if (volumeLetter == "C")
        {
            volumeLetter = a;
            continue;
        }

        if (int.TryParse(a, out var m)) pollMs = m;
    }
}

// If running as a service, redirect console output to a log file and enable JSON+pipe defaults
if (serviceMode)
{
    try
    {
        format = "json";
        enablePipe = true;

        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "usn-watcher");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "usn-watcher.log");

        var logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
        Console.SetOut(logWriter);
        Console.SetError(logWriter);
    }
    catch { /* best-effort */ }
}

if (!serviceMode)
{
    Console.WriteLine("╔══════════════════════════════════════════════════════╗");
    Console.WriteLine("║              USN Watcher — Milestone 1               ║");
    Console.WriteLine($"║  Volume: {volumeLetter}:   Poll interval: {pollMs}ms                   ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine("Press Ctrl+C to stop.");
}
Console.WriteLine();

// Service vs console cancellation token
CancellationToken appToken;
CancellationTokenSource? consoleCts = null;
UsnService? service = null;
if (serviceMode)
{
    service = new UsnService();
    // Notify SCM that the service is running. Run on a background task so we can continue initialization.
    _ = Task.Run(() => ServiceBase.Run(service));
    appToken = service.Token;
}
else
{
    consoleCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; consoleCts.Cancel(); };
    appToken = consoleCts.Token;
}

try
{
    using var volume = VolumeHandle.Open(volumeLetter);
    var reader = new UsnJournalReader(volume);
    var resolver = new PathResolver();
    var root = $"{volumeLetter}:\\";

    var filterEngine = new UsnWatcher.Stream.FilterEngine(filter);
    PipeServer? pipeServer = null;
    if (enablePipe)
    {
        pipeServer = new PipeServer(volumeLetter[0]);
        Console.WriteLine($"[PIPE] Listening on {pipeServer!.PipeName}");
    }

    // Deduper: coalesce rapid events per-FRN before emitting
    var deduper = new Deduper(50);
    var deduperTask = deduper.StartAsync(record =>
    {
        try
        {
            // Hardcoded exclusion: skip emitting any records under the usn-watcher appdata folder
            bool isCursorPath = false;
            try
            {
                if (!string.IsNullOrEmpty(record.FullPath) && record.FullPath.IndexOf("\\usn-watcher\\", StringComparison.OrdinalIgnoreCase) >= 0)
                    isCursorPath = true;
            }
            catch { }

            if (isCursorPath) return;

            // Always produce NDJSON for broadcasting; write to console in chosen format
            string jsonLine;
            using (var sw = new System.IO.StringWriter())
            {
                UsnWatcher.Stream.JsonSerializer.WriteNdjson(record, sw);
                jsonLine = sw.ToString().TrimEnd('\r', '\n');
            }

            if (filterEngine.Matches(record))
            {
                if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Out.WriteLine(jsonLine);
                    try { Console.Out.Flush(); } catch { }
                }
                else
                {
                    Console.WriteLine(
                        $"{record.Usn,-20} " +
                        $"{record.Timestamp:HH:mm:ss.fff}  " +
                        $"{string.Join("|", record.Reasons ?? Array.Empty<string>()),-30} " +
                        $"{(record.IsDirectory ? "[DIR] " : "")}{record.FileName}"
                    );
                }

                // Broadcast NDJSON to pipe clients if enabled
                try { pipeServer?.Broadcast(jsonLine); } catch { }
            }
            else
            {
                if (filterLog)
                {
                    var reasons = string.Join("|", record.Reasons ?? Array.Empty<string>());
                    Console.Error.WriteLine($"[FILTER] Excluded: {record.FileName} ({reasons})");
                }
            }
        }
        catch
        {
            // swallow emission errors — host logs elsewhere
        }
    }, appToken);
    var _dedupObs = deduperTask.ContinueWith(t => Console.Error.WriteLine($"[DEDUP] failed: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);

    if (!noPopulate)
    {
        try
        {
            // Try to load a fresh cache from disk first (fast path). If it loads, we still
            // kick off a background populate to reconcile changes while offline, but we
            // don't block streaming on the heavy MFT scan.
            bool loaded = resolver.TryLoadCache(root, msg => { if (verbose) Console.WriteLine("[POP] " + msg); });

            if (loaded)
            {
                Console.WriteLine($"[POP] Loaded cache entries: {resolver.Cache.Count:N0}");
                // Start background reconcile/populate but do not await it. Observe failures.
                var bgPopulate = System.Threading.Tasks.Task.Run(() => resolver.Populate(root, msg => { if (verbose) Console.WriteLine("[POP] " + msg); }));
                var _bgObs = bgPopulate.ContinueWith(t => Console.Error.WriteLine($"[POP] background populate failed: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
            }
            else
            {
                // No cache available — start the full populate in background as before.
                var populateTask = System.Threading.Tasks.Task.Run(() => resolver.Populate(root, msg => { if (verbose) Console.WriteLine("[POP] " + msg); }));

                if (verbose)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Console.WriteLine($"[POP] Starting MFT population from {root}");
                    var monitor = System.Threading.Tasks.Task.Run(async () =>
                    {
                        while (!populateTask.IsCompleted)
                        {
                            Console.WriteLine($"[POP] populating... elapsed {sw.Elapsed:mm\\:ss}");
                            await System.Threading.Tasks.Task.Delay(2000);
                        }
                    });
                    var _monObs = monitor.ContinueWith(t => Console.Error.WriteLine($"[POP] monitor failed: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
                }

                var _popObs = populateTask.ContinueWith(t =>
                {
                    if (t.IsFaulted) Console.Error.WriteLine($"[POP] population failed: {t.Exception?.GetBaseException().Message}");
                    else Console.WriteLine($"[POP] Path cache entries: {resolver.Cache.Count:N0}");
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] PathResolver population failed: {ex.Message}");
        }
    }
    else
    {
        if (verbose) Console.WriteLine("[POP] Skipping initial path population (--no-populate)");
    }

    // Load persisted cursor (best-effort)
    var stored = CursorStore.Load(volumeLetter);

    if (stored != null)
    {
        if (verbose) Console.WriteLine($"[USN] Found stored cursor: journal=0x{stored.JournalId:X16} next={stored.NextUsn} savedAt={stored.SavedAt:o}");
        // Initialize to obtain current journal id, then try to set cursor to stored value
        reader.Initialize();

        if (stored.JournalId == reader.JournalId)
        {
            bool ok = reader.SetCursor(stored.NextUsn);
            if (ok)
            {
                var age = DateTime.UtcNow - stored.SavedAt;
                Console.WriteLine($"[USN] Resuming from USN {stored.NextUsn} (saved {age.TotalMinutes:N0} minutes ago)");
            }
            else
            {
                // SetCursor returned false indicating the stored USN is older than FirstUsn (wrapped)
                long firstAvailable = reader.NextUsn; // SetCursor set nextUsn to FirstUsn
                var gapObj = new { type = "GAP", reason = "journal_wrapped", from = stored.NextUsn, to = firstAvailable };
                Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(gapObj));
                Console.Error.WriteLine($"[USN] Journal wrapped since saved cursor {stored.NextUsn}. Resuming from {firstAvailable}.");
            }
        }
        else
        {
            Console.Error.WriteLine($"[USN] WARNING: Journal ID changed (was 0x{stored.JournalId:X16}, now 0x{reader.JournalId:X16}). Starting fresh.");
        }
    }
    else
    {
        reader.Initialize();
    }
    Console.WriteLine();
    if (!format.Equals("json", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"{"USN",-20} {"Time",-12} {"Reason",-30} {"FileName"}");
        Console.WriteLine(new string('─', 100));
    }

    long eventCount = 0;
    long batchCount = 0;

    DateTime lastSaved = DateTime.UtcNow;
    while (!appToken.IsCancellationRequested)
    {
        int recordsThisBatch = 0;

        try
        {
                foreach (var record in reader.ReadBatch())
            {
                // For CREATE and RENAME_NEW_NAME records we want the cache to reflect
                // the new path before emitting so the event's `fullPath` matches.
                bool isRenameNew = record.Reasons != null && record.Reasons.Contains("RENAMENEWNAME");
                if (record.IsCreate || isRenameNew)
                {
                    try { _ = resolver.Update(record); } catch { }
                }

                // Try to resolve a full path from our cache before emitting
                try { resolver.Resolve(record); } catch { }

                // Hardcoded exclusion: skip emitting any records under the usn-watcher appdata folder
                bool isCursorPath = false;
                try
                {
                    if (!string.IsNullOrEmpty(record.FullPath) && record.FullPath.IndexOf("\\usn-watcher\\", StringComparison.OrdinalIgnoreCase) >= 0)
                        isCursorPath = true;
                }
                catch { }

                if (!isCursorPath)
                {
                    try
                    {
                        deduper.Add(record);
                    }
                    catch { }
                }

                // Keep cache in sync for delete and other events (remove on delete)
                if (record.IsDelete)
                {
                    try { _ = resolver.Update(record); } catch { }
                }

                recordsThisBatch++;
                eventCount++;
            }
        }
        catch (UsnJournalWrappedException ex)
        {
            Console.Error.WriteLine($"\n[WARNING] {ex.Message}");
        }

        batchCount++;

        // Update title bar with stats (useful for monitoring). Skip when running as a service.
        if (batchCount % 20 == 0)
        {
            try { if (!serviceMode) Console.Title = $"USN Watcher | Events: {eventCount:N0} | Batches: {batchCount:N0}"; } catch { }
        }

        await Task.Delay(pollMs, appToken).ContinueWith(_ => { }); // swallow cancellation

        // Periodically persist cursor so we don't lose more than ~30s of position
        if ((DateTime.UtcNow - lastSaved) >= TimeSpan.FromSeconds(30))
        {
            try { CursorStore.Save(volumeLetter, reader.JournalId, reader.NextUsn); } catch { }
            lastSaved = DateTime.UtcNow;
        }
    }

    // Clean shutdown — persist final cursor
    try
    {
        // Flush any outstanding coalesced events before exiting
        try
        {
            deduper.FlushAll(record =>
            {
                try
                {
                    bool isCursorPath = false;
                    try
                    {
                        if (!string.IsNullOrEmpty(record.FullPath) && record.FullPath.IndexOf("\\usn-watcher\\", StringComparison.OrdinalIgnoreCase) >= 0)
                            isCursorPath = true;
                    }
                    catch { }

                    if (isCursorPath) return;

                    string jsonLine;
                    using (var sw = new System.IO.StringWriter())
                    {
                        UsnWatcher.Stream.JsonSerializer.WriteNdjson(record, sw);
                        jsonLine = sw.ToString().TrimEnd('\r', '\n');
                    }

                    if (filterEngine.Matches(record))
                    {
                        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.Out.WriteLine(jsonLine);
                            try { Console.Out.Flush(); } catch { }
                        }
                        else
                        {
                            Console.WriteLine(
                                $"{record.Usn,-20} " +
                                $"{record.Timestamp:HH:mm:ss.fff}  " +
                                $"{string.Join("|", record.Reasons ?? Array.Empty<string>()),-30} " +
                                $"{(record.IsDirectory ? "[DIR] " : "")}{record.FileName}"
                            );
                        }

                        try { pipeServer?.Broadcast(jsonLine); } catch { }
                    }
                }
                catch { }
            });
        }
        catch { }

        try { deduper.Dispose(); } catch { }

        CursorStore.Save(volumeLetter, reader.JournalId, reader.NextUsn);
    }
    catch { }

    try { resolver.SaveCache(root, msg => { if (verbose) Console.WriteLine("[POP] " + msg); }); } catch { }

    Console.WriteLine($"\nStopped. Total events: {eventCount:N0}  Last USN: {reader.NextUsn}");
    Console.WriteLine($"To resume from where you left off, run with: --start-usn {reader.NextUsn}");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("Access Denied"))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine();
    Console.Error.WriteLine("ERROR: Access Denied.");
    Console.Error.WriteLine("You must run this program as Administrator.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Right-click your terminal → 'Run as Administrator', then try again.");
    Console.ResetColor();
    Environment.Exit(1);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"\nFATAL: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    Console.ResetColor();
    Environment.Exit(1);
}

// Minimal ServiceBase wrapper to integrate with SCM when running as a Windows Service
class UsnService : ServiceBase
{
    private readonly CancellationTokenSource _cts = new();
    public CancellationToken Token => _cts.Token;
    protected override void OnStop() => _cts.Cancel();
    protected override void OnShutdown() => _cts.Cancel();
}
