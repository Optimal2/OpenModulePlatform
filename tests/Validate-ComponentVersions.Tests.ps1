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


Describe 'Check 8: module-definition SQL diff enforcement' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')
    }

    It 'Completes with its normal summary when a definition without sqlScripts is diffed against a base ref' {
        # Second opinion (2026-10-08): Check 8 read $definition.sqlScripts
        # directly, so under Set-StrictMode a legal definition WITHOUT
        # sqlScripts crashed the validator with PropertyNotFoundException --
        # only when a base ref made Check 8 run at all, which is why the
        # base-ref-less StrictMode fixture never saw it.
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $originalLocation = Get-Location
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot
            Set-Location -LiteralPath $repoRoot
            $baseCommit = (& git -C $repoRoot rev-parse HEAD).Trim()

            # Remove sqlScripts from the definition and bump definitionVersion
            # (Check 12 requires a bump for any definition content change).
            $definitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'
            $definition = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $definition.PSObject.Properties.Remove('sqlScripts')
            $definition.definitionVersion = '2.0.0'
            [System.IO.File]::WriteAllText($definitionPath, ($definition | ConvertTo-Json -Depth 10), [System.Text.Encoding]::UTF8)

            $manifestPath = Join-Path $repoRoot 'omp-components.json'
            $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $manifest.moduleDefinitions[0].definitionVersion = '2.0.0'
            [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10), [System.Text.Encoding]::UTF8)

            & git -C $repoRoot add -A
            if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
            & git -C $repoRoot commit -m 'Drop sqlScripts and bump definitionVersion' --quiet
            if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath -BaseCommit $baseCommit

            $result.ExitCode | Should -Be 0 -Because $result.Output
            $result.Output | Should -Match 'Component version validation passed'
            $result.Output | Should -Not -Match 'cannot be found on this object'
        }
        finally {
            Set-Location $originalLocation
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }
}

