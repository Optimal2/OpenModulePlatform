<#
.SYNOPSIS
One-time bootstrap for the tracked git hooks.
.DESCRIPTION
Points this clone at the tracked .githooks directory. Run once after cloning,
or after the tracked hooks change. Mirrors the consumer repositories, which
have had this while OpenModulePlatform did not - which is why broken pushes
were only caught by GitHub CI here.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
& git -C $repoRoot config core.hooksPath '.githooks'
if ($LASTEXITCODE -ne 0) {
    throw "git config core.hooksPath failed with exit code $LASTEXITCODE"
}
$configuredPath = & git -C $repoRoot config core.hooksPath
Write-Host "Git hooks path configured: $configuredPath"
Write-Host "Tracked hooks active: pre-push (runs scripts/local-ci.ps1)."
Write-Host "Emergency bypass: git push --no-verify"
