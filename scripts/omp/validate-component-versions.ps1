<#
.SYNOPSIS
Validates component version metadata in omp-components.json.

.DESCRIPTION
Checks that every component listed in omp-components.json has a valid version,
points to an existing .csproj project, references a declared module definition,
and that module definition versions stay in sync with the manifest.

The "Check N" numbers are STABLE identifiers shared by every OMP-compatible
repository: a given number means the same check in every validator. The
canonical list, including which checks are platform-only or consumer-only by
design, lives in docs/VALIDATOR_CHECKS.md. The generic helper functions live in
validate-component-versions.helpers.ps1 next to this script; that file is part
of the shared core and is kept byte-identical across repositories by the
shared-script drift guard (Check 15).

This script validates the manifest only. Assembly versions in
Directory.Build.props are intentionally decoupled from omp-components.json
component versions: they are statically set to 0.1.0 for all C# projects.
OMP artifact identity is determined by the component manifest version plus
SHA-256 content hash, not by assembly version.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$BaseCommit = '',

    [Parameter(Mandatory = $false)]
    [switch]$SelfTest,

    # Treat problems in the optional omp-components.external.json overlay
    # (entries that match no manifest shared project, a missing sharedProjects
    # array) as errors instead of warnings.
    [Parameter(Mandatory = $false)]
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Ensure git output is decoded as UTF-8 so embedded BOMs and non-ASCII
# characters are preserved exactly as stored in the repository.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Get-ScriptDirectory {
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

# The shared validator core. Mandatory, not optional: without it most checks
# cannot run at all, and a gate that cannot run must not read as a passing one.
$helpersPath = Join-Path (Get-ScriptDirectory) 'validate-component-versions.helpers.ps1'
if (-not (Test-Path -LiteralPath $helpersPath -PathType Leaf)) {
    throw "Shared validator helpers not found: $helpersPath. The helpers file is part of the shared validator core (see docs/VALIDATOR_CHECKS.md) and must sit next to this script."
}
. $helpersPath

function Build-WebSharedForBinaryIdentity {
    <#
    .SYNOPSIS
    Builds OpenModulePlatform.Web.Shared.dll with deterministic settings for
    the binary identity check. Returns the full path to the emitted DLL.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot
    )

    $pathMap = '{0}={1}' -f $RepositoryRoot.TrimEnd('\', '/'), '/_/openmoduleplatform'
    & dotnet build $ProjectPath `
        -c Release `
        -o $OutputRoot `
        --verbosity minimal `
        -p:ContinuousIntegrationBuild=true `
        -p:Deterministic=true `
        -p:DeterministicSourcePaths=true `
        -p:IncludeSourceRevisionInInformationalVersion=false `
        "-p:PathMap=$pathMap" 2>&1 | ForEach-Object { Write-Host $_ }

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for $ProjectPath"
    }

    $dllPath = Join-Path $OutputRoot 'OpenModulePlatform.Web.Shared.dll'
    if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
        throw "Build succeeded but OpenModulePlatform.Web.Shared.dll was not found at $dllPath"
    }

    return $dllPath
}

$checkMark = [char]0x2713
$warningSign = [char]0x26A0
$crossMark = [char]0x2717

if ($SelfTest) {
    # Canonical self-test for the validator family: the PowerShell 5.1 pitfalls
    # that have actually broken this gate (BOM strip, git change detection).
    Invoke-ValidatorSelfTest -CheckMark $checkMark -CrossMark $crossMark
}

$scriptDirectory = Get-ScriptDirectory
$repositoryRoot = (Resolve-Path (Join-Path $scriptDirectory '..\..')).Path
$repositoryRoot = [System.IO.Path]::GetFullPath($repositoryRoot)

$manifestPath = Join-Path $repositoryRoot 'omp-components.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Component manifest not found: $manifestPath"
}

$jsonDepth = 100
$errors = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()
$manifestText = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
$manifest = ConvertFrom-JsonDocument -Json $manifestText -Depth $jsonDepth

# Optional local overlay naming consumers in repositories outside this one. The
# public manifest keeps every sharedProjects[].externalConsumers list empty; a
# gitignored omp-components.external.json with the same sharedProjects shape
# ({ projectPath, externalConsumers[] }) adds them for this checkout only. See
# docs/OMP_COMPONENT_MANIFEST.md, "Local external-consumer overlay".
$externalConsumersByProjectPath = [System.Collections.Generic.Dictionary[string, object[]]]::new([StringComparer]::OrdinalIgnoreCase)
$externalOverlayPath = Join-Path $repositoryRoot 'omp-components.external.json'
# Set only when the overlay file exists, so the summary can tell "no file" apart
# from "a file whose entries merged into nothing".
$externalOverlayMergedCount = $null

# An overlay entry that matches nothing used to be dropped silently, which reads
# exactly like a correct, empty overlay. Report it; -Strict makes it blocking.
function Add-ExternalOverlayProblem {
    param([Parameter(Mandatory = $true)][string]$Message)

    if ($Strict) {
        Add-ValidationError -Errors $errors -Message $Message
    }
    else {
        Add-ValidationWarning -Warnings $warnings -Message $Message
    }
}

if (Test-Path -LiteralPath $externalOverlayPath -PathType Leaf) {
    $externalOverlayMergedCount = 0
    $externalOverlay = $null
    try {
        $externalOverlay = ConvertFrom-JsonDocument -Json (Get-Content -LiteralPath $externalOverlayPath -Raw -Encoding UTF8) -Depth $jsonDepth
    }
    catch {
        Add-ValidationError -Errors $errors -Message "Could not read the external-consumer overlay '$externalOverlayPath': $($_.Exception.Message)"
    }

    if ($null -ne $externalOverlay) {
        $manifestSharedProjectPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($manifestSharedProject in @(Get-OptionalPropertyValue -Object $manifest -Name 'sharedProjects' | Where-Object { $null -ne $_ })) {
            $manifestSharedProjectPath = [string](Get-OptionalPropertyValue -Object $manifestSharedProject -Name 'projectPath')
            if (-not [string]::IsNullOrWhiteSpace($manifestSharedProjectPath)) {
                [void]$manifestSharedProjectPaths.Add($manifestSharedProjectPath)
            }
        }

        # Read the property itself: returning an empty array from a function (or
        # an if-expression) unrolls it to $null, which made an empty list read as
        # a missing key and sent the reader looking for a typo.
        $overlayProjectsProperty = $externalOverlay.PSObject.Properties['sharedProjects']
        $overlayProjects = $null
        if ($null -ne $overlayProjectsProperty) {
            $overlayProjects = $overlayProjectsProperty.Value
        }
        if ($null -eq $overlayProjectsProperty -or $null -eq $overlayProjectsProperty.Value) {
            Add-ExternalOverlayProblem -Message "The external-consumer overlay '$externalOverlayPath' has no 'sharedProjects' array; no external consumers were merged. Expected { `"sharedProjects`": [ { `"projectPath`": ..., `"externalConsumers`": [ ... ] } ] } (see docs/OMP_COMPONENT_MANIFEST.md)."
        }
        elseif (@($overlayProjectsProperty.Value).Count -eq 0) {
            Add-ExternalOverlayProblem -Message "The external-consumer overlay '$externalOverlayPath' has an empty 'sharedProjects' array; no external consumers were merged. Add an entry per shared project, or delete the overlay if it has nothing to declare."
        }

        foreach ($overlayProject in @($overlayProjects | Where-Object { $null -ne $_ })) {
            $overlayProjectPath = [string](Get-OptionalPropertyValue -Object $overlayProject -Name 'projectPath')
            if ([string]::IsNullOrWhiteSpace($overlayProjectPath)) {
                Add-ExternalOverlayProblem -Message "The external-consumer overlay '$externalOverlayPath' has a sharedProjects entry without 'projectPath'; it was ignored."
                continue
            }

            if (-not $manifestSharedProjectPaths.Contains($overlayProjectPath)) {
                Add-ExternalOverlayProblem -Message "The external-consumer overlay '$externalOverlayPath' names projectPath '$overlayProjectPath', which is not a sharedProjects entry in omp-components.json; its external consumers were ignored."
                continue
            }

            $overlayConsumers = @(Get-OptionalPropertyValue -Object $overlayProject -Name 'externalConsumers' | Where-Object { $null -ne $_ })
            $externalOverlayMergedCount += $overlayConsumers.Count
            if ($externalConsumersByProjectPath.ContainsKey($overlayProjectPath)) {
                $overlayConsumers = @($externalConsumersByProjectPath[$overlayProjectPath]) + $overlayConsumers
            }
            $externalConsumersByProjectPath[$overlayProjectPath] = $overlayConsumers
        }
    }
}

Write-Host 'Validating component versions...'
Write-Host ''

# The "Check N" numbers below are stable identifiers for cross-referencing
# error messages and docs (see docs/VALIDATOR_CHECKS.md); they are logical
# groupings, not execution order. Execution runs top-to-bottom, so a
# lower-numbered check may run after a higher-numbered one (for example
# Check 2 here precedes Check 1).

# ---------------------------------------------------------------------------
# Check 2: Repository version presence and format.
# ---------------------------------------------------------------------------
$repositoryVersion = [string](Get-OptionalPropertyValue -Object $manifest -Name 'repositoryVersion')
if ([string]::IsNullOrWhiteSpace($repositoryVersion)) {
    Add-ValidationError -Errors $errors -Message 'repositoryVersion is missing or empty in omp-components.json.'
}
elseif (-not (Test-SemverLikeVersion -Value $repositoryVersion)) {
    Add-ValidationError -Errors $errors -Message "repositoryVersion '$repositoryVersion' does not match the expected major.minor or major.minor.patch format."
}

# ---------------------------------------------------------------------------
# Build a lookup of module definitions for mapping and version checks.
# ---------------------------------------------------------------------------
$moduleDefinitionsByKey = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$moduleDefinitionObjectsByKey = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$moduleDefinitionVersionSyncCount = 0

foreach ($manifestDefinition in @($manifest.moduleDefinitions)) {
    if ($null -eq $manifestDefinition) {
        continue
    }

    $moduleKey = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'moduleKey')
    $definitionVersion = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'definitionVersion')
    $relativeDefinitionPath = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'path')

    if (-not [string]::IsNullOrWhiteSpace($moduleKey) -and -not $moduleDefinitionsByKey.ContainsKey($moduleKey)) {
        $moduleDefinitionsByKey.Add($moduleKey, $manifestDefinition)
    }

    # -----------------------------------------------------------------------
    # Check 4: Module definition version sync.
    # -----------------------------------------------------------------------
    if ([string]::IsNullOrWhiteSpace($relativeDefinitionPath)) {
        Add-ValidationError -Errors $errors -Message "Module definition '$moduleKey' is missing path in omp-components.json."
        continue
    }

    $definitionPath = Resolve-RepositoryPath -Path $relativeDefinitionPath -BasePath $repositoryRoot
    if (-not (Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
        Add-ValidationError -Errors $errors -Message "Module definition file was not found: $relativeDefinitionPath"
        continue
    }

    $definitionText = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8
    $definition = ConvertFrom-JsonDocument -Json $definitionText -Depth $jsonDepth

    $actualDefinitionVersion = [string](Get-OptionalPropertyValue -Object $definition -Name 'definitionVersion')
    if (-not [string]::Equals($definitionVersion, $actualDefinitionVersion, [StringComparison]::Ordinal)) {
        Add-ValidationError -Errors $errors -Message "Definition version mismatch for '$relativeDefinitionPath'. Manifest='$definitionVersion', definition='$actualDefinitionVersion'."
    }
    else {
        $moduleDefinitionVersionSyncCount++
    }

    if (-not [string]::IsNullOrWhiteSpace($moduleKey) -and -not $moduleDefinitionObjectsByKey.ContainsKey($moduleKey)) {
        $moduleDefinitionObjectsByKey.Add($moduleKey, $definition)
    }
}

# ---------------------------------------------------------------------------
# Component checks.
# ---------------------------------------------------------------------------
$projectPathCount = 0
$componentVersionCount = 0
$moduleMappingCount = 0
$minModuleVersionErrorCount = 0
$cascadeCheckCount = 0
$cascadeErrorCount = 0

