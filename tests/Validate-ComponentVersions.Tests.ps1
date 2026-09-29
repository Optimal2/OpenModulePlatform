# Pester 6's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (6.1.0), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
Pester tests for scripts/omp/validate-component-versions.ps1.

.DESCRIPTION
These tests validate the minModuleDefinitionVersion enforcement logic
introduced in Check 6 and Check 8b. Each test runs the validator inside
an isolated temporary git repository so that git-based diff checks can
be exercised without touching the OpenModulePlatform repository state.

Pester 6 runs every container in a separate session state, so the shared
harness (validator paths, dot-sourced validator helpers, temp-repo helpers)
lives in Validate-ComponentVersions.TestHelpers.ps1 and is dot-sourced from
each Describe block's BeforeAll.
#>

Describe 'Check 6: minModuleDefinitionVersion sanity' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')
    }

    It 'Passes when minModuleDefinitionVersion equals definitionVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'

        $exitCode = Invoke-Validator -ValidatorPath $validatorPath

        $exitCode | Should -Be 0
    }

    It 'Fails when minModuleDefinitionVersion is greater than definitionVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '2.0.0' -ModuleDefinitionVersion '1.0.0'

        $exitCode = Invoke-Validator -ValidatorPath $validatorPath

        $exitCode | Should -Not -Be 0
    }
}

Describe 'Check 4b: minWorkerHostVersion schema' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')
    }

    It 'Passes for a worker plugin with a semantic worker-host floor' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
        $manifestPath = Join-Path $repoRoot 'omp-components.json'
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $manifest.components[0] | Add-Member -NotePropertyName packageType -NotePropertyValue 'worker'
        $manifest.components[0] | Add-Member -NotePropertyName minWorkerHostVersion -NotePropertyValue '0.3.21'
        [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10), [System.Text.Encoding]::UTF8)

        (Invoke-Validator -ValidatorPath $validatorPath) | Should -Be 0
    }

    It 'Fails when a non-worker component declares minWorkerHostVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
        $manifestPath = Join-Path $repoRoot 'omp-components.json'
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $manifest.components[0] | Add-Member -NotePropertyName minWorkerHostVersion -NotePropertyValue '0.3.21'
        [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10), [System.Text.Encoding]::UTF8)

        (Invoke-Validator -ValidatorPath $validatorPath) | Should -Not -Be 0
    }
}

Describe 'Check 8b: minModuleDefinitionVersion lockstep after definitionVersion bump' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')
    }

    It 'Passes when minModuleDefinitionVersion is bumped with definitionVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $originalLocation = Get-Location
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0' -SqlContent 'SELECT 1;'
            Set-Location -LiteralPath $repoRoot
            $baseCommit = (& git -C $repoRoot rev-parse HEAD).Trim()

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

            & git -C $repoRoot add -A
            if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
            & git -C $repoRoot commit -m 'Bump definitionVersion and minModuleDefinitionVersion' --quiet
            if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }

            $exitCode = Invoke-Validator -ValidatorPath $validatorPath -BaseCommit $baseCommit

            $exitCode | Should -Be 0
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
            $baseCommit = (& git -C $repoRoot rev-parse HEAD).Trim()

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

            & git -C $repoRoot add -A
            if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
            & git -C $repoRoot commit -m 'Bump definitionVersion without minModuleDefinitionVersion' --quiet
            if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }

            $exitCode = Invoke-Validator -ValidatorPath $validatorPath -BaseCommit $baseCommit

            $exitCode | Should -Not -Be 0
        }
        finally {
            Set-Location $originalLocation
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }
}


