# Runs the universal installer's package refresh (and optionally upgrade/complete)
# headless, waiting for the detached runner and returning a real exit code.
# This is the scripted equivalent of the GUI's "Refresh installer package from
# source first" + "Install or update" flow.
#
# Example:
#   pwsh -File Invoke-OmpInstallerRefresh.ps1 `
#     -ConfigPath 'C:\...\Universal\hosts\linus-laptop\bootstrap.json' -Apply
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConfigPath,

    # Installer package root. Defaults to <hosts>\..\..\installer relative to the
    # host profile directory, matching the Universal layout.
    [string]$InstallerRoot,

    # Also run --upgrade-or-complete -y after a successful refresh so the new
    # artifacts and module definitions reach the live installation.
    [switch]$Apply,

    [int]$TimeoutSeconds = 1200
)

$ErrorActionPreference = 'Stop'

$ConfigPath = (Resolve-Path $ConfigPath).Path
if (-not $InstallerRoot) {
    $hostDir = Split-Path $ConfigPath -Parent
    $InstallerRoot = Join-Path (Split-Path (Split-Path $hostDir -Parent) -Parent) 'installer'
}
$InstallerRoot = (Resolve-Path $InstallerRoot).Path
$exe = Join-Path $InstallerRoot 'OpenModulePlatform.Bootstrapper.exe'
if (-not (Test-Path $exe)) {
    throw "Bootstrapper executable was not found: $exe"
}

$logFile = Join-Path ([IO.Path]::GetTempPath()) ("omp-installer-refresh-cli-{0:yyyyMMddHHmmss}.log" -f (Get-Date).ToUniversalTime())

Write-Host "Refresh: $exe"
Write-Host "Config:  $ConfigPath"
Write-Host "Log:     $logFile"

# The Bootstrapper is a GUI-subsystem executable: the call operator neither
# waits for it nor captures its stdout, which once let this script race ahead
# and run sync/apply while the detached runner was still rebuilding the
# package. Start-Process with explicit redirection gives the GUI process real
# std handles (so RunnerPid reaches us) and -Wait blocks until the launcher
# has handed off.
$launcherOutFile = "$logFile.launcher.out"
$launcherErrFile = "$logFile.launcher.err"
$launcherProcess = Start-Process -FilePath $exe `
    -ArgumentList @('--refresh-installer-package', '--config', $ConfigPath, '--payload-root', $InstallerRoot, '--log-file', $logFile) `
    -RedirectStandardOutput $launcherOutFile -RedirectStandardError $launcherErrFile `
    -NoNewWindow -Wait -PassThru
$launcherExit = $launcherProcess.ExitCode
$launcherOutput = @(Get-Content $launcherOutFile -ErrorAction SilentlyContinue) + @(Get-Content $launcherErrFile -ErrorAction SilentlyContinue)
$launcherOutput | Where-Object { $_ } | ForEach-Object { Write-Host "  $_" }

$runnerPid = ($launcherOutput | Select-String 'RunnerPid:\s*(\d+)' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1)

if ($runnerPid) {
    Write-Host "Waiting for detached refresh runner (PID $runnerPid)..."
    try { Wait-Process -Id ([int]$runnerPid) -Timeout $TimeoutSeconds -ErrorAction Stop } catch {
        Write-Error "Refresh runner (PID $runnerPid) did not finish within $TimeoutSeconds seconds."
        exit 1
    }
}
elseif ($launcherExit -ne 0 -and $null -ne $launcherExit) {
    Write-Error "Refresh launcher failed with exit code $launcherExit."
    exit 1
}

# The runner writes the completion marker only after the package swap, so
# poll the log for the definitive result instead of trusting a single early
# read. A failure marker (or the timeout) aborts before sync/apply can run
# against a half-replaced package.
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$refreshCompleted = $false
while ((Get-Date) -lt $deadline) {
    $logText = if (Test-Path $logFile) { Get-Content $logFile -Raw -ErrorAction SilentlyContinue } else { $null }
    if ($logText -match 'Installer package refresh failed\.') { break }
    if ($logText -match 'Installer package refresh completed\.') { $refreshCompleted = $true; break }
    Start-Sleep -Seconds 2
}

if (-not $refreshCompleted) {
    if (Test-Path $logFile) {
        Write-Host '--- Refresh log tail ---'
        Get-Content $logFile -Tail 20 | ForEach-Object { Write-Host "  $_" }
    }
    else {
        Write-Host "Refresh log was never created: $logFile"
    }
    Write-Error 'Installer package refresh did not complete. See the log above.'
    exit 1
}
Write-Host 'Installer package refresh completed.'

if (-not $Apply) {
    exit 0
}

# The refresh rebuilds the installer skeleton and the OMP/ODV payload, but
# module artifacts from the other source repositories (IbsPackager & co) are
# produced by the package-object sync - without it the package library keeps
# serving the previous versions.
# Long-lived MSBuild nodes (/nodeReuse:true) from the refresh build keep
# obj-files open and intermittently fail the selective builds below with
# "file is being used by another process"; shut the build servers down first.
dotnet build-server shutdown 2>&1 | Out-Null

Write-Host 'Syncing package objects from source repositories (--sync-package-objects)...'
$syncLog = [IO.Path]::ChangeExtension($logFile, '.sync.log')
$syncProcess = Start-Process -FilePath $exe `
    -ArgumentList @('--sync-package-objects', '--config', $ConfigPath, '--payload-root', $InstallerRoot) `
    -RedirectStandardOutput $syncLog `
    -RedirectStandardError ([IO.Path]::ChangeExtension($syncLog, '.err.log')) `
    -NoNewWindow -Wait -PassThru
Write-Host "Sync log: $syncLog"
if ($syncProcess.ExitCode -ne 0) {
    Get-Content $syncLog -Tail 15 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
    Write-Error "Package-object sync failed with exit code $($syncProcess.ExitCode)."
    exit $syncProcess.ExitCode
}

# The refresh replaced the package (including the exe); run upgrade/complete
# from the fresh package so new module definitions and artifacts are applied.
Write-Host 'Applying package to the installation (--upgrade-or-complete)...'
$applyLog = [IO.Path]::ChangeExtension($logFile, '.apply.log')
# --full-content-check on purpose: fast mode trusts version numbers and has
# been observed to skip importing a newer module definition after a failed
# earlier apply; the content check costs seconds and always converges.
$process = Start-Process -FilePath $exe `
    -ArgumentList @('--upgrade-or-complete', '--config', $ConfigPath, '--payload-root', $InstallerRoot, '--full-content-check', '-y') `
    -RedirectStandardOutput $applyLog `
    -RedirectStandardError ([IO.Path]::ChangeExtension($applyLog, '.err.log')) `
    -NoNewWindow -Wait -PassThru

Write-Host "Apply log: $applyLog"
Get-Content $applyLog -Tail 15 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
if ($process.ExitCode -ne 0) {
    Write-Error "Upgrade/complete failed with exit code $($process.ExitCode)."
    exit $process.ExitCode
}

Write-Host 'Upgrade/complete finished. HostAgent picks up the new desired versions on its next cycle.'
exit 0
