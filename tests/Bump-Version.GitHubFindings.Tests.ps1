# Pester 6's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (6.1.0), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
Behavioral regressions for the canonical version helper, found by the
github-findings review of the campaign package.

.DESCRIPTION
Four findings are covered here, each with a behavioural assertion and no
source-spelling assertion:

  1. ONE confirmation boundary per real write. Save-JsonFile used to ask
     ShouldProcess a second time inside the caller's own ShouldProcess block,
     so -Confirm asked twice for ONE file, and a declined inner prompt left
     the version report claiming a write that never happened. The proof uses a
     fake PSHost that answers the prompts, so no test can wait for a human.

  2. A selected component whose module definition file is missing must fail
     BEFORE the first write. The helper used to store a null definition,
     continue, advance the component version and exit 0, leaving the
     definition's compatibleArtifacts.maxVersion cap stale behind the
     component version.

  3. A selected widget whose package file is missing must fail before the
     first write. This one was already fixed (the widget paths are validated
     before the write phase); the regression is kept so it cannot come back.

  4. The numeric version core has to stay inside the range the platform can
     parse and order: 0..2147483647 for every major, minor and patch, with
     ASCII digits and no leading zeroes. The helper used to normalise a
     leading zero away ('01.2.3' became '1.2.4'), to accept a numeric
     prerelease identifier with a leading zero, to accept a core written with
     non-ASCII digits, and to write a core above the Int32 range -- a version
     the platform does not reject but orders wrongly, because
     ArtifactVersionComparer falls back to an ordinal string comparison once
     Version.TryParse fails. Every refusal has to happen before the first
     write, and the files have to be left byte for byte and timestamp for
     timestamp as they were.

Pester 6 runs every container in a separate session state, so the shared
harness lives in Bump-Version.TestHelpers.ps1 and is dot-sourced from each
Describe block's BeforeAll.
#>

Describe 'Bump-Version: one confirmation boundary per written file' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Bump-Version.TestHelpers.ps1')
    }

    BeforeEach {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bumpScriptPath = New-BumpVersionFindingsRepository -RootPath $repoRoot
    }

    It 'Asks once for every file it writes, and writes all of them when every confirmation is approved' {
        $before = Get-BumpVersionTreeHash -RootPath $repoRoot

        $result = Invoke-BumpVersionWithFakeHost -BumpScriptPath $bumpScriptPath -Parameters @{
            RepositoryRoot = $repoRoot
            ComponentKey   = @('test_app')
            WidgetFile     = @('widget.json')
            Confirm        = $true
        }

        $after = Get-BumpVersionTreeHash -RootPath $repoRoot
        $changed = @($after.Keys | Where-Object { -not $before.ContainsKey($_) -or $before[$_] -ne $after[$_] })

        $result.ExitCode | Should -Be 0
        $result.Failed | Should -BeFalse

        # Three selected targets: the module definition, the widget package and
        # the manifest.
        $changed.Count | Should -Be 3

        # ONE confirmation per real write: as many prompts as files written, and
        # no file confirmed twice. The old helper asked twice per file, once
        # for the caller's action and once inside Save-JsonFile.
        $result.Prompts.Count | Should -Be $changed.Count
        $distinctTargets = @($result.PromptTargets | Sort-Object -Unique)
        $distinctTargets.Count | Should -Be $result.Prompts.Count
        $distinctTargets.Count | Should -Be $changed.Count
    }

    It 'Writes nothing and names the declined file when a confirmation is declined' {
        $before = Get-BumpVersionTreeHash -RootPath $repoRoot

        $result = Invoke-BumpVersionWithFakeHost -BumpScriptPath $bumpScriptPath -Parameters @{
            RepositoryRoot = $repoRoot
            ComponentKey   = @('test_app')
            WidgetFile     = @('widget.json')
            Confirm        = $true
        } -DeclinePatterns @('dashboard widget')

        $after = Get-BumpVersionTreeHash -RootPath $repoRoot
        $changed = @($after.Keys | Where-Object { -not $before.ContainsKey($_) -or $before[$_] -ne $after[$_] })

        # A declined confirmation is a refusal of the whole bump, not of one
        # file: the other files must not be written either, or the manifest,
        # the module definition and the widget package would disagree about
        # the same version.
        $changed.Count | Should -Be 0

        # The refusal is reported as a refusal: a non-zero exit code and an
        # error that names the declined file.
        $result.ExitCode | Should -Be 1
        $errorText = ($result.Errors -replace '\s+', ' ')
        $errorText | Should -Match 'declined'
        $errorText | Should -Match 'widget\.json'

        # And the version report must not claim a write that did not happen.
        $reportedRows = @($result.Output | Where-Object { $_.GetType().Name -eq 'FormatEntryData' })
        $reportedRows.Count | Should -Be 0
    }

    It 'Writes nothing under -WhatIf' {
        $before = Get-BumpVersionTreeHash -RootPath $repoRoot

        $result = Invoke-BumpVersionWithFakeHost -BumpScriptPath $bumpScriptPath -Parameters @{
            RepositoryRoot = $repoRoot
            ComponentKey   = @('test_app')
            WidgetFile     = @('widget.json')
            WhatIf         = $true
        }

        $after = Get-BumpVersionTreeHash -RootPath $repoRoot
        $changed = @($after.Keys | Where-Object { -not $before.ContainsKey($_) -or $before[$_] -ne $after[$_] })

        $changed.Count | Should -Be 0
        $result.ExitCode | Should -Be 0
        $result.Failed | Should -BeFalse
    }
}