Describe 'Check 10: compatibleArtifacts range sanity' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')
    }

    It 'Passes when component version is within maxVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentVersion '1.0.0' -ComponentAppKey 'test_app' -CompatibleArtifactMaxVersion '2.0.0'

        $exitCode = Invoke-Validator -ValidatorPath $validatorPath

        $exitCode | Should -Be 0
    }

    It 'Passes when component version equals maxVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentVersion '1.0.0' -ComponentAppKey 'test_app' -CompatibleArtifactMaxVersion '1.0.0'

        $exitCode = Invoke-Validator -ValidatorPath $validatorPath

        $exitCode | Should -Be 0
    }

    It 'Fails when component version exceeds maxVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentVersion '2.0.0' -ComponentAppKey 'test_app' -CompatibleArtifactMaxVersion '1.0.0'

        $exitCode = Invoke-Validator -ValidatorPath $validatorPath

        $exitCode | Should -Not -Be 0
    }

    It 'Fails when component version is below minVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentVersion '0.5.0' -ComponentAppKey 'test_app' -CompatibleArtifactMinVersion '1.0.0'

        $exitCode = Invoke-Validator -ValidatorPath $validatorPath

        $exitCode | Should -Not -Be 0
    }
}

Describe 'Check 11: Web.Shared binary identity comparison function' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')
    }

    It 'Passes when parent and HEAD hashes are identical' {
        $result = Compare-WebSharedBinaryIdentity -ParentHash 'a' -HeadHash 'a' -CascadeBumped $false

        $result.Result | Should -Be 'Pass'
    }

    It 'Fails when hashes differ and consumers were not cascade-bumped' {
        $result = Compare-WebSharedBinaryIdentity -ParentHash 'aaaa' -HeadHash 'bbbb' -CascadeBumped $false

        $result.Result | Should -Be 'Fail'
    }

    It 'Passes when hashes differ and consumers were cascade-bumped' {
        $result = Compare-WebSharedBinaryIdentity -ParentHash 'aaaa' -HeadHash 'bbbb' -CascadeBumped $true

        $result.Result | Should -Be 'Pass'
    }

    It 'Skips when a hash is missing' {
        $result = Compare-WebSharedBinaryIdentity -ParentHash '' -HeadHash 'bbbb' -CascadeBumped $false

        $result.Result | Should -Be 'Skip'
    }
}

