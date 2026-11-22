#Requires -Version 5.1
<#
.SYNOPSIS
Shared helpers for the module SQL validator scripts. Dot-source this file.

.DESCRIPTION
Test-ModuleSqlGuards.ps1 and get-module-sql-validator-identity.ps1 have to agree on
what the validator is built from, and the guard has to keep its isolated build
folders from accumulating. Both live here so there is one definition of each:

- Get-ModuleSqlValidatorSourceFiles lists every file the validator is built from,
  derived from ModuleSqlGuard.csproj rather than from a hand-kept list.
- Get-ModuleSqlValidatorStaleSource tells whether a built validator is older than
  those files.
- Remove-StaleModuleSqlGuardBuildRoots removes isolated build folders that are no
  longer used.

The file defines functions only and has no effect when dot-sourced.
#>

Set-StrictMode -Version Latest

$script:ModuleSqlGuardLastUsedMarker = '.omp-guard-last-used'
$script:ModuleSqlGuardCallerRootMarker = '.omp-guard-caller-root'
$script:ModuleSqlGuardRootName = 'omp-module-sql-guard'

function Test-ModuleSqlValidatorGeneratedPath {
    <#
    .SYNOPSIS
    True for a path below a project folder that holds build output, not source.
    #>
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    return $RelativePath -match '(?i)(^|[\\/])(bin|obj)([\\/]|$)'
}

function Get-ModuleSqlValidatorSourceFiles {
    <#
    .SYNOPSIS
    Every file the module SQL validator is built from, as sorted full paths.

    .DESCRIPTION
    Starts at tools/ModuleSqlGuard/ModuleSqlGuard.csproj and follows ProjectReference
    items transitively. For each project it takes every file below the project folder
    except bin and obj, and every file an item in the project file includes from
    outside that folder (the rule sources under shared/ are compiled in that way).
    The repository-wide build files that shape every project are included as well.

    Taking whole project folders is deliberately generous: an extra file can only
    make a cache key change once too often, while a missing one lets a changed rule
    keep an old key or an old validator pass as current.

    A referenced project or an included file that does not exist is an error, because
    a list that silently omits it would not describe the validator.
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
        # No project file to derive from: fall back to the tool folder itself, which is
        # all a checkout without the project can offer.
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

function Get-ModuleSqlValidatorStaleSource {
    <#
    .SYNOPSIS
    The newest validator build input that was written after the given validator was
    built, or $null when the build is at least as new as everything it is built from.

    .DESCRIPTION
    "Built" is the newest of the validator DLL and the platform assemblies beside it.
    The validator's own DLL is not enough: a change inside a referenced project that
    leaves its public surface alone recompiles that project only, so the rule can be
    current while ModuleSqlGuard.dll keeps its old time.

    Only files the build reads count (C# sources, project and props/targets files,
    resources). A file that cannot change the build, such as a note in a project
    folder or global.json, would otherwise condemn a current validator, because no
    rebuild would ever make the DLL newer than it.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ValidatorPath,
        [Parameter(Mandatory = $true)][string]$PlatformRoot
    )

    $builtUtc = [System.IO.File]::GetLastWriteTimeUtc($ValidatorPath)
    $outputDirectory = Split-Path -Parent $ValidatorPath
    foreach ($assembly in [System.IO.Directory]::GetFiles($outputDirectory, 'OpenModulePlatform.*.dll')) {
        $assemblyUtc = [System.IO.File]::GetLastWriteTimeUtc($assembly)
        if ($assemblyUtc -gt $builtUtc) { $builtUtc = $assemblyUtc }
    }

    $buildInputExtensions = @('.cs', '.csproj', '.props', '.targets', '.resx')
    $newest = $null
    $newestUtc = $builtUtc
    foreach ($file in (Get-ModuleSqlValidatorSourceFiles -PlatformRoot $PlatformRoot)) {
        if ($buildInputExtensions -notcontains [System.IO.Path]::GetExtension($file).ToLowerInvariant()) { continue }
        $writtenUtc = [System.IO.File]::GetLastWriteTimeUtc($file)
        if ($writtenUtc -gt $newestUtc) {
            $newestUtc = $writtenUtc
            $newest = $file
        }
    }

    return $newest
}

function Set-ModuleSqlGuardBuildRootInUse {
    <#
    .SYNOPSIS
    Marks an isolated build folder as used now, and records which caller owns it.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$BuildRoot,
        [Parameter(Mandatory = $true)][string]$CallerRoot
    )

    [void][System.IO.Directory]::CreateDirectory($BuildRoot)
    [System.IO.File]::WriteAllText((Join-Path $BuildRoot $script:ModuleSqlGuardCallerRootMarker), $CallerRoot)
    [System.IO.File]::WriteAllText((Join-Path $BuildRoot $script:ModuleSqlGuardLastUsedMarker), [System.DateTime]::UtcNow.ToString('o'))
}

function Remove-ModuleSqlGuardTree {
    <#
    .SYNOPSIS
    Deletes a folder tree without ever following a junction or symbolic link.

    .DESCRIPTION
    A link found inside the tree is removed as a link; what it points at is left
    alone. Read-only files are made writable first, as build output can carry that
    attribute.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)

    foreach ($entry in [System.IO.Directory]::GetFileSystemEntries($Path)) {
        $attributes = [System.IO.File]::GetAttributes($entry)
        $isDirectory = ($attributes -band [System.IO.FileAttributes]::Directory) -ne 0
        $isLink = ($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
        if ($isDirectory -and -not $isLink) {
            Remove-ModuleSqlGuardTree -Path $entry
            continue
        }

        if (($attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
            [System.IO.File]::SetAttributes($entry, ($attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)))
        }
        if ($isDirectory) { [System.IO.Directory]::Delete($entry, $false) } else { [System.IO.File]::Delete($entry) }
    }

    [System.IO.Directory]::Delete($Path, $false)
}

