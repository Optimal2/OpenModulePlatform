param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Sha256Hex {
    param([string]$Text)

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

$jsonDepth = 100
$manifestPath = Join-Path $RepositoryRoot 'omp-components.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Component manifest was not found: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$definitionFiles = @()
foreach ($definition in @($manifest.moduleDefinitions)) {
    if ($null -eq $definition) {
        continue
    }

    $relativePath = [string]$definition.path
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        throw "Module definition entry in '$manifestPath' is missing path."
    }

    $definitionPath = Resolve-RepositoryPath -Path $relativePath -BasePath $RepositoryRoot
    if (-not (Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
        throw "Module definition file was not found: $definitionPath"
    }

    $definitionFiles += Get-Item -LiteralPath $definitionPath
}

$definitionFiles = $definitionFiles | Sort-Object FullName

foreach ($definitionFile in $definitionFiles) {
    $jsonText = Get-Content -LiteralPath $definitionFile.FullName -Raw -Encoding UTF8
    $document = ConvertFrom-JsonDocument -Json $jsonText -Depth $jsonDepth

    if ($null -eq $document.sqlScripts) {
        continue
    }

    $changed = $false
    foreach ($script in @($document.sqlScripts)) {
        if ([string]::IsNullOrWhiteSpace([string]$script.path)) {
            continue
        }

        $sqlPath = Join-Path $RepositoryRoot ([string]$script.path)
        if (-not (Test-Path -LiteralPath $sqlPath)) {
            Write-Warning "Skipping missing SQL script referenced by $($definitionFile.Name): $($script.path)"
            continue
        }

        $sqlText = Get-Content -LiteralPath $sqlPath -Raw -Encoding UTF8
        $contentEncoding = 'base64-utf8'
        $content = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($sqlText))
        $sha256 = Get-Sha256Hex -Text $sqlText

        if ([string](Get-OptionalPropertyValue -Object $script -Name 'contentEncoding') -eq $contentEncoding `
            -and [string](Get-OptionalPropertyValue -Object $script -Name 'content') -eq $content `
            -and [string](Get-OptionalPropertyValue -Object $script -Name 'sha256') -eq $sha256) {
            continue
        }

        $script | Add-Member -NotePropertyName contentEncoding -NotePropertyValue 'base64-utf8' -Force
        $script | Add-Member -NotePropertyName content -NotePropertyValue $content -Force
        $script | Add-Member -NotePropertyName sha256 -NotePropertyValue $sha256 -Force
        $changed = $true
    }

    if ($changed) {
        $updated = $document | ConvertTo-Json -Depth $jsonDepth
        [System.IO.File]::WriteAllText($definitionFile.FullName, $updated + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
        Write-Host "Embedded SQL in $($definitionFile.Name)"
    }
}
