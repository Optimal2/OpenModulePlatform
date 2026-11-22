#Requires -Version 5.1
<#
.SYNOPSIS
Shared helpers for Test-ModuleSqlGuards.ps1. Dot-source this file.

.DESCRIPTION
The guard builds the validator into an isolated folder per caller and has to keep
those folders from accumulating, and under -NoBuild it has to know whether a built
validator is still current.

- Get-ModuleSqlValidatorStaleSource tells whether a built validator is older than
  the files it is built from.
- Enter-ModuleSqlGuardBuildRoot marks an isolated build folder as used and holds it
  against removal for as long as the caller keeps the returned handle.
- Remove-StaleModuleSqlGuardBuildRoots removes isolated build folders that are no
  longer used.

The list of files the validator is built from is NOT defined here. It lives in
get-module-sql-validator-identity.ps1, which consumers copy on its own into their
test fixtures and which therefore has to work without this file; this file takes
the definition from there, so there is still exactly one.

The file defines functions only and has no effect when dot-sourced.
#>

Set-StrictMode -Version Latest

# The one definition of "what the validator is built from"
# (Get-ModuleSqlValidatorSourceFiles and the build-output filter it uses).
. (Join-Path $PSScriptRoot 'get-module-sql-validator-identity.ps1') -DefineFunctionsOnly

$script:ModuleSqlGuardLastUsedMarker = '.omp-guard-last-used'
$script:ModuleSqlGuardCallerRootMarker = '.omp-guard-caller-root'
$script:ModuleSqlGuardRootName = 'omp-module-sql-guard'

# Names are compared case-sensitively (-cmatch): the guard writes its hash in lower
# case, and anything else in the folder is not the guard's to remove.
$script:ModuleSqlGuardBuildRootPattern = '^[0-9a-f]{64}$'
$script:ModuleSqlGuardLeftoverPattern = '^del-[0-9a-f]{16}$'

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
    rebuild would ever make the DLL newer than it. For the same reason the source
    list leaves out what MSBuild leaves out of a project (bin, obj and artifacts
    directly below the project folder, and dot-folders).

    The comparison is by modification time. A source whose content changed while its
    time was kept is not detected.
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

function Enter-ModuleSqlGuardBuildRoot {
    <#
    .SYNOPSIS
    Marks an isolated build folder as used and returns an open handle on its marker.
    The folder cannot be removed by the cleanup while the handle is open.

    .DESCRIPTION
    Marking alone leaves a gap: a cleanup in another process can read the folder's
    age just before this run marks it and rename it just after, taking the folder
    away from under a build that has already started. The cleanup renames a folder
    before it deletes anything, and Windows refuses to rename a folder while a file
    in it is open, so holding the marker open closes the gap without a lock of its
    own: the rename fails and the cleanup leaves the folder alone.

    The handle is opened for reading and shares reading and writing, but not
    deletion, so several runs of the same caller can hold it at once and each can
    still refresh the marker.

    If a cleanup wins the race between the marking and the open, the folder is gone
    and the open fails; the folder is then simply created and marked again.

    With -ExistingOnly nothing is created: the result is $null when the folder does
    not exist (a -NoBuild run has no business creating a build folder).
    #>
    param(
        [Parameter(Mandatory = $true)][string]$BuildRoot,
        [Parameter(Mandatory = $true)][string]$CallerRoot,
        [switch]$ExistingOnly
    )

    $marker = Join-Path $BuildRoot $script:ModuleSqlGuardLastUsedMarker
    for ($attempt = 1; ; $attempt++) {
        if ($ExistingOnly -and -not [System.IO.Directory]::Exists($BuildRoot)) { return $null }
        try {
            Set-ModuleSqlGuardBuildRootInUse -BuildRoot $BuildRoot -CallerRoot $CallerRoot
            return [System.IO.File]::Open(
                $marker,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::ReadWrite)
        }
        catch [System.IO.IOException] {
            # DirectoryNotFoundException and FileNotFoundException are IOExceptions: the
            # folder was renamed away between the marking and the open.
            if ($attempt -ge 5) { throw }
        }
    }
}

