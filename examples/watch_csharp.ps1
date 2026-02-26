# watch_csharp.ps1
# Example: Watch for C# file saves and print a notification
# Run usn-watcher first in another terminal, piping to this script
#
# Usage (from an Admin terminal):
#   usn-watcher C --format json | powershell -File examples\watch_csharp.ps1

param([string]$Filter = "*.cs")

Write-Host "Watching for C# file changes... (Ctrl+C to stop)"
Write-Host ""

$stdin = [Console]::In

while ($true) {
    $line = $stdin.ReadLine()
    if ($null -eq $line) { break }

    try {
        $event = $line | ConvertFrom-Json

        # Only show CLOSE events on .cs files
        if ($event.reasons -contains "CLOSE" -and $event.fileName -like $Filter) {
            $time = $event.timestamp
            $path = if ($event.fullPath) { $event.fullPath } else { $event.fileName }
            Write-Host "[$time] SAVED: $path" -ForegroundColor Green
        }
    }
    catch {
        # Ignore parse errors
    }
}
