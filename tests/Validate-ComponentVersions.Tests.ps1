# Pester 5's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (5.9.1), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 5 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
Pester tests for scripts/omp/validate-component-versions.ps1.

.DESCRIPTION
These tests validate the minModuleDefinitionVersion enforcement logic
introduced in Check 6 and Check 8b. Each test runs the validator inside
an isolated temporary git repository so that git-based diff checks can
be exercised without touching the OpenModulePlatform repository state.

Pester 5 runs every container in a separate session state, so the shared
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
