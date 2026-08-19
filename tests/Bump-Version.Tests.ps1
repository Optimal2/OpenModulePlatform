<#
.SYNOPSIS
Pester tests for scripts/omp/bump-version.ps1.

.DESCRIPTION
These tests verify that bumping a component also updates the matching
compatibleArtifacts.maxVersion entry in the module definition. The
2026-08-18 IbsPackager import failure showed that leaving this step
manual lets the artifact version cap drift behind the component version,
so the host rejects the produced artifact at import time.
#>

$ErrorActionPreference = 'Stop'

$scriptPath = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\omp\bump-version.ps1')

function New-TemporaryBumpRepository {
    <#
    .SYNOPSIS
    Creates a temporary repository with bump-version.ps1, a minimal
    omp-components.json, a module definition, and a fake .csproj project.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter(Mandatory = $false)]
        [string]$ComponentVersion = '1.0.0',

        [Parameter(Mandatory = $false)]
        [string]$CompatibleArtifactMaxVersion = ''
    )

    if (Test-Path -LiteralPath $RootPath -PathType Container) {
        Remove-Item -LiteralPath $RootPath -Recurse -Force
    }

    $null = New-Item -ItemType Directory -Path $RootPath -Force

    # Copy the bump script so its $repositoryRoot resolves to the temp repo.
    $ompScriptsDir = Join-Path $RootPath 'scripts\omp'
    $null = New-Item -ItemType Directory -Path $ompScriptsDir -Force
    Copy-Item -LiteralPath $scriptPath -Destination (Join-Path $ompScriptsDir 'bump-version.ps1') -Force

    # Create component project.
    $projectDir = Join-Path $RootPath 'TestApp'
    $null = New-Item -ItemType Directory -Path $projectDir -Force
    $csprojContent = "<Project Sdk=`"Microsoft.NET.Sdk`">`r`n  <PropertyGroup>`r`n    <TargetFramework>net8.0</TargetFramework>`r`n  </PropertyGroup>`r`n</Project>`r`n"
    [System.IO.File]::WriteAllText((Join-Path $projectDir 'TestApp.csproj'), $csprojContent, [System.Text.Encoding]::UTF8)

    # Create module definition.
    $moduleDir = Join-Path $RootPath 'TestModule'
    $null = New-Item -ItemType Directory -Path $moduleDir -Force

    $compatibleArtifact = @{
        appKey = 'test_app'
        packageType = 'web-app'
        targetName = 'test-app'
        relativePathTemplate = 'test-app/web/{version}'
        minVersion = '1.0.0'
    }
    if (-not [string]::IsNullOrWhiteSpace($CompatibleArtifactMaxVersion)) {
        $compatibleArtifact['maxVersion'] = $CompatibleArtifactMaxVersion
    }

    $moduleDefinition = @{
        moduleKey = 'test_module'
        definitionVersion = '1.0.0'
        compatibleArtifacts = @($compatibleArtifact)
        sqlScripts = @()
    }

    $moduleDefinitionJson = $moduleDefinition | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText((Join-Path $moduleDir 'test.module-definition.json'), $moduleDefinitionJson, [System.Text.Encoding]::UTF8)

    # Create the component manifest.
    $manifest = @{
        repositoryVersion = '1.0.0'
        repositoryKey = 'testrepo'
        moduleDefinitions = @(
            @{
                moduleKey = 'test_module'
                definitionVersion = '1.0.0'
                path = 'TestModule/test.module-definition.json'
            }
        )
        components = @(
            @{
                componentKey = 'test_app'
                appKey = 'test_app'
                moduleKey = 'test_module'
                version = $ComponentVersion
                projectPath = 'TestApp/TestApp.csproj'
            }
        )
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText((Join-Path $RootPath 'omp-components.json'), $manifestJson, [System.Text.Encoding]::UTF8)

    return (Join-Path $ompScriptsDir 'bump-version.ps1')
}

function Remove-TemporaryBumpRepository {
    <#
    .SYNOPSIS
    Removes a temporary repository created by New-TemporaryBumpRepository.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath
    )

    if (Test-Path -LiteralPath $RootPath -PathType Container) {
        Remove-Item -LiteralPath $RootPath -Recurse -Force
    }
}

function Invoke-BumpVersion {
    <#
    .SYNOPSIS
    Runs bump-version.ps1 in the specified repository and returns its exit code.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$BumpScriptPath,
        [Parameter(Mandatory = $false)][string]$ComponentKey = 'test_app'
    )

    $exitCode = $null
    try {
        & $BumpScriptPath -ComponentKey $ComponentKey 2>&1 | Out-String | Out-Null
    }
    finally {
        $exitCode = $LASTEXITCODE
    }

    return $exitCode
}

Describe 'Bump-Version updates compatibleArtifacts.maxVersion' {
    It 'Sets maxVersion from null to the bumped component version' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $bumpScriptPath = New-TemporaryBumpRepository -RootPath $repoRoot -ComponentVersion '1.0.0'

            $exitCode = Invoke-BumpVersion -BumpScriptPath $bumpScriptPath

            ($exitCode -eq 0 -or $exitCode -eq $null) | Should Be $true

            $moduleDefinitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'
            $moduleDefinition = Get-Content -LiteralPath $moduleDefinitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $moduleDefinition.compatibleArtifacts[0].maxVersion | Should Be '1.0.1'
        }
        finally {
            Remove-TemporaryBumpRepository -RootPath $repoRoot
        }
    }

    It 'Raises maxVersion when the existing cap is below the bumped component version' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $bumpScriptPath = New-TemporaryBumpRepository -RootPath $repoRoot -ComponentVersion '1.0.7' -CompatibleArtifactMaxVersion '1.0.7'

            $exitCode = Invoke-BumpVersion -BumpScriptPath $bumpScriptPath

            ($exitCode -eq 0 -or $exitCode -eq $null) | Should Be $true

            $moduleDefinitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'
            $moduleDefinition = Get-Content -LiteralPath $moduleDefinitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $moduleDefinition.compatibleArtifacts[0].maxVersion | Should Be '1.0.8'
        }
        finally {
            Remove-TemporaryBumpRepository -RootPath $repoRoot
        }
    }

    It 'Leaves maxVersion unchanged when it already equals or exceeds the bumped component version' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $bumpScriptPath = New-TemporaryBumpRepository -RootPath $repoRoot -ComponentVersion '1.0.5' -CompatibleArtifactMaxVersion '2.0.0'

            $exitCode = Invoke-BumpVersion -BumpScriptPath $bumpScriptPath

            ($exitCode -eq 0 -or $exitCode -eq $null) | Should Be $true

            $moduleDefinitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'
            $moduleDefinition = Get-Content -LiteralPath $moduleDefinitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $moduleDefinition.compatibleArtifacts[0].maxVersion | Should Be '2.0.0'
        }
        finally {
            Remove-TemporaryBumpRepository -RootPath $repoRoot
        }
    }
}