foreach ($component in @($manifest.components)) {
    if ($null -eq $component) {
        continue
    }

    $componentKey = [string](Get-OptionalPropertyValue -Object $component -Name 'componentKey')
    if ([string]::IsNullOrWhiteSpace($componentKey)) {
        $componentKey = '<unknown>'
    }

    # -----------------------------------------------------------------------
    # Check 1: Component projectPath existence.
    # -----------------------------------------------------------------------
    $projectPath = [string](Get-OptionalPropertyValue -Object $component -Name 'projectPath')
    if ([string]::IsNullOrWhiteSpace($projectPath)) {
        Add-ValidationError -Errors $errors -Message "Component '$componentKey' is missing projectPath."
    }
    else {
        $fullProjectPath = Resolve-RepositoryPath -Path $projectPath -BasePath $repositoryRoot
        $foundCsproj = $false

        if ($projectPath -like '*.csproj') {
            $foundCsproj = Test-Path -LiteralPath $fullProjectPath -PathType Leaf
        }
        elseif (Test-Path -LiteralPath $fullProjectPath -PathType Container) {
            $csprojFiles = @(Get-ChildItem -LiteralPath $fullProjectPath -Filter '*.csproj' -File -ErrorAction SilentlyContinue)
            $foundCsproj = $csprojFiles.Count -gt 0
        }

        if (-not $foundCsproj) {
            Add-ValidationError -Errors $errors -Message "Component '$componentKey' projectPath does not resolve to a .csproj file: $projectPath"
        }
        else {
            $projectPathCount++
        }
    }

    # -----------------------------------------------------------------------
    # Check 3: Component version presence and format.
    # -----------------------------------------------------------------------
    $componentVersion = [string](Get-OptionalPropertyValue -Object $component -Name 'version')
    if ([string]::IsNullOrWhiteSpace($componentVersion)) {
        Add-ValidationError -Errors $errors -Message "Component '$componentKey' is missing version."
    }
    elseif (-not (Test-SemverLikeVersion -Value $componentVersion)) {
        Add-ValidationError -Errors $errors -Message "Component '$componentKey' version '$componentVersion' does not match the expected major.minor or major.minor.patch format."
    }
    else {
        $componentVersionCount++
    }

    # -----------------------------------------------------------------------
    # Check 4b: Worker plugin host-contract requirement.
    # -----------------------------------------------------------------------
    $minWorkerHostVersion = [string](Get-OptionalPropertyValue -Object $component -Name 'minWorkerHostVersion')
    if (-not [string]::IsNullOrWhiteSpace($minWorkerHostVersion)) {
        $packageType = [string](Get-OptionalPropertyValue -Object $component -Name 'packageType')
        if (-not [string]::Equals($packageType, 'worker', [StringComparison]::OrdinalIgnoreCase) `
                -and -not [string]::Equals($packageType, 'worker-plugin', [StringComparison]::OrdinalIgnoreCase)) {
            Add-ValidationError -Errors $errors -Message "Component '$componentKey' declares minWorkerHostVersion but packageType '$packageType' is not a worker plugin. Remove the declaration or set packageType to 'worker'/'worker-plugin'."
        }

        if (-not (Test-SemverLikeVersion -Value $minWorkerHostVersion)) {
            Add-ValidationError -Errors $errors -Message "Component '$componentKey' minWorkerHostVersion '$minWorkerHostVersion' does not match the expected major.minor or major.minor.patch format."
        }
    }

    # -----------------------------------------------------------------------
    # Check 5: Component-to-module mapping integrity.
    # -----------------------------------------------------------------------
    $moduleKey = [string](Get-OptionalPropertyValue -Object $component -Name 'moduleKey')
    if (-not [string]::IsNullOrWhiteSpace($moduleKey)) {
        if (-not $moduleDefinitionsByKey.ContainsKey($moduleKey)) {
            Add-ValidationError -Errors $errors -Message "Component '$componentKey' references moduleKey '$moduleKey' which is not declared in moduleDefinitions."
        }
        else {
            $moduleMappingCount++

            # -------------------------------------------------------------------
            # Check 6: minModuleDefinitionVersion sanity — HARD ERROR.
            # A component requiring a definition version higher than what the
            # module declares produces an internally inconsistent manifest. Any
            # package built from this state would carry a minVersion requirement
            # that no existing module definition can satisfy, so import would
            # always fail at runtime.
            # -------------------------------------------------------------------
            $minModuleDefinitionVersion = [string](Get-OptionalPropertyValue -Object $component -Name 'minModuleDefinitionVersion')
            if (-not [string]::IsNullOrWhiteSpace($minModuleDefinitionVersion)) {
                $actualVersion = [string](Get-OptionalPropertyValue -Object $moduleDefinitionsByKey[$moduleKey] -Name 'definitionVersion')
                $minVersionObj = ConvertTo-VersionOrNull -Value $minModuleDefinitionVersion
                $actualVersionObj = ConvertTo-VersionOrNull -Value $actualVersion

                if ($null -ne $minVersionObj -and $null -ne $actualVersionObj -and $minVersionObj -gt $actualVersionObj) {
                    Add-ValidationError -Errors $errors -Message "Component '$componentKey' requires minModuleDefinitionVersion '$minModuleDefinitionVersion' which is greater than the declared module definition version '$actualVersion' for moduleKey '$moduleKey'."
                    $minModuleVersionErrorCount++
                }
            }

            # -------------------------------------------------------------------
            # Check 10: compatibleArtifacts range sanity — HARD ERROR.
            # A component's version must fall within the minVersion/maxVersion
            # range declared in its module's compatibleArtifacts entry for the
            # same appKey, otherwise the produced artifact cannot be imported.
            # -------------------------------------------------------------------
            $componentAppKey = [string](Get-OptionalPropertyValue -Object $component -Name 'appKey')
            if (-not [string]::IsNullOrWhiteSpace($componentAppKey) -and $moduleDefinitionObjectsByKey.ContainsKey($moduleKey)) {
                $definitionObject = $moduleDefinitionObjectsByKey[$moduleKey]
                $compatibleArtifacts = Get-OptionalPropertyValue -Object $definitionObject -Name 'compatibleArtifacts'
                if ($null -ne $compatibleArtifacts) {
                    $matchingArtifact = $null
                    foreach ($artifact in @($compatibleArtifacts)) {
                        if ($null -eq $artifact) {
                            continue
                        }

                        $artifactAppKey = [string](Get-OptionalPropertyValue -Object $artifact -Name 'appKey')
                        if ([string]::Equals($artifactAppKey, $componentAppKey, [StringComparison]::Ordinal)) {
                            $matchingArtifact = $artifact
                            break
                        }
                    }

                    if ($null -ne $matchingArtifact) {
                        $componentVersionObj = ConvertTo-VersionOrNull -Value $componentVersion
                        $maxArtifactVersion = [string](Get-OptionalPropertyValue -Object $matchingArtifact -Name 'maxVersion')
                        $minArtifactVersion = [string](Get-OptionalPropertyValue -Object $matchingArtifact -Name 'minVersion')

                        if (-not [string]::IsNullOrWhiteSpace($maxArtifactVersion)) {
                            $maxVersionObj = ConvertTo-VersionOrNull -Value $maxArtifactVersion
                            if ($null -ne $componentVersionObj -and $null -ne $maxVersionObj -and $componentVersionObj -gt $maxVersionObj) {
                                Add-ValidationError -Errors $errors -Message "Component '$componentKey' version '$componentVersion' exceeds compatibleArtifacts maxVersion '$maxArtifactVersion' for appKey '$componentAppKey'. Bump maxVersion to at least '$componentVersion'."
                            }
                        }

                        if (-not [string]::IsNullOrWhiteSpace($minArtifactVersion)) {
                            $minVersionObj = ConvertTo-VersionOrNull -Value $minArtifactVersion
                            if ($null -ne $componentVersionObj -and $null -ne $minVersionObj -and $componentVersionObj -lt $minVersionObj) {
                                Add-ValidationError -Errors $errors -Message "Component '$componentKey' version '$componentVersion' is below compatibleArtifacts minVersion '$minArtifactVersion' for appKey '$componentAppKey'. Bump minVersion to at most '$componentVersion'."
                            }
                        }
                    }
                }
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Resolve base ref for diff-based checks (Checks 7, 8, 9, 12, 13).
# Exemption: Behavior-neutral refactors (identical emitted strings/IL) do not require
# a cascade consumer bump. Only binary-affecting changes (new/removed APIs, changed
# default values, changed serialization format, etc.) require all consumers to be bumped.
# When running multi-phase campaigns, pass -BaseCommit to pin the diff baseline.
# ---------------------------------------------------------------------------
$baseRef = 'origin/main'
$baseRefAvailable = $false

if (-not [string]::IsNullOrWhiteSpace($BaseCommit)) {
    if (Test-GitRefAvailable -RepositoryRoot $repositoryRoot -Ref $BaseCommit) {
        $baseRef = $BaseCommit
        $baseRefAvailable = $true
    }
    else {
        Add-ValidationError -Errors $errors -Message "The specified -BaseCommit '$BaseCommit' could not be resolved. Verify the commit SHA exists in this repository."
    }
}
else {
    Add-ValidationWarning -Warnings $warnings -Message 'No -BaseCommit specified; cascade diff uses origin/main. Binary-affecting shared changes committed in earlier campaign phases may not trigger cascade bumps. Pass -BaseCommit <sha> to diff against a fixed baseline.'

    $baseRefAvailable = Test-GitRefAvailable -RepositoryRoot $repositoryRoot -Ref $baseRef
    if (-not $baseRefAvailable) {
        Add-ValidationWarning -Warnings $warnings -Message 'origin/main could not be resolved; skipping shared-project cascade validation.'
    }
}

$baseManifest = $null
$baseComponentsByKey = $null
if ($baseRefAvailable) {
    # Fail loudly when the baseline manifest cannot be read. Swallowing the git error left
    # $baseComponentsByKey empty while $baseRefAvailable stayed true, so every consumer lookup
    # missed, no consumer was ever flagged as unbumped, and the script still printed
    # "validation passed" -- the entire cascade check silently disabled. Reachable on a
    # blobless clone, after a rebase or squash, or with a -BaseCommit predating the manifest.
    # The consumer-repository sibling validators already error on both an unreadable manifest
    # and invalid JSON (R8-P4-7).
    $baseManifestText = Get-GitFileTextAtRef -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path 'omp-components.json' -Errors $errors -CheckDescription 'The baseline manifest read'
    if ([string]::IsNullOrWhiteSpace($baseManifestText)) {
        Add-ValidationError -Errors $errors -Message "Could not read 'omp-components.json' at '$baseRef'. The cascade and lockstep checks cannot run without a baseline manifest."
    }
    else {
        $baseManifest = ConvertFrom-JsonDocument -Json $baseManifestText -Depth $jsonDepth
    }

    $baseComponentsByKey = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    if ($null -ne $baseManifest) {
        foreach ($baseComponent in @($baseManifest.components)) {
            if ($null -eq $baseComponent) {
                continue
            }

            $key = [string](Get-OptionalPropertyValue -Object $baseComponent -Name 'componentKey')
            if (-not [string]::IsNullOrWhiteSpace($key) -and -not $baseComponentsByKey.ContainsKey($key)) {
                $baseComponentsByKey.Add($key, $baseComponent)
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Consistent-artifact-set membership, resolved up front.
# ---------------------------------------------------------------------------
# The cascade and lockstep checks below say "bump component X". When X belongs to a
# consistentArtifactSets set, doing exactly that breaks the set check further down --
# every member has to move together -- so following the instruction produced the next
# error, deterministically (R7-G17). The membership map is built here so those messages
# can name the siblings that must be bumped in the same commit.
$consistentSetSiblingsByComponentKey = [System.Collections.Generic.Dictionary[string, string[]]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($membershipModuleKey in $moduleDefinitionObjectsByKey.Keys) {
    $membershipDefinition = $moduleDefinitionObjectsByKey[$membershipModuleKey]
    foreach ($membershipSet in @(Get-OptionalPropertyValue -Object $membershipDefinition -Name 'consistentArtifactSets')) {
        if ($null -eq $membershipSet) {
            continue
        }

        $membershipComponentKeys = [System.Collections.Generic.List[string]]::new()
        foreach ($membershipMember in @(Get-OptionalPropertyValue -Object $membershipSet -Name 'expectedArtifacts')) {
            if ($null -eq $membershipMember) {
                continue
            }

            $membershipTargetName = [string](Get-OptionalPropertyValue -Object $membershipMember -Name 'targetName')
            $membershipPackageType = [string](Get-OptionalPropertyValue -Object $membershipMember -Name 'packageType')
            foreach ($membershipComponent in @($manifest.components)) {
                if ($null -eq $membershipComponent) {
                    continue
                }

                if ([string]::Equals([string](Get-OptionalPropertyValue -Object $membershipComponent -Name 'targetName'), $membershipTargetName, [StringComparison]::OrdinalIgnoreCase) -and
                    [string]::Equals([string](Get-OptionalPropertyValue -Object $membershipComponent -Name 'packageType'), $membershipPackageType, [StringComparison]::OrdinalIgnoreCase)) {
                    $membershipKey = [string](Get-OptionalPropertyValue -Object $membershipComponent -Name 'componentKey')
                    if (-not [string]::IsNullOrWhiteSpace($membershipKey)) {
                        [void]$membershipComponentKeys.Add($membershipKey)
                    }

                    break
                }
            }
        }

        foreach ($membershipKey in $membershipComponentKeys) {
            $siblings = @($membershipComponentKeys | Where-Object { $_ -ne $membershipKey })
            if ($siblings.Count -gt 0) {
                $consistentSetSiblingsByComponentKey[$membershipKey] = $siblings
            }
        }
    }
}

function Get-ConsistentSetBumpHint {
    param([string]$ComponentKey)

    if ([string]::IsNullOrWhiteSpace($ComponentKey) -or -not $consistentSetSiblingsByComponentKey.ContainsKey($ComponentKey)) {
        return ''
    }

    $siblings = $consistentSetSiblingsByComponentKey[$ComponentKey] -join ', '
    return " '$ComponentKey' is in a consistentArtifactSets set, so bump it together with: $siblings."
}

# ---------------------------------------------------------------------------
# Check 7: Shared project cascade version bumps.
# ---------------------------------------------------------------------------
# Normalize to a null-free array once so every loop below (Checks 7, 11 and the
# summary) iterates real entries instead of a single $null element when the
# manifest has no sharedProjects property.
$sharedProjects = @(Get-OptionalPropertyValue -Object $manifest -Name 'sharedProjects' | Where-Object { $null -ne $_ })
if ($sharedProjects.Count -gt 0 -and $baseRefAvailable) {
    foreach ($sharedProject in @($sharedProjects)) {
        if ($null -eq $sharedProject) {
            continue
        }

        $projectPath = [string](Get-OptionalPropertyValue -Object $sharedProject -Name 'projectPath')
        if ([string]::IsNullOrWhiteSpace($projectPath)) {
            continue
        }

        $diffPath = $projectPath
        if ($projectPath -like '*.csproj') {
            $diffPath = Split-Path -Parent $projectPath
        }

        $changedFiles = Get-GitChangedFiles -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path $diffPath -Errors $errors -CheckDescription "Check 7 (shared project cascade) for '$projectPath'"
        if ($null -eq $changedFiles -or [string]::IsNullOrWhiteSpace($changedFiles)) {
            continue
        }

        # R8-P4-17. The cascade rule stopped at the repository boundary: this loop
        # works out which components consume a shared project, but only OMP's own.
        # A module web app in a consumer repository references Web.Shared straight
        # out of the sibling repository, and nothing here knew it. The solution still compiles -- the
        # reference is by project, not by package -- so the mismatch only surfaced
        # when the host rejected the artifact at import, at the end of a full
        # refresh-and-stage run. That happened three deploys in a row on
        # 2026-08-13.
        #
        # This is a warning rather than an error on purpose: the bump has to happen
        # in the other repository, so OMP cannot satisfy its own check. The gate
        # that blocks is Check 14 in the consuming repository's validator, which
        # compares the recorded tree id of this project against the sibling's
        # actual state. The warning here exists so an OMP author learns about the
        # sibling now instead of at the far end of a deploy.
        #
        # The public manifest lists no external consumers; they come from the optional
        # local overlay read at the top of this script and are merged in here.
        $externalConsumers = @(Get-OptionalPropertyValue -Object $sharedProject -Name 'externalConsumers' | Where-Object { $null -ne $_ })
        if ($externalConsumersByProjectPath.ContainsKey($projectPath)) {
            $externalConsumers = $externalConsumers + @($externalConsumersByProjectPath[$projectPath])
        }
        foreach ($externalConsumer in $externalConsumers) {
            $externalRepositoryKey = [string](Get-OptionalPropertyValue -Object $externalConsumer -Name 'repositoryKey')
            $externalComponentKey = [string](Get-OptionalPropertyValue -Object $externalConsumer -Name 'componentKey')
            Add-ValidationWarning -Warnings $warnings -Message "Shared project '$projectPath' changed and is consumed by '$externalComponentKey' in the '$externalRepositoryKey' repository. Bump that component there and re-record with 'scripts/omp/validate-component-versions.ps1 -UpdateSharedDependencies', or the host will reject its artifact at import."
        }

        $consumers = @(Get-OptionalPropertyValue -Object $sharedProject -Name 'consumers' | Where-Object { $null -ne $_ })
        if ($consumers.Count -eq 0) {
            continue
        }

        $unbumpedConsumers = [System.Collections.Generic.List[string]]::new()
        foreach ($consumerKey in $consumers) {
            $currentComponent = $null
            foreach ($component in @($manifest.components)) {
                # Ordinal, like every other component-key comparison in this file. -eq is
                # case-insensitive, so a consumer key cased differently from the component
                # key matched here and nowhere else.
                if ([string]::Equals(
                        [string](Get-OptionalPropertyValue -Object $component -Name 'componentKey'),
                        [string]$consumerKey,
                        [StringComparison]::Ordinal)) {
                    $currentComponent = $component
                    break
                }
            }

            if ($null -eq $currentComponent) {
                Add-ValidationWarning -Warnings $warnings -Message "Shared project '$projectPath' lists consumer '$consumerKey' which is not declared in components."
                continue
            }

            $baseVersion = $null
            if ($baseComponentsByKey.ContainsKey($consumerKey)) {
                $baseVersion = [string](Get-OptionalPropertyValue -Object $baseComponentsByKey[$consumerKey] -Name 'version')
            }

            $currentVersion = [string](Get-OptionalPropertyValue -Object $currentComponent -Name 'version')

            if (-not [string]::IsNullOrWhiteSpace($baseVersion) -and $baseVersion -eq $currentVersion) {
                $unbumpedConsumers.Add($consumerKey)
            }
        }

        if ($unbumpedConsumers.Count -gt 0) {
            $consumerList = ($unbumpedConsumers | Sort-Object) -join ', '
            $setHints = ''
            foreach ($unbumpedConsumer in @($unbumpedConsumers | Sort-Object)) {
                $setHints += Get-ConsistentSetBumpHint -ComponentKey $unbumpedConsumer
            }

            Add-ValidationError -Errors $errors -Message ("Shared project '$projectPath' changed but the following consumers were not bumped: $consumerList. Run `.\scripts\omp\bump-version.ps1 -CascadeFrom $projectPath` or manually bump the listed components." + $setHints)
            $cascadeErrorCount++
        }
        else {
            $cascadeCheckCount++
        }
    }
}

# ---------------------------------------------------------------------------
# Check 11: Web.Shared binary identity (environment-stable parent-vs-HEAD).
# Check 7 forces cascade-bumps when Web.Shared SOURCE changes. This check
# catches when the Web.Shared BINARY changes without its own source changing
# (for example a change in a referenced project or dependency). It builds
# OpenModulePlatform.Web.Shared.dll from BOTH the parent commit and HEAD with
# identical settings in the SAME environment, then compares the two hashes.
# Because both hashes come from the same runner, environment differences are
# eliminated and no absolute committed baseline is required.
# ---------------------------------------------------------------------------
$webSharedBinaryCheckPassed = $false
$webSharedBinaryCheckMessage = ''

$normalizedWebSharedProjectPath = 'OpenModulePlatform.Web.Shared/OpenModulePlatform.Web.Shared.csproj'

$webSharedProject = $null
foreach ($sharedProject in @($sharedProjects)) {
    if ($null -eq $sharedProject) {
        continue
    }

    $projectPath = [string](Get-OptionalPropertyValue -Object $sharedProject -Name 'projectPath')
    if ([string]::Equals($projectPath, $normalizedWebSharedProjectPath, [StringComparison]::OrdinalIgnoreCase)) {
        $webSharedProject = $sharedProject
        break
    }
}

if ($null -eq $webSharedProject) {
    Add-ValidationWarning -Warnings $warnings -Message "Shared project '$normalizedWebSharedProjectPath' was not found in omp-components.json; skipping Web.Shared binary identity check (Check 11)."
}
elseif (-not $baseRefAvailable) {
    Add-ValidationWarning -Warnings $warnings -Message 'No valid base ref available; skipping Web.Shared binary identity check (Check 11). Pass -BaseCommit to enable it.'
}
else {
    Write-Host 'Check 11: Comparing OpenModulePlatform.Web.Shared.dll binary identity between parent and HEAD...'

    $sourceRoot = $null
    $parentOutputRoot = $null
    $headOutputRoot = $null
    $archivePath = $null

    try {
        # Both commits are extracted into the SAME temporary source directory
        # (sequentially) so the absolute source path is identical for both
        # builds. This eliminates path-dependent binary differences that can
        # leak even with PathMap/DeterministicSourcePaths. Using git archive
        # also excludes .git metadata so embedded commit hashes cannot differ
        # between the parent and HEAD builds.
        $sourceRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-webshared-src-' + [Guid]::NewGuid().ToString('N'))
        $parentOutputRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-webshared-parent-out-' + [Guid]::NewGuid().ToString('N'))
        $headOutputRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-webshared-head-out-' + [Guid]::NewGuid().ToString('N'))
        $archivePath = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-webshared-src-' + [Guid]::NewGuid().ToString('N') + '.zip')

        New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null

        function Export-CommitSource {
            param(
                [Parameter(Mandatory = $true)][string]$Commit,
                [Parameter(Mandatory = $true)][string]$Destination
            )

            if (Test-Path -LiteralPath $archivePath) {
                Remove-Item -LiteralPath $archivePath -Force
            }

            & git -C $repositoryRoot archive --format=zip -o $archivePath $Commit
            if ($LASTEXITCODE -ne 0) {
                throw "git archive failed for $Commit"
            }

            if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
                throw "git archive did not produce $archivePath for $Commit"
            }

            Expand-Archive -LiteralPath $archivePath -DestinationPath $Destination -Force
        }

        Export-CommitSource -Commit $baseRef -Destination $sourceRoot

        $parentProjectPath = Join-Path $sourceRoot $normalizedWebSharedProjectPath
        if (-not (Test-Path -LiteralPath $parentProjectPath -PathType Leaf)) {
            throw "Web.Shared project was not found in archived parent source: $parentProjectPath"
        }

        $parentDllPath = Build-WebSharedForBinaryIdentity -ProjectPath $parentProjectPath -RepositoryRoot $sourceRoot -OutputRoot $parentOutputRoot

        # Clean the shared source directory so HEAD can be extracted to the same path.
        Get-ChildItem -LiteralPath $sourceRoot | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

        Export-CommitSource -Commit 'HEAD' -Destination $sourceRoot

        $headProjectPath = Join-Path $sourceRoot $normalizedWebSharedProjectPath
        if (-not (Test-Path -LiteralPath $headProjectPath -PathType Leaf)) {
            throw "Web.Shared project was not found in archived HEAD source: $headProjectPath"
        }

        $headDllPath = Build-WebSharedForBinaryIdentity -ProjectPath $headProjectPath -RepositoryRoot $sourceRoot -OutputRoot $headOutputRoot

        $parentHash = Get-FileSha256Hex -Path $parentDllPath
        $headHash = Get-FileSha256Hex -Path $headDllPath

        $consumerKeys = @(Get-OptionalPropertyValue -Object $webSharedProject -Name 'consumers')
        $unbumpedConsumers = [System.Collections.Generic.List[string]]::new()
        foreach ($consumerKey in $consumerKeys) {
            $currentComponent = $null
            foreach ($component in @($manifest.components)) {
                # Ordinal, like every other component-key comparison in this file. -eq is
                # case-insensitive, so a consumer key cased differently from the component
                # key matched here and nowhere else.
                if ([string]::Equals(
                        [string](Get-OptionalPropertyValue -Object $component -Name 'componentKey'),
                        [string]$consumerKey,
                        [StringComparison]::Ordinal)) {
                    $currentComponent = $component
                    break
                }
            }

            if ($null -eq $currentComponent) {
                Add-ValidationWarning -Warnings $warnings -Message "Check 11: Web.Shared consumer '$consumerKey' was not found in components."
                continue
            }

            $baseVersion = $null
            if ($baseComponentsByKey.ContainsKey($consumerKey)) {
                $baseVersion = [string](Get-OptionalPropertyValue -Object $baseComponentsByKey[$consumerKey] -Name 'version')
            }

            $currentVersion = [string](Get-OptionalPropertyValue -Object $currentComponent -Name 'version')

            if (-not [string]::IsNullOrWhiteSpace($baseVersion) -and $baseVersion -eq $currentVersion) {
                [void]$unbumpedConsumers.Add($consumerKey)
            }
        }

        $cascadeBumped = $unbumpedConsumers.Count -eq 0

        $comparison = Compare-WebSharedBinaryIdentity -ParentHash $parentHash -HeadHash $headHash -CascadeBumped $cascadeBumped
        if ($comparison.Result -eq 'Pass') {
            $webSharedBinaryCheckPassed = $true
            $webSharedBinaryCheckMessage = $comparison.Message
        }
        elseif ($comparison.Result -eq 'Fail') {
            Add-ValidationError -Errors $errors -Message $comparison.Message
        }
        else {
            Add-ValidationWarning -Warnings $warnings -Message "Check 11: $($comparison.Message)"
        }
    }
    catch {
        # A build failure is not an infrastructure hiccup. Every exception used to become a
        # warning, and warnings do not affect the exit code, so 'dotnet build failed' --
        # Web.Shared not compiling at all -- passed validation (R7-G10). Only failures that
        # really are environmental stay warnings; a failed compile is an error.
        $failureText = [string]$_
        if ($failureText -match 'dotnet build failed|Build succeeded but .* was not found') {
            Add-ValidationError -Errors $errors -Message "Check 11: Web.Shared did not build, so binary identity could not be compared: $failureText"
        }
        else {
            Add-ValidationWarning -Warnings $warnings -Message "Check 11: Could not perform Web.Shared binary identity comparison (infra issue): $failureText"
        }
    }
    finally {
        foreach ($pathToClean in @($sourceRoot, $parentOutputRoot, $headOutputRoot, $archivePath)) {
            if (-not [string]::IsNullOrWhiteSpace($pathToClean) -and (Test-Path -LiteralPath $pathToClean)) {
                try {
                    Remove-Item -LiteralPath $pathToClean -Recurse -Force -ErrorAction SilentlyContinue
                }
                catch {
                    # Best-effort cleanup of temporary build artifacts.
                }
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Check 8: Module-definition SQL diff enforcement.
# If an owned SQL script referenced by a production module definition changes
# in a material way (not just comments or whitespace), the module's
# definitionVersion must be bumped in both omp-components.json and the
# .module-definition.json file.
# ---------------------------------------------------------------------------
$sqlFilesChecked = 0
$sqlFilesPassed = 0
$sqlFilesChanged = 0

if (-not $baseRefAvailable) {
    Add-ValidationWarning -Warnings $warnings -Message 'No valid base ref available; skipping module-definition SQL diff enforcement (Check 8). Pass -BaseCommit to enable it.'
}
else {
    $baseModuleDefinitionsByKey = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    if ($null -ne $baseManifest) {
        foreach ($baseDefinition in @($baseManifest.moduleDefinitions)) {
            if ($null -eq $baseDefinition) {
                continue
            }

            $key = [string](Get-OptionalPropertyValue -Object $baseDefinition -Name 'moduleKey')
            if (-not [string]::IsNullOrWhiteSpace($key) -and -not $baseModuleDefinitionsByKey.ContainsKey($key)) {
                $baseModuleDefinitionsByKey.Add($key, $baseDefinition)
            }
        }
    }

    $ownedSqlFiles = [System.Collections.Generic.List[System.Collections.Hashtable]]::new()
    foreach ($manifestDefinition in @($manifest.moduleDefinitions)) {
        if ($null -eq $manifestDefinition) {
            continue
        }

        $moduleKey = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'moduleKey')
        $relativeDefinitionPath = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'path')

        if ([string]::IsNullOrWhiteSpace($moduleKey) -or [string]::IsNullOrWhiteSpace($relativeDefinitionPath)) {
            continue
        }

        $definitionPath = Resolve-RepositoryPath -Path $relativeDefinitionPath -BasePath $repositoryRoot
        if (-not (Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
            continue
        }

        $definitionText = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8
        $definition = ConvertFrom-JsonDocument -Json $definitionText -Depth $jsonDepth

        # Get-OptionalPropertyValue, not $definition.sqlScripts: a definition
        # without sqlScripts is legal, and a direct read throws
        # PropertyNotFoundException under Set-StrictMode (second opinion on the
        # line-ending cleanup, 2026-10-08).
        foreach ($script in @(Get-OptionalPropertyValue -Object $definition -Name 'sqlScripts')) {
            if ($null -eq $script) {
                continue
            }

            $sqlPath = [string](Get-OptionalPropertyValue -Object $script -Name 'path')
            if ([string]::IsNullOrWhiteSpace($sqlPath)) {
                continue
            }

            $alreadyOwned = $false
            foreach ($ownedSqlFile in $ownedSqlFiles) {
                if ([string]::Equals($ownedSqlFile.sqlPath, $sqlPath, [StringComparison]::OrdinalIgnoreCase)) {
                    $alreadyOwned = $true
                    break
                }
            }

            if (-not $alreadyOwned) {
                $ownedSqlFiles.Add(@{
                    moduleKey = $moduleKey
                    relativeDefinitionPath = $relativeDefinitionPath
                    sqlPath = $sqlPath
                })
            }
        }
    }

    foreach ($ownedSqlFile in $ownedSqlFiles) {
        $moduleKey = $ownedSqlFile.moduleKey
        $relativeDefinitionPath = $ownedSqlFile.relativeDefinitionPath
        $sqlPath = $ownedSqlFile.sqlPath
        $fullSqlPath = Resolve-RepositoryPath -Path $sqlPath -BasePath $repositoryRoot

        $sqlFilesChecked++

        if (-not (Test-Path -LiteralPath $fullSqlPath -PathType Leaf)) {
            Add-ValidationError -Errors $errors -Message "SQL script referenced by module '$moduleKey' was not found: $sqlPath"
            continue
        }

        $changedFiles = Get-GitChangedFiles -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path $sqlPath -Errors $errors -CheckDescription "The SQL change check for '$sqlPath'"
        if ($null -eq $changedFiles) {
            continue
        }

        if ([string]::IsNullOrWhiteSpace($changedFiles)) {
            $sqlFilesPassed++
            continue
        }

        $headText = Get-Content -LiteralPath $fullSqlPath -Raw -Encoding UTF8

        $baseText = Get-GitFileTextAtRef -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path $sqlPath -Errors $errors -CheckDescription "The SQL change check for '$sqlPath'"
        if ($null -eq $baseText) {
            continue
        }

        $isNewFile = [string]::IsNullOrWhiteSpace($baseText)

        $headNormalized = ConvertTo-NormalizedSql -SqlText $headText
        # A file that does not exist at the base ref has no text to normalize or
        # hash; the mandatory string parameters below reject an empty string.
        $baseNormalized = if ($isNewFile) { '' } else { ConvertTo-NormalizedSql -SqlText $baseText }

        $headHash = Get-Sha256Hex -Text $headNormalized
        $baseHash = if ($isNewFile) { '' } else { Get-Sha256Hex -Text $baseNormalized }

        if (-not $isNewFile -and $headHash -eq $baseHash) {
            $sqlFilesPassed++
            continue
        }

        $sqlFilesChanged++

        $headManifestDefinitionVersion = [string](Get-OptionalPropertyValue -Object $moduleDefinitionsByKey[$moduleKey] -Name 'definitionVersion')
        $baseManifestDefinitionVersion = $null
        if ($baseModuleDefinitionsByKey.ContainsKey($moduleKey)) {
            $baseManifestDefinitionVersion = [string](Get-OptionalPropertyValue -Object $baseModuleDefinitionsByKey[$moduleKey] -Name 'definitionVersion')
        }

        $manifestBumpPresent = $false
        if ([string]::IsNullOrWhiteSpace($baseManifestDefinitionVersion)) {
            $manifestBumpPresent = -not [string]::IsNullOrWhiteSpace($headManifestDefinitionVersion)
        }
        else {
            $manifestBumpPresent = -not [string]::Equals($baseManifestDefinitionVersion, $headManifestDefinitionVersion, [StringComparison]::Ordinal)
        }

        $headDefinitionText = Get-Content -LiteralPath (Resolve-RepositoryPath -Path $relativeDefinitionPath -BasePath $repositoryRoot) -Raw -Encoding UTF8
        $headDefinition = ConvertFrom-JsonDocument -Json $headDefinitionText -Depth $jsonDepth
        $headDefinitionVersion = [string](Get-OptionalPropertyValue -Object $headDefinition -Name 'definitionVersion')

        $baseDefinitionText = Get-GitFileTextAtRef -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path $relativeDefinitionPath -Errors $errors -CheckDescription "The module definition version check for '$relativeDefinitionPath'"
        if ($null -eq $baseDefinitionText) {
            continue
        }

        $baseDefinitionVersion = $null
        if (-not [string]::IsNullOrWhiteSpace($baseDefinitionText)) {
            $baseDefinition = ConvertFrom-JsonDocument -Json $baseDefinitionText -Depth $jsonDepth
            $baseDefinitionVersion = [string](Get-OptionalPropertyValue -Object $baseDefinition -Name 'definitionVersion')
        }

        $definitionBumpPresent = $false
        if ([string]::IsNullOrWhiteSpace($baseDefinitionVersion)) {
            $definitionBumpPresent = -not [string]::IsNullOrWhiteSpace($headDefinitionVersion)
        }
        else {
            $definitionBumpPresent = -not [string]::Equals($baseDefinitionVersion, $headDefinitionVersion, [StringComparison]::Ordinal)
        }

        if (-not $manifestBumpPresent -or -not $definitionBumpPresent) {
            Add-ValidationError -Errors $errors -Message "SQL '$sqlPath' changed (module '$moduleKey') but definitionVersion was not bumped in omp-components.json and/or the module-definition JSON. Bump definitionVersion, re-run scripts/dev/embed-module-definition-sql.ps1, and update relevant minModuleDefinitionVersion values."
        }
        else {
            $newDefinitionVersion = $headManifestDefinitionVersion
            $newDefinitionVersionObj = ConvertTo-VersionOrNull -Value $newDefinitionVersion

            foreach ($component in @($manifest.components)) {
                if ($null -eq $component) {
                    continue
                }

                $componentModuleKey = [string](Get-OptionalPropertyValue -Object $component -Name 'moduleKey')
                if (-not [string]::Equals($componentModuleKey, $moduleKey, [StringComparison]::Ordinal)) {
                    continue
                }

                $minModuleDefinitionVersion = [string](Get-OptionalPropertyValue -Object $component -Name 'minModuleDefinitionVersion')
                if ([string]::IsNullOrWhiteSpace($minModuleDefinitionVersion)) {
                    continue
                }

                $minVersionObj = ConvertTo-VersionOrNull -Value $minModuleDefinitionVersion
                if ($null -ne $minVersionObj -and $null -ne $newDefinitionVersionObj -and $minVersionObj -lt $newDefinitionVersionObj) {
                    $componentKey = [string](Get-OptionalPropertyValue -Object $component -Name 'componentKey')
                    if ([string]::IsNullOrWhiteSpace($componentKey)) {
                        $componentKey = '<unknown>'
                    }

                    # Check 8b: minModuleDefinitionVersion lags a bumped definitionVersion — HARD ERROR.
                    # The module's SQL contract changed and the definitionVersion was raised. Any
                    # component that exposes a minModuleDefinitionVersion for the same module must
                    # be updated to at least the new version, otherwise packages can be imported
                    # into environments with an older definition and fail at runtime due to missing
                    # schema/metadata.
                    Add-ValidationError -Errors $errors -Message "Component '$componentKey' has minModuleDefinitionVersion '$minModuleDefinitionVersion' which is less than the new definitionVersion '$newDefinitionVersion' for module '$moduleKey'. Update minModuleDefinitionVersion to at least '$newDefinitionVersion'."
                }
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Check 12: Module-definition content diff enforcement.
# HostAgent rejects re-importing a module definition whose version already
# exists in the database with different JSON, and that rejection silently
# skips every artifact item bundled in the same universal package (the import
# summary only shows "Skipped"). Any content change to a .module-definition.json
# therefore requires a definitionVersion bump - not only SQL-affecting changes,
# which Check 8 already covers. The comparison reads the working tree directly
# so uncommitted edits are caught before local CI commits them.
# ---------------------------------------------------------------------------
$definitionDiffChecked = 0
$definitionDiffChanged = 0
$definitionDiffPassed = 0

if (-not $baseRefAvailable) {
    Add-ValidationWarning -Warnings $warnings -Message 'No valid base ref available; skipping module-definition content diff enforcement (Check 12). Pass -BaseCommit to enable it.'
}
else {
    foreach ($manifestDefinition in @($manifest.moduleDefinitions)) {
        if ($null -eq $manifestDefinition) {
            continue
        }

        $moduleKey = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'moduleKey')
        $relativeDefinitionPath = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'path')
        if ([string]::IsNullOrWhiteSpace($moduleKey) -or [string]::IsNullOrWhiteSpace($relativeDefinitionPath)) {
            continue
        }

        $definitionPath = Resolve-RepositoryPath -Path $relativeDefinitionPath -BasePath $repositoryRoot
        if (-not (Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
            continue # missing file is already an error in Check 4
        }

        $definitionDiffChecked++

        $baseDefinitionText = Get-GitFileTextAtRef -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path $relativeDefinitionPath -Errors $errors -CheckDescription "The module definition diff check for '$relativeDefinitionPath'"
        if ($null -eq $baseDefinitionText) {
            continue
        }

        if ([string]::IsNullOrWhiteSpace($baseDefinitionText)) {
            $definitionDiffPassed++
            continue # new definition file; nothing to bump against
        }

        $headDefinitionText = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8
        $headNormalized = (Remove-Utf8Bom -Text $headDefinitionText).Replace("`r`n", "`n").TrimEnd("`n")
        $baseNormalized = $baseDefinitionText.Replace("`r`n", "`n").TrimEnd("`n")
        if ([string]::Equals($headNormalized, $baseNormalized, [StringComparison]::Ordinal)) {
            $definitionDiffPassed++
            continue
        }

        $definitionDiffChanged++

        $headDefinition = ConvertFrom-JsonDocument -Json $headDefinitionText -Depth $jsonDepth
        $headDefinitionVersion = [string](Get-OptionalPropertyValue -Object $headDefinition -Name 'definitionVersion')
        $baseDefinition = ConvertFrom-JsonDocument -Json $baseDefinitionText -Depth $jsonDepth
        $baseDefinitionVersion = [string](Get-OptionalPropertyValue -Object $baseDefinition -Name 'definitionVersion')

        # The content changed, so this check passes only when a bump can be shown. It used
        # to pass whenever the base version was blank or unreadable, which is the same
        # evidence Check 8 treats as a hard error -- two checks reading one file and
        # drawing opposite conclusions from the identical missing value (R7-G9). An
        # unprovable bump is now a failure.
        if ([string]::IsNullOrWhiteSpace($headDefinitionVersion)) {
            Add-ValidationError -Errors $errors -Message "Module definition '$relativeDefinitionPath' (module '$moduleKey') changed but carries no definitionVersion. HostAgent rejects a re-imported definition version with different JSON and silently skips artifacts packaged with it. Add definitionVersion to the definition file and omp-components.json."
        }
        elseif ([string]::IsNullOrWhiteSpace($baseDefinitionVersion)) {
            # No version at the base ref and one at HEAD is a bump by definition.
            $definitionDiffPassed++
        }
        elseif ([string]::Equals($baseDefinitionVersion, $headDefinitionVersion, [StringComparison]::Ordinal)) {
            Add-ValidationError -Errors $errors -Message "Module definition '$relativeDefinitionPath' (module '$moduleKey') changed but definitionVersion is still '$headDefinitionVersion'. HostAgent rejects a re-imported definition version with different JSON and silently skips artifacts packaged with it. Bump definitionVersion in both the definition file and omp-components.json."
        }
        else {
            $definitionDiffPassed++
        }
    }
}

# ---------------------------------------------------------------------------
# Check 16: Embedded sqlScripts freshness.
# For every module definition listed in omp-components.json, every sqlScripts
# (and runtimeMaintenance.steps) entry with contentEncoding 'base64-utf8' must carry content/sha256 that match
# the current SQL file bytes on disk, after the same USE/GO prologue stripping
# applied by the OpenModulePlatform embed tool
# (scripts/dev/embed-module-definition-sql.ps1). This is a working-tree/HEAD
# consistency check, not a diff-range check, so it runs with or without
# -BaseCommit.
#
# Line-ending handling: the embed tool normalizes each SQL file to the form
# .gitattributes declares for it before embedding, so this check compares both
# sides normalized to that same declared form. Where no form is declared (text
# unset, or no eol attribute) the historical LF normalization applies on both
# sides and pure CRLF/LF drift is still tolerated; any other byte difference
# is reported as staleness. Check 20 is the hard guard that a declared form is
# also what the embedded bytes actually carry. When git cannot ANSWER the
# declaration question at all the declared form is unknown -- that is an
# error, never a silent "bytes untouched".
# ---------------------------------------------------------------------------
$embeddedSqlChecked = 0
$embeddedSqlFresh = 0
$embeddedSqlErrorCount = 0

foreach ($manifestDefinition in @($manifest.moduleDefinitions)) {
    if ($null -eq $manifestDefinition) {
        continue
    }

    $moduleKey = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'moduleKey')
    $relativeDefinitionPath = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'path')
    if ([string]::IsNullOrWhiteSpace($relativeDefinitionPath)) {
        continue
    }

    $definitionPath = Resolve-RepositoryPath -Path $relativeDefinitionPath -BasePath $repositoryRoot
    if (-not (Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
        continue
    }

    $definitionText = Remove-Utf8Bom -Text (Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8)
    $definition = ConvertFrom-JsonDocument -Json $definitionText -Depth $jsonDepth

    # runtimeMaintenance.steps embed SQL in the same fields as sqlScripts.
    $embeddedEntries = @(Get-OptionalPropertyValue -Object $definition -Name 'sqlScripts')
    $runtimeMaintenance = Get-OptionalPropertyValue -Object $definition -Name 'runtimeMaintenance'
    if ($null -ne $runtimeMaintenance) {
        $embeddedEntries += @(Get-OptionalPropertyValue -Object $runtimeMaintenance -Name 'steps')
    }

    foreach ($script in $embeddedEntries) {
        if ($null -eq $script) {
            continue
        }

        $contentEncoding = [string](Get-OptionalPropertyValue -Object $script -Name 'contentEncoding')
        if (-not [string]::Equals($contentEncoding, 'base64-utf8', [StringComparison]::Ordinal)) {
            continue
        }

        $scriptKey = [string](Get-OptionalPropertyValue -Object $script -Name 'key')
        if ([string]::IsNullOrWhiteSpace($scriptKey)) {
            $scriptKey = '<no-key>'
        }

        $sqlPath = [string](Get-OptionalPropertyValue -Object $script -Name 'path')
        if ([string]::IsNullOrWhiteSpace($sqlPath)) {
            Add-ValidationError -Errors $errors -Message "Embedded SQL script '$scriptKey' (module '$moduleKey') has contentEncoding 'base64-utf8' but no path."
            $embeddedSqlErrorCount++
            continue
        }

        $embeddedSqlChecked++

        $fullSqlPath = Resolve-RepositoryPath -Path $sqlPath -BasePath $repositoryRoot
        if (-not (Test-Path -LiteralPath $fullSqlPath -PathType Leaf)) {
            Add-ValidationError -Errors $errors -Message "Embedded SQL script '$scriptKey' (module '$moduleKey') references a missing file: $sqlPath"
            $embeddedSqlErrorCount++
            continue
        }

        $diskText = Get-Content -LiteralPath $fullSqlPath -Raw -Encoding UTF8
        $portableText = ConvertTo-PortableModuleDefinitionSql -SqlText $diskText

        # The same normalization the embed tool applies: the line-ending form
        # .gitattributes declares for the SQL path. When nothing is declared
        # the comparison falls back to the historical LF normalization, which
        # stays byte-tolerant on purpose (there is no contract to be exact
        # against); Check 20 below is the hard guard when a form IS declared.
        # When git cannot answer, the declared form is UNKNOWN and comparing
        # against any assumed form would be a guess -- that is an error, like
        # every other unreadable git answer in this family.
        $declaredEol = Get-GitDeclaredLineEnding -RepositoryRoot $repositoryRoot -RelativePath $sqlPath
        if ($null -eq $declaredEol) {
            Add-ValidationError -Errors $errors -Message "Check 16 could not determine the line-ending form .gitattributes declares for '$sqlPath' (script '$scriptKey', module '$moduleKey'): 'git check-attr text eol' did not answer (is '$repositoryRoot' a git work tree?). An unreadable declaration is an error, not 'bytes untouched'."
            $embeddedSqlErrorCount++
            continue
        }
        $comparisonEol = $declaredEol
        if ([string]::IsNullOrEmpty($comparisonEol)) {
            $comparisonEol = 'LF'
        }

        $actualContent = [string](Get-OptionalPropertyValue -Object $script -Name 'content')
        $actualSha256 = [string](Get-OptionalPropertyValue -Object $script -Name 'sha256')

        $embeddedText = ''
        if (-not [string]::IsNullOrWhiteSpace($actualContent)) {
            try {
                $embeddedText = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($actualContent))
            }
            catch {
                $embeddedText = ''
            }
        }

        # Compare with both sides normalized to the declared line-ending form
        # (see the Check 16 note above).
        $normalizedDiskText = ConvertTo-DeclaredLineEndings -Text $portableText -Declared $comparisonEol
        $normalizedEmbeddedText = ConvertTo-DeclaredLineEndings -Text $embeddedText -Declared $comparisonEol
        $contentMatches = [string]::Equals($normalizedEmbeddedText, $normalizedDiskText, [StringComparison]::Ordinal)

        # The stored hash was computed over whichever line-ending form the
        # embed tool saw, so accept a match against the raw embedded text, the
        # raw disk text, or the declared-form-normalized form.
        $sha256Matches = $false
        foreach ($shaCandidateText in @($embeddedText, $portableText, $normalizedDiskText)) {
            if ([string]::Equals($actualSha256, (Get-Sha256Hex -Text $shaCandidateText), [StringComparison]::Ordinal)) {
                $sha256Matches = $true
                break
            }
        }

        if (-not $contentMatches -or -not $sha256Matches) {
            Add-ValidationError -Errors $errors -Message "Embedded SQL for script '$scriptKey' ($sqlPath, module '$moduleKey') is stale: sqlScripts content/sha256 do not match the current file bytes. Refresh it with the embed tool in the OpenModulePlatform repository: scripts/dev/embed-module-definition-sql.ps1 -RepositoryRoot '<path to this repository>'."
            $embeddedSqlErrorCount++
            continue
        }

        $embeddedSqlFresh++
    }
}

# ---------------------------------------------------------------------------
# Check 20: Embedded sqlScripts line endings match .gitattributes.
# Check 16 proves the embedded bytes are fresh; this check proves they carry
# the line-ending form the repository declares for the SQL path. An embed run
# over a file whose line endings were accidentally rewritten (a Git Bash
# 'sed -i' writes LF regardless of .gitattributes) otherwise sailed through
# every gate on the machine that produced it -- the local embed and the local
# freshness check both saw the same wrong bytes -- and failed
# validate-module-definitions on every normal checkout, stopping deploys.
# Only the manifest's module definitions are checked (never build-output
# copies); where git declares no form for the SQL path (text unset, no eol
# attribute) any line-ending form is accepted. A lookup that cannot run at
# all is an error, like every other unreadable git answer in this family.
# ---------------------------------------------------------------------------
$embeddedEolChecked = 0
$embeddedEolDeclared = 0
$embeddedEolErrorCount = 0

foreach ($manifestDefinition in @($manifest.moduleDefinitions)) {
    if ($null -eq $manifestDefinition) {
        continue
    }

    $moduleKey = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'moduleKey')
    $relativeDefinitionPath = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'path')
    if ([string]::IsNullOrWhiteSpace($relativeDefinitionPath)) {
        continue
    }

    $definitionPath = Resolve-RepositoryPath -Path $relativeDefinitionPath -BasePath $repositoryRoot
    if (-not (Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
        continue
    }

    $definitionText = Remove-Utf8Bom -Text (Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8)
    $definition = ConvertFrom-JsonDocument -Json $definitionText -Depth $jsonDepth

    # runtimeMaintenance.steps embed SQL in the same fields as sqlScripts.
    $embeddedEolEntries = @(Get-OptionalPropertyValue -Object $definition -Name 'sqlScripts')
    $runtimeMaintenance = Get-OptionalPropertyValue -Object $definition -Name 'runtimeMaintenance'
    if ($null -ne $runtimeMaintenance) {
        $embeddedEolEntries += @(Get-OptionalPropertyValue -Object $runtimeMaintenance -Name 'steps')
    }

    foreach ($script in $embeddedEolEntries) {
        if ($null -eq $script) {
            continue
        }

        $contentEncoding = [string](Get-OptionalPropertyValue -Object $script -Name 'contentEncoding')
        if (-not [string]::Equals($contentEncoding, 'base64-utf8', [StringComparison]::Ordinal)) {
            continue
        }

        $embeddedContent = [string](Get-OptionalPropertyValue -Object $script -Name 'content')
        $sqlPath = [string](Get-OptionalPropertyValue -Object $script -Name 'path')
        if ([string]::IsNullOrWhiteSpace($embeddedContent) -or [string]::IsNullOrWhiteSpace($sqlPath)) {
            continue
        }

        $declaredEol = Get-GitDeclaredLineEnding -RepositoryRoot $repositoryRoot -RelativePath $sqlPath
        if ($null -eq $declaredEol) {
            # git could not answer the declaration question, so 'any form is
            # accepted' would be an unmeasured check reading as a passing one.
            Add-ValidationError -Errors $errors -Message "Check 20 could not determine the line-ending form .gitattributes declares for '$sqlPath' (module '$moduleKey'): 'git check-attr text eol' did not answer (is '$repositoryRoot' a git work tree?). An unreadable declaration is an error, not 'bytes untouched'."
            $embeddedEolErrorCount++
            continue
        }
        if ($declaredEol -eq '') {
            # git leaves these bytes alone; there is no declared form to violate.
            continue
        }

        $embeddedEolChecked++

        $scriptKey = [string](Get-OptionalPropertyValue -Object $script -Name 'key')
        if ([string]::IsNullOrWhiteSpace($scriptKey)) {
            $scriptKey = '<no-key>'
        }

        $embeddedBytes = $null
        try {
            $embeddedBytes = [Convert]::FromBase64String($embeddedContent)
        }
        catch {
            # Undecodable content is already a Check 16 freshness error.
            continue
        }

        $embeddedSqlText = [System.Text.Encoding]::UTF8.GetString($embeddedBytes)
        $crlfCount = ([regex]::Matches($embeddedSqlText, "`r`n")).Count
        $loneLfCount = ([regex]::Matches($embeddedSqlText, "(?<!`r)`n")).Count

        $declaredEolLower = $declaredEol.ToLowerInvariant()
        $matchesDeclaration = ($declaredEol -eq 'CRLF' -and $loneLfCount -eq 0) -or ($declaredEol -eq 'LF' -and $crlfCount -eq 0)
        if (-not $matchesDeclaration) {
            Add-ValidationError -Errors $errors -Message "Embedded SQL for script '$scriptKey' ($sqlPath, module '$moduleKey') has the wrong line endings: the embedded content carries $crlfCount CRLF and $loneLfCount lone-LF line ending(s), but .gitattributes declares eol=$declaredEolLower for that path. Re-embed with the embed tool in the OpenModulePlatform repository: scripts/dev/embed-module-definition-sql.ps1 -RepositoryRoot '<path to this repository>'."
            $embeddedEolErrorCount++
            continue
        }

        $embeddedEolDeclared++
    }
}

# ---------------------------------------------------------------------------
# Check 22: the embed tool's line-ending helpers are byte-identical copies of
# the shared core. scripts/dev/embed-module-definition-sql.ps1 stays
# standalone (it must run in a consumer repository without the validator next
# to it), so it carries its OWN copies of Get-GitDeclaredLineEnding and
# ConvertTo-DeclaredLineEndings. Nothing held the copies identical: a fix in
# the shared core (for example the unreadable-git-answer error semantics)
# silently left the embed tool with the old behaviour, and the two drift
# detectors (Checks 16/20) would then disagree with the tool they guard. The
# comparison extracts each function's exact text with the PowerShell parser,
# so it is insensitive to everything outside the function and sensitive to
# every byte inside it. Platform-only: where the embed tool does not exist
# (consumer repositories, test fixtures) the check is vacuous.
# ---------------------------------------------------------------------------
$embedHelperCopyCount = 0
$embedHelperCopyErrorCount = 0
$embedToolPath = Join-Path $repositoryRoot 'scripts\dev\embed-module-definition-sql.ps1'
$helpersCorePath = Join-Path $repositoryRoot 'scripts\omp\validate-component-versions.helpers.ps1'

function Get-FunctionExtentText {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$FunctionName
    )

    $parseTokens = $null
    $parseErrors = $null
    $parsedAst = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$parseTokens, [ref]$parseErrors)
    if (@($parseErrors).Count -gt 0) {
        return $null
    }

    $functionAst = $parsedAst.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            [string]::Equals($node.Name, $FunctionName, [StringComparison]::Ordinal)
        }, $false)
    if ($null -eq $functionAst) {
        return $null
    }

    return $functionAst.Extent.Text
}

if ((Test-Path -LiteralPath $embedToolPath -PathType Leaf) -and (Test-Path -LiteralPath $helpersCorePath -PathType Leaf)) {
    foreach ($copiedFunctionName in @('Get-GitDeclaredLineEnding', 'ConvertTo-DeclaredLineEndings')) {
        $embedHelperCopyCount++
        $coreFunctionText = Get-FunctionExtentText -Path $helpersCorePath -FunctionName $copiedFunctionName
        $embedFunctionText = Get-FunctionExtentText -Path $embedToolPath -FunctionName $copiedFunctionName
        if ($null -eq $coreFunctionText -or $null -eq $embedFunctionText) {
            # Name the side(s) the function could not be extracted from:
            # "from both" leaves the reader guessing which file is broken.
            $missingSides = @()
            if ($null -eq $coreFunctionText) {
                $missingSides += 'scripts/omp/validate-component-versions.helpers.ps1 (the shared core)'
            }
            if ($null -eq $embedFunctionText) {
                $missingSides += 'scripts/dev/embed-module-definition-sql.ps1 (the embed tool)'
            }
            Add-ValidationError -Errors $errors -Message "Check 22: could not extract function '$copiedFunctionName' from $($missingSides -join ' and ') (parse error or missing function). The embed tool must carry a byte-identical copy of the shared core."
            $embedHelperCopyErrorCount++
            continue
        }

        if (-not [string]::Equals($coreFunctionText, $embedFunctionText, [StringComparison]::Ordinal)) {
            Add-ValidationError -Errors $errors -Message "Check 22: function '$copiedFunctionName' in scripts/dev/embed-module-definition-sql.ps1 differs from the shared-core copy in scripts/omp/validate-component-versions.helpers.ps1. The two must stay byte-identical: edit the shared core and copy the function into the embed tool in the same change."
            $embedHelperCopyErrorCount++
        }
    }
}

# ---------------------------------------------------------------------------
# Check 9: Transitive ProjectReference lockstep bumps.
# If a component's own project or any project it references (directly or
# through one level of ProjectReference transitivity) changed since the base,
# the component's version must be bumped. References already covered by
# Check 7's sharedProjects cascade are excluded to avoid double-counting.
# ---------------------------------------------------------------------------
$transitiveCheckCount = 0
$transitiveErrorCount = 0

if (-not $baseRefAvailable) {
    Add-ValidationWarning -Warnings $warnings -Message 'No valid base ref available; skipping transitive ProjectReference lockstep validation (Check 9). Pass -BaseCommit to enable it.'
}
else {
    $sharedProjectDirs = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($sharedProject in @($sharedProjects)) {
        if ($null -eq $sharedProject) {
            continue
        }

        $sharedProjectPath = [string](Get-OptionalPropertyValue -Object $sharedProject -Name 'projectPath')
        if ([string]::IsNullOrWhiteSpace($sharedProjectPath)) {
            continue
        }

        $fullSharedProjectPath = Resolve-RepositoryPath -Path $sharedProjectPath -BasePath $repositoryRoot
        $sharedProjectDir = $fullSharedProjectPath
        if ($fullSharedProjectPath -like '*.csproj') {
            $sharedProjectDir = Split-Path -Parent $fullSharedProjectPath
        }

        if (Test-Path -LiteralPath $sharedProjectDir -PathType Container) {
            [void]$sharedProjectDirs.Add([System.IO.Path]::GetFullPath($sharedProjectDir))
        }
    }

    foreach ($component in @($manifest.components)) {
        if ($null -eq $component) {
            continue
        }

        $componentKey = [string](Get-OptionalPropertyValue -Object $component -Name 'componentKey')
        if ([string]::IsNullOrWhiteSpace($componentKey)) {
            $componentKey = '<unknown>'
        }

        $projectPath = [string](Get-OptionalPropertyValue -Object $component -Name 'projectPath')
        if ([string]::IsNullOrWhiteSpace($projectPath)) {
            continue
        }

        $fullProjectPath = Resolve-RepositoryPath -Path $projectPath -BasePath $repositoryRoot
        $csprojPath = $fullProjectPath
        if (Test-Path -LiteralPath $fullProjectPath -PathType Container) {
            $csprojFiles = @(Get-ChildItem -LiteralPath $fullProjectPath -Filter '*.csproj' -File -ErrorAction SilentlyContinue)
            if ($csprojFiles.Count -eq 0) {
                continue
            }
            $csprojPath = $csprojFiles[0].FullName
        }

        if (-not (Test-Path -LiteralPath $csprojPath -PathType Leaf)) {
            continue
        }

        $directRefDirs = @(Get-ProjectReferences -CsprojPath $csprojPath)
        $allRefDirs = [System.Collections.Generic.List[string]]::new()
        foreach ($directRefDir in $directRefDirs) {
            if (-not $allRefDirs.Contains($directRefDir)) {
                [void]$allRefDirs.Add($directRefDir)
            }

            $directRefCsprojFiles = @(Get-ChildItem -LiteralPath $directRefDir -Filter '*.csproj' -File -ErrorAction SilentlyContinue)
            if ($directRefCsprojFiles.Count -gt 0) {
                $transitiveRefDirs = @(Get-ProjectReferences -CsprojPath $directRefCsprojFiles[0].FullName)
                foreach ($transitiveRefDir in $transitiveRefDirs) {
                    if (-not $allRefDirs.Contains($transitiveRefDir)) {
                        [void]$allRefDirs.Add($transitiveRefDir)
                    }
                }
            }
        }

        $changedRefDirs = [System.Collections.Generic.List[string]]::new()
        foreach ($refDir in $allRefDirs) {
            if ($sharedProjectDirs.Contains($refDir)) {
                continue
            }

            $relRefDir = $refDir.Substring($repositoryRoot.Length).TrimStart('\', '/')
            $changedFiles = Get-GitChangedFiles -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path $relRefDir -Errors $errors -CheckDescription "The referenced-project change check for '$relRefDir'"
            if ($null -ne $changedFiles -and -not [string]::IsNullOrWhiteSpace($changedFiles)) {
                [void]$changedRefDirs.Add($relRefDir)
            }
        }

        if ($changedRefDirs.Count -eq 0) {
            continue
        }

        $baseVersion = $null
        if ($baseComponentsByKey.ContainsKey($componentKey)) {
            $baseVersion = [string](Get-OptionalPropertyValue -Object $baseComponentsByKey[$componentKey] -Name 'version')
        }

        $currentVersion = [string](Get-OptionalPropertyValue -Object $component -Name 'version')

        if (-not [string]::IsNullOrWhiteSpace($baseVersion) -and [string]::Equals($baseVersion, $currentVersion, [StringComparison]::Ordinal)) {
            $changedRefList = ($changedRefDirs | Sort-Object) -join ', '
            Add-ValidationError -Errors $errors -Message ("Component '$componentKey' references changed project(s) ($changedRefList) since $baseRef but its version was not bumped. Bump the component version." + (Get-ConsistentSetBumpHint -ComponentKey $componentKey))
            $transitiveErrorCount++
        }
        else {
            $transitiveCheckCount++
        }
    }

    # repositoryVersion must MOVE when any component version moved.
    #
    # Until 2026-09-04 this file only checked that repositoryVersion EXISTS and is
    # semver-shaped (the Test-SemverLikeVersion check far above). Nothing checked that it
    # changed, so "Repository version validated" meant "the field is filled in", not "the
    # number is right" -- a line that reads like a result and is not one.
    #
    # It cost a real defect the same day: commit 3dfbc743 raised omp-hostagent-service
    # 0.3.244 -> 0.3.245 and left repositoryVersion at 0.3.621, byte-identical to its parent.
    # Local CI green, GitHub CI 5/5 green; an independent reviewer found it, not the gate.
    # The universal package takes its filename and sourceRepositoryVersion from this value, so
    # new content can ship under an old package identity -- exactly what the identity guard in
    # HostAgent exists to refuse, discovered at import time instead of at push time.
    #
    # bump-version.ps1 raises repositoryVersion on every bump, so a change that used the
    # repository's own tool cannot trip this. Tripping it means the manifest was hand-edited,
    # which is the case worth catching.
    #
    # SCOPE, deliberately narrow: this compares COMPONENT versions only. A hand-edit that
    # moves a module definitionVersion without touching any component version is not caught
    # here; the module-definition checks above own that ground, and a broad check that guesses
    # is worse than a narrow one that is right.
    if ($baseRefAvailable -and $null -ne $baseManifest -and $null -ne $baseComponentsByKey) {
        $movedComponents = [System.Collections.Generic.List[string]]::new()
        foreach ($component in @($manifest.components)) {
            $componentKey = [string](Get-OptionalPropertyValue -Object $component -Name 'componentKey')
            if ([string]::IsNullOrWhiteSpace($componentKey)) { continue }
            if (-not $baseComponentsByKey.ContainsKey($componentKey)) { continue }

            $baseComponentVersion = [string](Get-OptionalPropertyValue -Object $baseComponentsByKey[$componentKey] -Name 'version')
            $currentComponentVersion = [string](Get-OptionalPropertyValue -Object $component -Name 'version')
            if ([string]::IsNullOrWhiteSpace($baseComponentVersion) -or [string]::IsNullOrWhiteSpace($currentComponentVersion)) { continue }

            if (-not [string]::Equals($baseComponentVersion, $currentComponentVersion, [StringComparison]::Ordinal)) {
                [void]$movedComponents.Add($componentKey)
            }
        }

        if ($movedComponents.Count -gt 0) {
            $baseRepositoryVersion = [string](Get-OptionalPropertyValue -Object $baseManifest -Name 'repositoryVersion')
            if (-not [string]::IsNullOrWhiteSpace($baseRepositoryVersion) -and
                [string]::Equals($baseRepositoryVersion, $repositoryVersion, [StringComparison]::Ordinal)) {
                $movedList = ($movedComponents | Sort-Object) -join ', '
                Add-ValidationError -Errors $errors -Message ("Component version(s) moved since $baseRef ($movedList) but repositoryVersion stayed at '$repositoryVersion'. The universal package takes its filename and sourceRepositoryVersion from repositoryVersion, so new content would ship under an old package identity. Run scripts/omp/bump-version.ps1 instead of editing omp-components.json by hand -- it raises repositoryVersion for you.")
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Check 13: LOCKSTEP version bump against baseline (own project source).
# When a component's OWN project directory changed since the base ref, both
# the component version and repositoryVersion must move. Check 9 covers
# referenced projects only; this check covers the component's own tree. Its
# absence in this validator let an own-source change ship with untouched
# versions (measured 2026-09-06: a committed own-project source edit passed
# validation green).
#
# Two refinements, both proven against the BUILT artifact on 2026-09-06:
# - Markdown outside wwwroot never reaches the published payload, so a
#   docs-only change must not force a bump. Markdown UNDER wwwroot is payload
#   (dotnet publish copies wwwroot verbatim; the portal artifact contains
#   wwwroot/img/blank-widget/README.md) and must force one.
# - A project can publish content from OUTSIDE its own directory: Portal's
#   csproj includes ..\tools\universal-package-builder\** into wwwroot. Those
#   bytes shape the artifact exactly like own source, so this check also
#   watches every out-of-project Content/None/Compile/EmbeddedResource
#   include directory that lives inside this repository.
# ---------------------------------------------------------------------------
$lockstepCheckCount = 0
$lockstepErrorCount = 0

if (-not $baseRefAvailable) {
    Add-ValidationWarning -Warnings $warnings -Message 'No valid base ref available; skipping LOCKSTEP validation (Check 13). Pass -BaseCommit to enable it.'
}
elseif ($null -eq $baseManifest -or $null -eq $baseComponentsByKey) {
    Add-ValidationWarning -Warnings $warnings -Message 'Baseline manifest unreadable; skipping LOCKSTEP validation (Check 13).'
}
else {
    $baseRepositoryVersion = [string](Get-OptionalPropertyValue -Object $baseManifest -Name 'repositoryVersion')

    foreach ($component in @($manifest.components)) {
        if ($null -eq $component) {
            continue
        }

        $componentKey = [string](Get-OptionalPropertyValue -Object $component -Name 'componentKey')
        if ([string]::IsNullOrWhiteSpace($componentKey)) {
            continue
        }

        $projectPath = [string](Get-OptionalPropertyValue -Object $component -Name 'projectPath')
        if ([string]::IsNullOrWhiteSpace($projectPath)) {
            continue
        }

        $diffPath = $projectPath
        if ($projectPath -like '*.csproj') {
            $diffPath = Split-Path -Parent $projectPath
        }

        $watchPaths = [System.Collections.Generic.List[string]]::new()
        [void]$watchPaths.Add($diffPath)

        # Watch out-of-project payload inputs as well (see the block comment).
        $componentCsproj = $null
        $projectFullPath = Resolve-RepositoryPath -Path $projectPath -BasePath $repositoryRoot
        if (Test-Path -LiteralPath $projectFullPath -PathType Leaf) {
            $componentCsproj = $projectFullPath
        }
        elseif (Test-Path -LiteralPath $projectFullPath -PathType Container) {
            $foundCsproj = @(Get-ChildItem -LiteralPath $projectFullPath -Filter '*.csproj' -File -ErrorAction SilentlyContinue)
            if ($foundCsproj.Count -gt 0) {
                $componentCsproj = $foundCsproj[0].FullName
            }
        }

        if ($null -ne $componentCsproj) {
            $componentCsprojDir = Split-Path -Parent $componentCsproj
            $componentCsprojText = Get-Content -LiteralPath $componentCsproj -Raw -Encoding UTF8
            foreach ($includeMatch in [System.Text.RegularExpressions.Regex]::Matches($componentCsprojText, '<(?:Content|None|Compile|EmbeddedResource)\s+[^>]*?Include="([^"]+)"')) {
                $includeValue = $includeMatch.Groups[1].Value
                if (-not $includeValue.StartsWith('..') -or $includeValue.Contains('$')) {
                    continue
                }

                $includeDir = $includeValue
                $wildcardAt = $includeDir.IndexOf('*')
                if ($wildcardAt -ge 0) {
                    $includeDir = $includeDir.Substring(0, $wildcardAt)
                }

                $resolvedInclude = [System.IO.Path]::GetFullPath((Join-Path $componentCsprojDir $includeDir))
                if (-not (Test-Path -LiteralPath $resolvedInclude -PathType Container)) {
                    continue
                }
                if (-not $resolvedInclude.StartsWith($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
                    continue # outside this repository: Check 14's jurisdiction, not this check's
                }

                $relativeInclude = $resolvedInclude.Substring($repositoryRoot.Length).TrimStart('\', '/')
                if (-not $watchPaths.Contains($relativeInclude)) {
                    [void]$watchPaths.Add($relativeInclude)
                }
            }
        }

        $changedFilesText = ''
        $changedFilesFailed = $false
        foreach ($watchPath in $watchPaths) {
            $watchChanges = Get-GitChangedFiles -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path $watchPath -Errors $errors -CheckDescription "The LOCKSTEP check (Check 13) for '$componentKey'"
            if ($null -eq $watchChanges) {
                $changedFilesFailed = $true
                break
            }
            if (-not [string]::IsNullOrWhiteSpace($watchChanges)) {
                $changedFilesText = ($changedFilesText + "`n" + $watchChanges).Trim()
            }
        }
        if ($changedFilesFailed) {
            continue
        }

        # Markdown outside wwwroot never reaches the published payload, so a
        # docs-only change must not force a version bump. Markdown UNDER
        # wwwroot is payload (dotnet publish copies wwwroot verbatim) and does.
        $changedFiles = @($changedFilesText -split "`n" | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and -not (
                $_.Trim().EndsWith('.md', [StringComparison]::OrdinalIgnoreCase) -and
                $_ -notmatch '[\\/]wwwroot[\\/]'
            )
        })
        if ($changedFiles.Count -eq 0) {
            continue
        }

        $lockstepCheckCount++

        $currentVersion = [string](Get-OptionalPropertyValue -Object $component -Name 'version')
        $baseVersion = ''
        if ($baseComponentsByKey.ContainsKey($componentKey)) {
            $baseVersion = [string](Get-OptionalPropertyValue -Object $baseComponentsByKey[$componentKey] -Name 'version')
        }

        $versionBumped = $false
        if (-not [string]::IsNullOrWhiteSpace($baseVersion)) {
            $versionBumped = -not [string]::Equals($baseVersion, $currentVersion, [StringComparison]::Ordinal)
        }
        else {
            # No baseline version means this is a new component; treat as bumped if it has a valid version.
            $versionBumped = (-not [string]::IsNullOrWhiteSpace($currentVersion))
        }

        $repositoryVersionBumped = $false
        if (-not [string]::IsNullOrWhiteSpace($baseRepositoryVersion)) {
            $repositoryVersionBumped = -not [string]::Equals($baseRepositoryVersion, $repositoryVersion, [StringComparison]::Ordinal)
        }
        else {
            $repositoryVersionBumped = (-not [string]::IsNullOrWhiteSpace($repositoryVersion))
        }

        if (-not $versionBumped -or -not $repositoryVersionBumped) {
            $missing = [System.Collections.Generic.List[string]]::new()
            if (-not $versionBumped) {
                [void]$missing.Add("component version (current '$currentVersion', baseline '$baseVersion')")
            }
            if (-not $repositoryVersionBumped) {
                [void]$missing.Add("repositoryVersion (current '$repositoryVersion', baseline '$baseRepositoryVersion')")
            }

            $missingText = ($missing | Sort-Object) -join ' and '
            Add-ValidationError -Errors $errors -Message ("Component '$componentKey' project files changed since '$baseRef' but $missingText were not bumped (LOCKSTEP)." + (Get-ConsistentSetBumpHint -ComponentKey $componentKey))
            $lockstepErrorCount++
        }
    }
}

