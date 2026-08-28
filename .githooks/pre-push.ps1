<#
.SYNOPSIS
Runs the local CI gate before a push reaches GitHub.

.DESCRIPTION
Every consumer repository already had this; OpenModulePlatform did not, and on
2026-08-23 three pushes in a row broke CI on the same check and mailed the
operator each time. The check itself was never the problem - it was only ever
run after the push, and with the default baseline (origin/main), which after a
push is the pushed commit itself.

scripts/local-ci.ps1 resolves the baseline the way the workflow does, so the
version validator compares against what the push will actually be measured
against.

Emergency bypass: git push --no-verify
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve the work tree being pushed, not the directory holding the hook file:
# with git worktrees the hook always loads from the main checkout's .githooks
# (core.hooksPath is the relative .githooks, resolved against the main
# checkout), so $PSScriptRoot pointed a worktree push at the MAIN checkout's
# tree and local-ci.ps1 -- the gate then measured content that was not being
# pushed. Git runs pre-push with the pushing work tree as cwd.
$repoRoot = (git rev-parse --show-toplevel).Trim().Replace('/', '\')
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

$localCi = Join-Path $repoRoot 'scripts\local-ci.ps1'

if (-not (Test-Path $localCi)) {
    Write-Host "pre-push: scripts/local-ci.ps1 is missing; refusing to pass silently."
    exit 1
}

# Gate cache: local-ci.ps1 stamps every green gate run (never -SkipTests,
# never a dirty tree) with the key (HEAD tree, resolved baseline). If the
# single ref being pushed carries a valid stamp, the identical rerun is
# skipped. The baseline leg mirrors local-ci's baseline resolution through
# stdin: the remote sha is what origin/main pointed at before the push (what
# local-ci resolved as upstream), and an all-zero remote sha (brand-new ref)
# maps to local-ci's no-upstream fallback of HEAD^. Every case where the two
# resolutions could disagree - a run with an explicit -BaseCommit override
# (local-ci.ps1 never stamps those), a push to a ref that is not the branch
# upstream, any failing git command - deliberately fails OPEN to a miss and
# runs the full gate; a wrong hit is worse than a rerun, so do not "tighten"
# this. Anything unexpected -
# no stdin, several refs, deletes, the OMP_GATE_NOCACHE=1 escape hatch -
# falls open the same way. Keep the key computation in sync with
# scripts/local-ci.ps1.
$cacheHit = $false
try {
    if ($env:OMP_GATE_NOCACHE -ne '1') {
        $stdinLines = @([Console]::In.ReadToEnd() -split "`n" | Where-Object { $_.Trim() })
        if ($stdinLines.Count -eq 1) {
            $parts = $stdinLines[0].Trim() -split '\s+'
            if ($parts.Count -eq 4 -and $parts[1] -notmatch '^0+$') {
                $localSha = $parts[1]
                $baselineSha = $parts[3]
                if ($baselineSha -match '^0+$') {
                    $baselineSha = (git -C $repoRoot rev-parse "$localSha^" 2>$null)
                }
                $treeSha = (git -C $repoRoot rev-parse "$localSha^{tree}" 2>$null)
                $gitCommonDir = (git -C $repoRoot rev-parse --git-common-dir 2>$null)
                if ($treeSha -and $baselineSha -and $gitCommonDir) {
                    if (-not [System.IO.Path]::IsPathRooted($gitCommonDir)) {
                        $gitCommonDir = Join-Path $repoRoot $gitCommonDir
                    }
                    $stamp = Join-Path (Join-Path $gitCommonDir 'local-ci-pass') "$treeSha-$baselineSha"
                    if (Test-Path $stamp) { $cacheHit = $true }
                }
            }
        }
    }
}
catch { $cacheHit = $false }

if ($cacheHit) {
    Write-Host 'GATE CACHE HIT: this exact tree already passed local-ci against the same baseline - skipping the rerun.' -ForegroundColor Green
    Write-Host 'Force a full run with OMP_GATE_NOCACHE=1.' -ForegroundColor DarkGray
    exit 0
}

& powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File $localCi
$code = $LASTEXITCODE

if ($code -ne 0) {
    Write-Host ""
    Write-Host "Push blocked: the local CI gate failed (see above)."
    Write-Host "Fix it, or bypass deliberately with: git push --no-verify"
    exit $code
}

exit 0
