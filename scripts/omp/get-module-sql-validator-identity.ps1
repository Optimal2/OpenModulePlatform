#Requires -Version 5.1
<#
.SYNOPSIS
Returns a cache identity for the module validators in this checkout.

.DESCRIPTION
Writes one JSON object with the resolved platform checkout, its HEAD commit, a
SHA-256 over the validator scripts and ModuleSqlGuard source files, and a
cacheKey that combines those values. The content hash reads the working tree,
so committed, unstaged, staged and untracked source changes all produce a new
identity. Build output below tools/ModuleSqlGuard/bin and obj is excluded.

Consumers should call this script from the same resolved OpenModulePlatform
checkout whose validate-module-definitions.ps1 and Test-ModuleSqlGuards.ps1
they run, and include cacheKey in their local-CI pass-stamp key.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-GitText {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& git @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0) {
        throw "Could not read module validator git identity: $($output -join [Environment]::NewLine)"
    }
    return ([string]($output | Select-Object -First 1)).Trim()
}

function Get-Sha256Text {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

$ompRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\', '/')
$platformRoot = Invoke-GitText -Arguments @('-C', $ompRoot, 'rev-parse', '--show-toplevel')
if ([string]::IsNullOrWhiteSpace($platformRoot)) {
    throw "Module validator root is not a git work tree: $ompRoot"
}
$platformRoot = [System.IO.Path]::GetFullPath($platformRoot).TrimEnd('\', '/')
$headCommit = Invoke-GitText -Arguments @('-C', $platformRoot, 'rev-parse', '--verify', 'HEAD')
if ($headCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Module validator HEAD could not be resolved in '$platformRoot'."
}

$inputFiles = [System.Collections.Generic.List[string]]::new()
foreach ($relativePath in @(
    'scripts/omp/Test-ModuleSqlGuards.ps1',
    'scripts/omp/validate-module-definitions.ps1'
)) {
    $inputFiles.Add((Join-Path $platformRoot ($relativePath -replace '/', '\')))
}

$toolRoot = Join-Path $platformRoot 'tools\ModuleSqlGuard'
if (Test-Path -LiteralPath $toolRoot -PathType Container) {
    foreach ($file in [System.IO.Directory]::EnumerateFiles($toolRoot, '*', [System.IO.SearchOption]::AllDirectories)) {
        $relativeToolPath = $file.Substring($toolRoot.Length).TrimStart('\', '/')
        if ($relativeToolPath -match '(?i)(^|[\\/])(bin|obj)([\\/]|$)') {
            continue
        }
        $inputFiles.Add($file)
    }
}

$contentLines = [System.Collections.Generic.List[string]]::new()
foreach ($file in @($inputFiles | Sort-Object)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Module validator input is missing: $file"
    }
    $relativePath = $file.Substring($platformRoot.Length).TrimStart('\', '/').Replace('\', '/')
    $fileHash = Get-Sha256Text -Bytes ([System.IO.File]::ReadAllBytes($file))
    $contentLines.Add("$relativePath`:$fileHash")
}
$contentText = ($contentLines -join "`n") + "`n"
$contentSha256 = Get-Sha256Text -Bytes ([System.Text.Encoding]::UTF8.GetBytes($contentText))
$cacheKeySource = "1`n$($platformRoot.ToLowerInvariant())`n$($headCommit.ToLowerInvariant())`n$contentSha256`n"
$cacheKey = Get-Sha256Text -Bytes ([System.Text.Encoding]::UTF8.GetBytes($cacheKeySource))

[pscustomobject]@{
    schemaVersion = 1
    platformRoot = $platformRoot
    headCommit = $headCommit
    contentSha256 = $contentSha256
    cacheKey = $cacheKey
} | ConvertTo-Json -Compress
