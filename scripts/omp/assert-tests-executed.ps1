<#
.SYNOPSIS
    Zero-execution gate for VSTest TRX results.

.DESCRIPTION
    Parses every *.trx file in a results directory and sums the ResultSummary
    Counters (total/executed). A test step whose filter silently matches
    nothing (or whose tests all skip) still lets VSTest exit 0, so a green
    step does not prove anything ran. This script turns that false green red:
    it exits 1 when no TRX file exists or when executed == 0.

    With -ShowSkipReasons it also prints every NotExecuted test together with
    its recorded skip reason, so intentional skips (for example UI tests
    without local prerequisites) stay readable instead of hiding as green.

    With -RequirePerFile every individual .trx file must show executed > 0.
    The default sums executed across ALL files in the directory, so a test
    project whose filter matched nothing is masked by a sibling project in
    the same step that did run tests; -RequirePerFile closes that hole.

    A .trx file WITHOUT a ResultSummary/Counters node (truncated, corrupt, or
    written by a logger stub) is categorically different from a legitimate
    executed="0" and always fails the gate, with or without -RequirePerFile:
    it must never blend silently into the directory sum.

    With -MinimumTrxFiles the gate also fails when FEWER .trx files exist than
    the caller expected, which catches a results file that never got written
    at all (per-file checks can only inspect files that exist).

    Canonical location: OpenModulePlatform/scripts/omp/. Consumer
    repositories invoke this copy (from a sibling checkout or a CI checkout
    of OpenModulePlatform) instead of keeping their own, so the gate cannot
    drift per repo.

.PARAMETER ResultsDirectory
    Directory containing the *.trx files to check (searched non-recursively).

.PARAMETER SuiteName
    Label used in log output, e.g. 'fast gate (Category!=Ui)'.

.PARAMETER ShowSkipReasons
    Print each skipped (NotExecuted) test with its skip reason.

.PARAMETER RequirePerFile
    Require executed > 0 in EVERY .trx file, not just in the directory sum.

.PARAMETER MinimumTrxFiles
    Fail when the directory contains fewer .trx files than this. 0 (default)
    disables the check. Callers that test a known list of projects should
    pass that list's count, so a project whose results file never got
    written cannot hide.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ResultsDirectory,

    [Parameter(Mandatory = $false)]
    [string]$SuiteName = 'tests',

    [Parameter(Mandatory = $false)]
    [switch]$ShowSkipReasons,

    [Parameter(Mandatory = $false)]
    [switch]$RequirePerFile,

    [Parameter(Mandatory = $false)]
    [int]$MinimumTrxFiles = 0
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $ResultsDirectory -PathType Container)) {
    Write-Host "GATE FAIL [$SuiteName]: results directory not found: $ResultsDirectory" -ForegroundColor Red
    exit 1
}

$trxFiles = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -File)
if ($trxFiles.Count -eq 0) {
    Write-Host "GATE FAIL [$SuiteName]: no .trx files found in $ResultsDirectory" -ForegroundColor Red
    exit 1
}

if ($MinimumTrxFiles -gt 0 -and $trxFiles.Count -lt $MinimumTrxFiles) {
    Write-Host "GATE FAIL [$SuiteName]: expected at least $MinimumTrxFiles .trx file(s), found $($trxFiles.Count) in $ResultsDirectory." -ForegroundColor Red
    Write-Host 'A results file that never got written is invisible to the per-file checks.' -ForegroundColor Red
    exit 1
}

$totalTests = 0
$executedTests = 0
$skipped = [System.Collections.Generic.List[hashtable]]::new()
$zeroExecutedFiles = [System.Collections.Generic.List[string]]::new()
$malformedFiles = [System.Collections.Generic.List[string]]::new()

foreach ($trxFile in $trxFiles) {
    # XmlDocument.Load honours the file's declared encoding; a Get-Content
    # cast would have to guess it.
    $trx = New-Object System.Xml.XmlDocument
    $trx.Load($trxFile.FullName)

    $fileExecuted = 0
    # Anchored to the VSTest shape: exactly one Counters node, under
    # ResultSummary at the document root.
    $counters = $trx.SelectSingleNode('/*[local-name()="TestRun"]/*[local-name()="ResultSummary"]/*[local-name()="Counters"]')
    # A missing Counters node (or missing counters attributes: GetAttribute
    # returns '' for an absent one, which casts to 0) means a truncated or
    # corrupt file. That is categorically different from a legitimate
    # executed="0" and must never blend silently into the directory sum.
    if ($null -eq $counters -or -not $counters.HasAttribute('total') -or -not $counters.HasAttribute('executed')) {
        $malformedFiles.Add($trxFile.Name)
        continue
    }
    $totalTests += [int]$counters.GetAttribute('total')
    $fileExecuted = [int]$counters.GetAttribute('executed')
    $executedTests += $fileExecuted

    if ($fileExecuted -eq 0) {
        $zeroExecutedFiles.Add($trxFile.Name)
    }

    if ($ShowSkipReasons) {
        $skippedNodes = $trx.SelectNodes('//*[local-name()="UnitTestResult"][@outcome="NotExecuted"]')
        foreach ($node in $skippedNodes) {
            $messageNode = $node.SelectSingleNode('./*[local-name()="Output"]/*[local-name()="ErrorInfo"]/*[local-name()="Message"]')
            if ($null -eq $messageNode) {
                $messageNode = $node.SelectSingleNode('./*[local-name()="Output"]/*[local-name()="Message"]')
            }

            $reason = '(no skip reason recorded)'
            if ($null -ne $messageNode) {
                $firstLine = @($messageNode.InnerText -split "\r?\n")[0]
                if (-not [string]::IsNullOrWhiteSpace($firstLine)) {
                    $reason = $firstLine.Trim()
                }
            }

            $skipped.Add(@{ Name = $node.GetAttribute('testName'); Reason = $reason })
        }
    }
}

Write-Host "Gate [$SuiteName]: executed $executedTests of $totalTests test(s) across $($trxFiles.Count) .trx file(s)."

if ($ShowSkipReasons -and $skipped.Count -gt 0) {
    Write-Host "Skipped tests ($($skipped.Count)):"
    foreach ($entry in $skipped) {
        Write-Host "  SKIP: $($entry.Name)"
        Write-Host "        $($entry.Reason)"
    }
}

if ($malformedFiles.Count -gt 0) {
    Write-Host "GATE FAIL [$SuiteName]: $($malformedFiles.Count) .trx file(s) have no ResultSummary/Counters node (truncated or corrupt):" -ForegroundColor Red
    foreach ($name in $malformedFiles) {
        Write-Host "  - $name" -ForegroundColor Red
    }
    Write-Host 'A malformed file is not a legitimate zero run; re-run the test step and inspect the logger.' -ForegroundColor Red
    exit 1
}

if ($RequirePerFile -and $zeroExecutedFiles.Count -gt 0) {
    Write-Host "GATE FAIL [$SuiteName]: $($zeroExecutedFiles.Count) .trx file(s) show 0 executed tests:" -ForegroundColor Red
    foreach ($name in $zeroExecutedFiles) {
        Write-Host "  - $name" -ForegroundColor Red
    }
    Write-Host 'A per-project zero run is masked by the directory sum without -RequirePerFile.' -ForegroundColor Red
    exit 1
}

if ($executedTests -eq 0) {
    Write-Host "GATE FAIL [$SuiteName]: 0 tests executed. The run would have reported green without running anything." -ForegroundColor Red
    exit 1
}

exit 0
