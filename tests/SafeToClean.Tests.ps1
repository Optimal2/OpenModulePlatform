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
        $script:packageVersion = '9.9.9-clean-guard'
        $trackedPackage = Join-Path $script:tempRepo ('OpenModulePlatformHostAgentFirst-' + $script:packageVersion)
        [void](New-Item -ItemType Directory -Path $trackedPackage -Force)
        Set-Content -LiteralPath (Join-Path $trackedPackage 'README.md') -Value 'tracked' -Encoding UTF8
        $script:trackedPackageFile = Join-Path $trackedPackage 'README.md'
        & git -C $script:tempRepo init --quiet
        & git -C $script:tempRepo add -- 'artifacts/README.md' ('OpenModulePlatformHostAgentFirst-' + $script:packageVersion + '/README.md')
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

    It 'package-hostagent-first.ps1 refuses to replace a package folder that holds tracked files' {
        # The script deletes <OutputRoot>\OpenModulePlatformHostAgentFirst-<Version>
        # before it publishes anything; a missing -ConfigPath means defaults.
        $package = Join-Path $script:repositoryRoot 'scripts\deployment\package-hostagent-first.ps1'
        $noConfig = Join-Path $script:workRoot 'no-such-config.psd1'
        { & $package -ConfigPath $noConfig -RepositoryRoot $script:repositoryRoot -OutputRoot $script:tempRepo -Version $script:packageVersion -SkipZip } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
        Test-Path -LiteralPath $script:trackedPackageFile | Should -BeTrue
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

Describe 'Clean guard: path aliases, nested repositories and links' {
    # git resolves junctions and 8.3 short names to the final long path, while
    # the caller's path keeps the form it was given in. The guard must reach the
    # same verdict whichever form the folder is named in: AI Orchestrator
    # instances reach the repository through a junction, and %TEMP% is often an
    # 8.3 path. Every junction below points into this suite's own throwaway
    # folder, never at a real repository, and is removed as a link before the
    # folder is deleted.
    BeforeAll {
        $script:repositoryRoot = Split-Path -Parent $PSScriptRoot
        . (Join-Path $script:repositoryRoot 'scripts\omp\Assert-SafeToClean.ps1')

        $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-clean-alias-' + [Guid]::NewGuid().ToString('N'))
        [void](New-Item -ItemType Directory -Path $tempRoot -Force)
        $script:workRoot = (Get-Item -LiteralPath $tempRoot).FullName

        # A repository under a parent whose name has a generated 8.3 alias.
        $script:longParent = Join-Path $script:workRoot 'long parent folder name'
        $script:repo = Join-Path $script:longParent 'repo'
        [void](New-Item -ItemType Directory -Path (Join-Path $script:repo 'artifacts') -Force)
        Set-Content -LiteralPath (Join-Path $script:repo '.gitignore') -Value 'out/' -Encoding ASCII
        Set-Content -LiteralPath (Join-Path $script:repo 'artifacts\README.md') -Value 'tracked' -Encoding ASCII
        & git -C $script:repo init --quiet
        & git -C $script:repo add -- '.gitignore' 'artifacts/README.md'
        & git -C $script:repo -c user.name=omp-test -c user.email=omp-test@example.invalid commit --quiet -m 'alias repo'
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the throwaway git repository.' }
        $script:trackedFile = Join-Path $script:repo 'artifacts\README.md'

        # Junction aliases of that repository: one longer and one shorter than
        # the resolved path, the two shapes that broke the string comparison.
        $script:links = New-Object System.Collections.Generic.List[string]
        $script:longAlias = Join-Path $script:workRoot 'a-junction-alias-that-is-longer-than-the-real-path'
        $script:shortAlias = Join-Path $script:workRoot 'r'
        foreach ($alias in @($script:longAlias, $script:shortAlias)) {
            [void](New-Item -ItemType Junction -Path $alias -Value $script:repo)
            $script:links.Add($alias)
        }

        # A folder outside every repository that holds another repository.
        $script:holdsRepo = Join-Path $script:workRoot 'holds-repo'
        $nested = Join-Path $script:holdsRepo 'nested\foreign'
        [void](New-Item -ItemType Directory -Path $nested -Force)
        & git -C $nested init --quiet

        # A folder outside every repository that holds a worktree-style .git file.
        $script:holdsGitFile = Join-Path $script:workRoot 'holds-git-file'
        [void](New-Item -ItemType Directory -Path (Join-Path $script:holdsGitFile 'wt') -Force)
        Set-Content -LiteralPath (Join-Path $script:holdsGitFile 'wt\.git') -Value 'gitdir: C:/nowhere/.git/worktrees/wt' -Encoding ASCII

        # A folder outside every repository that holds a junction; Windows
        # PowerShell 5.1 Remove-Item -Recurse deletes what a junction points at.
        $script:linkTarget = Join-Path $script:workRoot 'link-target'
        [void](New-Item -ItemType Directory -Path $script:linkTarget -Force)
        Set-Content -LiteralPath (Join-Path $script:linkTarget 'precious.txt') -Value 'keep' -Encoding ASCII
        $script:holdsJunction = Join-Path $script:workRoot 'holds-junction'
        [void](New-Item -ItemType Directory -Path (Join-Path $script:holdsJunction 'a\b') -Force)
        $innerLink = Join-Path $script:holdsJunction 'a\b\link'
        [void](New-Item -ItemType Junction -Path $innerLink -Value $script:linkTarget)
        $script:links.Add($innerLink)

        # A folder that is itself a junction.
        $script:junctionTarget = Join-Path $script:workRoot 'is-junction'
        [void](New-Item -ItemType Junction -Path $script:junctionTarget -Value $script:linkTarget)
        $script:links.Add($script:junctionTarget)

        # Plain generated output outside every repository.
        $script:plainOutput = Join-Path $script:workRoot 'plain-output'
        [void](New-Item -ItemType Directory -Path (Join-Path $script:plainOutput 'app\wwwroot\lib') -Force)
        Set-Content -LiteralPath (Join-Path $script:plainOutput 'app\wwwroot\lib\site.js') -Value '//' -Encoding ASCII

        # The 8.3 alias of the long parent, when the volume generates them.
        $script:shortParent = (New-Object -ComObject Scripting.FileSystemObject).GetFolder($script:longParent).ShortPath
    }

    AfterAll {
        # Links first, as links: a recursive delete must never walk into them.
        foreach ($link in $script:links) {
            if (Test-Path -LiteralPath $link) {
                [System.IO.Directory]::Delete($link)
            }
        }
        if (Test-Path -LiteralPath $script:workRoot) {
            Remove-Item -LiteralPath $script:workRoot -Recurse -Force
        }
    }

    It 'Refuses tracked files reached through a junction alias longer than the real path' {
        { Assert-SafeToClean -Path (Join-Path $script:longAlias 'artifacts') -RepositoryRoot $script:repo } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
        Test-Path -LiteralPath $script:trackedFile | Should -BeTrue
    }

    It 'Refuses tracked files reached through a junction alias shorter than the real path' {
        { Assert-SafeToClean -Path (Join-Path $script:shortAlias 'artifacts') -RepositoryRoot $script:repo } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
    }

    It 'Refuses the repository root reached through a junction alias' {
        { Assert-SafeToClean -Path $script:longAlias -RepositoryRoot $script:repositoryRoot } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
    }

    It 'Refuses tracked files reached through an 8.3 short name' {
        if ([string]::Equals($script:shortParent, $script:longParent, [StringComparison]::OrdinalIgnoreCase)) {
            Set-ItResult -Skipped -Because "the volume of '$script:longParent' does not generate 8.3 names (fsutil 8dot3name query)"
            return
        }
        { Assert-SafeToClean -Path (Join-Path $script:shortParent 'repo\artifacts') -RepositoryRoot $script:repo } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
    }

    It 'Allows an ignored output folder of another repository reached through a junction alias' {
        { Assert-SafeToClean -Path (Join-Path $script:longAlias 'out\publish') -RepositoryRoot $script:repositoryRoot } |
            Should -Not -Throw
        { Assert-SafeToClean -Path (Join-Path $script:shortAlias 'out\publish') -RepositoryRoot $script:repositoryRoot } |
            Should -Not -Throw
    }

    It 'Allows an ignored output folder of another repository reached through an 8.3 short name' {
        if ([string]::Equals($script:shortParent, $script:longParent, [StringComparison]::OrdinalIgnoreCase)) {
            Set-ItResult -Skipped -Because "the volume of '$script:longParent' does not generate 8.3 names (fsutil 8dot3name query)"
            return
        }
        { Assert-SafeToClean -Path (Join-Path $script:shortParent 'repo\out\publish') -RepositoryRoot $script:repositoryRoot } |
            Should -Not -Throw
    }

    It 'Refuses a missing folder of another repository that is not ignored there, reached through a junction alias' {
        { Assert-SafeToClean -Path (Join-Path $script:longAlias 'scratch\new') -RepositoryRoot $script:repositoryRoot } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
    }

    It 'Refuses a folder outside every repository that holds another repository' {
        { Assert-SafeToClean -Path $script:holdsRepo -RepositoryRoot $script:repositoryRoot } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
    }

    It 'Refuses a folder outside every repository that holds a worktree .git file' {
        { Assert-SafeToClean -Path $script:holdsGitFile -RepositoryRoot $script:repositoryRoot } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
    }

    It 'Refuses a folder that holds a junction, and leaves what the junction points at alone' {
        { Assert-SafeToClean -Path $script:holdsJunction -RepositoryRoot $script:repositoryRoot } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
        Test-Path -LiteralPath (Join-Path $script:linkTarget 'precious.txt') | Should -BeTrue
    }

    It 'Refuses a folder that is itself a junction' {
        { Assert-SafeToClean -Path $script:junctionTarget -RepositoryRoot $script:repositoryRoot } |
            Should -Throw -ExpectedMessage '*Refusing to clean*'
    }

    It 'Allows existing generated output outside every repository' {
        { Assert-SafeToClean -Path $script:plainOutput -RepositoryRoot $script:repositoryRoot } |
            Should -Not -Throw
    }
}
