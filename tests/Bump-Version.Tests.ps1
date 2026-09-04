# Pester 5's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (5.9.1), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 5 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
Pester tests for scripts/omp/bump-version.ps1.

.DESCRIPTION
These tests verify that bumping a component also updates the matching
compatibleArtifacts.maxVersion entry in the module definition. The
2026-08-18 IbsPackager import failure showed that leaving this step
manual lets the artifact version cap drift behind the component version,
so the host rejects the produced artifact at import time.

Pester 5 runs every container in a separate session state, so the shared
harness (script path + temp-repo helpers) lives in
Bump-Version.TestHelpers.ps1 and is dot-sourced from each Describe block's
BeforeAll.
#>

Describe 'Bump-Version updates compatibleArtifacts.maxVersion' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Bump-Version.TestHelpers.ps1')
    }

    It 'Sets maxVersion from null to the bumped component version' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $bumpScriptPath = New-TemporaryBumpRepository -RootPath $repoRoot -ComponentVersion '1.0.0'

            $exitCode = Invoke-BumpVersion -BumpScriptPath $bumpScriptPath

            $exitCode | Should -Be 0

            $moduleDefinitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'
            $moduleDefinition = Get-Content -LiteralPath $moduleDefinitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $moduleDefinition.compatibleArtifacts[0].maxVersion | Should -Be '1.0.1'
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

            $exitCode | Should -Be 0

            $moduleDefinitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'
            $moduleDefinition = Get-Content -LiteralPath $moduleDefinitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $moduleDefinition.compatibleArtifacts[0].maxVersion | Should -Be '1.0.8'
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

            $exitCode | Should -Be 0

            # Discriminating assertion: the component bump itself must have run.
            # Without this, 'maxVersion still 2.0.0' would also pass if the new
            # sync logic never executed at all.
            $manifestPath = Join-Path $repoRoot 'omp-components.json'
            $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $manifest.components[0].version | Should -Be '1.0.6'

            $moduleDefinitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'
            $moduleDefinition = Get-Content -LiteralPath $moduleDefinitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $moduleDefinition.compatibleArtifacts[0].maxVersion | Should -Be '2.0.0'
        }
        finally {
            Remove-TemporaryBumpRepository -RootPath $repoRoot
        }
    }

    It 'Throws and leaves both files unmodified when maxVersion is non-numeric' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $bumpScriptPath = New-TemporaryBumpRepository -RootPath $repoRoot -ComponentVersion '1.0.0' -CompatibleArtifactMaxVersion 'latest'

            $manifestPath = Join-Path $repoRoot 'omp-components.json'
            $moduleDefinitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'
            $manifestBefore = [System.IO.File]::ReadAllText($manifestPath)
            $moduleDefinitionBefore = [System.IO.File]::ReadAllText($moduleDefinitionPath)

            # bump-version.ps1 ends with 'exit 1' on failure, which would
            # terminate the Pester host, so the failure path must run in a
            # child process. The file-level $ErrorActionPreference = 'Stop'
            # would turn the child's redirected stderr into a throwing
            # ErrorRecord, so relax it locally for the capture.
            $previousErrorActionPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $output = & powershell.exe -NoProfile -File $bumpScriptPath -ComponentKey 'test_app' 2>&1 | Out-String
                $exitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $previousErrorActionPreference
            }

            $exitCode | Should -Be 1
            $output | Should -Match 'non-numeric maxVersion'

            # Atomic abort: the throw must happen before any file is written.
            [System.IO.File]::ReadAllText($manifestPath) | Should -Be $manifestBefore
            [System.IO.File]::ReadAllText($moduleDefinitionPath) | Should -Be $moduleDefinitionBefore
        }
        finally {
            Remove-TemporaryBumpRepository -RootPath $repoRoot
        }
    }
}

