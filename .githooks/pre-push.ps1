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
# (core.hooksPath is absolute), so $PSScriptRoot pointed a worktree push at the
# MAIN checkout's tree and local-ci.ps1 -- the gate then measured content that
# was not being pushed. Git runs pre-push with the pushing work tree as cwd.
$repoRoot = (git rev-parse --show-toplevel).Trim().Replace('/', '\')
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

$localCi = Join-Path $repoRoot 'scripts\local-ci.ps1'

if (-not (Test-Path $localCi)) {
    Write-Host "pre-push: scripts/local-ci.ps1 is missing; refusing to pass silently."
    exit 1
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