Describe 'Bump-Version: a selected component whose module definition file is missing' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Bump-Version.TestHelpers.ps1')
    }

    BeforeEach {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bumpScriptPath = New-BumpVersionFindingsRepository -RootPath $repoRoot -MissingModuleDefinition
    }

    It 'Fails before writing any selected file and leaves every version untouched' {
        $before = Get-BumpVersionTreeHash -RootPath $repoRoot

        $result = Invoke-BumpVersionScript -BumpScriptPath $bumpScriptPath -Parameters @{ AllComponents = $true; AllWidgets = $true }

        $after = Get-BumpVersionTreeHash -RootPath $repoRoot
        $changed = @($after.Keys | Where-Object { -not $before.ContainsKey($_) -or $before[$_] -ne $after[$_] })

        # Atomic failure: the healthy module definition, the widget package
        # and the manifest are all left exactly as they were.
        $changed.Count | Should -Be 0

        $result.ExitCode | Should -Be 1
        $outputText = ($result.Output -replace '\s', '')
        $outputText | Should -Match "Moduledefinition'broken_module'filewasnotfound"

        # The component version of the healthy component must not have moved
        # either: the failure is not hidden behind a partial bump.
        $manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'omp-components.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        @($manifest.components | Where-Object { $_.version -ne '1.0.0' }).Count | Should -Be 0
    }
}

Describe 'Bump-Version: a selected component whose module definition file is invalid' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Bump-Version.TestHelpers.ps1')
    }

    BeforeEach {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bumpScriptPath = New-BumpVersionFindingsRepository -RootPath $repoRoot -InvalidModuleDefinition
    }

    It 'Fails before writing any selected file' {
        $before = Get-BumpVersionTreeHash -RootPath $repoRoot

        $result = Invoke-BumpVersionScript -BumpScriptPath $bumpScriptPath -Parameters @{ AllComponents = $true; AllWidgets = $true }

        $after = Get-BumpVersionTreeHash -RootPath $repoRoot
        $changed = @($after.Keys | Where-Object { -not $before.ContainsKey($_) -or $before[$_] -ne $after[$_] })

        $changed.Count | Should -Be 0
        $result.ExitCode | Should -Be 1
    }
}

Describe 'Bump-Version: a selected widget whose package file is missing' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Bump-Version.TestHelpers.ps1')
    }

    BeforeEach {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bumpScriptPath = New-BumpVersionFindingsRepository -RootPath $repoRoot -MissingWidgetFile
    }

    It 'Fails before writing any selected file' {
        # The widget paths were already validated before the write phase when
        # this finding was raised; the regression is kept so the late
        # validation cannot come back.
        $before = Get-BumpVersionTreeHash -RootPath $repoRoot

        $result = Invoke-BumpVersionScript -BumpScriptPath $bumpScriptPath -Parameters @{ AllComponents = $true; AllWidgets = $true }

        $after = Get-BumpVersionTreeHash -RootPath $repoRoot
        $changed = @($after.Keys | Where-Object { -not $before.ContainsKey($_) -or $before[$_] -ne $after[$_] })

        $changed.Count | Should -Be 0
        $result.ExitCode | Should -Be 1
        $outputText = ($result.Output -replace '\s', '')
        $outputText | Should -Match 'Widgetfilewasnotfound'
    }
}

