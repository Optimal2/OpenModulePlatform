#requires -Version 5.1
<#
.SYNOPSIS
    Lists the sibling repositories that reference a shared OpenModulePlatform
    project, and whether each one declares the dependency for Check 14.

.DESCRIPTION
    A change to a shared OMP project (OpenModulePlatform.Web.Shared and the
    other entries in this repository's omp-components.json sharedProjects
    array) must cascade to every repository that compiles against it. That
    cascade list must come from THIS SCRIPT, never from memory: a cascade that
    ran against a remembered list (measured 2026-10-07) missed two referencing
    repositories -- one that already carried a treeId, and one with a
    ProjectReference but no sharedDependencies block at all.

    The script walks the sibling directories of the OpenModulePlatform
    checkout, reads every git-tracked .csproj AND every git-tracked
    Directory.Build.props/Directory.Build.targets (a ProjectReference can
    legally live there, and a reference hidden in one is invisible to a
    .csproj-only scan), and matches ProjectReference entries against the
    shared projects declared in omp-components.json -- including references
    written with the $(OpenModulePlatformRoot) MSBuild variable. For every
    referencing repository it reports whether the repository's own
    omp-components.json declares a sharedDependencies entry (with a treeId)
    for that project, which is what Check 14
    (validate-shared-dependencies.ps1) enforces.

    Exit code 1 when any referencing repository does not declare the shared
    dependency, so the script doubles as the gap detector before a cascade.
    Exit code 1 also when a sibling repository cannot be read (git ls-files
    fails): a skipped repository is exactly how a referencing consumer goes
    unseen, so an unreadable one must never read as "nothing to declare".

    sharedProjects entries whose projectPath is a bare directory (source-linked
    content, not a .csproj) cannot be matched through ProjectReference and are
    listed as skipped.

.PARAMETER PlatformRepositoryRoot
    Root of the OpenModulePlatform checkout. Defaults to this script's
    repository.

.PARAMETER SiblingRoot
    Folder whose immediate children are scanned for consumer repositories.
    Defaults to the parent of the platform checkout.

.EXAMPLE
    powershell.exe -NoProfile -File scripts/omp/list-shared-consumers.ps1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$PlatformRepositoryRoot = '',

    [Parameter(Mandatory = $false)]
    [string]$SiblingRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ScriptDirectory {
    # $PSScriptRoot is EMPTY inside an advanced-function parameter default when
    # the script runs under Windows PowerShell 5.1 -File, so the default cannot
    # be expressed in the param block (measured: the default below crashed with
    # "Cannot bind argument to parameter 'Path' because it is an empty string"
    # exactly on the runtime the pre-push hook uses). Resolve it in the body.
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        return $PSScriptRoot
    }

    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        $scriptPath = $MyInvocation.MyCommand.Path
    }

    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        throw 'Could not resolve script directory.'
    }

    return Split-Path -Parent $scriptPath
}

