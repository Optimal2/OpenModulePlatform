#Requires -Version 5.1
<#
.SYNOPSIS
    Guard for scripts that delete an output folder recursively and create it
    again. Dot-source this file and call Assert-SafeToClean before the delete.

.DESCRIPTION
    publish-all.ps1 -CleanOutput, merge-universal-package-objects.ps1 and
    scripts/dev/test-deterministic-web-publish.ps1 each remove the folder they
    are given with Remove-Item -Recurse. Nothing checked what that folder held,
    and a consumer repository's CI once deleted the tracked artifacts/README.md
    of this repository that way. Assert-SafeToClean throws, before anything is
    deleted, when the folder:

      1. is the root of a git work tree, or contains this repository;
      2. holds files tracked by git (git ls-files is not empty for it);
      3. lies in another git work tree than this repository and is not
         ignored there (git check-ignore).

    Rule 3 allows an IGNORED folder of another repository on purpose: a
    consumer's packaging script passes its own ignored output folder (for
    example <consumer>\artifacts\...) to publish-all.ps1 -CleanOutput, and that
    folder can only ever hold generated files. A folder outside every git work
    tree is allowed, which covers %TEMP% work roots.

    Windows PowerShell 5.1 safe: git's stderr is discarded with the error
    preference relaxed to Continue inside the helper, because 5.1 turns
    redirected native stderr into a terminating error under 'Stop'.
#>

function Get-OmpCleanGuardFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Get-OmpCleanGuardGitTopLevel {
    param([Parameter(Mandatory = $true)][string]$Path)

    # git -C needs an existing directory; walk up to the nearest one so a
    # folder that does not exist yet still resolves to its work tree.
    $probe = $Path
    while ($probe -and -not (Test-Path -LiteralPath $probe -PathType Container)) {
        $probe = Split-Path -Parent $probe
    }
    if (-not $probe) {
        return $null
    }

    $ErrorActionPreference = 'Continue'
    $output = & git -C $probe rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $output) {
        return $null
    }

    return Get-OmpCleanGuardFullPath -Path ([string]@($output)[0]).Trim()
}

function Test-OmpCleanGuardIsSameOrBelow {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )

    if ([string]::Equals($Path, $Root, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    return $Path.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)
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
    $ownRoot = Get-OmpCleanGuardGitTopLevel -Path $RepositoryRoot
    if (-not $ownRoot) {
        $ownRoot = Get-OmpCleanGuardFullPath -Path $RepositoryRoot
    }
    $advice = 'Pass an output folder that holds only generated files: an ignored folder such as artifacts\publish, or a folder outside every git repository.'

    if (Test-OmpCleanGuardIsSameOrBelow -Path $ownRoot -Root $target) {
        throw "Refusing to clean '$target': it is, or contains, the repository '$ownRoot'. $advice"
    }

    $topLevel = Get-OmpCleanGuardGitTopLevel -Path $target
    if (-not $topLevel) {
        return
    }

    if ([string]::Equals($target, $topLevel, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean '$target': it is the root of a git repository. $advice"
    }

    $relative = $target.Substring($topLevel.Length + 1).Replace('\', '/')
    $ErrorActionPreference = 'Continue'
    $tracked = @(& git -C $topLevel ls-files -- $relative 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Refusing to clean '$target': git ls-files failed in '$topLevel', so tracked files cannot be ruled out. $advice"
    }
    if ($tracked.Count -gt 0) {
        throw "Refusing to clean '$target': it holds files tracked by git in '$topLevel' (for example '$($tracked[0])'). $advice"
    }

    if (-not [string]::Equals($topLevel, $ownRoot, [StringComparison]::OrdinalIgnoreCase)) {
        & git -C $topLevel check-ignore -q -- $relative 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw "Refusing to clean '$target': it lies in another git repository ('$topLevel') and is not ignored there. $advice"
        }
    }
}