function Remove-StaleModuleSqlGuardBuildRoots {
    <#
    .SYNOPSIS
    Removes isolated validator build folders that are no longer used. Returns the
    folders it removed.

    .DESCRIPTION
    Each caller gets its own build folder below the guard root, named by a hash of the
    caller's repository root, and nothing ever removed them: a test that validates a
    fixture in a new temporary folder left a complete build behind on every run.

    A folder is removed when
    - its recorded caller root no longer exists and it has not been used for
      OrphanGraceMinutes (a fixture that is gone cannot come back), or
    - it has not been used for MaxAgeDays.

    "Used" is the time of the last-used marker every guard run writes, or the folder's
    own time for a folder from before the markers existed.

    What it never touches:
    - anything unless GuardRoot is an absolute path whose last segment is
      'omp-module-sql-guard' and which is a real folder, not a link;
    - anything that is not a direct child folder of GuardRoot named by a 64-character
      hash (or such a folder this function itself renamed for deletion earlier);
    - a child that is a link, the folder passed as CurrentBuildRoot, or a folder that
      was used recently;
    - a folder in which a file is open: it is renamed before deletion, which Windows
      refuses while anything inside is in use, and is then left for a later run.

    Failures are swallowed per folder. Cleanup must never fail the validation it rides on.
    #>
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$GuardRoot,
        [AllowEmptyString()][string]$CurrentBuildRoot = '',
        [int]$MaxAgeDays = 7,
        [int]$OrphanGraceMinutes = 60,
        [System.DateTime]$NowUtc = [System.DateTime]::UtcNow
    )

    $removed = New-Object 'System.Collections.Generic.List[string]'
    if ([string]::IsNullOrWhiteSpace($GuardRoot) -or -not [System.IO.Path]::IsPathRooted($GuardRoot)) { return @() }

    try {
        $root = [System.IO.Path]::GetFullPath($GuardRoot).TrimEnd('\', '/')
    }
    catch {
        return @()
    }
    if ([System.IO.Path]::GetFileName($root) -ne $script:ModuleSqlGuardRootName) { return @() }
    if (-not [System.IO.Directory]::Exists($root)) { return @() }
    if (([System.IO.File]::GetAttributes($root) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { return @() }

    $current = ''
    if (-not [string]::IsNullOrWhiteSpace($CurrentBuildRoot)) {
        $current = [System.IO.Path]::GetFullPath($CurrentBuildRoot).TrimEnd('\', '/')
    }

    foreach ($candidate in [System.IO.Directory]::GetDirectories($root)) {
        try {
            $name = [System.IO.Path]::GetFileName($candidate)
            $isBuildRoot = $name -match '^[0-9a-f]{64}$'
            $isLeftover = $name -match '^del-[0-9a-f]{16}$'
            if (-not ($isBuildRoot -or $isLeftover)) { continue }
            if (-not [string]::Equals([System.IO.Path]::GetDirectoryName($candidate), $root, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
            if (([System.IO.File]::GetAttributes($candidate) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            if ($current.Length -gt 0 -and [string]::Equals($candidate, $current, [System.StringComparison]::OrdinalIgnoreCase)) { continue }

            $doomed = $candidate
            if ($isBuildRoot) {
                $usedUtc = [System.IO.Directory]::GetLastWriteTimeUtc($candidate)
                $marker = Join-Path $candidate $script:ModuleSqlGuardLastUsedMarker
                if ([System.IO.File]::Exists($marker)) {
                    $markerUtc = [System.IO.File]::GetLastWriteTimeUtc($marker)
                    if ($markerUtc -gt $usedUtc) { $usedUtc = $markerUtc }
                }

                $orphaned = $false
                $callerMarker = Join-Path $candidate $script:ModuleSqlGuardCallerRootMarker
                if ([System.IO.File]::Exists($callerMarker)) {
                    $callerRoot = [System.IO.File]::ReadAllText($callerMarker).Trim()
                    $orphaned = $callerRoot.Length -gt 0 -and -not [System.IO.Directory]::Exists($callerRoot)
                }

                $age = $NowUtc - $usedUtc
                $expired = $age.TotalDays -ge $MaxAgeDays
                $abandoned = $orphaned -and $age.TotalMinutes -ge $OrphanGraceMinutes
                if (-not ($expired -or $abandoned)) { continue }

                # The rename is the in-use test: Windows refuses it while any file below
                # the folder is open, and it takes the folder out of every caller's way
                # before the first file is deleted. The new name is shorter than the old
                # one on purpose: build output sits close to the classic path length
                # limit, and a longer name would put its files out of reach of the
                # deletion that follows.
                $doomed = Join-Path $root ('del-' + [System.Guid]::NewGuid().ToString('N').Substring(0, 16))
                [System.IO.Directory]::Move($candidate, $doomed)
            }

            Remove-ModuleSqlGuardTree -Path $doomed
            $removed.Add($candidate)
        }
        catch {
            Write-Verbose "Module SQL validator build folder was left in place: $candidate ($($_.Exception.Message))"
        }
    }

    return @($removed)
}
