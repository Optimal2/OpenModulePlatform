#Requires -Version 5.1
<#
.SYNOPSIS
    Proves that a web project publishes the same artifact content twice when
    only the file system timestamps of its static web assets change.

.DESCRIPTION
    Publishes the project, moves the LastWriteTime of every file under the
    project's wwwroot folder forward, publishes again into a second folder, and
    hashes both publish folders with the same algorithm as
    OpenModulePlatform.HostAgent.Runtime.Services.ArtifactHash (relative path
    with forward slashes, a zero byte, then the file bytes, files ordered by
    relative path ignoring case). The artifact import compares exactly that hash
    with omp.Artifacts.Sha256, so equal hashes here mean the same source and
    version can be imported again without "The artifact content has changed
    under the same version".

    Pass -DisableDeterministicStaticWebAssets to publish with
    OmpDeterministicStaticWebAssets=false and show the BEFORE state: the
    endpoints manifest then carries the file timestamps and the hashes differ.

    Exit 0 when the two hashes match (or, with -DisableDeterministicStaticWebAssets,
    when they differ as expected); exit 1 otherwise.

.PARAMETER ProjectPath
    The web project (.csproj) to publish.

.PARAMETER WorkRoot
    Folder for the two publish outputs and the isolated obj/bin folders. It is
    emptied first.

.PARAMETER DisableDeterministicStaticWebAssets
    Publish with the pinning target switched off, to measure the before state.

.EXAMPLE
    .\scripts\dev\test-deterministic-web-publish.ps1 -ProjectPath ..\SomeModule\SomeModule.Web\SomeModule.Web.csproj -WorkRoot $env:TEMP\omp-det
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ProjectPath,

    [Parameter(Mandatory = $true)]
    [string] $WorkRoot,

    [switch] $DisableDeterministicStaticWebAssets
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ArtifactContentSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    $root = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $files = [System.IO.Directory]::GetFiles($root, '*', [System.IO.SearchOption]::AllDirectories) |
        ForEach-Object { $_.Substring($root.Length + 1) }
    $ordered = [System.Collections.Generic.List[string]]::new([string[]] @($files))
    $ordered.Sort([System.StringComparer]::OrdinalIgnoreCase)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        foreach ($relative in $ordered) {
            $nameBytes = [System.Text.Encoding]::UTF8.GetBytes($relative.Replace('\', '/'))
            [void] $sha.TransformBlock($nameBytes, 0, $nameBytes.Length, $null, 0)
            [void] $sha.TransformBlock([byte[]] @(0), 0, 1, $null, 0)
            $content = [System.IO.File]::ReadAllBytes((Join-Path $root $relative))
            [void] $sha.TransformBlock($content, 0, $content.Length, $null, 0)
        }
        [void] $sha.TransformFinalBlock([byte[]] @(), 0, 0)
        return ([System.BitConverter]::ToString($sha.Hash) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Invoke-Publish {
    param(
        [Parameter(Mandatory = $true)][string] $OutputPath,
        [Parameter(Mandatory = $true)][string] $IsolatedRoot
    )

    $arguments = @(
        'publish', $project,
        '-c', 'Release',
        '-o', $OutputPath,
        '--nologo',
        '-v', 'quiet',
        ('-p:OmpIsolatedBuildRoot={0}' -f $IsolatedRoot)
    )
    if ($DisableDeterministicStaticWebAssets) {
        $arguments += '-p:OmpDeterministicStaticWebAssets=false'
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE for '$project'."
    }
}

$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$work = [System.IO.Path]::GetFullPath($WorkRoot)
if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force
}
[void] (New-Item -ItemType Directory -Path $work)

$first = Join-Path $work 'publish-1'
$second = Join-Path $work 'publish-2'
$isolated = Join-Path $work 'build'

Invoke-Publish -OutputPath $first -IsolatedRoot $isolated

# Newer than everything the first publish wrote, so the incremental build
# recompresses the assets and regenerates the endpoints manifest - the same
# thing a fresh checkout or worktree does.
$wwwroot = Join-Path (Split-Path -Parent $project) 'wwwroot'
$touched = 0
if (Test-Path -LiteralPath $wwwroot -PathType Container) {
    $stamp = (Get-Date).AddMinutes(1)
    foreach ($file in Get-ChildItem -LiteralPath $wwwroot -File -Recurse) {
        $file.LastWriteTime = $stamp
        $touched++
    }
}

Invoke-Publish -OutputPath $second -IsolatedRoot $isolated

$firstHash = Get-ArtifactContentSha256 -Path $first
$secondHash = Get-ArtifactContentSha256 -Path $second
$manifests = @(Get-ChildItem -LiteralPath $first -Filter '*.staticwebassets.endpoints.json' -File)

$differing = @()
foreach ($file in Get-ChildItem -LiteralPath $first -File -Recurse) {
    $relative = $file.FullName.Substring($first.Length + 1)
    $other = Join-Path $second $relative
    if (-not (Test-Path -LiteralPath $other -PathType Leaf) -or
        (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $other).Hash) {
        $differing += $relative
    }
}

$mode = if ($DisableDeterministicStaticWebAssets) { 'OmpDeterministicStaticWebAssets=false (before)' } else { 'deterministic (after)' }
Write-Host "Project:            $project"
Write-Host "Mode:               $mode"
Write-Host "wwwroot files touched: $touched"
Write-Host "Endpoints manifests:   $($manifests.Count)"
Write-Host "Publish 1 SHA-256:  $firstHash"
Write-Host "Publish 2 SHA-256:  $secondHash"
foreach ($relative in $differing) {
    Write-Host "  differs: $relative"
}

$identical = $firstHash -eq $secondHash
if ($DisableDeterministicStaticWebAssets) {
    if ($identical) {
        Write-Host 'Before state NOT reproduced: the hashes are equal even without the pinning target.'
        exit 1
    }
    Write-Host 'Before state reproduced: the hashes differ without the pinning target.'
    exit 0
}

if (-not $identical) {
    Write-Host 'NOT deterministic: the same source published two different artifacts.'
    exit 1
}
Write-Host 'Deterministic: both publishes give the same artifact SHA-256.'
exit 0
