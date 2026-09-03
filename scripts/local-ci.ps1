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
    $explicitBaseCommit = -not [string]::IsNullOrWhiteSpace($BaseCommit)
    if (-not $explicitBaseCommit) {
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
        # Invoke via powershell.exe like pre-push.ps1 and ci.yml do: the suites
        # intentionally run on Windows PowerShell 5.1 (one suite spawns child
        # powershell.exe processes as a Windows requirement), and an in-process
        # & call would run them under whatever engine started local-ci.
        & powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $repoRoot 'scripts\omp\run-script-tests.ps1')
        if ($LASTEXITCODE -ne 0) { throw "run-script-tests.ps1 failed ($LASTEXITCODE)" }
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
                'OpenModulePlatform.Worker.Abstractions.Tests',
                'OpenModulePlatform.WorkerProcessHost.Tests',
                'OpenModulePlatform.WorkerManager.WindowsService.Tests'
            )
            # Remove stale TRX files so the zero-execution gate below only sees
            # the current run; old files would mask a run that produced nothing.
            $testResultsDirectory = Join-Path $repoRoot 'TestResults'
            if (Test-Path -LiteralPath $testResultsDirectory) {
                Remove-Item -LiteralPath $testResultsDirectory -Recurse -Force
            }
            # A skipped project used to read as a passing one: the loop simply
            # never ran it. A missing csproj means a renamed/moved project, and
            # that is a hard failure, not a warning: the remaining projects
            # would still satisfy the zero-execution gate, and the green run
            # would even stamp the gate cache, silently dropping the project's
            # coverage from then on.
            foreach ($project in $projects) {
                $path = Join-Path $repoRoot ("{0}\{0}.csproj" -f $project)
                if (-not (Test-Path $path)) {
                    throw "SKIPPED PROJECT: $project -- no project file at $path. A renamed/moved test project must update this list, not hide."
                }
                # LogFileName names each trx after its project: the default
                # machine/user/timestamp name lets two projects finishing in
                # the same second overwrite each other, and named files make
                # the gate's per-file output readable.
                dotnet test $path --configuration Release --nologo -v q --no-build --logger "trx;LogFileName=$project.trx" --results-directory $testResultsDirectory
                if ($LASTEXITCODE -ne 0) { throw "$project failed" }
            }
            # Zero-execution gate: VSTest exits 0 even when nothing ran, so a
            # green step does not prove anything executed. -RequirePerFile is
            # correct here: every listed project is a non-UI suite that must
            # run at least one test (the UiTests project is deliberately not
            # in the list). -MinimumTrxFiles catches a results file that never
            # got written at all, which the per-file check cannot see.
            & (Join-Path $repoRoot 'scripts\omp\assert-tests-executed.ps1') -ResultsDirectory $testResultsDirectory -SuiteName 'local tests' -ShowSkipReasons -RequirePerFile -MinimumTrxFiles $projects.Count
            if ($LASTEXITCODE -ne 0) { throw "zero-execution gate failed ($LASTEXITCODE)" }
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
        # -SkipTests ("never as the gate"), never from a dirty tree, and never
        # for an explicit -BaseCommit override: the hook can only reproduce the
        # auto-resolved baseline leg from the push's stdin, so an explicitly
        # overridden run must fail OPEN to a cache miss there (a wrong hit is
        # worse than a rerun - do not "tighten" this). The script has no
        # -Configuration parameter - build/test hardcode Release - so no
        # configuration guard is possible or needed. Best-effort.
        try {
            $treeClean = -not (git -C $repoRoot status --porcelain 2>$null)
            if (-not $SkipTests -and $treeClean -and -not $explicitBaseCommit -and -not [string]::IsNullOrWhiteSpace($BaseCommit)) {
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