function Get-OptionalPropertyValue {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$Object,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

if ([string]::IsNullOrWhiteSpace($PlatformRepositoryRoot)) {
    $PlatformRepositoryRoot = (Resolve-Path (Join-Path (Get-ScriptDirectory) '..\..')).Path
}
$platformRoot = [System.IO.Path]::GetFullPath($PlatformRepositoryRoot)
if ([string]::IsNullOrWhiteSpace($SiblingRoot)) {
    $SiblingRoot = Split-Path -Parent $platformRoot
}
$siblingRootFull = [System.IO.Path]::GetFullPath($SiblingRoot)

$manifestPath = Join-Path $platformRoot 'omp-components.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    Write-Host "ERROR: component manifest not found: $manifestPath" -ForegroundColor Red
    exit 1
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json

# The shared projects that can be referenced through ProjectReference.
$sharedProjects = @()
foreach ($sharedProject in @(Get-OptionalPropertyValue -Object $manifest -Name 'sharedProjects')) {
    if ($null -eq $sharedProject) {
        continue
    }

    $projectPath = [string](Get-OptionalPropertyValue -Object $sharedProject -Name 'projectPath')
    if ([string]::IsNullOrWhiteSpace($projectPath)) {
        continue
    }

    if (-not $projectPath.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "SKIP  shared project '$projectPath' is not a .csproj (source-linked content); it cannot be matched through ProjectReference."
        continue
    }

    $relativeNormalized = $projectPath -replace '\\', '/'
    $sharedProjects += [pscustomobject]@{
        RelativeProjectPath = $relativeNormalized
        RelativeDirectory   = ($relativeNormalized -replace '/[^/]+$', '')
    }
}

if ($sharedProjects.Count -eq 0) {
    Write-Host 'No referenceable shared projects (sharedProjects entries naming a .csproj) are declared.'
    exit 0
}

# The platform roots a consumer reference can legitimately resolve to: the
# checkout this script reads the manifest from, and the conventional sibling
# checkout (<SiblingRoot>\OpenModulePlatform) that relative references such as
# '..\..\OpenModulePlatform\...' and the $(OpenModulePlatformRoot) MSBuild
# variable point at. The two differ when this script runs from a worktree.
$platformRootNormalized = ($platformRoot -replace '\\', '/').TrimEnd('/')
$platformCandidateRoots = [System.Collections.Generic.List[string]]::new()
[void]$platformCandidateRoots.Add($platformRootNormalized)
$conventionalSiblingRoot = Join-Path $siblingRootFull 'OpenModulePlatform'
if ((Test-Path -LiteralPath $conventionalSiblingRoot -PathType Container)) {
    $conventionalSiblingNormalized = ([System.IO.Path]::GetFullPath($conventionalSiblingRoot) -replace '\\', '/').TrimEnd('/')
    if (-not [string]::Equals($conventionalSiblingNormalized, $platformRootNormalized, [StringComparison]::OrdinalIgnoreCase)) {
        [void]$platformCandidateRoots.Add($conventionalSiblingNormalized)
    }
}

function Test-PathMatch {
    <#
    .SYNOPSIS
    Decides whether a ProjectReference Include value points at a given shared
    project in a platform checkout. Handles relative paths, absolute paths,
    and the $(OpenModulePlatformRoot) MSBuild variable, with either slash.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Include,
        [Parameter(Mandatory = $true)][string]$ProjectDirectory,
        [Parameter(Mandatory = $true)][string]$RelativeProjectPath
    )

    $normalizedInclude = $Include -replace '\\', '/'

    if ($normalizedInclude -match '\$\([^)]*\)') {
        # MSBuild variable form, e.g. $(OpenModulePlatformRoot)/OpenModulePlatform.Web.Shared/...
        # The variable stands for the platform checkout; what identifies the
        # reference is the repository-relative suffix after it.
        $suffix = ($normalizedInclude -replace '^.*?\$\([^)]*\)', '').TrimStart('/')
        return [string]::Equals($suffix, $RelativeProjectPath, [StringComparison]::OrdinalIgnoreCase)
    }

    if ([System.IO.Path]::IsPathRooted($normalizedInclude)) {
        $resolved = ([System.IO.Path]::GetFullPath($normalizedInclude) -replace '\\', '/')
    }
    else {
        $resolved = ([System.IO.Path]::GetFullPath((Join-Path $ProjectDirectory $normalizedInclude)) -replace '\\', '/')
    }

    foreach ($candidateRoot in $platformCandidateRoots) {
        if ([string]::Equals($resolved, ($candidateRoot + '/' + $RelativeProjectPath), [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

$references = @()
$scanFailures = 0
foreach ($sibling in @(Get-ChildItem -LiteralPath $siblingRootFull -Directory)) {
    # The platform checkout itself is not its own consumer -- neither the copy
    # this script reads the manifest from, nor the conventional sibling one.
    $siblingNormalized = ($sibling.FullName -replace '\\', '/').TrimEnd('/')
    $isPlatformCheckout = $false
    foreach ($candidateRoot in $platformCandidateRoots) {
        if ([string]::Equals($siblingNormalized, $candidateRoot, [StringComparison]::OrdinalIgnoreCase)) {
            $isPlatformCheckout = $true
            break
        }
    }
    if ($isPlatformCheckout) {
        continue
    }

    # A sibling REPOSITORY: git work tree (directory marker or worktree file).
    if (-not (Test-Path -LiteralPath (Join-Path $sibling.FullName '.git'))) {
        continue
    }

    # A ProjectReference can live in a .csproj or in a git-tracked
    # Directory.Build.props/.targets; scan all three shapes in one git call.
    $projectFiles = $null
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $projectFiles = & git -C $sibling.FullName ls-files -- '*.csproj' '*Directory.Build.props' '*Directory.Build.targets' 2>$null
        $gitExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($gitExitCode -ne 0) {
        # WARN + skip is how a referencing consumer goes unseen: the repository
        # is dropped from the list and the run can still read "all declared".
        # Count it and make the exit code non-zero at the end.
        $scanFailures++
        Write-Host "ERROR could not list project files in '$($sibling.FullName)'; the repository was not scanned and the result is incomplete." -ForegroundColor Red
        continue
    }

    foreach ($projectFileRelative in @($projectFiles)) {
        $projectFileFullPath = Join-Path $sibling.FullName $projectFileRelative
        if (-not (Test-Path -LiteralPath $projectFileFullPath -PathType Leaf)) {
            continue
        }

        $projectFileText = Get-Content -LiteralPath $projectFileFullPath -Raw -Encoding UTF8
        $projectDirectory = Split-Path -Parent $projectFileFullPath
        foreach ($referenceMatch in [regex]::Matches($projectFileText, '(?i)<ProjectReference\s+[^>]*Include\s*=\s*"([^"]+)"')) {
            $include = $referenceMatch.Groups[1].Value
            foreach ($sharedProject in $sharedProjects) {
                if (Test-PathMatch -Include $include -ProjectDirectory $projectDirectory -RelativeProjectPath $sharedProject.RelativeProjectPath) {
                    $references += [pscustomobject]@{
                        Repository        = $sibling.Name
                        RepositoryFullPath = $sibling.FullName
                        SharedProject     = $sharedProject.RelativeDirectory
                        ReferencedVia     = $projectFileRelative
                    }
                }
            }
        }
    }
}

if ($references.Count -eq 0) {
    if ($scanFailures -gt 0) {
        Write-Host "$scanFailures sibling repositor$(if ($scanFailures -eq 1) { 'y' } else { 'ies' }) under '$siblingRootFull' could not be scanned (see the ERROR lines above), so 'no references found' is not a trustworthy answer." -ForegroundColor Red
        exit 1
    }

    Write-Host "No sibling repository under '$siblingRootFull' references a shared OpenModulePlatform project."
    exit 0
}

$references = @($references | Sort-Object Repository, SharedProject | Group-Object Repository, SharedProject | ForEach-Object { $_.Group[0] })

$undeclaredCount = 0
Write-Host ''
Write-Host ('{0,-28} {1,-45} {2,-9} {3}' -f 'REPOSITORY', 'SHARED PROJECT', 'DECLARED', 'TREEID')
foreach ($reference in $references) {
    $declared = $false
    $treeId = '-'
    $consumerManifestPath = Join-Path $reference.RepositoryFullPath 'omp-components.json'
    if (Test-Path -LiteralPath $consumerManifestPath -PathType Leaf) {
        $consumerManifest = Get-Content -LiteralPath $consumerManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($dependency in @(Get-OptionalPropertyValue -Object $consumerManifest -Name 'sharedDependencies')) {
            if ($null -eq $dependency) {
                continue
            }

            $dependencyPath = ([string](Get-OptionalPropertyValue -Object $dependency -Name 'projectPath')) -replace '\\', '/'
            $dependencyPath = $dependencyPath.TrimEnd('/')
            if ([string]::Equals($dependencyPath, $reference.SharedProject, [StringComparison]::OrdinalIgnoreCase) -or
                [string]::Equals($dependencyPath, ($reference.SharedProject + '/' + ($reference.SharedProject -replace '.*/', '') + '.csproj'), [StringComparison]::OrdinalIgnoreCase)) {
                $declared = $true
                $dependencyTreeId = [string](Get-OptionalPropertyValue -Object $dependency -Name 'treeId')
                if (-not [string]::IsNullOrWhiteSpace($dependencyTreeId)) {
                    $treeId = $dependencyTreeId.Substring(0, [Math]::Min(8, $dependencyTreeId.Length))
                }
                break
            }
        }
    }

    if (-not $declared) {
        $undeclaredCount++
    }

    Write-Host ('{0,-28} {1,-45} {2,-9} {3}' -f $reference.Repository, $reference.SharedProject, $(if ($declared) { 'yes' } else { 'NO' }), $treeId) -ForegroundColor $(if ($declared) { 'Gray' } else { 'Red' })
}

Write-Host ''
if ($scanFailures -gt 0) {
    Write-Host "$scanFailures sibling repositor$(if ($scanFailures -eq 1) { 'y' } else { 'ies' }) under '$siblingRootFull' could not be scanned (see the ERROR lines above); the declaration table above is incomplete." -ForegroundColor Red
    exit 1
}

if ($undeclaredCount -gt 0) {
    Write-Host "$undeclaredCount of $($references.Count) shared-project reference(s) are NOT declared in the consumer's omp-components.json sharedDependencies. Add the declaration (and its treeId) so Check 14 sees the consumer." -ForegroundColor Red
    exit 1
}

Write-Host "All $($references.Count) shared-project reference(s) under '$siblingRootFull' are declared in their consumer's sharedDependencies."
exit 0
