<#
.SYNOPSIS
Pester tests for scripts/omp/validate-component-versions.ps1.

.DESCRIPTION
These tests validate the minModuleDefinitionVersion enforcement logic
introduced in Check 6 and Check 8b. Each test runs the validator inside
an isolated temporary git repository so that git-based diff checks can
be exercised without touching the OpenModulePlatform repository state.
#>

$ErrorActionPreference = 'Stop'

$scriptPath = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\omp\validate-component-versions.ps1')

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
        & git init --quiet
        if ($LASTEXITCODE -ne 0) { throw 'git init failed.' }

        & git config core.autocrlf false
        if ($LASTEXITCODE -ne 0) { throw 'git config core.autocrlf failed.' }

        & git config user.email 'test@example.com'
        if ($LASTEXITCODE -ne 0) { throw 'git config user.email failed.' }

        & git config user.name 'Test User'
        if ($LASTEXITCODE -ne 0) { throw 'git config user.name failed.' }

        & git add -A
        if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }

        & git commit -m 'Initial commit' --quiet
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
        [Parameter(Mandatory = $false)][string]$BaseCommit = '',
        [Parameter(Mandatory = $false)][string]$WebSharedDllPath = ''
    )

    $exitCode = $null
    try {
        $arguments = @{ }
        if (-not [string]::IsNullOrWhiteSpace($BaseCommit)) {
            $arguments['BaseCommit'] = $BaseCommit
        }
        if (-not [string]::IsNullOrWhiteSpace($WebSharedDllPath)) {
            $arguments['WebSharedDllPath'] = $WebSharedDllPath
        }

        if ($arguments.Count -eq 0) {
            & $ValidatorPath 2>&1 | Out-String | Out-Null
        }
        else {
            & $ValidatorPath @arguments 2>&1 | Out-String | Out-Null
        }
    }
    finally {
        $exitCode = $LASTEXITCODE
    }

    return $exitCode
}

