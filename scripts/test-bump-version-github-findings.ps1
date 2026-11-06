#Requires -Version 5.1
<#
.SYNOPSIS
Runs the Bump-Version Pester suites and turns the result into an exit code.

.DESCRIPTION
The canonical way to check the version helper. It loads the repository's
pinned Pester (6.1.0, from the repository-local cache in <repo>/.psmodules),
runs every Bump-Version*.Tests.ps1 suite and exits non-zero when a suite
fails, when no test ran, or when no test passed.

Nothing is installed globally: the pinned module comes from the repository-local
cache, and the cache is restored from PSGallery only when it is empty, exactly
like scripts/omp/run-script-tests.ps1 does.

The exit code is the contract:
    0  every suite passed and at least one test ran and passed
    1  a suite failed, or no test ran, or no test passed, or a suite that
       was found did not run

Every Bump-Version*.Tests.ps1 suite under the tests directory runs, with
no prefix filter and no exclusion: the encoding guards are part of the
gate, and a suite that is found but does not run has to fail the run
instead of quietly reducing the coverage.

.EXAMPLE
    powershell.exe -NoProfile -File scripts/test-bump-version-github-findings.ps1
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Invoke-Pester comes from the pinned Pester 6.1.0 module, not the inbox 3.4.0 profile.')]
[CmdletBinding()]
param(
    # Test-suite directory; defaults to the repository's tests folder.
    [Parameter(Mandatory = $false)]
    [string]$TestsPath = ''
)

$ErrorActionPreference = 'Stop'

$script:RequiredPesterVersion = '6.1.0'
# This driver lives in <repo>/scripts, so the repository root is one level up.
$repoRoot = Split-Path -Parent $PSScriptRoot

# The pinned Pester lives in the repository-local cache; pester-bootstrap.ps1
# restores that exact version into the cache when it is missing and never
# installs a global module.
. (Join-Path $PSScriptRoot 'omp/pester-bootstrap.ps1')
$pesterModulePath = Ensure-PinnedPester -RequiredVersion $script:RequiredPesterVersion -CacheRoot (Join-Path $repoRoot '.psmodules')
Import-Module (Join-Path $pesterModulePath 'Pester.psd1') -Force

$loadedPester = Get-Module -Name Pester | Select-Object -First 1
if (-not $loadedPester -or $loadedPester.Version.ToString() -ne $script:RequiredPesterVersion) {
    $loadedText = 'nothing'
    if ($loadedPester) {
        $loadedText = $loadedPester.Version.ToString()
    }
    Write-Host "GATE FAIL: expected Pester $script:RequiredPesterVersion from $pesterModulePath but loaded '$loadedText'."
    exit 1
}

if ([string]::IsNullOrWhiteSpace($TestsPath)) {
    $TestsPath = Join-Path $repoRoot 'tests'
}
$TestsPath = [System.IO.Path]::GetFullPath($TestsPath)

$suiteFiles = @(Get-ChildItem -LiteralPath $TestsPath -Filter 'Bump-Version*.Tests.ps1' -File | Sort-Object -Property Name)
if ($suiteFiles.Count -eq 0) {
    Write-Host "GATE FAIL: no Bump-Version*.Tests.ps1 suite under $TestsPath."
    exit 1
}

Write-Host ("Running {0} Bump-Version suite(s) with Pester {1}:" -f $suiteFiles.Count, $script:RequiredPesterVersion)
foreach ($suiteFile in $suiteFiles) {
    Write-Host ("  - {0}" -f $suiteFile.Name)
}

$results = Invoke-Pester -Path $suiteFiles.FullName -PassThru

# Every suite file that was found has to have run. A suite that silently
# disappeared, or a filter that excluded it, would reduce the coverage of
# the gate without saying so.
$ranPaths = @(@($results.Containers) | ForEach-Object {
        $item = $_.Item
        if ($item -is [System.IO.FileSystemInfo]) {
            $item.FullName
        }
        else {
            [System.IO.Path]::GetFullPath([string]$item)
        }
    })
$suitePaths = @($suiteFiles | ForEach-Object { $_.FullName })
$notRun = @($suitePaths | Where-Object { $ranPaths -notcontains $_ })
if ($notRun.Count -gt 0 -or $ranPaths.Count -ne $suitePaths.Count) {
    Write-Host "GATE FAIL: $($suitePaths.Count) suite file(s) were found, but only $($ranPaths.Count) container(s) ran."
    foreach ($path in $notRun) {
        Write-Host "  - found but did not run: $path"
    }
    exit 1
}

# Every suite has to run at least one test, and at least one of them has to
# pass. A suite whose tests were all removed or all skipped proves nothing,
# and a passing test in ANOTHER suite must not carry it to green.
foreach ($container in @($results.Containers)) {
    if ($container.TotalCount -eq 0) {
        Write-Host "GATE FAIL: suite file ran zero tests: $($container.Item)"
        exit 1
    }
    if ($container.PassedCount -eq 0) {
        Write-Host "GATE FAIL: suite file passed no test (skipped: $($container.SkippedCount), not run: $($container.NotRunCount)): $($container.Item)"
        exit 1
    }
}

if ($results.Result -ne 'Passed') {
    Write-Host "GATE FAIL: overall Pester result is '$($results.Result)', not 'Passed'."
    exit 1
}
if ($results.TotalCount -eq 0) {
    Write-Host 'GATE FAIL: Pester ran 0 tests. A green exit from zero assertions proves nothing.'
    exit 1
}
if ($results.PassedCount -eq 0) {
    Write-Host 'GATE FAIL: Pester ran no passing test.'
    exit 1
}
if ($results.FailedCount -gt 0) {
    Write-Host "GATE FAIL: $($results.FailedCount) test(s) failed."
    exit 1
}

Write-Host ("GATE PASS: {0} test(s) passed, 0 failed." -f $results.PassedCount)
exit 0
