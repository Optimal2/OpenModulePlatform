# Shared setup for Validate-ComponentVersions.Tests.ps1.
# Dot-sourced from each Describe block's BeforeAll: Pester 5 runs every
# container in a separate session state, so functions and variables defined
# at file scope are not visible inside It blocks.

$ErrorActionPreference = 'Stop'

$scriptPath = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\omp\validate-component-versions.ps1')
$helpersPath = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\omp\validate-component-versions.helpers.ps1')

# Dot-source the helpers so pure functions such as Compare-WebSharedBinaryIdentity
# can be exercised directly without invoking the full validator.
. $helpersPath

function New-TemporaryTestRepository {
    <#
    .SYNOPSIS
    Creates a temporary repository with the validator script, a minimal
    omp-components.json, a module definition, an owned SQL file, and a
    fake .csproj project.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter(Mandatory = $false)]
        [string]$ComponentMinVersion = '1.0.0',

        [Parameter(Mandatory = $false)]
        [string]$ModuleDefinitionVersion = '1.0.0',

        [Parameter(Mandatory = $false)]
        [string]$SqlContent = 'SELECT 1;',

        [Parameter(Mandatory = $false)]
        [string]$ComponentVersion = '1.0.0',

        [Parameter(Mandatory = $false)]
        [string]$ComponentAppKey = 'test_app',

        [Parameter(Mandatory = $false)]
        [string]$CompatibleArtifactMaxVersion = '',

        [Parameter(Mandatory = $false)]
        [string]$CompatibleArtifactMinVersion = ''
    )

    if (Test-Path -LiteralPath $RootPath -PathType Container) {
        Remove-Item -LiteralPath $RootPath -Recurse -Force
    }

    $null = New-Item -ItemType Directory -Path $RootPath -Force

    # Copy the validator so its $repositoryRoot resolves to the temp repo.
    $ompScriptsDir = Join-Path $RootPath 'scripts\omp'
    $null = New-Item -ItemType Directory -Path $ompScriptsDir -Force
    Copy-Item -LiteralPath $scriptPath -Destination (Join-Path $ompScriptsDir 'validate-component-versions.ps1') -Force
    Copy-Item -LiteralPath $helpersPath -Destination (Join-Path $ompScriptsDir 'validate-component-versions.helpers.ps1') -Force

    # Create component project.
    $projectDir = Join-Path $RootPath 'TestApp'
    $null = New-Item -ItemType Directory -Path $projectDir -Force
    $csprojContent = "<Project Sdk=`"Microsoft.NET.Sdk`">`r`n  <PropertyGroup>`r`n    <TargetFramework>net8.0</TargetFramework>`r`n  </PropertyGroup>`r`n</Project>`r`n"
    [System.IO.File]::WriteAllText((Join-Path $projectDir 'TestApp.csproj'), $csprojContent, [System.Text.Encoding]::UTF8)

    # Create module definition and SQL.
    $moduleDir = Join-Path $RootPath 'TestModule'
    $sqlDir = Join-Path $moduleDir 'sql'
    $null = New-Item -ItemType Directory -Path $sqlDir -Force
    [System.IO.File]::WriteAllText((Join-Path $sqlDir 'init.sql'), $SqlContent, [System.Text.Encoding]::UTF8)

    $moduleDefinition = @{
        moduleKey = 'test_module'
        definitionVersion = $ModuleDefinitionVersion
        sqlScripts = @(
            @{
                path = 'TestModule/sql/init.sql'
            }
        )
    }

    $compatibleArtifact = @{}
    if (-not [string]::IsNullOrWhiteSpace($CompatibleArtifactMaxVersion)) {
        $compatibleArtifact['maxVersion'] = $CompatibleArtifactMaxVersion
    }
    if (-not [string]::IsNullOrWhiteSpace($CompatibleArtifactMinVersion)) {
        $compatibleArtifact['minVersion'] = $CompatibleArtifactMinVersion
    }
    if (-not [string]::IsNullOrWhiteSpace($ComponentAppKey) -and $compatibleArtifact.Count -gt 0) {
        $compatibleArtifact['appKey'] = $ComponentAppKey
        $moduleDefinition['compatibleArtifacts'] = @($compatibleArtifact)
    }

    $moduleDefinitionJson = $moduleDefinition | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText((Join-Path $moduleDir 'test.module-definition.json'), $moduleDefinitionJson, [System.Text.Encoding]::UTF8)

    # Create the component manifest.
    $componentEntry = @{
        componentKey = 'test_app'
        version = $ComponentVersion
        projectPath = 'TestApp/TestApp.csproj'
        moduleKey = 'test_module'
        minModuleDefinitionVersion = $ComponentMinVersion
    }
    if (-not [string]::IsNullOrWhiteSpace($ComponentAppKey)) {
        $componentEntry['appKey'] = $ComponentAppKey
    }

    $manifest = @{
        repositoryVersion = '1.0.0'
        moduleDefinitions = @(
            @{
                moduleKey = 'test_module'
                definitionVersion = $ModuleDefinitionVersion
                path = 'TestModule/test.module-definition.json'
            }
        )
        components = @(
            $componentEntry
        )
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText((Join-Path $RootPath 'omp-components.json'), $manifestJson, [System.Text.Encoding]::UTF8)

    # Initialize git repository and create initial commit.
    $originalLocation = Get-Location
    try {
        Set-Location -LiteralPath $RootPath
        & git -C $RootPath init --quiet
        if ($LASTEXITCODE -ne 0) { throw 'git init failed.' }

        & git -C $RootPath config core.autocrlf false
        if ($LASTEXITCODE -ne 0) { throw 'git config core.autocrlf failed.' }

        & git -C $RootPath config user.email 'test@example.com'
        if ($LASTEXITCODE -ne 0) { throw 'git config user.email failed.' }

        & git -C $RootPath config user.name 'Test User'
        if ($LASTEXITCODE -ne 0) { throw 'git config user.name failed.' }

        & git -C $RootPath add -A
        if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }

        & git -C $RootPath commit -m 'Initial commit' --quiet
        if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
    }
    finally {
        Set-Location $originalLocation
    }

    return (Join-Path $ompScriptsDir 'validate-component-versions.ps1')
}

function Remove-TemporaryTestRepository {
    <#
    .SYNOPSIS
    Removes a temporary test repository created by New-TemporaryTestRepository.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath
    )

    if (Test-Path -LiteralPath $RootPath -PathType Container) {
        Remove-Item -LiteralPath $RootPath -Recurse -Force
    }
}

function Invoke-Validator {
    <#
    .SYNOPSIS
    Runs the validator in the specified repository and returns its exit code.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ValidatorPath,
        [Parameter(Mandatory = $false)][string]$BaseCommit = ''
    )

    $exitCode = $null
    try {
        if ([string]::IsNullOrWhiteSpace($BaseCommit)) {
            & $ValidatorPath 2>&1 | Out-String | Out-Null
        }
        else {
            & $ValidatorPath -BaseCommit $BaseCommit 2>&1 | Out-String | Out-Null
        }
    }
    finally {
        $exitCode = $LASTEXITCODE
    }

    return $exitCode
}

function Invoke-ValidatorWithOutput {
    <#
    .SYNOPSIS
    Runs the validator and returns its exit code together with everything it
    wrote, so tests can assert on warnings and summary lines.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ValidatorPath,
        [Parameter(Mandatory = $false)][string]$BaseCommit = '',
        [Parameter(Mandatory = $false)][switch]$Strict
    )

    $arguments = @{}
    if (-not [string]::IsNullOrWhiteSpace($BaseCommit)) {
        $arguments['BaseCommit'] = $BaseCommit
    }
    if ($Strict) {
        $arguments['Strict'] = $true
    }

    $output = ''
    $exitCode = $null
    try {
        $output = & $ValidatorPath @arguments *>&1 | Out-String -Width 4096
    }
    catch {
        $output += $_.Exception.Message
    }
    finally {
        $exitCode = $LASTEXITCODE
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function New-OverlayTestRepository {
    <#
    .SYNOPSIS
    Creates a temporary repository whose manifest declares one shared project
    (SharedLib) with no in-repository consumers, then commits a change to it.
    Returns the validator path and the base commit before the change, so
    Check 7 sees the shared project as changed.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RootPath
    )

    $validatorPath = New-TemporaryTestRepository -RootPath $RootPath

    $sharedDir = Join-Path $RootPath 'SharedLib'
    $null = New-Item -ItemType Directory -Path $sharedDir -Force
    $csprojContent = "<Project Sdk=`"Microsoft.NET.Sdk`">`r`n  <PropertyGroup>`r`n    <TargetFramework>net8.0</TargetFramework>`r`n  </PropertyGroup>`r`n</Project>`r`n"
    [System.IO.File]::WriteAllText((Join-Path $sharedDir 'SharedLib.csproj'), $csprojContent, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText((Join-Path $sharedDir 'Shared.cs'), 'class Shared { }', [System.Text.Encoding]::UTF8)

    $manifestPath = Join-Path $RootPath 'omp-components.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $manifest | Add-Member -NotePropertyName sharedProjects -NotePropertyValue @(
        [pscustomobject]@{
            projectPath = 'SharedLib/SharedLib.csproj'
            consumers = @()
            externalConsumers = @()
        }
    )
    [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10), [System.Text.Encoding]::UTF8)

    & git -C $RootPath add -A
    if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
    & git -C $RootPath commit -m 'Add shared project' --quiet
    if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
    $baseCommit = (& git -C $RootPath rev-parse HEAD).Trim()

    [System.IO.File]::WriteAllText((Join-Path $sharedDir 'Shared.cs'), 'class Shared { int x; }', [System.Text.Encoding]::UTF8)
    & git -C $RootPath add -A
    if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
    & git -C $RootPath commit -m 'Change shared project' --quiet
    if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }

    return [pscustomobject]@{
        ValidatorPath = $validatorPath
        BaseCommit = $baseCommit
        OverlayPath = (Join-Path $RootPath 'omp-components.external.json')
    }
}
