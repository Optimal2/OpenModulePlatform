#Requires -Version 5.1
<#
.SYNOPSIS
    Source provenance for OMP package builds: commit SHA and clean-tree verdict.

.DESCRIPTION
    Package builds stamp every artifact manifest and the universal package
    manifest with the source commit the bytes were built from, plus whether the
    working tree was clean. A build from a dirty tree can otherwise ship the
    same component version with different content and leave no trace of it.

    Clean-tree method: `git status --porcelain`, the same primary signal the
    repository already uses in scripts/omp/push-with-rebump.ps1,
    scripts/local-ci.ps1 (gate-cache stamp), and
    scripts/omp/validate-shared-dependencies.ps1 (Check 14 sibling warning).
    A non-empty porcelain listing is then judged content-wise so pure
    line-ending noise does not count as dirty: a tracked modification is
    ignored only when `git diff --ignore-cr-at-eol` reports no difference
    against HEAD. That is the same content verdict the release comparisons
    use for CRLF drift (compare with CRLF normalized, as Check 16 in
    scripts/omp/validate-component-versions.ps1 and Get-FileSha256 in
    scripts/omp/validate-shared-scripts.ps1 document), while a missing or
    extra trailing newline still counts as content. Anything that cannot be
    judged (deleted/added/renamed paths, untracked files, unmerged entries,
    or a git error) fails closed to dirty.

    This file is dot-sourced; it must never call exit.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-SourceProvenanceGit {
    <#
    .SYNOPSIS
        Runs git and captures stdout lines plus the exit code without letting
        Windows PowerShell 5.1 turn stderr into a terminating RemoteException.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& git -C $RepositoryRoot @Arguments 2>&1)
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Lines    = @($output | ForEach-Object { "$_" })
        }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Test-ProvenanceContentSame {
    <#
    .SYNOPSIS
        True when a tracked path's worktree content matches its HEAD blob
        ignoring carriage returns at end of line. False (dirty) when the
        content really differs or when git cannot answer.

        `git diff --quiet --ignore-cr-at-eol` is the whole verdict: it compares
        bytes rather than line-split strings, so a missing or extra trailing
        newline still counts as content (the same rule Get-FileSha256 in
        validate-shared-scripts.ps1 documents), while pure CRLF/LF drift does
        not. Anything undecidable fails closed to dirty.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $diff = Invoke-SourceProvenanceGit -RepositoryRoot $RepositoryRoot -Arguments @('diff', '--quiet', '--ignore-cr-at-eol', 'HEAD', '--', $Path)
    return ($diff.ExitCode -eq 0)
}

function ConvertFrom-ProvenancePorcelainPath {
    <#
    .SYNOPSIS
        Unquotes a porcelain v1 path and resolves rename arrows to the new path.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Value
    )

    $path = $Value.Trim()
    $arrowIndex = $path.IndexOf(' -> ')
    if ($arrowIndex -ge 0) {
        $path = $path.Substring($arrowIndex + 4).Trim()
    }

    if ($path.Length -ge 2 -and $path.StartsWith('"') -and $path.EndsWith('"')) {
        $path = $path.Substring(1, $path.Length - 2)
        $path = $path.Replace('\\', '\').Replace('\"', '"')
    }

    return $path
}

function Test-ProvenancePorcelainDirty {
    <#
    .SYNOPSIS
        Judges porcelain v1 lines content-wise. Untracked, added, deleted,
        renamed, type-changed, and unmerged entries are dirty; tracked content
        modifications are dirty only when the normalized bytes really differ.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$PorcelainLines
    )

    foreach ($line in $PorcelainLines) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line.Length -lt 4) {
            return $true
        }

        $x = $line[0]
        $y = $line[1]
        $path = ConvertFrom-ProvenancePorcelainPath -Value $line.Substring(3)
        if ([string]::IsNullOrWhiteSpace($path)) {
            return $true
        }

        if ($x -eq '?' -and $y -eq '?') {
            return $true
        }

        if ($x -eq '!' -or $y -eq '!') {
            continue
        }

        if ($x -eq 'U' -or $y -eq 'U' -or ($x -eq 'A' -and $y -eq 'A') -or ($x -eq 'D' -and $y -eq 'D')) {
            return $true
        }

        if ($y -eq 'D' -or $x -eq 'D' -or $x -eq 'A' -or $x -eq 'R' -or $x -eq 'C' -or $x -eq 'T' -or $y -eq 'T') {
            return $true
        }

        if (-not (Test-ProvenanceContentSame -RepositoryRoot $RepositoryRoot -Path $path)) {
            return $true
        }
    }

    return $false
}

function Get-OmpSourceProvenance {
    <#
    .SYNOPSIS
        Returns the source provenance of a repository checkout: HEAD commit SHA
        and whether the working tree is clean. Throws when git cannot answer,
        so a build never stamps an unknown state as clean.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )

    $root = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $head = Invoke-SourceProvenanceGit -RepositoryRoot $root -Arguments @('rev-parse', 'HEAD')
    if ($head.ExitCode -ne 0) {
        throw "Could not determine the source commit of '$root': $(($head.Lines -join "`n")). Refusing to stamp an unknown provenance."
    }

    $status = Invoke-SourceProvenanceGit -RepositoryRoot $root -Arguments @('status', '--porcelain')
    if ($status.ExitCode -ne 0) {
        throw "Could not determine whether the source tree of '$root' is clean: $(($status.Lines -join "`n")). Refusing to stamp an unknown provenance."
    }

    $porcelainLines = @($status.Lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $dirty = Test-ProvenancePorcelainDirty -RepositoryRoot $root -PorcelainLines $porcelainLines

    return [pscustomobject]@{
        RepositoryRoot = $root
        CommitSha      = ($head.Lines -join "`n").Trim()
        Dirty          = [bool]$dirty
        Porcelain      = ($porcelainLines -join "`n")
    }
}

function Get-OmpSourceProvenanceOrNull {
    <#
    .SYNOPSIS
        Get-OmpSourceProvenance that returns $null instead of throwing when git
        cannot answer (for example a build from an exported source tree that is
        not a checkout). The package-build gate must fail closed and never use
        this; object builders use it so a non-checkout build omits the stamp
        instead of failing a build whose inputs are otherwise fine.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )

    try {
        return Get-OmpSourceProvenance -RepositoryRoot $RepositoryRoot
    }
    catch {
        return $null
    }
}

function Assert-OmpSourceTreeClean {    <#
    .SYNOPSIS
        Fails a package build on a dirty source tree unless -AllowDirtySource
        is passed for local troubleshooting. Returns the provenance either way
        so the caller can stamp it into the package manifests.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [switch]$AllowDirtySource
    )

    $provenance = Get-OmpSourceProvenance -RepositoryRoot $RepositoryRoot
    if ($provenance.Dirty -and -not $AllowDirtySource) {
        throw ("Refusing to build a package from a dirty source tree in '{0}'. Commit the changes first, or rebuild with -AllowDirtySource for local troubleshooting (the package is then stamped sourceDirty=true). Uncommitted changes:`n{1}" -f $provenance.RepositoryRoot, $provenance.Porcelain)
    }

    return $provenance
}