Describe 'Bump-Version: the numeric version core stays inside the supported range' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Bump-Version.TestHelpers.ps1')

        # The platform compares artifact versions with System.Version, whose
        # major, minor and patch are Int32. Build the non-ASCII digit at run
        # time so this file stays ASCII.
        $script:arabicDigit = [string][char]0x0661
    }

    It 'Refuses a component version whose core carries a leading zero instead of normalizing it away' {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bumpScriptPath = New-BumpVersionCoreRepository -RootPath $repoRoot -ComponentVersion '01.2.3'
        $before = Get-BumpVersionTreeHash -RootPath $repoRoot

        $result = Invoke-BumpVersionScript -BumpScriptPath $bumpScriptPath -Parameters @{ ComponentKey = @('test_app') }

        $after = Get-BumpVersionTreeHash -RootPath $repoRoot
        $changed = @($after.Keys | Where-Object { -not $before.ContainsKey($_) -or $before[$_] -ne $after[$_] })

        # The old helper read '01.2.3' as major 1 and wrote '1.2.4', so the
        # manifest no longer held the version anybody could read back.
        $changed.Count | Should -Be 0
        $result.ExitCode | Should -Be 1

        $manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'omp-components.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $manifest.components[0].version | Should -Be '01.2.3'
    }

    It 'Refuses a prerelease identifier that carries a leading zero' {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bumpScriptPath = New-BumpVersionCoreRepository -RootPath $repoRoot
        $before = Get-BumpVersionTreeHash -RootPath $repoRoot

        $result = Invoke-BumpVersionScript -BumpScriptPath $bumpScriptPath -Parameters @{
            ComponentKey = @('test_app')
            Version = '1.2.3-01'
            SkipRepositoryVersion = $true
        }

        $after = Get-BumpVersionTreeHash -RootPath $repoRoot
        $changed = @($after.Keys | Where-Object { -not $before.ContainsKey($_) -or $before[$_] -ne $after[$_] })

        $changed.Count | Should -Be 0
        $result.ExitCode | Should -Be 1
    }

    It 'Refuses a numeric core written with non-ASCII digits' {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        # The component already carries the digit, so the encoding guard
        # cannot be the accidental protector: the version contract itself has
        # to refuse the text.
        $bumpScriptPath = New-BumpVersionCoreRepository -RootPath $repoRoot -ComponentVersion ($script:arabicDigit + '.2.3')
        $before = Get-BumpVersionTreeHash -RootPath $repoRoot

        $result = Invoke-BumpVersionScript -BumpScriptPath $bumpScriptPath -Parameters @{
            ComponentKey = @('test_app')
            Version = ($script:arabicDigit + '.2.4')
            SkipRepositoryVersion = $true
        }

        $after = Get-BumpVersionTreeHash -RootPath $repoRoot
        $changed = @($after.Keys | Where-Object { -not $before.ContainsKey($_) -or $before[$_] -ne $after[$_] })

        $changed.Count | Should -Be 0
        $result.ExitCode | Should -Be 1
    }

    It 'Refuses a numeric core above the supported Int32 range instead of writing a version the platform orders wrongly' {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bumpScriptPath = New-BumpVersionCoreRepository -RootPath $repoRoot
        $before = Get-BumpVersionTreeHash -RootPath $repoRoot

        $result = Invoke-BumpVersionScript -BumpScriptPath $bumpScriptPath -Parameters @{
            ComponentKey = @('test_app')
            Version = '2147483648.0.0'
            SkipRepositoryVersion = $true
        }

        $after = Get-BumpVersionTreeHash -RootPath $repoRoot
        $changed = @($after.Keys | Where-Object { -not $before.ContainsKey($_) -or $before[$_] -ne $after[$_] })

        $changed.Count | Should -Be 0
        $result.ExitCode | Should -Be 1
        ($result.Output -replace '\s', '') | Should -Match 'supportedrange'

        # The reason the range is a hard boundary: the platform does not
        # reject such a version, it orders it wrongly. Version.TryParse
        # fails, ArtifactVersionComparer falls back to an ordinal string
        # comparison, and 2147483648.0.0 then sorts BEFORE 999999999.0.0.
        $parsed = $null
        [System.Version]::TryParse('2147483648.0.0', [ref]$parsed) | Should -BeFalse
        [string]::Compare('2147483648.0.0', '999999999.0.0', [System.StringComparison]::OrdinalIgnoreCase) | Should -BeLessThan 0
    }

    It 'Refuses an automatic bump that would leave the supported range, with a range error instead of an Int32 overflow' {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bumpScriptPath = New-BumpVersionCoreRepository -RootPath $repoRoot -ComponentVersion '1.0.2147483647'
        $before = Get-BumpVersionTreeHash -RootPath $repoRoot

        $result = Invoke-BumpVersionScript -BumpScriptPath $bumpScriptPath -Parameters @{ ComponentKey = @('test_app') }

        $after = Get-BumpVersionTreeHash -RootPath $repoRoot
        $changed = @($after.Keys | Where-Object { -not $before.ContainsKey($_) -or $before[$_] -ne $after[$_] })

        $changed.Count | Should -Be 0
        $result.ExitCode | Should -Be 1

        # A clear supported-range error, not the Int32 conversion failure the
        # old helper produced when the patch component overflowed.
        ($result.Output -replace '\s', '') | Should -Match 'supportedrange'
        ($result.Output -replace '\s', '') | Should -Not -Match 'System.Int32'
    }

    It 'Accepts the valid version next to the upper boundary' {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bumpScriptPath = New-BumpVersionCoreRepository -RootPath $repoRoot -ComponentVersion '2147483646.2147483646.2147483646'

        $result = Invoke-BumpVersionScript -BumpScriptPath $bumpScriptPath -Parameters @{ ComponentKey = @('test_app') }

        $result.ExitCode | Should -Be 0
        $manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'omp-components.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $manifest.components[0].version | Should -Be '2147483646.2147483646.2147483647'
    }

    It 'Accepts the upper boundary itself and bumps the component next to it' {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bumpScriptPath = New-BumpVersionCoreRepository -RootPath $repoRoot -ComponentVersion '2147483647.0.0'

        $result = Invoke-BumpVersionScript -BumpScriptPath $bumpScriptPath -Parameters @{ ComponentKey = @('test_app') }

        $result.ExitCode | Should -Be 0
        $manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'omp-components.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $manifest.components[0].version | Should -Be '2147483647.0.1'
    }

    It 'Still accepts a zero core' {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bumpScriptPath = New-BumpVersionCoreRepository -RootPath $repoRoot -ComponentVersion '0.0.0'

        $result = Invoke-BumpVersionScript -BumpScriptPath $bumpScriptPath -Parameters @{ ComponentKey = @('test_app') }

        $result.ExitCode | Should -Be 0
        $manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'omp-components.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $manifest.components[0].version | Should -Be '0.0.1'
    }

    It 'Still accepts an alphanumeric prerelease identifier and build metadata that carry leading zeroes' {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bumpScriptPath = New-BumpVersionCoreRepository -RootPath $repoRoot

        $result = Invoke-BumpVersionScript -BumpScriptPath $bumpScriptPath -Parameters @{
            ComponentKey = @('test_app')
            Version = '1.2.3-01a+001'
            SkipRepositoryVersion = $true
        }

        $result.ExitCode | Should -Be 0
        $manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'omp-components.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $manifest.components[0].version | Should -Be '1.2.3-01a+001'
    }
}

