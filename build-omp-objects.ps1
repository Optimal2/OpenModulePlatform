<#
.SYNOPSIS
Builds portable OMP objects for this repository.

.DESCRIPTION
This root wrapper keeps the same command shape across OMP-related repositories
while delegating the implementation to the canonical OMP script.
#>
[CmdletBinding()]
param(
    [string]$OutputRoot = '',
    [string[]]$ComponentKey = @(),
    [switch]$AllComponents,
    [switch]$BuildArtifacts,
    [string]$Configuration = 'Release',
    [string[]]$ArtifactConfigurationFile = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = $PSScriptRoot
$scriptPath = Join-Path $repositoryRoot 'scripts\omp\build-repository-objects.ps1'

& $scriptPath `
    -RepositoryRoot $repositoryRoot `
    -OmpRepositoryRoot $repositoryRoot `
    -OutputRoot $OutputRoot `
    -ComponentKey $ComponentKey `
    -AllComponents:$AllComponents `
    -BuildArtifacts:$BuildArtifacts `
    -Configuration $Configuration `
    -ArtifactConfigurationFile $ArtifactConfigurationFile
