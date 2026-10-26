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

# Get-GitDeclaredLineEnding and ConvertTo-DeclaredLineEndings are BYTE-IDENTICAL
# copies of the shared validator core (scripts/omp/validate-component-versions.helpers.ps1),
# held identical by Check 22. The embed tool normalizes SQL to the line-ending form
# .gitattributes declares before it embeds; this validator has to compare against the
# same normalized text, or a working copy rewritten with other line endings (sed -i
# writes LF) fails here although the embedded content is correct.
function Get-GitDeclaredLineEnding {
    <#
    .SYNOPSIS
    Returns the line-ending form .gitattributes declares for a repository path:
    'CRLF', 'LF', '' when git leaves the bytes alone (the text attribute is
    unset or no eol attribute applies), or $null when GIT COULD NOT ANSWER
    (git missing, non-zero exit, no output -- for example a repository root
    that is not a git work tree).

    .DESCRIPTION
    The embed tool (scripts/dev/embed-module-definition-sql.ps1) used to embed
    whichever line-ending form the SQL file happened to have on disk, so the
    same module definition held LF bytes on one machine and CRLF bytes on
    another, and a file rewritten by a Git Bash text tool (sed -i writes LF)
    produced an embedding that failed validation on every normal checkout.
    Reading the declared form from git check-attr makes the embedded bytes a
    function of the repository contract, not of the local working tree.

    '' and $null are different answers. '' is a declaration: git stores the
    bytes exactly, so embed and freshness logic must keep them exactly as they
    are -- never a license to normalize. $null is NO answer: every caller must
    treat it as an error (fail validation / abort the embed), because silently
    falling back to "bytes untouched" re-embeds the local accident the
    declared-form read exists to prevent.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $gitPath = $RelativePath -replace '\\', '/'
    $output = $null
    $exitCode = 0
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & git -C $RepositoryRoot check-attr text eol -- $gitPath 2>$null
        $exitCode = $LASTEXITCODE
    }
    catch {
        return $null
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0 -or $null -eq $output) {
        return $null
    }

    $textAttribute = ''
    $eolAttribute = ''
    foreach ($attributeLine in @($output)) {
        if ($attributeLine -match ': text: (\S+)\s*$') {
            $textAttribute = $Matches[1]
        }
        elseif ($attributeLine -match ': eol: (\S+)\s*$') {
            $eolAttribute = $Matches[1]
        }
    }

    # -text (text: unset) means git stores the file byte for byte; no eol applies.
    if ($textAttribute -eq 'unset') {
        return ''
    }

    switch ($eolAttribute) {
        'crlf' { return 'CRLF' }
        'lf' { return 'LF' }
        default { return '' }
    }
}