Describe 'Bump-Version: a refused version leaves every selected file untouched' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Bump-Version.TestHelpers.ps1')
    }

    It 'Leaves the manifest, the module definition and the widget package byte-identical and unwritten' {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bumpScriptPath = New-BumpVersionFindingsRepository -RootPath $repoRoot
        # Give the component a version the contract refuses, so the refusal
        # happens with a module definition and a widget package selected too.
        $manifestPath = Join-Path $repoRoot 'omp-components.json'
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $manifest.components[0].version = '01.2.3'
        $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

        $before = Get-BumpVersionTreeStamp -RootPath $repoRoot

        $result = Invoke-BumpVersionScript -BumpScriptPath $bumpScriptPath -Parameters @{
            ComponentKey = @('test_app')
            WidgetFile = @('widget.json')
        }

        $after = Get-BumpVersionTreeStamp -RootPath $repoRoot
        $changed = @($after.Keys | Where-Object { -not $before.ContainsKey($_) -or $before[$_] -ne $after[$_] })

        # Byte for byte AND timestamp for timestamp: a rewrite with the same
        # content is still a write, and a refusal has to be a refusal.
        $changed.Count | Should -Be 0
        $result.ExitCode | Should -Be 1

        foreach ($relative in @('omp-components.json', 'TestModule\test.module-definition.json', 'widget.json')) {
            $before.ContainsKey($relative) | Should -BeTrue -Because "the fixture has to contain $relative"
            $after[$relative] | Should -Be $before[$relative]
        }
    }
}
