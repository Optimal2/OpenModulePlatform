#Requires -Version 5.1
<#
.SYNOPSIS
Returns a cache identity for the module validators in this checkout.

.DESCRIPTION
Writes one JSON object with the resolved platform checkout, its HEAD commit, a
SHA-256 over the validator scripts and every file the validator is built from,
and a cacheKey that combines those values. The build inputs are derived from
tools/ModuleSqlGuard/ModuleSqlGuard.csproj: the project folders it references
transitively, the files those projects compile in from elsewhere (the rule
sources under shared/), and the repository-wide build files. The content hash
reads the working tree, so committed, unstaged, staged and untracked source
changes all produce a new identity. What MSBuild leaves out of a project is left
out here too: bin, obj and artifacts directly below a project folder, and
dot-folders.

Consumers should call this script from the same resolved OpenModulePlatform
checkout whose validate-module-definitions.ps1 and Test-ModuleSqlGuards.ps1
they run, and include cacheKey in their local-CI pass-stamp key.

The script is self-contained on purpose. Consumer test fixtures copy this one
file into a stand-in checkout, beside stand-ins for the two validator scripts
and nothing else, and it has to produce an identity there. It therefore holds
the definition of "what the validator is built from" itself, and the guard's
helper file (module-sql-validator-support.ps1) takes it from here instead of the
other way round. Do not make this script depend on a neighbouring file.

.PARAMETER DefineFunctionsOnly
Define the functions and return without computing anything. For dot-sourcing by
module-sql-validator-support.ps1, so the source list has one definition.
#>
[CmdletBinding()]
param([switch]$DefineFunctionsOnly)

Set-StrictMode -Version Latest

function Test-ModuleSqlValidatorGeneratedPath {
    <#
    .SYNOPSIS
    True for a path below a project folder that MSBuild does not take sources from.

    .DESCRIPTION
    Mirrors the default item excludes that apply to projects in this repository: the
    SDK leaves out bin and obj directly below the project folder and every dot-folder,
    and Directory.Build.props adds artifacts directly below the project folder. A file
    there is never compiled, so it must not change the identity, and it must not make
    a current validator look stale.
    #>
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    return $RelativePath -match '(?i)^(bin|obj|artifacts)([\\/]|$)' -or
        $RelativePath -match '(^|[\\/])\.[^\\/]+[\\/]'
}

