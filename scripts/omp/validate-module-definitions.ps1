<#
.SYNOPSIS
Validates module definitions against omp-components.json and embedded SQL.

.DESCRIPTION
Checks that every module definition listed in omp-components.json exists, has
the same module key and definition version as the manifest entry, and embeds SQL
content that still matches the referenced source .sql files.
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
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '', Justification = 'The ConvertFrom-Json -Depth call is guarded at runtime by checking Get-Command for the Depth parameter; on Windows PowerShell 5.1 the fallback branch without -Depth runs.')]
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

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Text)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash($bytes)
        return ([System.BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function ConvertTo-PortableModuleDefinitionSql {
    param([Parameter(Mandatory = $true)][string]$SqlText)

    # Embedded module-definition SQL runs on the already-selected target
    # database. Keep this normalization in sync with scripts/dev/embed-module-definition-sql.ps1.
    return [System.Text.RegularExpressions.Regex]::Replace(
        $SqlText,
        '(?im)^\s*USE\s+\[OpenModulePlatform\]\s*;\s*\r?\n\s*GO\s*(?:--.*)?\s*(?:\r?\n)?',
        '')
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
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Errors,
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    [void]$Errors.Add($Message)
}

$scriptDirectory = Get-ScriptDirectory
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = (Resolve-Path (Join-Path $scriptDirectory '..\..')).Path
}

$repositoryRootPath = [System.IO.Path]::GetFullPath($RepositoryRoot)
$manifestPath = Join-Path $repositoryRootPath 'omp-components.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Component manifest not found: $manifestPath"
}

$jsonDepth = 100
$errors = [System.Collections.Generic.List[string]]::new()
$manifestText = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
$manifest = ConvertFrom-JsonDocument -Json $manifestText -Depth $jsonDepth

