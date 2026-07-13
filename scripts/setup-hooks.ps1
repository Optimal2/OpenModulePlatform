<#
.SYNOPSIS
One-time bootstrap for the OMP tracked git hooks.

.DESCRIPTION
Configures this git clone to use the .githooks directory under the repository
root. Run this once after cloning (or after the tracked hooks change).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$hooksDir = '.githooks'

& git -C $repoRoot config core.hooksPath $hooksDir
if ($LASTEXITCODE -ne 0) {
    throw "git config core.hooksPath failed with exit code $LASTEXITCODE"
}

$configuredPath = & git -C $repoRoot config core.hooksPath
Write-Host "Git hooks path configured: $configuredPath"
Write-Host "Tracked hooks active: pre-commit (fast static checks), pre-push (full CI-equivalent gate)."
Write-Host "Emergency bypass for any hook: git push --no-verify"
