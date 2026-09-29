#Requires -Version 5.1
<#
.SYNOPSIS
    Source provenance for OMP package builds: commit SHA and clean-tree verdict.

.DESCRIPTION
    Package builds stamp every artifact manifest and the universal package
    manifest with the source commit the bytes were built from, plus whether the
    working tree was clean. A build from a dirty tree can otherwise ship the
    same component version with different content and leave no trace of it.

    Clean-tree method: the same three questions the release tooling asks in
    Get-GitChangedFiles (scripts/omp/validate-component-versions.helpers.ps1):

      git diff --name-only                        worktree vs index
      git diff --cached --name-only               index vs HEAD
      git ls-files --others --exclude-standard    untracked, not ignored

    Any path in any of the three answers makes the tree dirty. The verdict is
    about content, not file-system state: git diff compares the worktree after
    git's own clean conversion (core.autocrlf, .gitattributes eol/text), so a
    file that only looks changed -- for example a CRLF checkout under
    core.autocrlf=true, which `git status --porcelain` can still list as
    modified -- is clean, while a byte change git itself records (a CRLF
    rewrite with no normalization configured, a missing trailing newline) is
    dirty. That is deliberate: those bytes are what the build would ship.
    Any git error fails closed (throws), so an unknown state is never stamped
    as clean.

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

