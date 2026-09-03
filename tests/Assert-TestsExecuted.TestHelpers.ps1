# Shared setup for Assert-TestsExecuted.Tests.ps1.
# Dot-sourced from each Describe block's BeforeAll: Pester 5 runs every
# container in a separate session state, so functions and variables defined
# at file scope are not visible inside It blocks.

$ErrorActionPreference = 'Stop'

$script:GateScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/omp/assert-tests-executed.ps1'

function New-TrxFile {
    <#
    .SYNOPSIS
    Writes a minimal VSTest .trx fixture with the given Counters values.
    The gate parses with namespace-agnostic XPath (local-name()), so the
    fixture carries the standard TeamTest namespace to prove real files parse.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $false)][int]$Total = 3,
        [Parameter(Mandatory = $false)][int]$Executed = 3,
        [Parameter(Mandatory = $false)][string]$SkippedTestName = '',
        [Parameter(Mandatory = $false)][string]$SkipReason = ''
    )

    $resultsXml = ''
    if (-not [string]::IsNullOrWhiteSpace($SkippedTestName)) {
        $resultsXml = @"
  <Results>
    <UnitTestResult testName="$SkippedTestName" outcome="NotExecuted">
      <Output>
        <ErrorInfo>
          <Message>$SkipReason</Message>
        </ErrorInfo>
      </Output>
    </UnitTestResult>
  </Results>
"@
    }

    $trx = @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
$resultsXml
  <ResultSummary outcome="Completed">
    <Counters total="$Total" executed="$Executed" passed="$Executed" failed="0" />
  </ResultSummary>
</TestRun>
"@

    [System.IO.File]::WriteAllText($Path, $trx, [System.Text.UTF8Encoding]::new($false))
}

function New-RawTrxFile {
    <#
    .SYNOPSIS
    Writes raw text as a .trx file, for the malformed/truncated fixture cases
    where New-TrxFile's well-formed shape is exactly what must NOT be emitted.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content
    )

    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Invoke-ExecutionGate {
    <#
    .SYNOPSIS
    Runs the gate as its own child process and measures its EXIT CODE, which
    is the contract. The gate ends with explicit exit statements, so an
    in-process & call would terminate the Pester host on failure.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ResultsDirectory,
        [Parameter(Mandatory = $false)][switch]$ShowSkipReasons,
        [Parameter(Mandatory = $false)][switch]$RequirePerFile,
        [Parameter(Mandatory = $false)][int]$MinimumTrxFiles = 0
    )

    $arguments = @(
        '-NoProfile', '-File', $script:GateScript,
        '-ResultsDirectory', $ResultsDirectory,
        '-SuiteName', 'pester-fixture'
    )
    if ($ShowSkipReasons) { $arguments += '-ShowSkipReasons' }
    if ($RequirePerFile) { $arguments += '-RequirePerFile' }
    if ($MinimumTrxFiles -gt 0) { $arguments += @('-MinimumTrxFiles', "$MinimumTrxFiles") }

    # ErrorActionPreference 'Stop' would turn the child's redirected stderr
    # into a throwing ErrorRecord, so relax it locally for the capture.
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & powershell.exe $arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return @{ ExitCode = $exitCode; Output = $output }
}

function New-GateResultsDirectory {
    <#
    .SYNOPSIS
    Creates an empty temporary results directory and returns its path.
    #>
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-gate-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $path -Force
    return $path
}

function Remove-GateResultsDirectory {
    param([Parameter(Mandatory = $false)][AllowNull()][AllowEmptyString()][string]$Path)
    if (-not [string]::IsNullOrWhiteSpace($Path) -and (Test-Path -LiteralPath $Path -PathType Container)) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}