function ConvertTo-DeclaredLineEndings {
    <#
    .SYNOPSIS
    Normalizes every line ending in $Text to the declared form ('CRLF' or
    'LF'). Any other declaration -- including '' for "git leaves the bytes
    alone" -- returns the text unchanged. $null is NOT a declaration (it is
    the unreadable-git-answer sentinel) and is rejected by [ValidateNotNull()].
    The parameter is deliberately UNTYPED: a [string]-typed parameter coerces
    $null to '' BEFORE the validation attributes run (measured on Windows
    PowerShell 5.1), which silently turned NO answer into "bytes untouched".
    #>
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory = $true)][AllowEmptyString()][ValidateNotNull()]$Declared
    )

    if ($Declared -eq 'CRLF') {
        return ($Text -replace "`r`n", "`n") -replace "`n", "`r`n"
    }

    if ($Declared -eq 'LF') {
        return $Text -replace "`r`n", "`n"
    }

    return $Text
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
$validationErrors = [System.Collections.Generic.List[string]]::new()
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
        Add-ValidationError -Errors $validationErrors -Message 'A module definition entry in omp-components.json is missing path.'
        continue
    }

    $definitionPath = Resolve-RepositoryPath -Path $relativeDefinitionPath -BasePath $repositoryRootPath
    if (-not (Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
        Add-ValidationError -Errors $validationErrors -Message "Module definition file was not found: $relativeDefinitionPath"
        continue
    }

    $definitionText = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8
    $definition = ConvertFrom-JsonDocument -Json $definitionText -Depth $jsonDepth

    $actualModuleKey = [string](Get-OptionalPropertyValue -Object $definition -Name 'moduleKey')
    if (-not [string]::Equals($manifestModuleKey, $actualModuleKey, [StringComparison]::Ordinal)) {
        Add-ValidationError -Errors $validationErrors -Message "Module key mismatch for '$relativeDefinitionPath'. Manifest='$manifestModuleKey', definition='$actualModuleKey'."
    }

    $actualDefinitionVersion = [string](Get-OptionalPropertyValue -Object $definition -Name 'definitionVersion')
    if (-not [string]::Equals($manifestDefinitionVersion, $actualDefinitionVersion, [StringComparison]::Ordinal)) {
        Add-ValidationError -Errors $validationErrors -Message "Definition version mismatch for '$relativeDefinitionPath'. Manifest='$manifestDefinitionVersion', definition='$actualDefinitionVersion'."
    }

    $createdTables = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $tableSources = @{}
    $setupPaths = [System.Collections.Generic.List[string]]::new()

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
            Add-ValidationError -Errors $validationErrors -Message "SQL script referenced by '$relativeDefinitionPath' was not found: $scriptPathValue"
            continue
        }

        $sqlText = Get-Content -LiteralPath $sqlPath -Raw -Encoding UTF8
        # Same normalization as the embed tool: compare against the declared line-ending
        # form, not the bytes the working copy happens to hold. When git cannot answer
        # (not a work tree), the on-disk text is compared as before.
        $declaredLineEnding = Get-GitDeclaredLineEnding -RepositoryRoot $repositoryRootPath -RelativePath $scriptPathValue
        if ($null -ne $declaredLineEnding) {
            $sqlText = ConvertTo-DeclaredLineEndings -Text $sqlText -Declared $declaredLineEnding
        }
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
            Add-ValidationError -Errors $validationErrors -Message "SQL script '$scriptKey' in '$relativeDefinitionPath' has contentEncoding '$actualEncoding', expected '$expectedEncoding'."
        }

        if (-not [string]::Equals($actualContent, $expectedContent, [StringComparison]::Ordinal)) {
            Add-ValidationError -Errors $validationErrors -Message "SQL script '$scriptKey' in '$relativeDefinitionPath' has embedded content that does not match '$scriptPathValue'. Run scripts/dev/embed-module-definition-sql.ps1."
        }

        if (-not [string]::Equals($actualSha256, $expectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
            Add-ValidationError -Errors $validationErrors -Message "SQL script '$scriptKey' in '$relativeDefinitionPath' has sha256 '$actualSha256', expected '$expectedSha256'. Run scripts/dev/embed-module-definition-sql.ps1."
        }

        # A module may split setup across several files. Collect the union before
        # comparing it with the healing witness in integrity.requiredTables.
        if ($scriptKey -like '*setup*') {
            [void]$setupPaths.Add($scriptPathValue)
            foreach ($match in [regex]::Matches($sqlText, '(?im)^\s*CREATE\s+TABLE\s+(?<schema>[\w\[\]]+)\.(?<table>[\w\[\]]+)')) {
                $schemaName = $match.Groups['schema'].Value.Trim('[', ']')
                $tableName = $match.Groups['table'].Value.Trim('[', ']')
                $qualifiedName = "$schemaName.$tableName"
                if ($createdTables.Add($qualifiedName)) {
                    $tableSources[$qualifiedName] = [System.Collections.Generic.List[string]]::new()
                }
                [void]$tableSources[$qualifiedName].Add($scriptPathValue)
            }
        }
    }

    # runtimeMaintenance.steps embed SQL exactly like sqlScripts. Their semantic
    # safety rules (events, module schema, OBJECT_ID guards) are enforced by the
    # shared C# validator through scripts/omp/Test-ModuleSqlGuards.ps1.
    $runtimeMaintenance = Get-OptionalPropertyValue -Object $definition -Name 'runtimeMaintenance'
    $runtimeSteps = @()
    if ($null -ne $runtimeMaintenance) {
        $stepsValue = Get-OptionalPropertyValue -Object $runtimeMaintenance -Name 'steps'
        if ($null -eq $stepsValue) {
            Add-ValidationError -Errors $validationErrors -Message "'$relativeDefinitionPath' declares runtimeMaintenance without a steps array."
        }
        else {
            $runtimeSteps = @($stepsValue)
        }
    }

    foreach ($step in $runtimeSteps) {
        if ($null -eq $step) {
            continue
        }

        $stepKey = [string](Get-OptionalPropertyValue -Object $step -Name 'key')
        $stepPathValue = [string](Get-OptionalPropertyValue -Object $step -Name 'path')
        if ([string]::IsNullOrWhiteSpace($stepPathValue)) {
            Add-ValidationError -Errors $validationErrors -Message "Runtime maintenance step '$stepKey' in '$relativeDefinitionPath' has no path; keep step SQL in a reviewable .sql file."
            continue
        }

        $stepSqlPath = Resolve-RepositoryPath -Path $stepPathValue -BasePath $repositoryRootPath
        if (-not (Test-Path -LiteralPath $stepSqlPath -PathType Leaf)) {
            Add-ValidationError -Errors $validationErrors -Message "Runtime maintenance SQL referenced by '$relativeDefinitionPath' was not found: $stepPathValue"
            continue
        }

        $stepSqlText = ConvertTo-PortableModuleDefinitionSql -SqlText (Get-Content -LiteralPath $stepSqlPath -Raw -Encoding UTF8)
        $expectedStepContent = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($stepSqlText))
        $expectedStepSha256 = Get-Sha256Hex -Text $stepSqlText
        if (-not [string]::Equals([string](Get-OptionalPropertyValue -Object $step -Name 'contentEncoding'), 'base64-utf8', [StringComparison]::Ordinal) `
            -or -not [string]::Equals([string](Get-OptionalPropertyValue -Object $step -Name 'content'), $expectedStepContent, [StringComparison]::Ordinal) `
            -or -not [string]::Equals([string](Get-OptionalPropertyValue -Object $step -Name 'sha256'), $expectedStepSha256, [StringComparison]::OrdinalIgnoreCase)) {
            Add-ValidationError -Errors $validationErrors -Message "Runtime maintenance step '$stepKey' in '$relativeDefinitionPath' has embedded content or sha256 that does not match '$stepPathValue'. Run scripts/dev/embed-module-definition-sql.ps1."
        }
    }

    # R12-G4: undeclared tables cannot trigger healing, while declared tables
    # must be created by at least one setup file. Check even an empty union.
    # Columns, indexes, constraints and triggers remain outside this comparison:
    # idempotent ALTER blocks do not provide one unambiguous declaration site.
    if ($setupPaths.Count -gt 0) {
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
            $undeclaredSources = @($undeclared | ForEach-Object { "$_ (created in '$($tableSources[$_] -join "', '")')" })
            Add-ValidationError -Errors $validationErrors -Message "'$relativeDefinitionPath' creates $($undeclared.Count) table(s) that integrity.requiredTables does not declare, so the import path cannot notice they are missing: $($undeclaredSources -join ', ')."
        }

        $phantom = @($declaredTables | Where-Object { -not $createdTables.Contains($_) } | Sort-Object)
        if ($phantom.Count -gt 0) {
            Add-ValidationError -Errors $validationErrors -Message "'$relativeDefinitionPath' declares $($phantom.Count) table(s) in integrity.requiredTables that no setup file creates (checked '$($setupPaths -join "', '")'): $($phantom -join ', ')."
        }
    }
}

if ($validationErrors.Count -gt 0) {
    Write-Host 'Module definition validation failed:'
    foreach ($errorMessage in $validationErrors) {
        Write-Host " - $errorMessage"
    }

    exit 1
}

Write-Host 'Module definition validation passed.'
# Explicit: the line-ending lookup runs git, and a fixture that is not a work tree leaves
# git's exit code (128) in $LASTEXITCODE, which would otherwise become this script's.
exit 0
