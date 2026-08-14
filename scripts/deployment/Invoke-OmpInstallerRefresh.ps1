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

# Exit with a specific code after reporting why.
#
# Every failure path here was `Write-Error` followed by `exit <code>`. Under
# ErrorActionPreference = 'Stop' a Write-Error is a terminating error, so the script died on
# the Write-Error line and the exit that followed never ran -- which made every failure
# report 1, including the two paths written specifically to propagate the Bootstrapper's own
# exit code (R7-G15). Writing to the error stream without terminating lets the intended code
# through.
function Exit-WithFailure {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [int]$Code = 1
    )

    Write-Error -Message $Message -ErrorAction Continue
    exit $Code
}

# R5-G1: the detached runner knows the name+PID of any process blocking the
# package swap, but that only ever reached the runner's log file - the CLI sat
# silent while it waited. Echo new log content to the console as it appears so
# the operator can see what is blocking without opening the log.
$script:LogEchoPosition = 0
function Write-NewLogLines {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return
    }

    try {
        $content = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
    }
    catch {
        # The runner may hold the file briefly; try again on the next tick.
        return
    }

    if ($null -eq $content -or $content.Length -le $script:LogEchoPosition) {
        return
    }

    $new = $content.Substring($script:LogEchoPosition)
    $script:LogEchoPosition = $content.Length
    foreach ($line in ($new -split "`r?`n")) {
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            Write-Host "  | $line"
        }
    }
}

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

# Include the PID so two refreshes started in the same second do not share a
# log file (which the runner truncates with append:false, letting the poll read
# the wrong run's marker) (R3-G9).
$logFile = Join-Path ([IO.Path]::GetTempPath()) ("omp-installer-refresh-cli-{0:yyyyMMddHHmmss}-{1}.log" -f (Get-Date).ToUniversalTime(), $PID)

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
# No -Wait here: Start-Process -Wait waits for the whole descendant tree,
# which includes MSBuild's idle nodeReuse processes and stalls the wrapper
# long after the refresh is done. WaitForExit() covers only the launcher.
$launcherProcess = Start-Process -FilePath $exe `
    -ArgumentList @('--refresh-installer-package', '--config', $ConfigPath, '--payload-root', $InstallerRoot, '--log-file', $logFile) `
    -RedirectStandardOutput $launcherOutFile -RedirectStandardError $launcherErrFile `
    -NoNewWindow -PassThru
if (-not $launcherProcess.WaitForExit($TimeoutSeconds * 1000)) {
    Exit-WithFailure -Message "Refresh launcher did not exit within $TimeoutSeconds seconds."
}
$launcherExit = $launcherProcess.ExitCode
$launcherOutput = @(Get-Content $launcherOutFile -ErrorAction SilentlyContinue) + @(Get-Content $launcherErrFile -ErrorAction SilentlyContinue)
$launcherOutput | Where-Object { $_ } | ForEach-Object { Write-Host "  $_" }

$runnerPid = ($launcherOutput | Select-String 'RunnerPid:\s*(\d+)' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1)

if ($runnerPid) {
    # The runner may already have exited (fast refresh); only an actual
    # timeout is fatal here. The marker poll below is the real authority.
    # Verify the process NAME too: Windows reuses PIDs aggressively, so if the
    # runner exits between handoff and this check the PID can already belong to
    # an unrelated long-lived process (e.g. an MSBuild node), and waiting on it
    # would block the full timeout even though the refresh finished (R4-G10).
    $runnerProcess = Get-Process -Id ([int]$runnerPid) -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -eq 'OpenModulePlatform.Bootstrapper' } |
        Select-Object -First 1
    if ($runnerProcess) {
        Write-Host "Waiting for detached refresh runner (PID $runnerPid)..."
        # R5-G1: poll instead of a single blocking WaitForExit so the runner's
        # log (including any "Waiting for processes running from the package to
        # exit: <name> (PID n)" blocker line) is echoed live to the console.
        $runnerDeadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while (-not $runnerProcess.HasExited) {
            Write-NewLogLines -Path $logFile
            if ((Get-Date) -ge $runnerDeadline) {
                Write-NewLogLines -Path $logFile
                Exit-WithFailure -Message "Refresh runner (PID $runnerPid) did not finish within $TimeoutSeconds seconds."
            }
            Start-Sleep -Milliseconds 500
        }
        Write-NewLogLines -Path $logFile
    }
}
elseif ($launcherExit -ne 0 -and $null -ne $launcherExit) {
    Exit-WithFailure -Message "Refresh launcher failed with exit code $launcherExit." -Code $launcherExit
}

# The runner writes the completion marker only after the package swap, so
# poll the log for the definitive result instead of trusting a single early
# read. The runner has ALREADY exited by this point (waited on above), so the
# marker should be present within a short flush window; a runner that crashed
# without writing it must fail fast rather than block the full timeout again
# (R3-G6). A failure marker aborts before sync/apply can run against a
# half-replaced package.
$deadline = (Get-Date).AddSeconds(30)
$refreshCompleted = $false
while ((Get-Date) -lt $deadline) {
    # R5-G1: keep echoing any log content flushed after the runner exited.
    Write-NewLogLines -Path $logFile
    $logText = if (Test-Path $logFile) { Get-Content $logFile -Raw -ErrorAction SilentlyContinue } else { $null }
    if ($logText -match 'Installer package refresh failed\.') { break }
    if ($logText -match 'Installer package refresh completed\.') { $refreshCompleted = $true; break }
    Start-Sleep -Seconds 2
}
Write-NewLogLines -Path $logFile

if (-not $refreshCompleted) {
    if (Test-Path $logFile) {
        Write-Host '--- Refresh log tail ---'
        Get-Content $logFile -Tail 20 | ForEach-Object { Write-Host "  $_" }
    }
    else {
        Write-Host "Refresh log was never created: $logFile"
    }
    Exit-WithFailure -Message "Installer package refresh did not complete. See the log above."
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
    Exit-WithFailure -Message "Package-object sync failed with exit code $($syncProcess.ExitCode)." -Code $syncProcess.ExitCode
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
    Exit-WithFailure -Message "Upgrade/complete failed with exit code $($process.ExitCode)." -Code $process.ExitCode
}

Write-Host 'Upgrade/complete finished. HostAgent picks up the new desired versions on its next cycle.'
exit 0
