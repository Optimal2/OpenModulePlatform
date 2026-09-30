#Requires -Version 5.1
<#
.SYNOPSIS
    Guard for scripts that delete an output folder recursively and create it
    again. Dot-source this file and call Assert-SafeToClean before the delete.

.DESCRIPTION
    publish-all.ps1 -CleanOutput, merge-universal-package-objects.ps1,
    scripts/dev/test-deterministic-web-publish.ps1 and
    scripts/deployment/package-hostagent-first.ps1 each remove a folder with
    Remove-Item -Recurse. Nothing checked what that folder held, and a consumer
    repository's CI once deleted the tracked artifacts/README.md of this
    repository that way. Assert-SafeToClean throws, before anything is
    deleted, when the folder:

      1. is a drive root, lies inside a .git folder, is the root of a git work
         tree, or contains this repository;
      2. holds files tracked by git (git ls-files is not empty for it);
      3. lies in another git work tree than this repository and is not
         ignored there (git check-ignore);
      4. is itself a junction or symbolic link, or holds a .git folder, a .git
         file (a worktree or submodule) or any junction or symbolic link at
         any depth.

    Rule 3 allows an IGNORED folder of another repository on purpose: a
    consumer's packaging script passes its own ignored output folder (for
    example <consumer>\artifacts\...) to publish-all.ps1 -CleanOutput, and that
    folder can only ever hold generated files. A folder outside every git work
    tree is allowed, which covers %TEMP% work roots.

    The same folder can be named in several forms: through a junction (an AI
    Orchestrator instance reaches the repositories through junctions), by an
    8.3 short name, or by its final path. git answers with the final path, so
    the guard never compares the caller's string with git's: rules 1-3 ask git
    about the folder from inside it (rev-parse --show-prefix gives the
    folder's place in its work tree whatever form the caller used), and only
    compare git's answers with each other. A folder that does not exist yet is
    asked about from its nearest existing parent.

    Rule 4 exists because Windows PowerShell 5.1 Remove-Item -Recurse walks
    into junctions and deletes what they point at, and because a folder that
    is not inside any work tree can still hold other repositories (an AI
    Orchestrator instance folder, a worktree root). The walk never follows a
    link, and it stops with a refusal after $script:OmpCleanGuardMaxFolders
    folders rather than allowing a folder it has not fully seen.

    Windows PowerShell 5.1 safe: git's stderr is discarded with the error
    preference relaxed to Continue inside the helpers, because 5.1 turns
    redirected native stderr into a terminating error under 'Stop'.
#>

$script:OmpCleanGuardMaxFolders = 20000

function Get-OmpCleanGuardFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $full = [System.IO.Path]::GetFullPath($Path)
    if ($full.Length -gt 3) {
        $full = $full.TrimEnd('\', '/')
    }
    return $full
}

function Get-OmpCleanGuardGitLocation {
    <#
    .SYNOPSIS
        Where Path lies in a git work tree, as git sees it: TopLevel (git's
        final path of the work tree root) and Relative (the path inside the
        work tree, '/'-separated, empty for the root). Returns $null outside
        every work tree.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)

    # git -C needs an existing directory; walk up to the nearest one and keep
    # the missing part as a tail below git's prefix for it.
    $probe = $Path
    while ($probe -and -not (Test-Path -LiteralPath $probe -PathType Container)) {
        $probe = Split-Path -Parent $probe
    }
    if (-not $probe) {
        return $null
    }
    $tail = $Path.Substring($probe.Length).Trim('\', '/').Replace('\', '/')

    $ErrorActionPreference = 'Continue'
    $output = @(& git -C $probe rev-parse --show-toplevel --show-prefix 2>$null)
    if ($LASTEXITCODE -ne 0 -or $output.Count -lt 1 -or -not $output[0]) {
        return $null
    }

    $prefix = ''
    if ($output.Count -gt 1 -and $output[1]) {
        $prefix = ([string]$output[1]).Trim()
    }
    $relative = ($prefix + $tail).Trim('/')

    return [pscustomobject]@{
        TopLevel = Get-OmpCleanGuardFullPath -Path ([string]$output[0]).Trim()
        Relative = $relative
    }
}

function Test-OmpCleanGuardIsSameOrBelow {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )

    if ([string]::Equals($Path, $Root, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    return $Path.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Find-OmpCleanGuardHazard {
    <#
    .SYNOPSIS
        Walks Path without following links and returns a description of the
        first .git entry or link it finds, or $null when there is none.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)

    $reparsePoint = [System.IO.FileAttributes]::ReparsePoint
    $pending = New-Object System.Collections.Generic.Stack[System.IO.DirectoryInfo]
    $pending.Push((New-Object System.IO.DirectoryInfo $Path))
    $visited = 0

    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        $visited++
        if ($visited -gt $script:OmpCleanGuardMaxFolders) {
            return "it holds more than $($script:OmpCleanGuardMaxFolders) folders, so it was not fully checked for repositories and links"
        }

        try {
            $entries = @($directory.EnumerateFileSystemInfos())
        }
        catch {
            return "'$($directory.FullName)' could not be listed ($($_.Exception.Message)), so it was not fully checked for repositories and links"
        }

        foreach ($entry in $entries) {
            if ([string]::Equals($entry.Name, '.git', [StringComparison]::OrdinalIgnoreCase)) {
                return "it holds a git repository or worktree ('$($entry.FullName)')"
            }
            if (($entry.Attributes -band $reparsePoint) -eq $reparsePoint) {
                return "it holds a junction or symbolic link ('$($entry.FullName)'), and a recursive delete in Windows PowerShell 5.1 removes what a junction points at"
            }
            if ($entry -is [System.IO.DirectoryInfo]) {
                $pending.Push($entry)
            }
        }
    }

    return $null
}

function Assert-SafeToClean {
    <#
    .SYNOPSIS
        Throws "Refusing to clean ..." when Path must not be deleted
        recursively. RepositoryRoot is the repository the calling script
        belongs to (normally derived from $PSScriptRoot).
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )

    $target = Get-OmpCleanGuardFullPath -Path $Path
    $advice = 'Pass an output folder that holds only generated files: an ignored folder such as artifacts\publish, or a folder outside every git repository.'

    if ([string]::IsNullOrEmpty([System.IO.Path]::GetFileName($target))) {
        throw "Refusing to clean '$target': it is the root of a drive or share. $advice"
    }
    if (@($target -split '[\\/]' | Where-Object { $_ -ieq '.git' }).Count -gt 0) {
        throw "Refusing to clean '$target': it lies inside a .git folder. $advice"
    }

    $ownLocation = Get-OmpCleanGuardGitLocation -Path (Get-OmpCleanGuardFullPath -Path $RepositoryRoot)
    $ownRoot = if ($ownLocation) { $ownLocation.TopLevel } else { Get-OmpCleanGuardFullPath -Path $RepositoryRoot }

    # A cheap string check for the common case; rule 4 below catches the
    # repository through any other form of the name, since it holds a .git.
    if ((Test-OmpCleanGuardIsSameOrBelow -Path $ownRoot -Root $target) -or
        (Test-OmpCleanGuardIsSameOrBelow -Path (Get-OmpCleanGuardFullPath -Path $RepositoryRoot) -Root $target)) {
        throw "Refusing to clean '$target': it is, or contains, the repository '$ownRoot'. $advice"
    }

    $exists = Test-Path -LiteralPath $target
    if ($exists) {
        $item = Get-Item -LiteralPath $target -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq [System.IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to clean '$target': it is a junction or symbolic link, and a recursive delete in Windows PowerShell 5.1 removes what it points at. $advice"
        }
    }

    $location = Get-OmpCleanGuardGitLocation -Path $target
    if ($location) {
        $topLevel = $location.TopLevel
        if (-not $location.Relative) {
            throw "Refusing to clean '$target': it is the root of the git repository '$topLevel'. $advice"
        }

        $ErrorActionPreference = 'Continue'
        $tracked = @(& git --literal-pathspecs -C $topLevel ls-files -- $location.Relative 2>$null)
        if ($LASTEXITCODE -ne 0) {
            throw "Refusing to clean '$target': git ls-files failed in '$topLevel', so tracked files cannot be ruled out. $advice"
        }
        if ($tracked.Count -gt 0) {
            throw "Refusing to clean '$target': it holds files tracked by git in '$topLevel' (for example '$($tracked[0])'). $advice"
        }

        if (-not [string]::Equals($topLevel, $ownRoot, [StringComparison]::OrdinalIgnoreCase)) {
            # check-ignore takes paths, not pathspecs (--literal-pathspecs makes it fail).
            & git -C $topLevel check-ignore -q -- $location.Relative 2>$null
            if ($LASTEXITCODE -ne 0) {
                throw "Refusing to clean '$target': it lies in another git repository ('$topLevel') and is not ignored there. $advice"
            }
        }
    }

    if ($exists -and (Test-Path -LiteralPath $target -PathType Container)) {
        $hazard = Find-OmpCleanGuardHazard -Path $target
        if ($hazard) {
            throw "Refusing to clean '$target': $hazard. $advice"
        }
    }
}
