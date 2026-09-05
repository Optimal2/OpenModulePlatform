#Requires -Version 5.1
# Pester 5's 'Should -Be/-Not -Be/-Match/-BeTrue' parameters are provided by
# the pinned Pester module (5.9.1), not by the inbox Pester 3.4.0 profile the
# compatibility rule measures against; suppress for the whole file, not per
# assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 5 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
    Proves the repository-local Pester bootstrap (scripts/omp/
    pester-bootstrap.ps1) restores the pinned version into an empty cache,
    reuses an already-restored cache, wins over a different globally
    installed Pester, and that run-script-tests.ps1 still fails red when a
    run executes zero tests.

.DESCRIPTION
    The bootstrap's contracts are per-process (module path, loaded module,
    exit code), so every test drives pester-bootstrap.ps1 or
    run-script-tests.ps1 as a child powershell.exe process against temporary
    directories and asserts on the exit code and printed output.

    The restore cases download the pinned Pester from PSGallery for real:
    stubbing Save-Module would test the stub, not the restore. The same
    gallery access is already a hard prerequisite of CI, which installed the
    pin from PSGallery before this bootstrap existed.

    Pester 5 runs every container in a separate session state, so the shared
    harness (child-process invoker + temp-directory helpers) lives in
    PesterBootstrap.TestHelpers.ps1 and is dot-sourced from each Describe
    block's BeforeAll.
#>

Set-StrictMode -Version Latest

Describe 'pester-bootstrap: empty module cache' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'PesterBootstrap.TestHelpers.ps1')
    }

    It 'Restores the pinned Pester into an empty repo-local cache and loads it from there' {
        $cache = New-TestDirectory
        try {
            $result = Invoke-PesterBootstrap -CacheRoot $cache

            $result.ExitCode | Should -Be 0
            $result.Output | Should -Match 'restoring from PSGallery'
            Test-Path -LiteralPath (Join-Path $cache 'Pester\5.9.1\Pester.psd1') -PathType Leaf | Should -BeTrue
            $result.Output | Should -Match 'Loaded Pester 5\.9\.1'
            $result.Output | Should -Match ([regex]::Escape((Join-Path $cache 'Pester\5.9.1')))
        }
        finally {
            Remove-TestDirectory -Path $cache
        }
    }
}

Describe 'pester-bootstrap: already-restored module cache' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'PesterBootstrap.TestHelpers.ps1')
        # Seed the cache with a real restore once; the test then measures the
        # SECOND invocation, which must be served from the cache alone.
        $script:WarmCache = New-TestDirectory
        $seed = Invoke-PesterBootstrap -CacheRoot $script:WarmCache
        if ($seed.ExitCode -ne 0) {
            throw "Could not seed the warm-cache fixture: $($seed.Output)"
        }
    }
    AfterAll {
        Remove-TestDirectory -Path $script:WarmCache
    }

    It 'Reuses the cached copy without restoring again' {
        $result = Invoke-PesterBootstrap -CacheRoot $script:WarmCache

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match 'found in the repository-local cache'
        $result.Output | Should -Not -Match 'restoring from PSGallery'
        $result.Output | Should -Match 'Loaded Pester 5\.9\.1'
    }
}

Describe 'pester-bootstrap: a different global Pester version' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'PesterBootstrap.TestHelpers.ps1')
    }

    It 'Loads the repo-local pin, not a globally installed Pester of another version' {
        $otherVersions = @(Get-Module -ListAvailable Pester | Where-Object { $_.Version -ne [Version]'5.9.1' })
        $cache = New-TestDirectory
        try {
            $result = Invoke-PesterBootstrap -CacheRoot $cache

            $result.ExitCode | Should -Be 0
            # The loaded module must come from the cache, never from a global
            # module root such as Program Files or the user's Documents.
            $result.Output | Should -Match 'Loaded Pester 5\.9\.1'
            $result.Output | Should -Match ([regex]::Escape((Join-Path $cache 'Pester\5.9.1')))
            if ($otherVersions.Count -gt 0) {
                # A divergent global Pester really is visible on this machine
                # (Windows carries 3.4.0 inbox): prove the pin still won.
                $result.Output | Should -Not -Match 'Loaded Pester 3\.'
            }
        }
        finally {
            Remove-TestDirectory -Path $cache
        }
    }
}

Describe 'run-script-tests: zero-test gate' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'PesterBootstrap.TestHelpers.ps1')
    }

    It 'Fails red when the only suite discovers zero tests' {
        $testsDir = New-TestDirectory
        try {
            # A suite file exists (so discovery and the container-count gate
            # pass) but declares no It blocks: PassedCount is 0 and the
            # zero-test gate must be what fails the run.
            $emptySuite = Join-Path $testsDir 'Empty.Tests.ps1'
            [System.IO.File]::WriteAllText($emptySuite, "Describe 'empty fixture' { }`n", [System.Text.UTF8Encoding]::new($false))

            $result = Invoke-ChildPowerShell -ScriptPath $script:RunnerScript `
                -ScriptArguments @('-TestsPath', $testsDir)

            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match '0 passing tests'
        }
        finally {
            Remove-TestDirectory -Path $testsDir
        }
    }
}
