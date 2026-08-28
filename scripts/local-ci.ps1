<#
.SYNOPSIS
Runs the same gate as .github/workflows/ci.yml, before pushing instead of after.

.DESCRIPTION
OpenModulePlatform had no local gate while every consumer repository did, so a
change that breaks CI was only discovered after the push - three times on
2026-08-23 alone, each one mailing the operator about a broken build.

The failure was always the same check, and always for the same reason: the
version validator defaults its baseline to origin/main, and once a commit is
pushed origin/main IS that commit, so it compares the change to itself and
passes on everything. This script resolves the baseline the way CI does - the
commit the push will be measured against - so it can actually fail.

.PARAMETER BaseCommit
Overrides the auto-detected baseline. Rarely needed; the default matches CI.

.PARAMETER SkipTests
Skips build and tests, keeping only the fast validators. For a quick check while
iterating - never as the gate before a push.
#>
[CmdletBinding()]
param(
    [string]$BaseCommit = '',
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    $repoRoot = (Resolve-Path (Join-Path (Get-Location) '..')).Path
}
Push-Location $repoRoot

$failures = New-Object System.Collections.Generic.List[string]

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Body
    )
    Write-Host ""
    Write-Host "--- $Name"
    try {
        # A step that never launches an external process leaves $LASTEXITCODE
        # unset, and Set-StrictMode turns reading it into a failure - which
        # reported a passing validator as broken on the first run.
        $global:LASTEXITCODE = 0
        & $Body
        if ((Test-Path 'variable:global:LASTEXITCODE') -and $global:LASTEXITCODE -ne 0) {
            throw "exit code $global:LASTEXITCODE"
        }
        Write-Host "PASS: $Name"
    }
    catch {
        Write-Host "FAIL: $Name -- $($_.Exception.Message)"
        $script:failures.Add($Name)
    }
}

