<#
.SYNOPSIS
Static scan that verifies each in-repo consumer's layout/Razor contract against
OpenModulePlatform.Web.Shared.

.DESCRIPTION
Reads omp-components.json to discover the sharedProjects consumers of
OpenModulePlatform.Web.Shared and checks that each consumer:

* Registers Web.Shared integration calls in Program.cs or Startup.cs
  (AddOmpWebDefaults / AddOmpAuthDefaults and UseOmpWebDefaults).
* Imports OpenModulePlatform.Web.Shared namespaces in _ViewImports.cshtml
  or _Imports.razor.
* References the shared OMP topbar (PortalTopBar) in its layout file.

Warnings are informational and do not cause a non-zero exit code, because some
consumers legitimately do not use all Web.Shared features.
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot = ''
)

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
        throw 'Could not resolve script directory. Pass -RepositoryRoot explicitly.'
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

function Test-FileContainsPattern {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [switch]$SimpleMatch
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    $match = Select-String -LiteralPath $Path -Pattern $Pattern -SimpleMatch:$SimpleMatch
    return $null -ne $match
}

function Find-StartupFile {
    param([Parameter(Mandatory = $true)][string]$ProjectDir)

    $candidates = @('Program.cs', 'Startup.cs')
    foreach ($candidate in $candidates) {
        $path = Join-Path $ProjectDir $candidate
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            return $path
        }
    }

    return $null
}

function Find-ViewImportsFile {
    param([Parameter(Mandatory = $true)][string]$ProjectDir)

    $candidates = @('_ViewImports.cshtml', '_Imports.razor')
    foreach ($candidate in $candidates) {
        $path = Join-Path $ProjectDir $candidate
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            return $path
        }

        # Razor files are often placed under Pages/ or Components/.
        $nested = Get-ChildItem -Path $ProjectDir -Recurse -File -Filter $candidate -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $nested) {
            return $nested.FullName
        }
    }

    return $null
}

function Find-LayoutFiles {
    param([Parameter(Mandatory = $true)][string]$ProjectDir)

    $layoutFiles = @(Get-ChildItem -Path $ProjectDir -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $name = $_.Name
            return ($name -ieq '_Layout.cshtml') -or
                ($name -ieq 'MainLayout.razor') -or
                ($name -like '*Layout*.cshtml') -or
                ($name -like '*Layout*.razor')
        })

    return $layoutFiles
}

$scriptDirectory = Get-ScriptDirectory
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = (Resolve-Path (Join-Path (Join-Path $scriptDirectory '..') '..')).Path
}

$repositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$manifestPath = Join-Path $repositoryRoot 'omp-components.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Component manifest not found: $manifestPath"
}

$jsonDepth = 100
$manifestText = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
$manifest = ConvertFrom-JsonDocument -Json $manifestText -Depth $jsonDepth

$componentsByKey = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($component in @($manifest.components)) {
    if ($null -eq $component) {
        continue
    }

    $componentKey = [string](Get-OptionalPropertyValue -Object $component -Name 'componentKey')
    if (-not [string]::IsNullOrWhiteSpace($componentKey) -and -not $componentsByKey.ContainsKey($componentKey)) {
        $componentsByKey.Add($componentKey, $component)
    }
}

$sharedProjects = @($manifest.sharedProjects)
if ($sharedProjects.Count -eq 0) {
    Write-Host 'No sharedProjects entries found in omp-components.json. Nothing to scan.'
    exit 0
}

$hardErrors = [System.Collections.Generic.List[string]]::new()
$consumerWarnings = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[string]]]::new([StringComparer]::OrdinalIgnoreCase)

