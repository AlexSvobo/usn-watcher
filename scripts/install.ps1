# Install script for usn-watcher
# 1) Requires Administrator
# 2) dotnet publish the Host project into ./dist
# 3) run the published usn-watcher.exe install
# 4) show service status

# Ensure running elevated
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run as Administrator. Right-click PowerShell -> Run as Administrator."
    exit 1
}

# Ensure any running instance is stopped before publish/install
Get-Process usn-watcher -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 1

# If the service already exists, remove it so install is idempotent
try {
    $svc = Get-Service -Name usn-watcher -ErrorAction SilentlyContinue
    if ($svc) {
        Write-Output "Service 'usn-watcher' exists - stopping and removing before install."
        try { Stop-Service usn-watcher -Force -ErrorAction SilentlyContinue } catch { }
        Start-Sleep -Seconds 1
        & sc.exe delete usn-watcher | Out-Null
        Start-Sleep -Seconds 1
    }
} catch {
    # ignore errors — best-effort cleanup
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir '..')
$project = Join-Path $repoRoot 'src\UsnWatcher.Host\UsnWatcher.Host.csproj'
$dist = Join-Path $repoRoot 'dist'

Write-Output "Publishing $project to $dist..."
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }

$publishArgs = @(
    'publish',
    $project,
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-o', $dist
)

Write-Output "Running: dotnet $($publishArgs -join ' ')"
# Run dotnet publish directly so output streams to the console and the call doesn't hang
& dotnet @publishArgs
$exit = $LASTEXITCODE
if ($exit -ne 0) {
    Write-Error "dotnet publish failed (exit code $exit)."
    exit $exit
}

$exe = Join-Path $dist 'usn-watcher.exe'
if (-not (Test-Path $exe)) {
    Write-Error "Published executable not found: $exe"
    exit 1
}

Write-Output "Running installer: $exe install"
& $exe install
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Installer returned exit code $LASTEXITCODE. Check output above for errors."
}

Write-Output "Service status:"
sc.exe query usn-watcher | Write-Output

Write-Output "Done."