Describe 'Bump-Version: repository-only bump' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Bump-Version.TestHelpers.ps1')
    }

    <#
        omp-components.json has no Bootstrapper component, and the installer
        package takes its identity from repositoryVersion
        (package-hostagent-first.ps1). Until 2026-09-02 bump-version.ps1 could
        not raise repositoryVersion without also selecting a component, module
        or widget target -- so a Bootstrapper-only change had no canonical way to
        get a new package identity, and the choice was between hand-editing the
        manifest or bumping an unrelated artifact.
    #>

    It 'Raises repositoryVersion without touching any component' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $bumpScriptPath = New-TemporaryBumpRepository -RootPath $repoRoot -ComponentVersion '1.0.0'
            $manifestPath = Join-Path $repoRoot 'omp-components.json'
            $before = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $componentBefore = $before.components[0].version

            $null = & $bumpScriptPath -RepositoryOnly 2>&1 | Out-String

            $after = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            ([System.Version]$after.repositoryVersion -gt [System.Version]$before.repositoryVersion) | Should -Be $true
            # The whole point: no unrelated artifact is dragged along.
            $after.components[0].version | Should -Be $componentBefore
        }
        finally {
            Remove-TemporaryBumpRepository -RootPath $repoRoot
        }
    }

    It 'Refuses to combine -RepositoryOnly with a component target' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $bumpScriptPath = New-TemporaryBumpRepository -RootPath $repoRoot -ComponentVersion '1.0.0'

            # The refusal path ends in 'throw', so run it in a child process:
            # in-process the ambient $LASTEXITCODE leaks between Pester
            # containers (the engine updates it process-wide) and the
            # assertion would measure whichever unrelated test ran last.
            $previousErrorActionPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $output = & powershell.exe -NoProfile -File $bumpScriptPath -RepositoryOnly -ComponentKey 'test_app' 2>&1 | Out-String
                $exitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $previousErrorActionPreference
            }

            # Windows PowerShell wraps the error record to the console width when
            # no console is attached (git hooks), which can split the word; match
            # with whitespace removed.
            ($output -replace '\s', '') | Should -Match 'RepositoryOnly'
            $exitCode | Should -Be 1
        }
        finally {
            Remove-TemporaryBumpRepository -RootPath $repoRoot
        }
    }

    It 'Refuses to combine -RepositoryOnly with -SkipRepositoryVersion' {
        # The two flags ask for opposite things; obeying either silently would be
        # worse than refusing.
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $bumpScriptPath = New-TemporaryBumpRepository -RootPath $repoRoot -ComponentVersion '1.0.0'

            $previousErrorActionPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $output = & powershell.exe -NoProfile -File $bumpScriptPath -RepositoryOnly -SkipRepositoryVersion 2>&1 | Out-String
                $exitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $previousErrorActionPreference
            }

            # Windows PowerShell wraps the error record to the console width when
            # no console is attached (git hooks), which can split the word; match
            # with whitespace removed.
            ($output -replace '\s', '') | Should -Match 'RepositoryOnly'
            $exitCode | Should -Be 1
        }
        finally {
            Remove-TemporaryBumpRepository -RootPath $repoRoot
        }
    }
}