function Test-ModuleSqlGuardRoot {
    <#
    .SYNOPSIS
    True when the path is a guard root this file may remove folders from: an absolute
    path whose last segment is 'omp-module-sql-guard' and which is a real folder, not
    a junction or symbolic link. Returns the normalized root through -Root.
    #>
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$GuardRoot,
        [ref]$Root
    )

    if ([string]::IsNullOrWhiteSpace($GuardRoot) -or -not [System.IO.Path]::IsPathRooted($GuardRoot)) { return $false }
    try {
        $normalized = [System.IO.Path]::GetFullPath($GuardRoot).TrimEnd('\', '/')
    }
    catch {
        return $false
    }
    if ([System.IO.Path]::GetFileName($normalized) -cne $script:ModuleSqlGuardRootName) { return $false }
    if (-not [System.IO.Directory]::Exists($normalized)) { return $false }
    if (([System.IO.File]::GetAttributes($normalized) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }

    if ($null -ne $Root) { $Root.Value = $normalized }
    return $true
}

function Test-ModuleSqlGuardRemovableFolder {
    <#
    .SYNOPSIS
    True when the path is a folder the cleanup may delete: a real folder (not a
    junction or symbolic link) directly below the given, already verified guard root,
    named like a build folder or like a folder the cleanup renamed for deletion.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $name = [System.IO.Path]::GetFileName($Path)
    if (-not ($name -cmatch $script:ModuleSqlGuardBuildRootPattern -or $name -cmatch $script:ModuleSqlGuardLeftoverPattern)) { return $false }
    if (-not [string]::Equals([System.IO.Path]::GetDirectoryName($Path), $Root, [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
    if (-not [System.IO.Directory]::Exists($Path)) { return $false }
    if (([System.IO.File]::GetAttributes($Path) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
    return $true
}

function Remove-ModuleSqlGuardTreeContent {
    <#
    .SYNOPSIS
    Deletes what is inside a folder, then the folder, without following links.
    Internal: call Remove-ModuleSqlGuardTree, which decides whether the folder may go.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)

    if (([System.IO.File]::GetAttributes($Path) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to delete through a junction or symbolic link: $Path"
    }

    foreach ($entry in [System.IO.Directory]::GetFileSystemEntries($Path)) {
        $attributes = [System.IO.File]::GetAttributes($entry)
        $isDirectory = ($attributes -band [System.IO.FileAttributes]::Directory) -ne 0
        $isLink = ($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
        if ($isDirectory -and -not $isLink) {
            Remove-ModuleSqlGuardTreeContent -Path $entry
            continue
        }

        if (($attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
            [System.IO.File]::SetAttributes($entry, ($attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)))
        }
        if ($isDirectory) { [System.IO.Directory]::Delete($entry, $false) } else { [System.IO.File]::Delete($entry) }
    }

    [System.IO.Directory]::Delete($Path, $false)
}

function Remove-ModuleSqlGuardTree {
    <#
    .SYNOPSIS
    Deletes one build folder below the guard root, without ever following a junction
    or symbolic link.

    .DESCRIPTION
    The function checks its own argument and does not rely on its caller having done
    so: the path must be a real folder directly below a real guard root and be named
    like a build folder or like one renamed for deletion. Anything else is refused
    with an error and nothing is deleted. In particular a junction passed here is
    refused; emptying it would delete the files of whatever it points at.

    A link found inside the tree is removed as a link; what it points at is left
    alone. Read-only files are made writable first, as build output can carry that
    attribute.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$GuardRoot
    )

    $root = ''
    if (-not (Test-ModuleSqlGuardRoot -GuardRoot $GuardRoot -Root ([ref]$root))) {
        throw "Refusing to delete below '$GuardRoot': it is not a module SQL guard root."
    }
    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if (-not (Test-ModuleSqlGuardRemovableFolder -Path $fullPath -Root $root)) {
        throw "Refusing to delete '$Path': it is not a build folder directly below '$root', or it is a junction or symbolic link."
    }

    Remove-ModuleSqlGuardTreeContent -Path $fullPath
}

function Test-ModuleSqlGuardTreeWithinClassicPathLimit {
    <#
    .SYNOPSIS
    True when every path in the tree is short enough for an engine without long path
    support to delete it. Links are not followed.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        $pending = New-Object 'System.Collections.Generic.Stack[string]'
        $pending.Push($Path)
        while ($pending.Count -gt 0) {
            foreach ($entry in [System.IO.Directory]::GetFileSystemEntries($pending.Pop())) {
                # 248 is the classic limit for a folder path and the stricter of the two.
                if ($entry.Length -ge 248) { return $false }
                $attributes = [System.IO.File]::GetAttributes($entry)
                if (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0 -and
                    ($attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
                    $pending.Push($entry)
                }
            }
        }
        return $true
    }
    catch {
        # An engine that cannot even list the tree cannot delete it either.
        return $false
    }
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
    - anything that is not a direct child folder of GuardRoot named by a lower-case
      64-character hash (or such a folder this function itself renamed for deletion
      earlier);
    - a child that is a link, the folder passed as CurrentBuildRoot, or a folder that
      was used recently;
    - a folder in which a file is open, which includes every folder a running guard
      holds through Enter-ModuleSqlGuardBuildRoot: it is renamed before deletion,
      which Windows refuses while anything inside is open, and is then left for a
      later run. The folder is checked again after the rename, immediately before
      the first file is deleted.

    Windows PowerShell cannot delete a path beyond the classic length limit, although
    it can rename the folder above it. A folder holding such a path is therefore not
    touched by that engine at all and is reported in one line; a PowerShell 7 run
    removes it. (ClassicPathLimit defaults to the running engine and exists as a
    parameter so the rule can be tested on either.)

    Failures are swallowed per folder. Cleanup must never fail the validation it rides on.
    #>
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$GuardRoot,
        [AllowEmptyString()][string]$CurrentBuildRoot = '',
        [int]$MaxAgeDays = 7,
        [int]$OrphanGraceMinutes = 60,
        [System.DateTime]$NowUtc = [System.DateTime]::UtcNow,
        [bool]$ClassicPathLimit = ($PSVersionTable.PSVersion.Major -le 5)
    )

    $removed = New-Object 'System.Collections.Generic.List[string]'
    $root = ''
    if (-not (Test-ModuleSqlGuardRoot -GuardRoot $GuardRoot -Root ([ref]$root))) { return @() }

    $current = ''
    if (-not [string]::IsNullOrWhiteSpace($CurrentBuildRoot)) {
        $current = [System.IO.Path]::GetFullPath($CurrentBuildRoot).TrimEnd('\', '/')
    }

    foreach ($candidate in [System.IO.Directory]::GetDirectories($root)) {
        try {
            if (-not (Test-ModuleSqlGuardRemovableFolder -Path $candidate -Root $root)) { continue }
            if ($current.Length -gt 0 -and [string]::Equals($candidate, $current, [System.StringComparison]::OrdinalIgnoreCase)) { continue }

            $name = [System.IO.Path]::GetFileName($candidate)
            $isBuildRoot = $name -cmatch $script:ModuleSqlGuardBuildRootPattern
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
            }

            if ($ClassicPathLimit -and -not (Test-ModuleSqlGuardTreeWithinClassicPathLimit -Path $candidate)) {
                Write-Host "Module SQL validator build folder was not removed: $candidate holds paths beyond the classic length limit, which this PowerShell engine cannot delete. A PowerShell 7 run removes it."
                continue
            }

            $doomed = $candidate
            if ($isBuildRoot) {
                # The rename is the in-use test: Windows refuses it while any file below
                # the folder is open, and it takes the folder out of every caller's way
                # before the first file is deleted. The new name is shorter than the old
                # one on purpose: build output sits close to the classic path length
                # limit, and a longer name would put its files out of reach of the
                # deletion that follows.
                $doomed = Join-Path $root ('del-' + [System.Guid]::NewGuid().ToString('N').Substring(0, 16))
                [System.IO.Directory]::Move($candidate, $doomed)
            }

            # Checks the renamed folder once more before anything is deleted.
            Remove-ModuleSqlGuardTree -Path $doomed -GuardRoot $root
            $removed.Add($candidate)
        }
        catch {
            Write-Verbose "Module SQL validator build folder was left in place: $candidate ($($_.Exception.Message))"
        }
    }

    return @($removed)
}
