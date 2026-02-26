#!/usr/bin/env pwsh
# Uninstall script for usn-watcher
# 1) Requires Administrator
# 2) Run installed usn-watcher uninstall (if present)
# 3) Remove %ProgramFiles%\usn-watcher\

# Ensure running elevated
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run as Administrator. Right-click PowerShell → Run as Administrator."
    exit 1
}

$installDir = Join-Path $env:ProgramFiles 'usn-watcher'
$exe = Join-Path $installDir 'usn-watcher.exe'

if (Test-Path $exe) {
    Write-Output "Running $exe uninstall"
    & $exe uninstall
    if ($LASTEXITCODE -ne 0) { Write-Warning "uninstall returned exit code $LASTEXITCODE" }
} else {
    Write-Output "Installed executable not found at $exe - attempting sc delete if service exists."
}

Write-Output "Deleting install directory: $installDir"
try {
    Remove-Item -Path $installDir -Recurse -Force -ErrorAction Stop
} catch {
    Write-Warning "Failed to remove ${installDir}: $($_.Exception.Message)"
}

Write-Output 'Service status after uninstall (if present):'
sc.exe query usn-watcher | Write-Output

Write-Output 'Done.'