Describe 'Bump-Version: a rewritten module definition always carries a version bump' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Bump-Version.TestHelpers.ps1')
    }

    <#
        The persist loop wrote EVERY loaded module definition unconditionally,
        while the follow-up definitionVersion bump only covered definitions whose
        compatibleArtifacts.maxVersion actually changed. A definition that was
        merely formatted differently on disk -- hand-edited, or written by another
        generator -- was therefore rewritten by Save-JsonFile while its
        definitionVersion stayed put. HostAgent rejects a re-imported definition
        that carries the same definitionVersion with different content, so the
        validator goes red and the operator has to discover a second command
        nothing mentions.

        Reproduced 2026-08-28 in iKrock2 under Windows PowerShell 5.1; pinned here
        so it cannot come back.
    #>

    It 'Does not rewrite a differently formatted definition without bumping definitionVersion' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            # maxVersion is set high enough that the component bump does NOT
            # exceed it, so shouldUpdate stays false and the definition is not
            # semantically touched.
            $bumpScriptPath = New-TemporaryBumpRepository -RootPath $repoRoot -ComponentVersion '1.0.0' -CompatibleArtifactMaxVersion '9.9.9'
            $definitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'

            # Reformat the definition on disk with four-space indentation, which
            # is valid JSON but not the shape Format-JsonText produces.
            $definition = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $reformatted = $definition | ConvertTo-Json -Depth 50
            [System.IO.File]::WriteAllText($definitionPath, $reformatted, [System.Text.UTF8Encoding]::new($false))

            $before = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8
            $versionBefore = ($before | ConvertFrom-Json).definitionVersion

            $null = & $bumpScriptPath -ComponentKey 'test_app' 2>&1 | Out-String

            $after = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8
            $versionAfter = ($after | ConvertFrom-Json).definitionVersion

            if ($after -ne $before) {
                # The file changed, so definitionVersion MUST have changed too.
                # This is the invariant the validator enforces.
                ($versionAfter -ne $versionBefore) | Should -Be $true
            }
            else {
                # Or the file was left alone entirely, which is equally fine.
                $versionAfter | Should -Be $versionBefore
            }
        }
        finally {
            Remove-TemporaryBumpRepository -RootPath $repoRoot
        }
    }

    It 'Normalises a differently formatted definition AND bumps it, never one without the other' {
        # The chosen fix is option (b): if the file will be written with different
        # bytes, the module counts as touched and gets its definitionVersion
        # bumped. Option (a) -- skip the write when the output equals the file --
        # would have left a hand-formatted file untouched, which only closes the
        # case where the file was already canonical. (b) makes the invariant
        # structural instead: different bytes on disk implies a bump.
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $bumpScriptPath = New-TemporaryBumpRepository -RootPath $repoRoot -ComponentVersion '1.0.0' -CompatibleArtifactMaxVersion '9.9.9'
            $definitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'

            $definition = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $reformatted = $definition | ConvertTo-Json -Depth 50
            [System.IO.File]::WriteAllText($definitionPath, $reformatted, [System.Text.UTF8Encoding]::new($false))
            $before = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8
            $versionBefore = ($before | ConvertFrom-Json).definitionVersion

            $null = & $bumpScriptPath -ComponentKey 'test_app' 2>&1 | Out-String

            $after = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8
            ($after -ne $before) | Should -Be $true
            ($after | ConvertFrom-Json).definitionVersion | Should -Not -Be $versionBefore
        }
        finally {
            Remove-TemporaryBumpRepository -RootPath $repoRoot
        }
    }

    It 'Does not touch a definition that is already canonically formatted and unchanged' {
        # The other half of the same rule: no gratuitous writes. A definition that
        # already holds exactly the bytes the script would produce, and that has no
        # semantic change, must be left alone entirely -- otherwise every bump
        # produces diffs nobody asked for, and the follow-up bump above would fire
        # for no reason.
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $bumpScriptPath = New-TemporaryBumpRepository -RootPath $repoRoot -ComponentVersion '1.0.0' -CompatibleArtifactMaxVersion '9.9.9'
            $definitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'

            # First run canonicalises the file and bumps it.
            $null = & $bumpScriptPath -ComponentKey 'test_app' 2>&1 | Out-String
            $afterFirst = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8

            # Second run has nothing to do to the definition.
            $null = & $bumpScriptPath -ComponentKey 'test_app' 2>&1 | Out-String
            $afterSecond = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8

            $afterSecond | Should -Be $afterFirst
        }
        finally {
            Remove-TemporaryBumpRepository -RootPath $repoRoot
        }
    }
}