foreach ($manifestDefinition in @($manifest.moduleDefinitions)) {
    if ($null -eq $manifestDefinition) {
        continue
    }

    $manifestModuleKey = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'moduleKey')
    $manifestDefinitionVersion = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'definitionVersion')
    $relativeDefinitionPath = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'path')

    if ([string]::IsNullOrWhiteSpace($relativeDefinitionPath)) {
        Add-ValidationError -Errors $errors -Message 'A module definition entry in omp-components.json is missing path.'
        continue
    }

    $definitionPath = Resolve-RepositoryPath -Path $relativeDefinitionPath -BasePath $repositoryRootPath
    if (-not (Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
        Add-ValidationError -Errors $errors -Message "Module definition file was not found: $relativeDefinitionPath"
        continue
    }

    $definitionText = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8
    $definition = ConvertFrom-JsonDocument -Json $definitionText -Depth $jsonDepth

    $actualModuleKey = [string](Get-OptionalPropertyValue -Object $definition -Name 'moduleKey')
    if (-not [string]::Equals($manifestModuleKey, $actualModuleKey, [StringComparison]::Ordinal)) {
        Add-ValidationError -Errors $errors -Message "Module key mismatch for '$relativeDefinitionPath'. Manifest='$manifestModuleKey', definition='$actualModuleKey'."
    }

    $actualDefinitionVersion = [string](Get-OptionalPropertyValue -Object $definition -Name 'definitionVersion')
    if (-not [string]::Equals($manifestDefinitionVersion, $actualDefinitionVersion, [StringComparison]::Ordinal)) {
        Add-ValidationError -Errors $errors -Message "Definition version mismatch for '$relativeDefinitionPath'. Manifest='$manifestDefinitionVersion', definition='$actualDefinitionVersion'."
    }

    foreach ($script in @($definition.sqlScripts)) {
        if ($null -eq $script) {
            continue
        }

        $scriptPathValue = [string](Get-OptionalPropertyValue -Object $script -Name 'path')
        if ([string]::IsNullOrWhiteSpace($scriptPathValue)) {
            continue
        }

        $sqlPath = Resolve-RepositoryPath -Path $scriptPathValue -BasePath $repositoryRootPath
        if (-not (Test-Path -LiteralPath $sqlPath -PathType Leaf)) {
            Add-ValidationError -Errors $errors -Message "SQL script referenced by '$relativeDefinitionPath' was not found: $scriptPathValue"
            continue
        }

        $sqlText = Get-Content -LiteralPath $sqlPath -Raw -Encoding UTF8
        $portableSqlText = ConvertTo-PortableModuleDefinitionSql -SqlText $sqlText
        $expectedEncoding = 'base64-utf8'
        $expectedContent = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($portableSqlText))
        $expectedSha256 = Get-Sha256Hex -Text $portableSqlText

        $actualEncoding = [string](Get-OptionalPropertyValue -Object $script -Name 'contentEncoding')
        $actualContent = [string](Get-OptionalPropertyValue -Object $script -Name 'content')
        $actualSha256 = [string](Get-OptionalPropertyValue -Object $script -Name 'sha256')
        $scriptKey = [string](Get-OptionalPropertyValue -Object $script -Name 'key')
        if ([string]::IsNullOrWhiteSpace($scriptKey)) {
            $scriptKey = $scriptPathValue
        }

        if (-not [string]::Equals($actualEncoding, $expectedEncoding, [StringComparison]::Ordinal)) {
            Add-ValidationError -Errors $errors -Message "SQL script '$scriptKey' in '$relativeDefinitionPath' has contentEncoding '$actualEncoding', expected '$expectedEncoding'."
        }

        if (-not [string]::Equals($actualContent, $expectedContent, [StringComparison]::Ordinal)) {
            Add-ValidationError -Errors $errors -Message "SQL script '$scriptKey' in '$relativeDefinitionPath' has embedded content that does not match '$scriptPathValue'. Run scripts/dev/embed-module-definition-sql.ps1."
        }

        if (-not [string]::Equals($actualSha256, $expectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
            Add-ValidationError -Errors $errors -Message "SQL script '$scriptKey' in '$relativeDefinitionPath' has sha256 '$actualSha256', expected '$expectedSha256'. Run scripts/dev/embed-module-definition-sql.ps1."
        }

        # R12-G4. Every CREATE TABLE in a setup script must be declared in
        # integrity.requiredTables, and every declared table must exist in the script.
        #
        # That list is what the import path's healing witness reads to decide whether a
        # package whose version is not newer still has to be applied (R12-G3). A table
        # missing from it is invisible to that decision. ibs_packager declared 16 of its 21
        # tables and nothing compared the two, so five tables -- Settings included -- could
        # never trigger a heal. The two sources are a JSON array and a SQL file; only a
        # comparison relates them, and until now nothing performed it.
        #
        # Deliberately limited to tables. Columns, indexes, constraints and triggers are now
        # declarable too, but they have no single unambiguous "declaration site" in a script
        # that adds them through idempotent ALTER blocks, so requiring a complete list would
        # produce false failures. Tables have exactly one CREATE TABLE each.
        if ($scriptKey -like '*setup*') {
            $createdTables = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($match in [regex]::Matches($sqlText, '(?im)^\s*CREATE\s+TABLE\s+(?<schema>[\w\[\]]+)\.(?<table>[\w\[\]]+)')) {
                $schemaName = $match.Groups['schema'].Value.Trim('[', ']')
                $tableName = $match.Groups['table'].Value.Trim('[', ']')
                [void]$createdTables.Add("$schemaName.$tableName")
            }

            if ($createdTables.Count -gt 0) {
                $integrity = Get-OptionalPropertyValue -Object $definition -Name 'integrity'
                $declaredTables = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                if ($null -ne $integrity) {
                    foreach ($declared in @(Get-OptionalPropertyValue -Object $integrity -Name 'requiredTables')) {
                        if ($null -eq $declared) { continue }
                        $declaredSchema = [string](Get-OptionalPropertyValue -Object $declared -Name 'schema')
                        $declaredName = [string](Get-OptionalPropertyValue -Object $declared -Name 'name')
                        if (-not [string]::IsNullOrWhiteSpace($declaredName)) {
                            [void]$declaredTables.Add("$declaredSchema.$declaredName")
                        }
                    }
                }

                $undeclared = @($createdTables | Where-Object { -not $declaredTables.Contains($_) } | Sort-Object)
                if ($undeclared.Count -gt 0) {
                    Add-ValidationError -Errors $errors -Message "'$relativeDefinitionPath' creates $($undeclared.Count) table(s) that integrity.requiredTables does not declare, so the import path cannot notice they are missing: $($undeclared -join ', ')."
                }

                $phantom = @($declaredTables | Where-Object { -not $createdTables.Contains($_) } | Sort-Object)
                if ($phantom.Count -gt 0) {
                    Add-ValidationError -Errors $errors -Message "'$relativeDefinitionPath' declares $($phantom.Count) table(s) in integrity.requiredTables that '$scriptPathValue' never creates: $($phantom -join ', ')."
                }
            }
        }
    }
}

if ($errors.Count -gt 0) {
    Write-Host 'Module definition validation failed:'
    foreach ($errorMessage in $errors) {
        Write-Host " - $errorMessage"
    }

    exit 1
}

Write-Host 'Module definition validation passed.'