function Get-ProvenanceChangedPaths {
    <#
    .SYNOPSIS
        Returns every path that makes the tree dirty, each prefixed with the
        question that found it (worktree:, staged:, untracked:). Throws when
        git cannot answer any of the three questions. Callers wrap the result
        in @() so an empty answer counts as zero paths.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )

    $questions = @(
        @{ Label = 'worktree';  Arguments = @('diff', '--name-only') },
        @{ Label = 'staged';    Arguments = @('diff', '--cached', '--name-only') },
        @{ Label = 'untracked'; Arguments = @('ls-files', '--others', '--exclude-standard') }
    )

    $changed = New-Object System.Collections.Generic.List[string]
    foreach ($question in $questions) {
        $answer = Invoke-SourceProvenanceGit -RepositoryRoot $RepositoryRoot -Arguments $question.Arguments
        if ($answer.ExitCode -ne 0) {
            throw "Could not determine whether the source tree of '$RepositoryRoot' is clean: 'git $($question.Arguments -join ' ')' exited with $($answer.ExitCode). $(($answer.Lines -join "`n")) Refusing to stamp an unknown provenance."
        }

        foreach ($line in $answer.Lines) {
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                $changed.Add(('{0}: {1}' -f $question.Label, $line.Trim()))
            }
        }
    }

    return $changed.ToArray()
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

    $changedPaths = @(Get-ProvenanceChangedPaths -RepositoryRoot $root)

    return [pscustomobject]@{
        RepositoryRoot = $root
        CommitSha      = ($head.Lines -join "`n").Trim()
        Dirty          = ($changedPaths.Count -gt 0)
        ChangedPaths   = ($changedPaths -join "`n")
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

function Assert-OmpSourceTreeClean {
    <#
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
        throw ("Refusing to build a package from a dirty source tree in '{0}'. Commit the changes first, or rebuild with -AllowDirtySource for local troubleshooting (the package is then stamped sourceDirty=true). Uncommitted changes:`n{1}" -f $provenance.RepositoryRoot, $provenance.ChangedPaths)
    }

    return $provenance
}

function Get-OmpSharedSourceRoots {
    <#
    .SYNOPSIS
        Resolves the sibling repositories a consumer build compiles against:
        one entry per distinct repository behind the sharedDependencies of the
        consumer's omp-components.json, in the same order Check 14
        (validate-shared-dependencies.ps1) uses -- an explicit root, then the
        OpenModulePlatformRoot environment variable, then each dependency's
        repositoryPathHint relative to the consumer root. Each entry carries
        the union of the dependencies' consumers (component keys); an empty
        list means a dependency declared no consumers and counts for every
        component. The consumer's own root is never returned. Returns an
        empty array when the manifest is missing or declares no shared
        dependencies.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [string]$ExplicitRoot = ''
    )

    $root = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $manifestPath = Join-Path $root 'omp-components.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return @()
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $property = $manifest.PSObject.Properties['sharedDependencies']
    if ($null -eq $property -or $null -eq $property.Value) {
        return @()
    }

    $byRoot = [ordered]@{}
    foreach ($dependency in @($property.Value)) {
        $siblingRoot = $ExplicitRoot
        if ([string]::IsNullOrWhiteSpace($siblingRoot)) {
            $siblingRoot = $env:OpenModulePlatformRoot
        }
        if ([string]::IsNullOrWhiteSpace($siblingRoot)) {
            $hint = $dependency.PSObject.Properties['repositoryPathHint']
            if ($null -eq $hint -or [string]::IsNullOrWhiteSpace([string]$hint.Value)) {
                throw "A sharedDependencies entry in '$manifestPath' has no repositoryPathHint, so the repository it compiles against cannot be verified clean."
            }
            $siblingRoot = Join-Path $root ([string]$hint.Value)
        }
        elseif (-not [System.IO.Path]::IsPathRooted($siblingRoot)) {
            $siblingRoot = Join-Path $root $siblingRoot
        }

        $siblingRoot = [System.IO.Path]::GetFullPath($siblingRoot)
        if ([string]::Equals($siblingRoot.TrimEnd('\', '/'), $root.TrimEnd('\', '/'), [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $key = $siblingRoot.ToUpperInvariant()
        if (-not $byRoot.Contains($key)) {
            $keyProperty = $dependency.PSObject.Properties['repositoryKey']
            $byRoot[$key] = [pscustomobject]@{
                RepositoryKey  = $(if ($null -ne $keyProperty) { [string]$keyProperty.Value } else { '' })
                RepositoryRoot = $siblingRoot
                Consumers      = New-Object System.Collections.Generic.List[string]
                AllConsumers   = $false
            }
        }

        $entry = $byRoot[$key]
        $consumersProperty = $dependency.PSObject.Properties['consumers']
        $consumers = @()
        if ($null -ne $consumersProperty -and $null -ne $consumersProperty.Value) {
            $consumers = @($consumersProperty.Value | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
        }
        if ($consumers.Count -eq 0) {
            $entry.AllConsumers = $true
        }
        foreach ($consumer in $consumers) {
            $entry.Consumers.Add(([string]$consumer).Trim())
        }
    }

    return @($byRoot.Values)
}

function Test-OmpSharedSourceConsumedBy {
    <#
    .SYNOPSIS
        True when the component compiles against the shared source: it is
        listed among the dependency consumers, or a dependency on that
        repository declared no consumers at all.
    #>
    param(
        [Parameter(Mandatory = $true)][object]$SharedSource,
        [Parameter(Mandatory = $true)][string]$ComponentKey
    )

    if ($SharedSource.AllConsumers) {
        return $true
    }

    foreach ($consumer in $SharedSource.Consumers) {
        if ([string]::Equals($consumer, $ComponentKey, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Assert-OmpSharedSourcesClean {
    <#
    .SYNOPSIS
        Applies the dirty-tree gate to the sibling repositories a build
        compiles against. Callers pass only the shared sources consumed by the
        components this invocation actually publishes; a sibling that
        contributes no package bytes is none of the package's business.
        Check 14 only warns about a dirty sibling -- the verification itself
        still succeeds there -- but a package build from one ships the
        uncommitted shared code under the consumer's unchanged version, so the
        package build refuses it exactly like a dirty own tree. A sibling that
        is missing or not a checkout cannot be verified and fails even with
        -AllowDirtySource: the build compiles against it, so its state is part
        of what ships.
    #>
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$SharedSources,
        [switch]$AllowDirtySource
    )

    $results = New-Object System.Collections.Generic.List[object]
    foreach ($shared in $SharedSources) {
        if (-not (Test-Path -LiteralPath $shared.RepositoryRoot -PathType Container)) {
            throw "Shared dependency repository '$($shared.RepositoryRoot)' was not found. The package build compiles against it, so it must be present and verified clean."
        }

        $provenance = Assert-OmpSourceTreeClean -RepositoryRoot $shared.RepositoryRoot -AllowDirtySource:$AllowDirtySource
        $results.Add([pscustomobject]@{
            RepositoryKey  = $shared.RepositoryKey
            RepositoryRoot = $provenance.RepositoryRoot
            CommitSha      = $provenance.CommitSha
            Dirty          = $provenance.Dirty
        })
    }

    return $results.ToArray()
}
