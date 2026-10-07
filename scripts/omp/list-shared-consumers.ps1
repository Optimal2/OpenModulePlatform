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
    written with the $(OpenModulePlatformRoot) or $(MSBuildThisFileDirectory)
    MSBuild variables. Two MSBuild resolution rules the scan must honour,
    because honouring only the .csproj rule is how a reference goes unseen
    (measured 2026-10-07: a $(MSBuildThisFileDirectory)-relative reference in
    a consumer's Directory.Build.props produced a false exit 0):

    - $(MSBuildThisFileDirectory) stands for the directory of the file the
      reference is written in, whichever file that is, so the scan expands
      the variable to that directory before resolving the path.
    - A RELATIVE Include path written in Directory.Build.props/.targets
      resolves against the IMPORTING project, not against the props file
      (that is exactly why MSBuild offers $(MSBuildThisFileDirectory)). The
      importing projects are not known to a file scan, so a relative Include
      in a props/targets file is tested against the props file's directory
      AND against every .csproj directory in the same repository. For every
    referencing repository it reports whether the repository's own
    omp-components.json declares a sharedDependencies entry (with a treeId)
    for that project, which is what Check 14
    (validate-shared-dependencies.ps1) enforces.

    Exit code 1 when any referencing repository does not declare the shared
    dependency, so the script doubles as the gap detector before a cascade.
    Exit code 1 also when a sibling repository cannot be read (git ls-files
    fails): a skipped repository is exactly how a referencing consumer goes
    unseen, so an unreadable one must never read as "nothing to declare".
    Exit code 1 with a clear message when git is not on PATH at all, instead
    of a raw CommandNotFoundException: a missing prerequisite must read as a
    missing prerequisite, never as a crash mid-scan.

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
    and the $(OpenModulePlatformRoot) and $(MSBuildThisFileDirectory) MSBuild
    variables, with either slash.

    .PARAMETER ReferenceFileDirectory
    Directory of the file the reference was read from. $(MSBuildThisFileDirectory)
    expands to exactly this directory, and a relative Include in a .csproj
    resolves against it.

    .PARAMETER ImportingProjectDirectories
    Every .csproj directory in the same repository. A relative Include in a
    Directory.Build.props/.targets resolves against the IMPORTING project
    under MSBuild, so the reference is tested against each of these too;
    ignored for .csproj-sourced references, whose only base is their own
    directory.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Include,
        [Parameter(Mandatory = $true)][string]$ReferenceFileDirectory,
        [Parameter(Mandatory = $false)][string[]]$ImportingProjectDirectories = @(),
        [Parameter(Mandatory = $true)][string]$RelativeProjectPath
    )

    $normalizedInclude = $Include -replace '\\', '/'

    # Expand $(MSBuildThisFileDirectory) BEFORE the generic variable handling:
    # the variable stands for the scanned file's own directory (with a trailing
    # slash), so a reference written with it is an ordinary relative path once
    # expanded. The MatchEvaluator keeps a '$' in the directory out of the
    # replacement string's group-reference syntax.
    $referenceDirectoryNormalized = ($ReferenceFileDirectory -replace '\\', '/').TrimEnd('/') + '/'
    $normalizedInclude = [regex]::Replace(
        $normalizedInclude,
        '\$\(\s*MSBuildThisFileDirectory\s*\)',
        [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $referenceDirectoryNormalized },
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

    if ($normalizedInclude -match '\$\([^)]*\)') {
        # Other MSBuild variable form, e.g. $(OpenModulePlatformRoot)/OpenModulePlatform.Web.Shared/...
        # The variable stands for the platform checkout; what identifies the
        # reference is the repository-relative suffix after it.
        $suffix = ($normalizedInclude -replace '^.*?\$\([^)]*\)', '').TrimStart('/')
        return [string]::Equals($suffix, $RelativeProjectPath, [StringComparison]::OrdinalIgnoreCase)
    }

    if ([System.IO.Path]::IsPathRooted($normalizedInclude)) {
        $resolved = ([System.IO.Path]::GetFullPath($normalizedInclude) -replace '\\', '/')
        foreach ($candidateRoot in $platformCandidateRoots) {
            if ([string]::Equals($resolved, ($candidateRoot + '/' + $RelativeProjectPath), [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }

        return $false
    }

    # Relative Include. A .csproj resolves it against its own directory; a
    # Directory.Build.props/.targets resolves it against the IMPORTING
    # project, which a file scan cannot know -- so test the file's directory
    # and every .csproj directory in the repository.
    $baseDirectories = [System.Collections.Generic.List[string]]::new()
    [void]$baseDirectories.Add($ReferenceFileDirectory)
    foreach ($importingDirectory in @($ImportingProjectDirectories)) {
        if ([string]::IsNullOrWhiteSpace($importingDirectory)) {
            continue
        }
        $alreadyPresent = $false
        foreach ($existing in $baseDirectories) {
            if ([string]::Equals($existing, $importingDirectory, [StringComparison]::OrdinalIgnoreCase)) {
                $alreadyPresent = $true
                break
            }
        }
        if (-not $alreadyPresent) {
            [void]$baseDirectories.Add($importingDirectory)
        }
    }

    foreach ($baseDirectory in $baseDirectories) {
        $resolved = ([System.IO.Path]::GetFullPath((Join-Path $baseDirectory $normalizedInclude)) -replace '\\', '/')
        foreach ($candidateRoot in $platformCandidateRoots) {
            if ([string]::Equals($resolved, ($candidateRoot + '/' + $RelativeProjectPath), [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
    }

    return $false
}

$references = @()
$scanFailures = 0
$gitAvailabilityChecked = $false
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
    # The git availability check is lazy (first sibling repository): without
    # git the scan cannot run at all, and the failure must read as a missing
    # prerequisite with exit 1, not as a raw CommandNotFoundException.
    if (-not $gitAvailabilityChecked) {
        $gitAvailabilityChecked = $true
        if (-not (Get-Command -Name git -CommandType Application -ErrorAction SilentlyContinue)) {
            Write-Host "ERROR: git was not found on PATH. Sibling repositories cannot be scanned without git; install git or add it to PATH and rerun." -ForegroundColor Red
            exit 1
        }
    }

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

    # The directories a relative Include in Directory.Build.props/.targets can
    # resolve against under MSBuild: the importing projects' directories.
    $importingProjectDirectories = [System.Collections.Generic.List[string]]::new()
    foreach ($projectFileRelative in @($projectFiles)) {
        if ($projectFileRelative -like '*.csproj') {
            $projectFileDirectory = Split-Path -Parent (Join-Path $sibling.FullName $projectFileRelative)
            if (-not [string]::IsNullOrWhiteSpace($projectFileDirectory) -and -not $importingProjectDirectories.Contains($projectFileDirectory)) {
                [void]$importingProjectDirectories.Add($projectFileDirectory)
            }
        }
    }

    foreach ($projectFileRelative in @($projectFiles)) {
        $projectFileFullPath = Join-Path $sibling.FullName $projectFileRelative
        if (-not (Test-Path -LiteralPath $projectFileFullPath -PathType Leaf)) {
            continue
        }

        $projectFileText = Get-Content -LiteralPath $projectFileFullPath -Raw -Encoding UTF8
        $projectDirectory = Split-Path -Parent $projectFileFullPath
        $isImportedBuildFile = (Split-Path -Leaf $projectFileFullPath) -in @('Directory.Build.props', 'Directory.Build.targets')
        $matchBaseDirectories = @()
        if ($isImportedBuildFile) {
            $matchBaseDirectories = $importingProjectDirectories.ToArray()
        }

        foreach ($referenceMatch in [regex]::Matches($projectFileText, '(?i)<ProjectReference\s+[^>]*Include\s*=\s*"([^"]+)"')) {
            $include = $referenceMatch.Groups[1].Value
            foreach ($sharedProject in $sharedProjects) {
                if (Test-PathMatch -Include $include -ReferenceFileDirectory $projectDirectory -ImportingProjectDirectories $matchBaseDirectories -RelativeProjectPath $sharedProject.RelativeProjectPath) {
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
