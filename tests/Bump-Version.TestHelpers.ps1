# Shared setup for Bump-Version.Tests.ps1.
# Dot-sourced from each Describe block's BeforeAll: Pester 5 runs every
# container in a separate session state, so functions and variables defined
# at file scope are not visible inside It blocks.

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
    Runs bump-version.ps1 in a child powershell.exe process and returns its
    REAL exit code. A child process is required for a meaningful exit-code
    assertion: an in-process & call never sets $LASTEXITCODE for a script, so
    the ambient value leaked between Pester containers ($LASTEXITCODE is
    updated process-wide; Pester 5's session-state isolation does not cover
    it) and made the old assertion depend on whichever unrelated test ran
    last.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$BumpScriptPath,
        [Parameter(Mandatory = $false)][string]$ComponentKey = 'test_app'
    )

    # ErrorActionPreference 'Stop' would turn the child's redirected stderr
    # into a throwing ErrorRecord, so relax it locally for the capture.
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $null = & powershell.exe -NoProfile -File $BumpScriptPath -ComponentKey $ComponentKey 2>&1 | Out-String
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}
