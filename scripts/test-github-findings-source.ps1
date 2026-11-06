[CmdletBinding()]
param(
    [ValidateSet('All', 'Bootstrapper', 'Installer', 'Node')]
    [string]$Suite = 'All',
    [string]$Filter = ''
)

# Pure source checks only: no database, service, web server or deployment.
# Compatible with Windows PowerShell 5.1. Every invocation gets its own output.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$ScratchRoot = $env:AO_JOB_SCRATCH
if ([string]::IsNullOrWhiteSpace($ScratchRoot)) { $ScratchRoot = 'G:\AI_Workdir' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $ScratchRoot ('github-findings-source-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($runRoot)
Write-Host "Evidence: $runRoot"

function Invoke-NativeCheck {
    param([string]$Program, [string[]]$Arguments, [string]$Name)
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = (Get-Command $Program -ErrorAction Stop).Source
    # Arguments here are controlled paths, filters and switches, never shell code.
    $info.Arguments = ($Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    $info.WorkingDirectory = $repoRoot
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.EnvironmentVariables['GIT_TERMINAL_PROMPT'] = '0'
    $info.EnvironmentVariables['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $info.EnvironmentVariables['TMP'] = $runRoot
    $info.EnvironmentVariables['TEMP'] = $runRoot
    $command = "$Program $($info.Arguments)"
    Write-Host "$Name command: $command"
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $runRoot ($Name + '.log')),
            $command + [Environment]::NewLine + $output + [Environment]::NewLine + "Native exit: $($process.ExitCode)")
        Write-Host $output
        if ($process.ExitCode -ne 0) {
            Write-Error "$Name failed with native exit $($process.ExitCode)." -ErrorAction Continue
            exit $process.ExitCode
        }
        return $output
    }
    finally { $process.Dispose() }
}

foreach ($name in @('Bootstrapper', 'Installer')) {
    if ($Suite -ne 'All' -and $Suite -ne $name) { continue }
    $testFilter = if ($name -eq 'Bootstrapper') {
        'FullyQualifiedName~NoOverwriteFileTests'
    } else {
        'FullyQualifiedName~AccountValidationInvalidationTests|FullyQualifiedName~AccountPasswordValidatorTests|FullyQualifiedName~MainFormTests'
    }
    if ($Filter) { $testFilter = $Filter }
    $project = Join-Path $repoRoot ("OpenModulePlatform.$name.Tests\OpenModulePlatform.$name.Tests.csproj")
    $results = Join-Path $runRoot $name
    $arguments = @('test', $project, '--filter', $testFilter, '--configuration', 'Debug',
        '--results-directory', $results, '--logger', 'trx;LogFileName=tests.trx',
        '--verbosity', 'minimal', '-m:1', '-p:UseSharedCompilation=false',
        '-p:NuGetAudit=false', ('-p:OmpIsolatedBuildRoot=' + (Join-Path $runRoot 'build')))
    $null = Invoke-NativeCheck 'dotnet' $arguments $name
    [xml]$trx = Get-Content -LiteralPath (Join-Path $results 'tests.trx') -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    if ([int]$counts.total -le 0 -or [int]$counts.executed -ne [int]$counts.total -or
        [int]$counts.passed -ne [int]$counts.total) {
        throw "$name must execute and pass every selected test. Counters: $($counts.OuterXml)"
    }
    Write-Host "$name native exit 0; $($counts.passed)/$($counts.total) passed."
}

if ($Suite -eq 'All' -or $Suite -eq 'Node') {
    $arguments = @('--test', '--test-reporter=tap')
    if ($Filter) { $arguments += '--test-name-pattern=' + $Filter }
    $arguments += @('tests/portal-dashboard-accessibility.test.cjs',
        'tests/portal-dashboard-fragment.test.cjs', 'tests/portal-dashboard-notification-restore.test.cjs')
    $output = Invoke-NativeCheck 'node' $arguments 'Node'
    if ($output -notmatch '(?m)^# tests ([1-9][0-9]*)\s*$' -or
        $output -notmatch '(?m)^# fail 0\s*$' -or $output -notmatch '(?m)^# pass ([1-9][0-9]*)\s*$') {
        throw 'Node must report a nonzero test count and zero failures.'
    }
    Write-Host 'Node native exit 0; nonzero test count verified.'
}
