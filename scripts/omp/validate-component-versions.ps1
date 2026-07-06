<#
.SYNOPSIS
Validates component version metadata in omp-components.json.

.DESCRIPTION
Checks that every component listed in omp-components.json has a valid version,
points to an existing .csproj project, references a declared module definition,
and that module definition versions stay in sync with the manifest.

This script validates the manifest only. Assembly versions in
Directory.Build.props are intentionally decoupled from omp-components.json
component versions: they are statically set to 0.1.0 for all C# projects.
OMP artifact identity is determined by the component manifest version plus
SHA-256 content hash, not by assembly version.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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

function ConvertFrom-JsonDocument {
    param(
        [Parameter(Mandatory = $true)][string]$Json,
        [Parameter(Mandatory = $true)][int]$Depth
    )

    $command = Get-Command ConvertFrom-Json
    if ($command.Parameters.ContainsKey('Depth')) {
        return $Json | ConvertFrom-Json -Depth $Depth
    }

    return $Json | ConvertFrom-Json
}

function Get-OptionalPropertyValue {
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Resolve-RepositoryPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$BasePath
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Add-ValidationError {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.List[string]]$Errors,
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    [void]$Errors.Add($Message)
}

function Add-ValidationWarning {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.List[string]]$Warnings,
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    [void]$Warnings.Add($Message)
}

function Test-SemverLikeVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Value
    )

    return $Value -match '^\d+\.\d+(?:\.\d+)?$'
}

function ConvertTo-VersionOrNull {
    param(
        [Parameter(Mandatory = $true)][string]$Value
    )

    $version = $null
    if ([Version]::TryParse($Value, [ref]$version)) {
        return $version
    }

    return $null
}

$scriptDirectory = Get-ScriptDirectory
$repositoryRoot = (Resolve-Path (Join-Path $scriptDirectory '..' '..')).Path
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

Write-Host 'Validating component versions...'
Write-Host ''

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
}

# ---------------------------------------------------------------------------
# Component checks.
# ---------------------------------------------------------------------------
$projectPathCount = 0
$componentVersionCount = 0
$moduleMappingCount = 0
$minModuleVersionWarningCount = 0

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
            # Check 6: minModuleDefinitionVersion sanity (warning only).
            # -------------------------------------------------------------------
            $minModuleDefinitionVersion = [string](Get-OptionalPropertyValue -Object $component -Name 'minModuleDefinitionVersion')
            if (-not [string]::IsNullOrWhiteSpace($minModuleDefinitionVersion)) {
                $actualVersion = [string](Get-OptionalPropertyValue -Object $moduleDefinitionsByKey[$moduleKey] -Name 'definitionVersion')
                $minVersionObj = ConvertTo-VersionOrNull -Value $minModuleDefinitionVersion
                $actualVersionObj = ConvertTo-VersionOrNull -Value $actualVersion

                if ($null -ne $minVersionObj -and $null -ne $actualVersionObj -and $minVersionObj -gt $actualVersionObj) {
                    Add-ValidationWarning -Warnings $warnings -Message "Component '$componentKey' requires minModuleDefinitionVersion '$minModuleDefinitionVersion' which is greater than the declared module definition version '$actualVersion' for moduleKey '$moduleKey'."
                    $minModuleVersionWarningCount++
                }
            }
        }
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

$repositoryVersionStatus = if ([string]::IsNullOrWhiteSpace($repositoryVersion)) { 'missing' } else { 'validated' }
Write-Host "`u{2713} $projectPathCount of $componentCount component project paths validated"
Write-Host "`u{2713} Repository version $repositoryVersionStatus"
Write-Host "`u{2713} $componentVersionCount of $componentCount component versions validated"
Write-Host "`u{2713} $moduleDefinitionVersionSyncCount of $moduleDefinitionCount module definition versions synced"
Write-Host "`u{2713} $moduleMappingCount component-to-module mappings validated"

if ($warnings.Count -gt 0) {
    Write-Host "`u{26A0} $($warnings.Count) warning(s):"
    foreach ($warningMessage in $warnings) {
        Write-Host "   $warningMessage"
    }
}

Write-Host ''

if ($errors.Count -gt 0) {
    Write-Host "`u{2717} $($errors.Count) error(s), $($warnings.Count) warning(s) found"
    foreach ($errorMessage in $errors) {
        Write-Host " - $errorMessage"
    }

    exit 1
}

Write-Host "`u{2713} Component version validation passed"
exit 0