Describe 'Check 21: direct TimeZoneInfo IANA lookups stay inside *TimeZoneLookup.cs' {
    # Windows hosts without icu.dll (before Windows 10 1903 / Server 2019) run
    # .NET in NLS mode, where TimeZoneInfo.FindSystemTimeZoneById throws for
    # IANA ids. Production .cs files must go through a *TimeZoneLookup.cs
    # fallback instead of calling the platform APIs directly.
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')
    }

    It 'Fails when a production .cs file calls TimeZoneInfo.FindSystemTimeZoneById directly' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                'class Calendar { void M() { _ = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"); } }',
                [System.Text.Encoding]::UTF8)

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'TestApp[\\/]Calendar\.cs'
            $result.Output | Should -Match 'OmpTimeZoneLookup'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails when a production .cs file calls TimeZoneInfo.TryConvertIanaIdToWindowsId directly' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                'class Calendar { void M() { _ = TimeZoneInfo.TryConvertIanaIdToWindowsId("Europe/Stockholm", out var w); } }',
                [System.Text.Encoding]::UTF8)

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'TestApp[\\/]Calendar\.cs'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Passes when the direct calls sit in a *TimeZoneLookup.cs file' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\TestAppTimeZoneLookup.cs'),
                'class TestAppTimeZoneLookup { void M() { _ = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"); _ = TimeZoneInfo.TryConvertIanaIdToWindowsId("UTC", out var w); } }',
                [System.Text.Encoding]::UTF8)

            (Invoke-Validator -ValidatorPath $validatorPath) | Should -Be 0
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Passes when the direct call sits in a test project' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            $testDir = Join-Path $repoRoot 'TestApp.Tests'
            $null = New-Item -ItemType Directory -Path $testDir -Force
            [System.IO.File]::WriteAllText((Join-Path $testDir 'CalendarTests.cs'),
                'class CalendarTests { void M() { _ = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"); } }',
                [System.Text.Encoding]::UTF8)

            (Invoke-Validator -ValidatorPath $validatorPath) | Should -Be 0
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Passes when no production .cs file calls TimeZoneInfo directly' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'

            (Invoke-Validator -ValidatorPath $validatorPath) | Should -Be 0
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    # --- Review findings on the first Check 21 implementation: each fixture
    # below failed (or passed) the wrong way before the fix. ---

    It 'Fails on a bare call under using static System.TimeZoneInfo (F1)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                "using static System.TimeZoneInfo;`nclass Calendar { void M() { _ = FindSystemTimeZoneById(`"Europe/Stockholm`"); } }",
                [System.Text.Encoding]::UTF8)

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'TestApp[\\/]Calendar\.cs'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails on a call through a using alias for System.TimeZoneInfo (F1)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                "using TZ = System.TimeZoneInfo;`nclass Calendar { void M() { _ = TZ.FindSystemTimeZoneById(`"Europe/Stockholm`"); } }",
                [System.Text.Encoding]::UTF8)

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'TestApp[\\/]Calendar\.cs'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails when a newline sits between TimeZoneInfo and the member access (F1)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                "class Calendar { void M() { _ = TimeZoneInfo`r`n        .FindSystemTimeZoneById(`"Europe/Stockholm`"); } }",
                [System.Text.Encoding]::UTF8)

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'TestApp[\\/]Calendar\.cs'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails on a fully qualified System.TimeZoneInfo call (F1)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                'class Calendar { void M() { _ = System.TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"); } }',
                [System.Text.Encoding]::UTF8)

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'TestApp[\\/]Calendar\.cs'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Passes when the mention sits in comments and string literals only (F2)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                "// Never call TimeZoneInfo.FindSystemTimeZoneById(`"x`") here.`n/* TimeZoneInfo.TryConvertIanaIdToWindowsId(`"x`", out var w) is banned. */`nclass Calendar { string S = `"Use TimeZoneInfo.FindSystemTimeZoneById only inside OmpTimeZoneLookup`"; }",
                [System.Text.Encoding]::UTF8)

            (Invoke-Validator -ValidatorPath $validatorPath) | Should -Be 0
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails when the call sits in a directory whose name merely ends in test (F3)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            $latestDir = Join-Path $repoRoot 'TestApp\Latest'
            $null = New-Item -ItemType Directory -Path $latestDir -Force
            [System.IO.File]::WriteAllText((Join-Path $latestDir 'Calendar.cs'),
                'class Calendar { void M() { _ = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"); } }',
                [System.Text.Encoding]::UTF8)

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'Latest[\\/]Calendar\.cs'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Passes when the call sits in a test project detected by its csproj (F3)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            # Directory name says nothing about tests; the csproj does.
            $testProjectDir = Join-Path $repoRoot 'Verification'
            $null = New-Item -ItemType Directory -Path $testProjectDir -Force
            [System.IO.File]::WriteAllText((Join-Path $testProjectDir 'Verification.csproj'),
                "<Project Sdk=`"Microsoft.NET.Sdk`">`r`n  <PropertyGroup>`r`n    <TargetFramework>net8.0</TargetFramework>`r`n  </PropertyGroup>`r`n  <ItemGroup>`r`n    <PackageReference Include=`"Microsoft.NET.Test.Sdk`" Version=`"17.0.0`" />`r`n  </ItemGroup>`r`n</Project>`r`n",
                [System.Text.Encoding]::UTF8)
            [System.IO.File]::WriteAllText((Join-Path $testProjectDir 'CalendarTests.cs'),
                'class CalendarTests { void M() { _ = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"); } }',
                [System.Text.Encoding]::UTF8)

            (Invoke-Validator -ValidatorPath $validatorPath) | Should -Be 0
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails when a file merely carries TimeZoneLookup in its name without declaring the type (F4)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\NotATimeZoneLookup.cs'),
                'class Calendar { void M() { _ = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"); } }',
                [System.Text.Encoding]::UTF8)

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'NotATimeZoneLookup\.cs'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails on a direct TimeZoneInfo.TryFindSystemTimeZoneById call (G1)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                'class Calendar { void M() { _ = TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Stockholm", out var z); } }',
                [System.Text.Encoding]::UTF8)

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'TestApp[\\/]Calendar\.cs'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails on a method-group use without a call parenthesis (G2)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                'using System; class Calendar { void M() { Func<string, TimeZoneInfo> f = TimeZoneInfo.FindSystemTimeZoneById; } }',
                [System.Text.Encoding]::UTF8)

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'TestApp[\\/]Calendar\.cs'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    # --- Second opinion on the synced Check 21 (omp-tidszon-utan-icu-server2016
    # job 2, OMP 6cf3b3e0): each fixture below failed (or passed) the wrong way
    # before the masking and matching fixes. ---

    It 'Passes when the mention sits on a later line of a multi-line verbatim string (H1)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                'class Calendar { string S = @"line one' + "`r`n" + 'TimeZoneInfo.FindSystemTimeZoneById(""x"") is banned in prose"; }',
                [System.Text.Encoding]::UTF8)

            (Invoke-Validator -ValidatorPath $validatorPath) | Should -Be 0
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails on a real call after the closing quote of a multi-line verbatim string (H1)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                'class Calendar { string S = @"line one' + "`r`n" + 'line two"; void M() { _ = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"); } }',
                [System.Text.Encoding]::UTF8)

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'TestApp[\\/]Calendar\.cs'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails on a real call inside an interpolation hole (H2)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                'class Calendar { string M(string id) { return $"zone: {TimeZoneInfo.FindSystemTimeZoneById(id)}"; } }',
                [System.Text.Encoding]::UTF8)

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'TestApp[\\/]Calendar\.cs'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails on a call after a verbatim string that holds a single quote (H3)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                'class Holder { string S = @""""; }' + "`r`n" + 'class Calendar { void M() { _ = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"); } }',
                [System.Text.Encoding]::UTF8)

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'TestApp[\\/]Calendar\.cs'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails on a bare call when global using static sits in another file (H4)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\GlobalUsings.cs'),
                'global using static System.TimeZoneInfo;',
                [System.Text.Encoding]::UTF8)
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                'class Calendar { void M() { _ = FindSystemTimeZoneById("Europe/Stockholm"); } }',
                [System.Text.Encoding]::UTF8)

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'TestApp[\\/]Calendar\.cs'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails on an alias call when the global using alias sits in another file (H4)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\GlobalUsings.cs'),
                'global using TZ = System.TimeZoneInfo;',
                [System.Text.Encoding]::UTF8)
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                'class Calendar { void M() { _ = TZ.FindSystemTimeZoneById("Europe/Stockholm"); } }',
                [System.Text.Encoding]::UTF8)

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'TestApp[\\/]Calendar\.cs'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Passes on nameof(TimeZoneInfo.FindSystemTimeZoneById) (H4)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                'class Calendar { string S = nameof(TimeZoneInfo.FindSystemTimeZoneById); }',
                [System.Text.Encoding]::UTF8)

            (Invoke-Validator -ValidatorPath $validatorPath) | Should -Be 0
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Passes on a bare nameof when global using static sits in another file (H4)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -ComponentMinVersion '1.0.0' -ModuleDefinitionVersion '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\GlobalUsings.cs'),
                'global using static System.TimeZoneInfo;',
                [System.Text.Encoding]::UTF8)
            [System.IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp\Calendar.cs'),
                'class Calendar { string S = nameof(FindSystemTimeZoneById); }',
                [System.Text.Encoding]::UTF8)

            (Invoke-Validator -ValidatorPath $validatorPath) | Should -Be 0
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Get-CSharpTestProjectDirectory throws an honest error for a missing repository root (H5)' {
        $missingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('missing-' + [Guid]::NewGuid().ToString('N'))
        { Get-CSharpTestProjectDirectory -RepositoryRoot $missingRoot } | Should -Throw '*does not exist*'
    }
}
