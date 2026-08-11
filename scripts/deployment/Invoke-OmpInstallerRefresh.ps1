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

$launcherOutput = & $exe --refresh-installer-package --config $ConfigPath --payload-root $InstallerRoot --log-file $logFile 2>&1
$launcherExit = $LASTEXITCODE
$launcherOutput | ForEach-Object { Write-Host "  $_" }

$runnerPid = ($launcherOutput | Select-String 'RunnerPid:\s*(\d+)' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1)

if ($runnerPid) {
    Write-Host "Waiting for detached refresh runner (PID $runnerPid)..."
    try { Wait-Process -Id ([int]$runnerPid) -Timeout $TimeoutSeconds -ErrorAction Stop } catch {}
}
elseif ($launcherExit -ne 0 -and $null -ne $launcherExit) {
    Write-Error "Refresh launcher failed with exit code $launcherExit."
    exit 1
}

# The launcher itself completed the refresh (started from outside the package),
# or the runner has now exited. The log carries the definitive result marker.
$deadline = (Get-Date).AddSeconds(30)
while (-not (Test-Path $logFile) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
if (-not (Test-Path $logFile)) {
    Write-Error "Refresh log was never created: $logFile"
    exit 1
}

$logText = Get-Content $logFile -Raw
if ($logText -notmatch 'Installer package refresh completed\.') {
    Write-Host '--- Refresh log tail ---'
    Get-Content $logFile -Tail 20 | ForEach-Object { Write-Host "  $_" }
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
