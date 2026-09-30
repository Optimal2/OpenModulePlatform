# Pester 6's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (6.1.0), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
Pester tests for the clean guard in front of every "delete a root and create it
again" in the platform scripts.

.DESCRIPTION
The tracked file artifacts/README.md was once deleted while a consumer
repository's CI ran: publish-all.ps1 -CleanOutput,
merge-universal-package-objects.ps1 and test-deterministic-web-publish.ps1 all
removed the folder they were given recursively, with nothing checking what the
folder held. Each suite below points one script at the artifacts folder of a
throwaway git repository whose artifacts/README.md is tracked, and requires
that the script refuses and the file survives.

A stub dotnet.cmd that exits 1 is put first on PATH so a script that does NOT
refuse (the state before the guard) fails fast instead of publishing fifteen
projects into the throwaway repository.
#>

Describe 'Clean guard: scripts refuse to delete a folder with tracked files' {
    BeforeAll {
        $script:repositoryRoot = Split-Path -Parent $PSScriptRoot
        $script:workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-clean-guard-' + [Guid]::NewGuid().ToString('N'))
        $script:tempRepo = Join-Path $script:workRoot 'repo'
        [void](New-Item -ItemType Directory -Path (Join-Path $script:tempRepo 'artifacts') -Force)
        Set-Content -LiteralPath (Join-Path $script:tempRepo 'artifacts\README.md') -Value 'tracked' -Encoding UTF8
        & git -C $script:tempRepo init --quiet
        & git -C $script:tempRepo add -- 'artifacts/README.md'
        & git -C $script:tempRepo -c user.name=omp-test -c user.email=omp-test@example.invalid commit --quiet -m 'tracked artifacts readme'
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the throwaway git repository.' }
        $script:trackedArtifacts = Join-Path $script:tempRepo 'artifacts'
        $script:trackedFile = Join-Path $script:trackedArtifacts 'README.md'

        $stubBin = Join-Path $script:workRoot 'stub-bin'
        [void](New-Item -ItemType Directory -Path $stubBin -Force)
        Set-Content -LiteralPath (Join-Path $stubBin 'dotnet.cmd') -Value '@exit /b 1' -Encoding ASCII
        $script:previousPath = $env:PATH
        $env:PATH = $stubBin + [System.IO.Path]::PathSeparator + $env:PATH

        $script:emptyPackageRoot = Join-Path $script:workRoot 'packages'
        [void](New-Item -ItemType Directory -Path $script:emptyPackageRoot -Force)
        $script:anyProject = Join-Path $script:repositoryRoot 'OpenModulePlatform.EventPublisher.Abstractions\OpenModulePlatform.EventPublisher.Abstractions.csproj'
    }

    AfterAll {
        $env:PATH = $script:previousPath
        if (Test-Path -LiteralPath $script:workRoot) {
            Remove-Item -LiteralPath $script:workRoot -Recurse -Force
        }
    }

    It 'publish-all.ps1 -CleanOutput refuses and keeps the tracked file' {
        $publishAll = Join-Path $script:repositoryRoot 'publish-all.ps1'
        { & $publishAll -Root $script:repositoryRoot -OutputRoot $script:trackedArtifacts -CleanOutput } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
        Test-Path -LiteralPath $script:trackedFile | Should -BeTrue
    }

    It 'merge-universal-package-objects.ps1 refuses and keeps the tracked file' {
        $merge = Join-Path $script:repositoryRoot 'scripts\omp\merge-universal-package-objects.ps1'
        { & $merge -PackageRoot $script:emptyPackageRoot -OutputRoot $script:trackedArtifacts } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
        Test-Path -LiteralPath $script:trackedFile | Should -BeTrue
    }

    It 'test-deterministic-web-publish.ps1 refuses and keeps the tracked file' {
        $deterministic = Join-Path $script:repositoryRoot 'scripts\dev\test-deterministic-web-publish.ps1'
        { & $deterministic -ProjectPath $script:anyProject -WorkRoot $script:trackedArtifacts } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
        Test-Path -LiteralPath $script:trackedFile | Should -BeTrue
    }
}

Describe 'Clean guard: Assert-SafeToClean rules' {
    BeforeAll {
        $script:repositoryRoot = Split-Path -Parent $PSScriptRoot
        . (Join-Path $script:repositoryRoot 'scripts\omp\Assert-SafeToClean.ps1')

        $script:workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-clean-rules-' + [Guid]::NewGuid().ToString('N'))
        $script:foreignRepo = Join-Path $script:workRoot 'foreign'
        [void](New-Item -ItemType Directory -Path (Join-Path $script:foreignRepo 'src') -Force)
        Set-Content -LiteralPath (Join-Path $script:foreignRepo '.gitignore') -Value 'out/' -Encoding ASCII
        Set-Content -LiteralPath (Join-Path $script:foreignRepo 'src\keep.txt') -Value 'tracked' -Encoding ASCII
        & git -C $script:foreignRepo init --quiet
        & git -C $script:foreignRepo add -- '.gitignore' 'src/keep.txt'
        & git -C $script:foreignRepo -c user.name=omp-test -c user.email=omp-test@example.invalid commit --quiet -m 'foreign'
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the foreign git repository.' }
    }

    AfterAll {
        if (Test-Path -LiteralPath $script:workRoot) {
            Remove-Item -LiteralPath $script:workRoot -Recurse -Force
        }
    }

    It 'Refuses the repository root of this repository' {
        { Assert-SafeToClean -Path $script:repositoryRoot -RepositoryRoot $script:repositoryRoot } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
    }

    It 'Refuses a folder that contains this repository' {
        $parent = Split-Path -Parent $script:repositoryRoot
        { Assert-SafeToClean -Path $parent -RepositoryRoot $script:repositoryRoot } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
    }

    It 'Refuses a folder of this repository that holds tracked files' {
        { Assert-SafeToClean -Path (Join-Path $script:repositoryRoot 'scripts') -RepositoryRoot $script:repositoryRoot } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
    }

    It 'Allows an ignored output folder of this repository' {
        { Assert-SafeToClean -Path (Join-Path $script:repositoryRoot 'artifacts\publish') -RepositoryRoot $script:repositoryRoot } |
            Should -Not -Throw
    }

    It 'Allows a folder outside every git repository' {
        { Assert-SafeToClean -Path (Join-Path $script:workRoot 'plain\output') -RepositoryRoot $script:repositoryRoot } |
            Should -Not -Throw
    }

    It 'Refuses a folder of another repository that is not ignored there' {
        { Assert-SafeToClean -Path (Join-Path $script:foreignRepo 'scratch') -RepositoryRoot $script:repositoryRoot } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
    }

    It 'Refuses a folder of another repository that holds tracked files' {
        { Assert-SafeToClean -Path (Join-Path $script:foreignRepo 'src') -RepositoryRoot $script:repositoryRoot } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
    }

    It 'Refuses the root of another repository' {
        { Assert-SafeToClean -Path $script:foreignRepo -RepositoryRoot $script:repositoryRoot } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
    }

    It 'Allows an ignored output folder of another repository (a consumer passing its own output root)' {
        { Assert-SafeToClean -Path (Join-Path $script:foreignRepo 'out\publish') -RepositoryRoot $script:repositoryRoot } |
            Should -Not -Throw
    }
}