# ---------------------------------------------------------------------------
# Check 21: Direct TimeZoneInfo platform lookups stay inside the lookup file.
# Windows hosts before Windows 10 1903 / Server 2019 have no icu.dll, so .NET
# runs in NLS globalization mode there: TimeZoneInfo.FindSystemTimeZoneById
# and TimeZoneInfo.TryFindSystemTimeZoneById throw TimeZoneNotFoundException
# for IANA ids and TimeZoneInfo.TryConvertIanaIdToWindowsId returns false.
# Production code must resolve zones through OmpTimeZoneLookup
# (OpenModulePlatform.Web.Shared) or a repository-local *TimeZoneLookup.cs,
# which falls back to a built-in IANA-to-Windows table. This is a working-tree
# scan, not a diff check, so it runs with or without -BaseCommit.
#
# Matching rules (the shared core implements them; docs/VALIDATOR_CHECKS.md
# has the canonical description):
# - Comments and string/char literals are masked before matching, so a mention
#   in prose or a literal never fails the build. Verbatim and raw strings are
#   masked across line breaks; interpolation holes are scanned as code, so a
#   call inside $"{...}" counts.
# - All qualified forms match: TimeZoneInfo.X, System.TimeZoneInfo.X,
#   global::-prefixed, whitespace/newlines around the dot, and using-alias
#   qualifiers (using TZ = System.TimeZoneInfo;). With
#   'using static System.TimeZoneInfo;' the bare method name matches too.
#   'global using' directives apply to every file in the compilation wherever
#   they are declared, so they are collected repo-wide before any file is
#   tested (Get-CSharpGlobalTimeZoneDirectives).
# - Method-group use without parentheses counts (the method is just as direct
#   when passed as a delegate); nameof(...) is a name lookup and never counts.
# - Excluded: bin/obj, directories named exactly 'test'/'tests' or ending in
#   '.Test'/'.Tests', and everything under a test .csproj (name or
#   Microsoft.NET.Test.Sdk / IsTestProject). A directory whose name merely
#   ENDS in 'test' ('Latest', 'Contest', 'Greatest') is production code.
# - Exemption: the file name must end in 'TimeZoneLookup.cs' AND the file must
#   declare a type named exactly like the file (Test-TimeZoneLookupSourceFile).
# ---------------------------------------------------------------------------
$timeZoneScanCount = 0