foreach ($sharedProject in $sharedProjects) {
    if ($null -eq $sharedProject) {
        continue
    }

    $projectPath = [string](Get-OptionalPropertyValue -Object $sharedProject -Name 'projectPath')
    $description = [string](Get-OptionalPropertyValue -Object $sharedProject -Name 'description')
    $consumerKeys = @(Get-OptionalPropertyValue -Object $sharedProject -Name 'consumers')

    if ([string]::IsNullOrWhiteSpace($projectPath)) {
        [void]$hardErrors.Add('A sharedProjects entry is missing projectPath.')
        continue
    }

    $sharedProjectFullPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $projectPath))
    if (-not (Test-Path -LiteralPath $sharedProjectFullPath -PathType Leaf)) {
        [void]$hardErrors.Add("Shared project file was not found: $projectPath")
        continue
    }

    foreach ($consumerKey in $consumerKeys) {
        if ($null -eq $consumerKey) {
            continue
        }

        $consumerKeyString = [string]$consumerKey
        if ([string]::IsNullOrWhiteSpace($consumerKeyString)) {
            continue
        }

        if (-not $componentsByKey.ContainsKey($consumerKeyString)) {
            [void]$hardErrors.Add("Consumer '$consumerKeyString' is listed in sharedProjects but not found in components array.")
            continue
        }

        $component = $componentsByKey[$consumerKeyString]
        $relativeProjectDir = [string](Get-OptionalPropertyValue -Object $component -Name 'projectPath')
        if ([string]::IsNullOrWhiteSpace($relativeProjectDir)) {
            [void]$hardErrors.Add("Component '$consumerKeyString' is missing projectPath.")
            continue
        }

        $projectDir = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $relativeProjectDir))
        if (-not (Test-Path -LiteralPath $projectDir -PathType Container)) {
            [void]$hardErrors.Add("Project directory for consumer '$consumerKeyString' was not found: $relativeProjectDir")
            continue
        }

        $warnings = [System.Collections.Generic.List[string]]::new()

        # -------------------------------------------------------------------
        # Check 1: integration call registration.
        # -------------------------------------------------------------------
        $startupFile = Find-StartupFile -ProjectDir $projectDir
        $hasAddDefaults = $false
        if ($null -ne $startupFile) {
            $hasAddDefaults = (Test-FileContainsPattern -Path $startupFile -Pattern 'AddOmpWebDefaults' -SimpleMatch) -or
                (Test-FileContainsPattern -Path $startupFile -Pattern 'AddOmpAuthDefaults' -SimpleMatch)
        }

        if (-not $hasAddDefaults) {
            if ($null -eq $startupFile) {
                [void]$warnings.Add('No Program.cs or Startup.cs found.')
            }
            else {
                [void]$warnings.Add('Missing AddOmpWebDefaults/AddOmpAuthDefaults registration.')
            }
        }

        $hasUseDefaults = $false
        if ($null -ne $startupFile) {
            $hasUseDefaults = Test-FileContainsPattern -Path $startupFile -Pattern 'UseOmpWebDefaults' -SimpleMatch
        }

        if (-not $hasUseDefaults) {
            if ($null -eq $startupFile) {
                [void]$warnings.Add('No Program.cs or Startup.cs found for UseOmpWebDefaults check.')
            }
            else {
                [void]$warnings.Add('Missing UseOmpWebDefaults middleware registration.')
            }
        }

        # -------------------------------------------------------------------
        # Check 2: shared namespace imports.
        # -------------------------------------------------------------------
        $viewImportsFile = Find-ViewImportsFile -ProjectDir $projectDir
        $hasSharedImport = $false
        if ($null -ne $viewImportsFile) {
            $hasSharedImport = Test-FileContainsPattern -Path $viewImportsFile -Pattern 'OpenModulePlatform.Web.Shared' -SimpleMatch
        }

        if (-not $hasSharedImport) {
            if ($null -eq $viewImportsFile) {
                [void]$warnings.Add('No _ViewImports.cshtml or _Imports.razor found.')
            }
            else {
                [void]$warnings.Add('Missing OpenModulePlatform.Web.Shared import in _ViewImports/_Imports.')
            }
        }

        # -------------------------------------------------------------------
        # Check 3: topbar/layout contract.
        # -------------------------------------------------------------------
        $layoutFiles = @(Find-LayoutFiles -ProjectDir $projectDir)
        $hasTopbarReference = $false
        if ($layoutFiles.Count -gt 0) {
            foreach ($layoutFile in $layoutFiles) {
                if (Test-FileContainsPattern -Path $layoutFile.FullName -Pattern 'PortalTopBar' -SimpleMatch) {
                    $hasTopbarReference = $true
                    break
                }
            }
        }

        if (-not $hasTopbarReference) {
            if ($layoutFiles.Count -eq 0) {
                [void]$warnings.Add('No layout file (_Layout.cshtml / MainLayout.razor) found.')
            }
            else {
                [void]$warnings.Add('No PortalTopBar reference found in layout files.')
            }
        }

        if ($warnings.Count -gt 0) {
            $consumerWarnings[$consumerKeyString] = $warnings
        }

        if ($warnings.Count -eq 0) {
            Write-Host "[PASS] $consumerKeyString"
        }
        else {
            $detail = [string]::Join('; ', $warnings)
            Write-Host "[WARN] ${consumerKeyString}: $detail"
        }
    }
}

if ($hardErrors.Count -gt 0) {
    Write-Host ''
    Write-Host 'Web.Shared contract validation failed:'
    foreach ($errorMessage in $hardErrors) {
        Write-Host " - $errorMessage"
    }

    exit 1
}

Write-Host ''
Write-Host 'Web.Shared contract scan completed.'
exit 0
