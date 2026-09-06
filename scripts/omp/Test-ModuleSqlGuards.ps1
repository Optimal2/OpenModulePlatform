#requires -Version 5.1
<#
.SYNOPSIS
Validates portable module SQL with the production validator, without a database connection.
.DESCRIPTION
Enforces OMP-MODULE-SQL-CONFIG-OWNERSHIP and the existing module SQL guards.
Defaults to the module definitions declared in omp-components.json, including decoded
base64-utf8 content. Explicit SQL paths use the portable USE-header transformation.
Unknown encodings, missing payloads, parse errors and blocked writes fail the command.
.PARAMETER Path
SQL files or module-definition JSON files to validate.
.PARAMETER RepositoryRoot
Repository containing omp-components.json. Defaults to this repository.
.PARAMETER NoBuild
Use the Release validator already built by the solution build.
#>
[CmdletBinding()]
param(
    [string[]]$Path = @(),
    [string]$RepositoryRoot = '',
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ompRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $ompRoot 'tools\ModuleSqlGuard\ModuleSqlGuard.csproj'
$validator = Join-Path $ompRoot 'tools\ModuleSqlGuard\bin\Release\net10.0\ModuleSqlGuard.dll'

if (-not $NoBuild) {
    & dotnet build $project --configuration Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Module SQL validator build failed ($LASTEXITCODE)." }
}
if (-not (Test-Path -LiteralPath $validator -PathType Leaf)) { throw 'Module SQL validator is missing. Build it first.' }

$arguments = @()
if ($Path.Count -gt 0) {
    $arguments = $Path
}
else {
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = $ompRoot }
    $arguments = @('--repository', $RepositoryRoot)
}

& dotnet $validator @arguments
if ($LASTEXITCODE -ne 0) { throw "Module SQL validation failed ($LASTEXITCODE)." }