$timeZoneTestProjectDirectories = @(Get-CSharpTestProjectDirectory -RepositoryRoot $repositoryRoot)
$allCsFiles = @(Get-ChildItem -LiteralPath $repositoryRoot -Recurse -Filter '*.cs' -File -ErrorAction SilentlyContinue)
$timeZoneProductionFiles = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
foreach ($csFile in $allCsFiles) {
    $relativeCsPath = $csFile.FullName.Substring($repositoryRoot.Length).TrimStart('\', '/')
    $csSegments = $relativeCsPath -split '[\\/]'
    $skipCsFile = $false
    for ($csSegmentIndex = 0; $csSegmentIndex -lt $csSegments.Count - 1; $csSegmentIndex++) {
        $csSegment = $csSegments[$csSegmentIndex]
        if ($csSegment -match '^(?i:bin|obj)$' -or $csSegment -match '^(?i:tests?)$' -or $csSegment -match '(?i)\.tests?$') {
            $skipCsFile = $true
            break
        }
    }
    if (-not $skipCsFile) {
        foreach ($timeZoneTestProjectDirectory in $timeZoneTestProjectDirectories) {
            if ($csFile.FullName.StartsWith($timeZoneTestProjectDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
                $skipCsFile = $true
                break
            }
        }
    }
    if ($skipCsFile) {
        continue
    }

    $timeZoneProductionFiles.Add($csFile)
}

# Repo-wide pass: a 'global using static [System.]TimeZoneInfo;' or 'global
# using X = [System.]TimeZoneInfo;' in ANY production file applies to every
# file in the compilation, so collect the directives before testing files.
$timeZoneGlobalUsingStatic = $false
$timeZoneGlobalAliases = [System.Collections.Generic.List[string]]::new()
foreach ($csFile in $timeZoneProductionFiles) {
    $timeZoneDirectives = Get-CSharpGlobalTimeZoneDirectives -MaskedText (Remove-CSharpCommentsAndStringLiterals -Text ([System.IO.File]::ReadAllText($csFile.FullName)))
    if ($timeZoneDirectives.UsingStatic) {
        $timeZoneGlobalUsingStatic = $true
    }
    foreach ($timeZoneGlobalAlias in @($timeZoneDirectives.Aliases)) {
        if (-not $timeZoneGlobalAliases.Contains($timeZoneGlobalAlias)) {
            $timeZoneGlobalAliases.Add($timeZoneGlobalAlias)
        }
    }
}

foreach ($csFile in $timeZoneProductionFiles) {
    $relativeCsPath = $csFile.FullName.Substring($repositoryRoot.Length).TrimStart('\', '/')
    $maskedCsText = Remove-CSharpCommentsAndStringLiterals -Text ([System.IO.File]::ReadAllText($csFile.FullName))
    if (Test-TimeZoneLookupSourceFile -FileName $csFile.Name -MaskedText $maskedCsText) {
        continue
    }

    $timeZoneScanCount++
    if (Test-DirectTimeZonePlatformCall -MaskedText $maskedCsText -GlobalUsingStatic:$timeZoneGlobalUsingStatic -GlobalAliases $timeZoneGlobalAliases) {
        Add-ValidationError -Errors $errors -Message "Production file '$relativeCsPath' uses TimeZoneInfo.FindSystemTimeZoneById, TimeZoneInfo.TryFindSystemTimeZoneById or TimeZoneInfo.TryConvertIanaIdToWindowsId directly (possibly through a using alias or using static). On Windows hosts without icu.dll (before Windows 10 1903 / Server 2019) .NET runs in NLS mode and all three fail for IANA ids. Resolve zones through OmpTimeZoneLookup (OpenModulePlatform.Web.Shared) or a repository-local *TimeZoneLookup.cs file instead."
    }
}

# ---------------------------------------------------------------------------
# Assembly version documentation (informational only, not enforced).
# ---------------------------------------------------------------------------
Write-Host 'Assembly version note:'
Write-Host '  Directory.Build.props sets assembly version to 0.1.0 intentionally.'
Write-Host '  Assembly version is decoupled from omp-components.json component versions.'
Write-Host '  OMP artifact identity uses manifest version + SHA-256, not assembly version.'
Write-Host '  This script validates the manifest, not the assembly versions.'
Write-Host ''

# ---------------------------------------------------------------------------
# Summaries.
# ---------------------------------------------------------------------------
$componentCount = 0
if ($null -ne $manifest.components) {
    $componentCount = @($manifest.components).Count
}

$moduleDefinitionCount = 0
if ($null -ne $manifest.moduleDefinitions) {
    $moduleDefinitionCount = @($manifest.moduleDefinitions).Count
}

# ---------------------------------------------------------------------------
# Check 18: consistentArtifactSets lockstep.
# A module definition may declare consistentArtifactSets; HostAgent supports only
# versionMatchRule 'exact', so every artifact in a set must deploy at the SAME version.
# NOTHING enforced this at build time, so a set whose members had different bump
# triggers drifted apart silently and the platform warned forever at runtime:
# example_serviceapp's web member is a Web.Shared consumer (cascade-bumped on every
# shared change) while its service member is not, giving web=0.3.82 vs service=0.3.9
# and an unsatisfiable set. Fail the build instead, so members are bumped together.
# ---------------------------------------------------------------------------
$consistentSetCount = 0
$consistentSetErrorCount = 0

foreach ($consistentSetModuleKey in $moduleDefinitionObjectsByKey.Keys) {
    $consistentSetDefinition = $moduleDefinitionObjectsByKey[$consistentSetModuleKey]
    $consistentSets = Get-OptionalPropertyValue -Object $consistentSetDefinition -Name 'consistentArtifactSets'
    if ($null -eq $consistentSets) {
        continue
    }

    foreach ($consistentSet in @($consistentSets)) {
        if ($null -eq $consistentSet) {
            continue
        }

        $setKey = [string](Get-OptionalPropertyValue -Object $consistentSet -Name 'setKey')
        if ([string]::IsNullOrWhiteSpace($setKey)) {
            $setKey = '<unnamed>'
        }

        $expectedArtifacts = Get-OptionalPropertyValue -Object $consistentSet -Name 'expectedArtifacts'
        if ($null -eq $expectedArtifacts) {
            continue
        }

        $consistentSetCount++
        $setMemberVersions = [System.Collections.Generic.List[string]]::new()
        $setMemberDescriptions = [System.Collections.Generic.List[string]]::new()

        foreach ($setMember in @($expectedArtifacts)) {
            if ($null -eq $setMember) {
                continue
            }

            $memberTargetName = [string](Get-OptionalPropertyValue -Object $setMember -Name 'targetName')
            $memberPackageType = [string](Get-OptionalPropertyValue -Object $setMember -Name 'packageType')

            $matchedComponent = $null
            foreach ($candidateComponent in @($manifest.components)) {
                if ($null -eq $candidateComponent) {
                    continue
                }

                $candidateTargetName = [string](Get-OptionalPropertyValue -Object $candidateComponent -Name 'targetName')
                $candidatePackageType = [string](Get-OptionalPropertyValue -Object $candidateComponent -Name 'packageType')

                if ([string]::Equals($candidateTargetName, $memberTargetName, [StringComparison]::OrdinalIgnoreCase) -and
                    [string]::Equals($candidatePackageType, $memberPackageType, [StringComparison]::OrdinalIgnoreCase)) {
                    $matchedComponent = $candidateComponent
                    break
                }
            }

            if ($null -eq $matchedComponent) {
                Add-ValidationError -Errors $errors -Message "Module '$consistentSetModuleKey' consistentArtifactSets set '$setKey' references artifact '$memberTargetName' ($memberPackageType), which has no matching component in omp-components.json."
                $consistentSetErrorCount++
                continue
            }

            $memberVersion = [string](Get-OptionalPropertyValue -Object $matchedComponent -Name 'version')
            [void]$setMemberVersions.Add($memberVersion)
            [void]$setMemberDescriptions.Add(('{0}={1}' -f $memberTargetName, $memberVersion))
        }

        $distinctSetVersions = @($setMemberVersions | Sort-Object -Unique)
        if ($distinctSetVersions.Count -gt 1) {
            Add-ValidationError -Errors $errors -Message ("Module '{0}' consistentArtifactSets set '{1}' requires every artifact at the SAME version (versionMatchRule 'exact'), but omp-components.json has {2}. Bump all members of the set together." -f $consistentSetModuleKey, $setKey, ($setMemberDescriptions -join ', '))
            $consistentSetErrorCount++
        }
    }
}

$sharedProjectCount = $sharedProjects.Count

$repositoryVersionStatus = if ([string]::IsNullOrWhiteSpace($repositoryVersion)) { 'missing' } else { 'validated' }
Write-Host "$checkMark $projectPathCount of $componentCount component project paths validated"
Write-Host "$checkMark Repository version $repositoryVersionStatus"
Write-Host "$checkMark $componentVersionCount of $componentCount component versions validated"
Write-Host "$checkMark $moduleDefinitionVersionSyncCount of $moduleDefinitionCount module definition versions synced"
Write-Host "$checkMark $moduleMappingCount component-to-module mappings validated"

if ($null -ne $externalOverlayMergedCount) {
    Write-Host "$checkMark overlay: $externalOverlayMergedCount external consumers from $externalOverlayPath"
}

if ($consistentSetCount -gt 0) {
    Write-Host "$checkMark $consistentSetCount consistent artifact set(s) validated ($consistentSetErrorCount error(s))"
}

if ($sharedProjectCount -gt 0 -and ($cascadeCheckCount -gt 0 -or $cascadeErrorCount -gt 0)) {
    Write-Host "$checkMark $cascadeCheckCount of $sharedProjectCount changed shared project(s) passed cascade bump validation ($cascadeErrorCount error(s))"
}

if (-not [string]::IsNullOrWhiteSpace($webSharedBinaryCheckMessage)) {
    Write-Host "$checkMark $webSharedBinaryCheckMessage"
}

if ($sqlFilesChecked -gt 0) {
    Write-Host "$checkMark $sqlFilesPassed of $sqlFilesChecked owned SQL file(s) passed diff validation ($sqlFilesChanged changed)"
}

if ($definitionDiffChecked -gt 0) {
    Write-Host "$checkMark $definitionDiffPassed of $definitionDiffChecked module definition(s) passed content diff validation ($definitionDiffChanged changed)"
}

if ($embeddedSqlChecked -gt 0) {
    # The check mark belongs to a clean run only: printing it next to a failed
    # freshness check reads as green on a red run (a duplicate Check 16 block
    # in the consumer fleet did exactly that by resetting the counters).
    if ($embeddedSqlErrorCount -eq 0) {
        Write-Host "$checkMark $embeddedSqlFresh of $embeddedSqlChecked embedded SQL script(s) passed freshness validation"
    }
    else {
        Write-Host "$crossMark $embeddedSqlFresh of $embeddedSqlChecked embedded SQL script(s) passed freshness validation ($embeddedSqlErrorCount error(s))"
    }
}

if ($embeddedEolChecked -gt 0 -or $embeddedEolErrorCount -gt 0) {
    # Same contract as Check 16: the check mark belongs to a clean run only.
    # The line used to print unconditionally, so a failed Check 20 still read
    # as green in the summary (second opinion, 2026-10-08).
    if ($embeddedEolErrorCount -eq 0) {
        Write-Host "$checkMark $embeddedEolDeclared of $embeddedEolChecked embedded SQL script(s) carry the line endings .gitattributes declares"
    }
    else {
        Write-Host "$crossMark $embeddedEolDeclared of $embeddedEolChecked embedded SQL script(s) carry the line endings .gitattributes declares ($embeddedEolErrorCount error(s))"
    }
}

if ($embedHelperCopyCount -gt 0 -and $embedHelperCopyErrorCount -eq 0) {
    Write-Host "$checkMark $embedHelperCopyCount embed-tool helper function(s) are byte-identical to the shared core"
}

if ($transitiveCheckCount -gt 0 -or $transitiveErrorCount -gt 0) {
    Write-Host "$checkMark $transitiveCheckCount component(s) passed transitive ProjectReference lockstep validation ($transitiveErrorCount error(s))"
}

if ($lockstepCheckCount -gt 0 -or $lockstepErrorCount -gt 0) {
    $lockstepPassed = $lockstepCheckCount - $lockstepErrorCount
    Write-Host "$checkMark $lockstepPassed of $lockstepCheckCount changed component(s) passed LOCKSTEP bump validation ($lockstepErrorCount error(s))"
}

Write-Host "$checkMark $timeZoneScanCount production .cs file(s) scanned; direct TimeZoneInfo IANA lookups are confined to the TimeZoneLookup lookup files (Check 21)"

if ($warnings.Count -gt 0) {
    Write-Host "$warningSign $($warnings.Count) warning(s):"
    foreach ($warningMessage in $warnings) {
        Write-Host "   $warningMessage"
    }
}

Write-Host ''

if ($errors.Count -gt 0) {
    Write-Host "$crossMark $($errors.Count) error(s), $($warnings.Count) warning(s) found"
    foreach ($errorMessage in $errors) {
        Write-Host " - $errorMessage"
    }

    exit 1
}

Write-Host "$checkMark Component version validation passed"
exit 0