function New-TemporaryWebSharedTestRepository {
    <#
    .SYNOPSIS
    Creates a temporary repository with a Web.Shared shared project entry,
    two consumers, a fake DLL, and a hash manifest for Check 11 tests.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$ManifestHash,
        [Parameter(Mandatory = $false)][string]$ConsumerAVersion = '1.0.0',
        [Parameter(Mandatory = $false)][string]$ConsumerBVersion = '1.0.0'
    )

    if (Test-Path -LiteralPath $RootPath -PathType Container) {
        Remove-Item -LiteralPath $RootPath -Recurse -Force
    }

    $null = New-Item -ItemType Directory -Path $RootPath -Force

    # Copy the validator so its $repositoryRoot resolves to the temp repo.
    $ompScriptsDir = Join-Path $RootPath 'scripts\omp'
    $null = New-Item -ItemType Directory -Path $ompScriptsDir -Force
    Copy-Item -LiteralPath $scriptPath -Destination (Join-Path $ompScriptsDir 'validate-component-versions.ps1') -Force

    # Create fake Web.Shared project and DLL directories.
    $webSharedDir = Join-Path $RootPath 'OpenModulePlatform.Web.Shared'
    $null = New-Item -ItemType Directory -Path $webSharedDir -Force
    $csprojContent = "<Project Sdk=`"Microsoft.NET.Sdk`">`r`n  <PropertyGroup>`r`n    <TargetFramework>net10.0</TargetFramework>`r`n  </PropertyGroup>`r`n</Project>`r`n"
    [System.IO.File]::WriteAllText((Join-Path $webSharedDir 'OpenModulePlatform.Web.Shared.csproj'), $csprojContent, [System.Text.Encoding]::UTF8)

    # Create two fake consumer projects.
    $consumerADir = Join-Path $RootPath 'ConsumerA'
    $null = New-Item -ItemType Directory -Path $consumerADir -Force
    [System.IO.File]::WriteAllText((Join-Path $consumerADir 'ConsumerA.csproj'), $csprojContent, [System.Text.Encoding]::UTF8)

    $consumerBDir = Join-Path $RootPath 'ConsumerB'
    $null = New-Item -ItemType Directory -Path $consumerBDir -Force
    [System.IO.File]::WriteAllText((Join-Path $consumerBDir 'ConsumerB.csproj'), $csprojContent, [System.Text.Encoding]::UTF8)

    # Create the component manifest with a Web.Shared shared project.
    $manifest = @{
        repositoryVersion = '1.0.0'
        moduleDefinitions = @()
        sharedProjects = @(
            @{
                projectPath = 'OpenModulePlatform.Web.Shared/OpenModulePlatform.Web.Shared.csproj'
                consumers = @('consumer-a', 'consumer-b')
            }
        )
        components = @(
            @{
                componentKey = 'consumer-a'
                version = $ConsumerAVersion
                projectPath = 'ConsumerA/ConsumerA.csproj'
            }
            @{
                componentKey = 'consumer-b'
                version = $ConsumerBVersion
                projectPath = 'ConsumerB/ConsumerB.csproj'
            }
        )
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText((Join-Path $RootPath 'omp-components.json'), $manifestJson, [System.Text.Encoding]::UTF8)

    # Write the committed hash manifest.
    [System.IO.File]::WriteAllText((Join-Path $RootPath '.webshared-build-hash.txt'), $ManifestHash + [Environment]::NewLine, [System.Text.Encoding]::UTF8)

    # Initialize git repository and create initial commit.
    $originalLocation = Get-Location
    try {
        Set-Location -LiteralPath $RootPath
        & git init --quiet
        if ($LASTEXITCODE -ne 0) { throw 'git init failed.' }

        & git config core.autocrlf false
        if ($LASTEXITCODE -ne 0) { throw 'git config core.autocrlf failed.' }

        & git config user.email 'test@example.com'
        if ($LASTEXITCODE -ne 0) { throw 'git config user.email failed.' }

        & git config user.name 'Test User'
        if ($LASTEXITCODE -ne 0) { throw 'git config user.name failed.' }

        & git add -A
        if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }

        & git commit -m 'Initial commit' --quiet
        if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
    }
    finally {
        Set-Location $originalLocation
    }

    return (Join-Path $ompScriptsDir 'validate-component-versions.ps1')
}

Describe 'Check 6: minModuleDefinitionVersion sanity' {
    It 'Passes when minModuleDefinitionVersion equals definitionVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'

        $exitCode = Invoke-Validator -ValidatorPath $validatorPath

        $exitCode | Should Be 0
    }

    It 'Fails when minModuleDefinitionVersion is greater than definitionVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '2.0.0' -ModuleDefinitionVersion '1.0.0'

        $exitCode = Invoke-Validator -ValidatorPath $validatorPath

        $exitCode | Should Not Be 0
    }
}

Describe 'Check 8b: minModuleDefinitionVersion lockstep after definitionVersion bump' {
    It 'Passes when minModuleDefinitionVersion is bumped with definitionVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $originalLocation = Get-Location
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0' -SqlContent 'SELECT 1;'
            Set-Location -LiteralPath $repoRoot
            $baseCommit = (& git rev-parse HEAD).Trim()

            # Change SQL, bump module definition version, and keep minVersion in sync.
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestModule/sql/init.sql'), 'SELECT 2;', [System.Text.Encoding]::UTF8)

            $moduleDefinitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'
            $moduleDefinition = Get-Content -LiteralPath $moduleDefinitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $moduleDefinition.definitionVersion = '2.0.0'
            [System.IO.File]::WriteAllText($moduleDefinitionPath, ($moduleDefinition | ConvertTo-Json -Depth 10), [System.Text.Encoding]::UTF8)

            $manifestPath = Join-Path $repoRoot 'omp-components.json'
            $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $manifest.moduleDefinitions[0].definitionVersion = '2.0.0'
            $manifest.components[0].minModuleDefinitionVersion = '2.0.0'
            [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10), [System.Text.Encoding]::UTF8)

            & git add -A
            if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
            & git commit -m 'Bump definitionVersion and minModuleDefinitionVersion' --quiet
            if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }

            $exitCode = Invoke-Validator -ValidatorPath $validatorPath -BaseCommit $baseCommit

            $exitCode | Should Be 0
        }
        finally {
            Set-Location $originalLocation
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails when minModuleDefinitionVersion lags a bumped definitionVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $originalLocation = Get-Location
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0' -SqlContent 'SELECT 1;'
            Set-Location -LiteralPath $repoRoot
            $baseCommit = (& git rev-parse HEAD).Trim()

            # Change SQL and bump module definition version, but leave minVersion behind.
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestModule/sql/init.sql'), 'SELECT 2;', [System.Text.Encoding]::UTF8)

            $moduleDefinitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'
            $moduleDefinition = Get-Content -LiteralPath $moduleDefinitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $moduleDefinition.definitionVersion = '2.0.0'
            [System.IO.File]::WriteAllText($moduleDefinitionPath, ($moduleDefinition | ConvertTo-Json -Depth 10), [System.Text.Encoding]::UTF8)

            $manifestPath = Join-Path $repoRoot 'omp-components.json'
            $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $manifest.moduleDefinitions[0].definitionVersion = '2.0.0'
            # minModuleDefinitionVersion intentionally remains 1.0.0.
            [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10), [System.Text.Encoding]::UTF8)

            & git add -A
            if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
            & git commit -m 'Bump definitionVersion without minModuleDefinitionVersion' --quiet
            if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }

            $exitCode = Invoke-Validator -ValidatorPath $validatorPath -BaseCommit $baseCommit

            $exitCode | Should Not Be 0
        }
        finally {
            Set-Location $originalLocation
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }
}


Describe 'Check 10: compatibleArtifacts range sanity' {
    It 'Passes when component version is within maxVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentVersion '1.0.0' -ComponentAppKey 'test_app' -CompatibleArtifactMaxVersion '2.0.0'

        $exitCode = Invoke-Validator -ValidatorPath $validatorPath

        $exitCode | Should Be 0
    }

    It 'Passes when component version equals maxVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentVersion '1.0.0' -ComponentAppKey 'test_app' -CompatibleArtifactMaxVersion '1.0.0'

        $exitCode = Invoke-Validator -ValidatorPath $validatorPath

        $exitCode | Should Be 0
    }

    It 'Fails when component version exceeds maxVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentVersion '2.0.0' -ComponentAppKey 'test_app' -CompatibleArtifactMaxVersion '1.0.0'

        $exitCode = Invoke-Validator -ValidatorPath $validatorPath

        $exitCode | Should Not Be 0
    }

    It 'Fails when component version is below minVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentVersion '0.5.0' -ComponentAppKey 'test_app' -CompatibleArtifactMinVersion '1.0.0'

        $exitCode = Invoke-Validator -ValidatorPath $validatorPath

        $exitCode | Should Not Be 0
    }
}


Describe 'Check 11: Web.Shared binary identity' {
    It 'Passes when Web.Shared binary hash matches the committed baseline' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $dllPath = Join-Path $repoRoot 'OpenModulePlatform.Web.Shared\OpenModulePlatform.Web.Shared.dll'
        $manifestHash = ''
        try {
            $validatorPath = New-TemporaryWebSharedTestRepository -RootPath $repoRoot -ManifestHash 'ignored-on-match'

            # Ensure the DLL directory exists and write a deterministic payload.
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $dllPath) -Force
            $dllBytes = [System.Text.Encoding]::UTF8.GetBytes('web-shared-dll-v1')
            [System.IO.File]::WriteAllBytes($dllPath, $dllBytes)
            $manifestHash = ([System.Security.Cryptography.SHA256]::Create().ComputeHash($dllBytes) | ForEach-Object { $_.ToString('x2') }) -join ''

            # Update the manifest to match the actual DLL hash.
            [System.IO.File]::WriteAllText((Join-Path $repoRoot '.webshared-build-hash.txt'), $manifestHash + [Environment]::NewLine, [System.Text.Encoding]::UTF8)
            Set-Location -LiteralPath $repoRoot
            & git add -A
            if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
            & git commit -m 'Update hash manifest' --quiet
            if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }

            $exitCode = Invoke-Validator -ValidatorPath $validatorPath -WebSharedDllPath $dllPath

            $exitCode | Should Be 0
        }
        finally {
            Set-Location $env:SystemDrive\
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails when Web.Shared binary hash changed but consumers were not bumped' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $dllPath = Join-Path $repoRoot 'OpenModulePlatform.Web.Shared\OpenModulePlatform.Web.Shared.dll'
        try {
            $validatorPath = New-TemporaryWebSharedTestRepository -RootPath $repoRoot -ManifestHash '0000000000000000000000000000000000000000000000000000000000000000'

            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $dllPath) -Force
            [System.IO.File]::WriteAllBytes($dllPath, [System.Text.Encoding]::UTF8.GetBytes('web-shared-dll-v2'))
            Set-Location -LiteralPath $repoRoot
            & git add -A
            if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
            & git commit -m 'Change Web.Shared binary without bumping consumers' --quiet
            if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
            $baseCommit = (& git rev-parse HEAD~1).Trim()

            $exitCode = Invoke-Validator -ValidatorPath $validatorPath -BaseCommit $baseCommit -WebSharedDllPath $dllPath

            $exitCode | Should Not Be 0
        }
        finally {
            Set-Location $env:SystemDrive\
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Passes when Web.Shared binary hash changed and all consumers were bumped' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $dllPath = Join-Path $repoRoot 'OpenModulePlatform.Web.Shared\OpenModulePlatform.Web.Shared.dll'
        try {
            $validatorPath = New-TemporaryWebSharedTestRepository -RootPath $repoRoot -ManifestHash '0000000000000000000000000000000000000000000000000000000000000000'

            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $dllPath) -Force
            [System.IO.File]::WriteAllBytes($dllPath, [System.Text.Encoding]::UTF8.GetBytes('web-shared-dll-v3'))

            # Bump both consumer versions.
            $manifestPath = Join-Path $repoRoot 'omp-components.json'
            $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $manifest.components[0].version = '1.0.1'
            $manifest.components[1].version = '1.0.1'
            [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10), [System.Text.Encoding]::UTF8)

            Set-Location -LiteralPath $repoRoot
            & git add -A
            if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
            & git commit -m 'Change Web.Shared binary and bump consumers' --quiet
            if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
            $baseCommit = (& git rev-parse HEAD~1).Trim()

            $exitCode = Invoke-Validator -ValidatorPath $validatorPath -BaseCommit $baseCommit -WebSharedDllPath $dllPath

            $exitCode | Should Be 0
        }
        finally {
            Set-Location $env:SystemDrive\
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }
}
