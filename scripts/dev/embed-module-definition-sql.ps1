param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Sha256Hex {
    param([string]$Text)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return ([System.BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
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
    $document = $jsonText | ConvertFrom-Json -Depth $jsonDepth

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
        $script | Add-Member -NotePropertyName contentEncoding -NotePropertyValue 'base64-utf8' -Force
        $script | Add-Member -NotePropertyName content -NotePropertyValue ([Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($sqlText))) -Force
        $script | Add-Member -NotePropertyName sha256 -NotePropertyValue (Get-Sha256Hex -Text $sqlText) -Force
        $changed = $true
    }

    if ($changed) {
        $updated = $document | ConvertTo-Json -Depth $jsonDepth
        [System.IO.File]::WriteAllText($definitionFile.FullName, $updated + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
        Write-Host "Embedded SQL in $($definitionFile.Name)"
    }
}