Describe 'Local external-consumer overlay (omp-components.external.json)' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')

        $script:validOverlayJson = @'
{
  "sharedProjects": [
    {
      "projectPath": "SharedLib/SharedLib.csproj",
      "externalConsumers": [
        { "repositoryKey": "example-module", "componentKey": "example-module-web" }
      ]
    }
  ]
}
'@
        $script:unmatchedOverlayJson = @'
{
  "sharedProjects": [
    {
      "projectPath": "Missing/Missing.csproj",
      "externalConsumers": [
        { "repositoryKey": "example-module", "componentKey": "example-module-web" }
      ]
    }
  ]
}
'@
    }

    BeforeEach {
        $script:repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $script:testRepo = New-OverlayTestRepository -RootPath $script:repoRoot
    }

    AfterEach {
        Remove-TemporaryTestRepository -RootPath $script:repoRoot
    }

    It 'Passes without overlay warnings when no overlay file exists' {
        $result = Invoke-ValidatorWithOutput -ValidatorPath $script:testRepo.ValidatorPath -BaseCommit $script:testRepo.BaseCommit

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Not -Match 'external-consumer overlay'
        $result.Output | Should -Not -Match 'is consumed by'
    }

    It 'Merges a valid overlay, reports the merge count, and warns about the external consumer' {
        [System.IO.File]::WriteAllText($script:testRepo.OverlayPath, $script:validOverlayJson, [System.Text.Encoding]::UTF8)

        $result = Invoke-ValidatorWithOutput -ValidatorPath $script:testRepo.ValidatorPath -BaseCommit $script:testRepo.BaseCommit

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match 'overlay: 1 external consumers from .*omp-components\.external\.json'
        $result.Output | Should -Match "consumed by 'example-module-web' in the 'example-module' repository"
    }

    It 'Warns about an overlay entry whose projectPath matches no manifest shared project' {
        [System.IO.File]::WriteAllText($script:testRepo.OverlayPath, $script:unmatchedOverlayJson, [System.Text.Encoding]::UTF8)

        $result = Invoke-ValidatorWithOutput -ValidatorPath $script:testRepo.ValidatorPath -BaseCommit $script:testRepo.BaseCommit

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match "overlay .*omp-components\.external\.json.* 'Missing/Missing\.csproj'"
        $result.Output | Should -Match 'overlay: 0 external consumers from'
    }

    It 'Fails with -Strict when an overlay entry matches no manifest shared project' {
        [System.IO.File]::WriteAllText($script:testRepo.OverlayPath, $script:unmatchedOverlayJson, [System.Text.Encoding]::UTF8)

        $result = Invoke-ValidatorWithOutput -ValidatorPath $script:testRepo.ValidatorPath -BaseCommit $script:testRepo.BaseCommit -Strict

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match "'Missing/Missing\.csproj'"
    }

    It 'Warns when the overlay has no sharedProjects array' {
        [System.IO.File]::WriteAllText($script:testRepo.OverlayPath, '{ "sharedProject": [] }', [System.Text.Encoding]::UTF8)

        $result = Invoke-ValidatorWithOutput -ValidatorPath $script:testRepo.ValidatorPath -BaseCommit $script:testRepo.BaseCommit

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match "overlay .*omp-components\.external\.json.* has no 'sharedProjects' array"
    }

    It 'Says the sharedProjects array is empty rather than missing' {
        # A valid but empty list is a different mistake from a missing or
        # misspelled key; the message must not send the reader looking for a typo.
        [System.IO.File]::WriteAllText($script:testRepo.OverlayPath, '{ "sharedProjects": [] }', [System.Text.Encoding]::UTF8)

        $result = Invoke-ValidatorWithOutput -ValidatorPath $script:testRepo.ValidatorPath -BaseCommit $script:testRepo.BaseCommit

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match "overlay .*omp-components\.external\.json.* has an empty 'sharedProjects' array"
        $result.Output | Should -Not -Match "has no 'sharedProjects' array"
    }

    It 'Fails with the file name when the overlay is not valid JSON' {
        [System.IO.File]::WriteAllText($script:testRepo.OverlayPath, '{ "sharedProjects": [ ', [System.Text.Encoding]::UTF8)

        $result = Invoke-ValidatorWithOutput -ValidatorPath $script:testRepo.ValidatorPath -BaseCommit $script:testRepo.BaseCommit

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'omp-components\.external\.json'
    }
}