function Get-ModuleSqlValidatorSourceFiles {
    <#
    .SYNOPSIS
    Every file the module SQL validator is built from, as sorted full paths.

    .DESCRIPTION
    Starts at tools/ModuleSqlGuard/ModuleSqlGuard.csproj and follows ProjectReference
    items transitively. For each project it takes every file below the project folder
    that MSBuild would consider, and every file an item in the project file includes
    from outside that folder (the rule sources under shared/ are compiled in that way).
    The repository-wide build files that shape every project are included as well.

    Taking whole project folders is deliberately generous: an extra file can only
    make a cache key change once too often, while a missing one lets a changed rule
    keep an old key or an old validator pass as current.

    A referenced project or an included file that does not exist is an error, because
    a list that silently omits it would not describe the validator. A checkout without
    the project file (a consumer's stand-in fixture) falls back to the files in the
    tool folder.
    #>
    param([Parameter(Mandatory = $true)][string]$PlatformRoot)

    $root = [System.IO.Path]::GetFullPath($PlatformRoot).TrimEnd('\', '/')
    $files = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json')) {
        $buildFile = Join-Path $root $name
        if (Test-Path -LiteralPath $buildFile -PathType Leaf) { [void]$files.Add($buildFile) }
    }

    $toolRoot = Join-Path $root 'tools\ModuleSqlGuard'
    $entryProject = Join-Path $toolRoot 'ModuleSqlGuard.csproj'
    if (-not (Test-Path -LiteralPath $entryProject -PathType Leaf)) {
        if (Test-Path -LiteralPath $toolRoot -PathType Container) {
            foreach ($file in [System.IO.Directory]::EnumerateFiles($toolRoot, '*', [System.IO.SearchOption]::AllDirectories)) {
                $relative = $file.Substring($toolRoot.Length).TrimStart('\', '/')
                if (-not (Test-ModuleSqlValidatorGeneratedPath -RelativePath $relative)) { [void]$files.Add($file) }
            }
        }
        return @($files | Sort-Object)
    }

    $pending = New-Object 'System.Collections.Generic.Queue[string]'
    $seenProjects = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $pending.Enqueue([System.IO.Path]::GetFullPath($entryProject))
    $includeItems = @('Compile', 'EmbeddedResource', 'Content', 'None', 'AdditionalFiles')

    while ($pending.Count -gt 0) {
        $projectPath = $pending.Dequeue()
        if (-not $seenProjects.Add($projectPath)) { continue }
        if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
            throw "Module SQL validator project is missing: $projectPath"
        }

        $projectDirectory = Split-Path -Parent $projectPath
        foreach ($file in [System.IO.Directory]::EnumerateFiles($projectDirectory, '*', [System.IO.SearchOption]::AllDirectories)) {
            $relative = $file.Substring($projectDirectory.Length).TrimStart('\', '/')
            if (-not (Test-ModuleSqlValidatorGeneratedPath -RelativePath $relative)) { [void]$files.Add($file) }
        }

        $projectXml = New-Object System.Xml.XmlDocument
        $projectXml.Load($projectPath)
        foreach ($node in $projectXml.SelectNodes('//*[@Include]')) {
            $include = [string]$node.GetAttribute('Include')
            if ([string]::IsNullOrWhiteSpace($include)) { continue }

            if ($node.LocalName -eq 'ProjectReference') {
                $referenced = [System.IO.Path]::GetFullPath((Join-Path $projectDirectory $include))
                if (-not (Test-Path -LiteralPath $referenced -PathType Leaf)) {
                    throw "Module SQL validator project '$projectPath' references a project that does not exist: $include"
                }
                $pending.Enqueue($referenced)
                continue
            }

            if ($includeItems -notcontains $node.LocalName) { continue }

            foreach ($pattern in $include.Split(';')) {
                $trimmed = $pattern.Trim()
                if ($trimmed.Length -eq 0 -or $trimmed.Contains('$(') -or $trimmed.Contains('@(')) { continue }
                if ($trimmed.IndexOfAny([char[]]@('*', '?')) -ge 0) {
                    # A wildcard below the project folder is already covered by the folder
                    # walk above. One that reaches outside cannot be listed without
                    # evaluating the project, so it is refused rather than guessed at.
                    $wildcardBase = [System.IO.Path]::GetFullPath((Join-Path $projectDirectory ($trimmed -replace '[*?].*$', '')))
                    if (-not ($wildcardBase + '\').StartsWith($projectDirectory.TrimEnd('\', '/') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
                        throw "Module SQL validator project '$projectPath' includes files outside its folder with a wildcard ('$trimmed'); list them explicitly so they can be tracked."
                    }
                    continue
                }

                $included = [System.IO.Path]::GetFullPath((Join-Path $projectDirectory $trimmed))
                if (-not (Test-Path -LiteralPath $included -PathType Leaf)) {
                    throw "Module SQL validator project '$projectPath' includes a file that does not exist: $trimmed"
                }
                [void]$files.Add($included)
            }
        }
    }

    return @($files | Sort-Object)
}

if ($DefineFunctionsOnly) { return }

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

# The validator scripts and every file the validator is built from. The build inputs
# are derived from ModuleSqlGuard.csproj and the projects it references: the rules
# themselves are compiled in from shared/ and from the HostAgent runtime, so a list
# that stopped at tools/ModuleSqlGuard kept the same key for a changed rule.
$inputFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($relativePath in @(
    'scripts/omp/Test-ModuleSqlGuards.ps1',
    'scripts/omp/validate-module-definitions.ps1'
)) {
    [void]$inputFiles.Add((Join-Path $platformRoot ($relativePath -replace '/', '\')))
}
# This script and the guard's helper shape the validation as well, so they are part
# of the identity where they exist. The helper is optional by design: a consumer's
# stand-in fixture holds this script without it.
foreach ($relativePath in @(
    'scripts/omp/get-module-sql-validator-identity.ps1',
    'scripts/omp/module-sql-validator-support.ps1'
)) {
    $optionalInput = Join-Path $platformRoot ($relativePath -replace '/', '\')
    if (Test-Path -LiteralPath $optionalInput -PathType Leaf) { [void]$inputFiles.Add($optionalInput) }
}
foreach ($file in (Get-ModuleSqlValidatorSourceFiles -PlatformRoot $platformRoot)) {
    [void]$inputFiles.Add($file)
}

$orderedInputs = New-Object 'System.Collections.Generic.List[string]'
$orderedInputs.AddRange([string[]]@($inputFiles))
$orderedInputs.Sort([System.StringComparer]::OrdinalIgnoreCase)

$contentLines = [System.Collections.Generic.List[string]]::new()
foreach ($file in $orderedInputs) {
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