try {
    # The baseline is the whole point. CI uses github.event.before on a push:
    # what origin/main pointed at BEFORE the push. Locally that is the upstream
    # ref while commits are still unpushed. With nothing to push, fall back to
    # the parent of HEAD so the script still measures something real rather
    # than comparing HEAD to itself.
    if ([string]::IsNullOrWhiteSpace($BaseCommit)) {
        # Resolve the upstream WITHOUT the '@{u}' revision syntax: on a branch
        # with no upstream (every first push of a new branch) git dies with
        # "fatal: no upstream configured" on stderr - even under
        # "rev-parse --verify --quiet" - and Windows PowerShell 5.1 with
        # ErrorActionPreference=Stop promotes redirected native stderr into a
        # terminating error, so the gate aborted here instead of reaching its
        # own fallback. for-each-ref reports a missing upstream as empty output
        # and exit code 0, never via stderr.
        $currentBranch = (git rev-parse --abbrev-ref HEAD).Trim()
        $upstreamRef = (git for-each-ref --format='%(upstream:short)' "refs/heads/$currentBranch" | Out-String).Trim()
        $hasUpstream = -not [string]::IsNullOrWhiteSpace($upstreamRef)
        $unpushed = if ($hasUpstream) { (git rev-list --count "$upstreamRef..HEAD") } else { '' }
        if ($hasUpstream -and $unpushed -and [int]$unpushed -gt 0) {
            $BaseCommit = (git rev-parse $upstreamRef).Trim()
            $reason = "$unpushed unpushed commit(s); baseline is upstream"
        }
        else {
            $BaseCommit = (git rev-parse 'HEAD^').Trim()
            $reason = if ($hasUpstream) { 'nothing to push; baseline is the parent of HEAD' } else { 'no upstream configured; baseline is the parent of HEAD' }
        }
    }
    else {
        $reason = 'explicit -BaseCommit'
    }

    Write-Host "OpenModulePlatform Local CI"
    Write-Host ("Baseline: {0}  ({1})" -f $BaseCommit.Substring(0, [Math]::Min(12, $BaseCommit.Length)), $reason)

    Invoke-Step 'Validate module definitions' {
        & (Join-Path $repoRoot 'scripts\omp\validate-module-definitions.ps1')
    }

    # The check that caught every broken push. A shared project changed without
    # its consumers moving fails HERE, not in a mail 90 seconds after the push.
    Invoke-Step 'Validate component versions' {
        & (Join-Path $repoRoot 'scripts\omp\validate-component-versions.ps1') -BaseCommit $BaseCommit
    }

    Invoke-Step 'Analyze PowerShell scripts' {
        & (Join-Path $repoRoot 'scripts\omp\run-script-analyzer.ps1')
    }

    Invoke-Step 'Pester script tests' {
        & (Join-Path $repoRoot 'scripts\omp\run-script-tests.ps1')
    }

    if (-not $SkipTests) {
        Invoke-Step 'Build solution' {
            dotnet build (Join-Path $repoRoot 'OpenModulePlatform.slnx') --configuration Release --nologo -v q
        }

        Invoke-Step 'Tests' {
            $projects = @(
                'OpenModulePlatform.HostAgent.Runtime.Tests',
                'OpenModulePlatform.Portal.Tests',
                'OpenModulePlatform.Bootstrapper.Tests',
                'OpenModulePlatform.WorkerManager.WindowsService.Tests'
            )
            foreach ($project in $projects) {
                $path = Join-Path $repoRoot ("{0}\{0}.csproj" -f $project)
                if (-not (Test-Path $path)) { continue }
                dotnet test $path --configuration Release --nologo -v q --no-build
                if ($LASTEXITCODE -ne 0) { throw "$project failed" }
            }
        }
    }
    else {
        Write-Host ""
        Write-Host "SKIPPED: build and tests (-SkipTests). Not a substitute for the gate."
    }

    Write-Host ""
    Write-Host "========================================"
    if ($failures.Count -eq 0) {
        Write-Host "LOCAL CI PASSED"

        # Known limitation (deliberate, do not "fix"): the consumer stamp keys
        # carry a neighbour-HEAD leg with a documented caveat (a stamp can
        # survive the neighbour's tree going dirty, losing the validator's
        # shared-project warning). OMP IS the neighbour, so that leg drops out
        # of this key and the caveat cannot occur here.
        # Gate cache: stamp this green run so the pre-push hook can skip an
        # identical rerun. Key: (HEAD tree, resolved baseline). Never for
        # -SkipTests ("never as the gate"), never from a dirty tree. The script
        # has no -Configuration parameter - build/test hardcode Release - so no
        # configuration guard is possible or needed. Best-effort.
        try {
            $treeClean = -not (git -C $repoRoot status --porcelain 2>$null)
            if (-not $SkipTests -and $treeClean -and -not [string]::IsNullOrWhiteSpace($BaseCommit)) {
                $treeSha = (git -C $repoRoot rev-parse 'HEAD^{tree}' 2>$null)
                $gitCommonDir = (git -C $repoRoot rev-parse --git-common-dir 2>$null)
                if ($treeSha -and $gitCommonDir) {
                    if (-not [System.IO.Path]::IsPathRooted($gitCommonDir)) {
                        $gitCommonDir = Join-Path $repoRoot $gitCommonDir
                    }
                    $stampDir = Join-Path $gitCommonDir 'local-ci-pass'
                    if (-not (Test-Path $stampDir)) { $null = New-Item -ItemType Directory -Path $stampDir }
                    $stampName = "$treeSha-$BaseCommit"
                    Set-Content -Path (Join-Path $stampDir $stampName) -Value (Get-Date -Format o)
                    Get-ChildItem $stampDir | Sort-Object LastWriteTime -Descending | Select-Object -Skip 20 | Remove-Item -Force -ErrorAction SilentlyContinue
                    Write-Host "Gate cache: stamped green run ($($stampName.Substring(0, 12))...)." -ForegroundColor DarkGray
                }
            }
        }
        catch { <# best-effort; a failed stamp only costs a rerun #> }

        exit 0
    }

    Write-Host "LOCAL CI FAILED"
    foreach ($failure in $failures) { Write-Host "  - $failure" }
    exit 1
}
finally {
    Pop-Location
}