Describe 'Checks 14 and 15: finding the platform checkout from a consumer (Resolve-PlatformCheckScript)' {
    # A consumer validator that cannot find the OpenModulePlatform checkout
    # used to skip Checks 14 and 15 with a warning and exit 0 -- green for a
    # check that never ran. The shared resolver makes that a validation error
    # unless the caller set an explicit, named exception.
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')

        function Invoke-Resolver {
            param(
                [string]$RepositoryRoot,
                [string]$PlatformRepositoryRoot = '',
                [hashtable]$Environment = @{}
            )

            $names = @('OMP_PLATFORM_ROOT', 'OpenModulePlatformRoot', 'OMP_ALLOW_MISSING_PLATFORM')
            $saved = @{}
            foreach ($name in $names) {
                $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
                [Environment]::SetEnvironmentVariable($name, $null, 'Process')
            }
            foreach ($name in $Environment.Keys) {
                [Environment]::SetEnvironmentVariable($name, $Environment[$name], 'Process')
            }
            try {
                $errors = [System.Collections.Generic.List[string]]::new()
                $warnings = [System.Collections.Generic.List[string]]::new()
                $resolved = Resolve-PlatformCheckScript -RepositoryRoot $RepositoryRoot `
                    -ScriptRelativePath 'scripts/omp/validate-shared-scripts.ps1' -CheckLabel 'Check 15' `
                    -Errors $errors -Warnings $warnings -PlatformRepositoryRoot $PlatformRepositoryRoot
                return @{ Resolved = $resolved; Errors = @($errors); Warnings = @($warnings) }
            }
            finally {
                foreach ($name in $names) {
                    [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
                }
            }
        }
    }

    BeforeEach {
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-platform-resolve-' + [Guid]::NewGuid().ToString('N'))
        $consumer = Join-Path $root 'Consumer'
        $null = New-Item -ItemType Directory -Path $consumer -Force
    }

    AfterEach {
        if (Test-Path -LiteralPath $root -PathType Container) {
            Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Records an error that says how to set OMP_PLATFORM_ROOT when no checkout is found' {
        $result = Invoke-Resolver -RepositoryRoot $consumer
        $result.Resolved | Should -BeNullOrEmpty
        $result.Errors.Count | Should -Be 1
        $result.Errors[0] | Should -Match 'Check 15'
        $result.Errors[0] | Should -Match 'OMP_PLATFORM_ROOT'
        $result.Warnings.Count | Should -Be 0
    }

    It 'Records a warning, not an error, under the explicit OMP_ALLOW_MISSING_PLATFORM exception' {
        $result = Invoke-Resolver -RepositoryRoot $consumer -Environment @{ OMP_ALLOW_MISSING_PLATFORM = '1' }
        $result.Resolved | Should -BeNullOrEmpty
        $result.Errors.Count | Should -Be 0
        $result.Warnings.Count | Should -Be 1
        $result.Warnings[0] | Should -Match 'NOT VERIFIED'
        $result.Warnings[0] | Should -Match 'OMP_ALLOW_MISSING_PLATFORM'
    }

    It 'Still fails when OMP_PLATFORM_ROOT names a directory without the script, exception or not' {
        $wrong = Join-Path $root 'NotPlatform'
        $null = New-Item -ItemType Directory -Path $wrong -Force
        $result = Invoke-Resolver -RepositoryRoot $consumer -Environment @{ OMP_PLATFORM_ROOT = $wrong; OMP_ALLOW_MISSING_PLATFORM = '1' }
        $result.Resolved | Should -BeNullOrEmpty
        $result.Errors.Count | Should -Be 1
        $result.Errors[0] | Should -Match 'OMP_PLATFORM_ROOT'
    }

    It 'Resolves the sibling checkout and returns the script and the platform root' {
        $platform = Join-Path $root 'OpenModulePlatform'
        $null = New-Item -ItemType Directory -Path (Join-Path $platform 'scripts\omp') -Force
        [IO.File]::WriteAllText((Join-Path $platform 'scripts\omp\validate-shared-scripts.ps1'), 'guard')
        $result = Invoke-Resolver -RepositoryRoot $consumer
        $result.Errors.Count | Should -Be 0
        $result.Warnings.Count | Should -Be 0
        $result.Resolved.PlatformRoot | Should -Be $platform
        $result.Resolved.ScriptPath | Should -Be (Join-Path $platform 'scripts\omp\validate-shared-scripts.ps1')
    }

    It 'Prefers -PlatformRepositoryRoot, then OMP_PLATFORM_ROOT, and anchors a relative root at the repository' {
        $named = Join-Path $root 'Named'
        $null = New-Item -ItemType Directory -Path (Join-Path $named 'scripts\omp') -Force
        [IO.File]::WriteAllText((Join-Path $named 'scripts\omp\validate-shared-scripts.ps1'), 'guard')
        $viaEnvironment = Invoke-Resolver -RepositoryRoot $consumer -Environment @{ OMP_PLATFORM_ROOT = '..\Named' }
        $viaEnvironment.Errors.Count | Should -Be 0
        $viaEnvironment.Resolved.PlatformRoot | Should -Be $named

        $viaParameter = Invoke-Resolver -RepositoryRoot $consumer -PlatformRepositoryRoot $named -Environment @{ OMP_PLATFORM_ROOT = (Join-Path $root 'Elsewhere') }
        $viaParameter.Errors.Count | Should -Be 0
        $viaParameter.Resolved.PlatformRoot | Should -Be $named
    }
}