Describe 'Bump-Version: repeated -ModuleKey does not double-bump' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Bump-Version.TestHelpers.ps1')
    }

    It 'Bumps a module exactly once even when its key is passed twice' {
        # '-ModuleKey foo,foo' used to select the same definition twice and bump it
        # twice in one run, producing a version nobody can explain from the command
        # that was typed.
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $bumpScriptPath = New-TemporaryBumpRepository -RootPath $repoRoot -ComponentVersion '1.0.0' -CompatibleArtifactMaxVersion '9.9.9'
            $definitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'
            $before = (Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8 | ConvertFrom-Json).definitionVersion

            $null = & $bumpScriptPath -ModuleKey 'test_module','test_module' -SkipRepositoryVersion 2>&1 | Out-String

            $after = (Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8 | ConvertFrom-Json).definitionVersion
            # One bump, not two: 1.0.0 -> 1.0.1, never 1.0.2.
            $after | Should -Be ([string]([System.Version]::Parse($before).Major.ToString() + '.' + [System.Version]::Parse($before).Minor.ToString() + '.' + ([System.Version]::Parse($before).Build + 1).ToString()))
        }
        finally {
            Remove-TemporaryBumpRepository -RootPath $repoRoot
        }
    }
}

Describe 'Bump-Version: single write and BOM handling' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Bump-Version.TestHelpers.ps1')
    }

    It 'Writes the maxVersion change and the definitionVersion bump in the same file state' {
        # The persist loop used to write the definition with the OLD
        # definitionVersion, and the definitionVersion loop then RELOADED the file
        # from disk and wrote it again. An interrupted run therefore left the file
        # in exactly the half-bumped state the follow-up bump exists to prevent.
        # The definition is now written once, from the in-memory object, so both
        # changes land together -- which is observable: if the reload were still
        # in place, the maxVersion change would be lost.
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            # maxVersion low enough that the component bump exceeds it, so the
            # definition IS semantically touched.
            $bumpScriptPath = New-TemporaryBumpRepository -RootPath $repoRoot -ComponentVersion '1.0.0' -CompatibleArtifactMaxVersion '1.0.0'
            $definitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'
            $before = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8 | ConvertFrom-Json

            $null = & $bumpScriptPath -ComponentKey 'test_app' 2>&1 | Out-String

            $after = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            # Both changes are present in the same file.
            $after.compatibleArtifacts[0].maxVersion | Should -Be '1.0.1'
            ($after.definitionVersion -ne $before.definitionVersion) | Should -Be $true
        }
        finally {
            Remove-TemporaryBumpRepository -RootPath $repoRoot
        }
    }

    It 'Leaves a byte-order mark intact on a definition it has no reason to write' {
        # Save-JsonFile always writes WITHOUT a BOM, so a definition that has one
        # would lose it on the first write and produce a diff nobody asked for.
        # The would-change comparison reads the file with BOM detection, so a
        # BOM-carrying file that is otherwise canonical is simply not written --
        # and if it ever IS written, the follow-up bump now makes that visible
        # rather than silent.
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $bumpScriptPath = New-TemporaryBumpRepository -RootPath $repoRoot -ComponentVersion '1.0.0' -CompatibleArtifactMaxVersion '9.9.9'
            $definitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'

            # First run canonicalises the content.
            $null = & $bumpScriptPath -ComponentKey 'test_app' 2>&1 | Out-String

            # Re-write the same content WITH a BOM.
            $canonical = [System.IO.File]::ReadAllText($definitionPath)
            [System.IO.File]::WriteAllText($definitionPath, $canonical, [System.Text.UTF8Encoding]::new($true))
            $bytesBefore = [System.IO.File]::ReadAllBytes($definitionPath)

            $null = & $bumpScriptPath -ComponentKey 'test_app' 2>&1 | Out-String

            $bytesAfter = [System.IO.File]::ReadAllBytes($definitionPath)
            ($bytesAfter[0] -eq 0xEF -and $bytesAfter[1] -eq 0xBB -and $bytesAfter[2] -eq 0xBF) | Should -Be $true
            $bytesAfter.Length | Should -Be $bytesBefore.Length
        }
        finally {
            Remove-TemporaryBumpRepository -RootPath $repoRoot
        }
    }
}
