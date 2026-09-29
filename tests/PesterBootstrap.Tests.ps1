#Requires -Version 5.1
# Pester 6's 'Should -Be/-Not -Be/-Match/-BeTrue' parameters are provided by
# the pinned Pester module (6.1.0), not by the inbox Pester 3.4.0 profile the
# compatibility rule measures against; suppress for the whole file, not per
# assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
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

    The restore cases run the real restore code, not a stub of Save-Module,
    but they are served offline: the helper puts the repository-local cache
    (.psmodules, which run-script-tests.ps1 has already populated) on the
    child's module path, and the bootstrap copies an available pinned Pester
    instead of downloading. Only a machine with neither that cache nor a
    global 6.1.0 would reach PSGallery, and there the restore cases skip with
    a reason rather than make the gate depend on the network.

    Pester 6 runs every container in a separate session state, so the shared
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
        Skip-UnlessPinnedPesterSeedAvailable
        $cache = New-TestDirectory
        try {
            $result = Invoke-PesterBootstrap -CacheRoot $cache

            $result.ExitCode | Should -Be 0
            $result.Output | Should -Match 'restoring from PSGallery'
            Test-Path -LiteralPath (Join-Path $cache 'Pester\6.1.0\Pester.psd1') -PathType Leaf | Should -BeTrue
            $result.Output | Should -Match 'Loaded Pester 6\.1\.0'
            $result.Output | Should -Match ([regex]::Escape((Join-Path $cache 'Pester\6.1.0')))
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
        $script:WarmCache = $null
        if (Test-PinnedPesterSeedAvailable) {
            $script:WarmCache = New-TestDirectory
            $seed = Invoke-PesterBootstrap -CacheRoot $script:WarmCache
            if ($seed.ExitCode -ne 0) {
                throw "Could not seed the warm-cache fixture: $($seed.Output)"
            }
        }
    }
    AfterAll {
        Remove-TestDirectory -Path $script:WarmCache
    }

    It 'Reuses the cached copy without restoring again' {
        Skip-UnlessPinnedPesterSeedAvailable
        $result = Invoke-PesterBootstrap -CacheRoot $script:WarmCache

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match 'found in the repository-local cache'
        $result.Output | Should -Not -Match 'restoring from PSGallery'
        $result.Output | Should -Match 'Loaded Pester 6\.1\.0'
    }
}

Describe 'pester-bootstrap: a different global Pester version' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'PesterBootstrap.TestHelpers.ps1')
    }

    It 'Loads the repo-local pin, not a globally installed Pester of another version' {
        Skip-UnlessPinnedPesterSeedAvailable
        $otherVersions = @(Get-Module -ListAvailable Pester | Where-Object { $_.Version -ne [Version]'6.1.0' })
        $cache = New-TestDirectory
        try {
            $result = Invoke-PesterBootstrap -CacheRoot $cache

            $result.ExitCode | Should -Be 0
            # The loaded module must come from the cache, never from a global
            # module root such as Program Files or the user's Documents.
            $result.Output | Should -Match 'Loaded Pester 6\.1\.0'
            $result.Output | Should -Match ([regex]::Escape((Join-Path $cache 'Pester\6.1.0')))
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
        # The runner bootstraps its own Pester from the repository cache first.
        Skip-UnlessPinnedPesterSeedAvailable
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

Describe 'run-script-tests: suite inventory guards' {
    # The runner's contract is that every suite file on disk runs and runs at
    # least one test. Each case below is a way a suite used to drop out of a
    # green run without anyone noticing.
    BeforeAll {
        . (Join-Path $PSScriptRoot 'PesterBootstrap.TestHelpers.ps1')
    }

    It 'Fails red when a suite is renamed away from *.Tests.ps1' {
        Skip-UnlessPinnedPesterSeedAvailable
        # Renamed.Test.ps1 matches no discovery glob: without an inventory of
        # every script in the tests tree it silently stops running.
        $testsDir = New-SuiteFixture -Files @{
            'Good.Tests.ps1'   = $script:PassingSuiteBody
            'Renamed.Test.ps1' = $script:PassingSuiteBody
        }
        try {
            $result = Invoke-RunnerAgainst -TestsPath $testsDir
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'Renamed\.Test\.ps1'
        }
        finally {
            Remove-TestDirectory -Path $testsDir
        }
    }

    It 'Fails red when one suite runs zero tests next to a passing one' {
        Skip-UnlessPinnedPesterSeedAvailable
        # Pester reports the empty container as NotRun and the whole run as
        # Passed, and PassedCount is 1: only a per-container check sees it.
        $testsDir = New-SuiteFixture -Files @{
            'Good.Tests.ps1'  = $script:PassingSuiteBody
            'Empty.Tests.ps1' = "Describe 'emptied' { }`n"
        }
        try {
            $result = Invoke-RunnerAgainst -TestsPath $testsDir
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'Empty\.Tests\.ps1'
        }
        finally {
            Remove-TestDirectory -Path $testsDir
        }
    }

    It 'Runs and counts a suite in a subdirectory' {
        Skip-UnlessPinnedPesterSeedAvailable
        # Pester discovers suites recursively; the inventory must count the
        # same set, or a nested suite either fails a correct run or goes unseen.
        $testsDir = New-SuiteFixture -Files @{
            'Good.Tests.ps1'       = $script:PassingSuiteBody
            'nested/Deep.Tests.ps1' = "Describe 'nested fixture' { It 'runs from a subdirectory' { 1 | Should -Be 1 } }`n"
        }
        try {
            $result = Invoke-RunnerAgainst -TestsPath $testsDir
            $result.ExitCode | Should -Be 0
            $result.Output | Should -Match 'Deep\.Tests\.ps1'
        }
        finally {
            Remove-TestDirectory -Path $testsDir
        }
    }

    It 'Allows *.TestHelpers.ps1 next to the suites without counting it as a suite' {
        Skip-UnlessPinnedPesterSeedAvailable
        $testsDir = New-SuiteFixture -Files @{
            'Good.Tests.ps1'       = "Describe 'with helpers' { BeforeAll { . (Join-Path `$PSScriptRoot 'Good.TestHelpers.ps1') }; It 'uses the helper' { Get-FixtureValue | Should -Be 42 } }`n"
            'Good.TestHelpers.ps1' = "function Get-FixtureValue { 42 }`n"
        }
        try {
            $result = Invoke-RunnerAgainst -TestsPath $testsDir
            $result.ExitCode | Should -Be 0
            $result.Output | Should -Not -Match 'GATE FAIL'
        }
        finally {
            Remove-TestDirectory -Path $testsDir
        }
    }
}

Describe 'run-script-tests: an incompatible Pester already loaded' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'PesterBootstrap.TestHelpers.ps1')
    }

    It 'Refuses with an instruction to start a new process' {
        Skip-UnlessPinnedPesterSeedAvailable
        # A Pester 5 session has Pester.dll 5.x loaded. A .NET assembly cannot
        # be unloaded, so 6.1.0 cannot be imported into that process. The
        # fixture loads a stand-in assembly named Pester, version 5.7.1, that
        # defines the PesterConfiguration type Pester's own import check looks
        # for -- what the runner meets in such a session, without a second
        # Pester download. (Measured with the real Pester 5.7.1: the import
        # then fails inside Pester.psm1 with Pester's generic restart advice.)
        $testsDir = New-SuiteFixture -Files @{ 'Good.Tests.ps1' = $script:PassingSuiteBody }
        $work = New-TestDirectory
        try {
            $child = Join-Path $work 'load-incompatible-pester.ps1'
            $body = @(
                'param([string]$Runner, [string]$TestsPath, [string]$Work)',
                '$source = "using System.Reflection; [assembly: AssemblyVersion(""5.7.1.0"")] public class PesterConfiguration { }"',
                '$dll = Join-Path $Work "Pester.dll"',
                'Add-Type -TypeDefinition $source -OutputAssembly $dll -OutputType Library',
                'Add-Type -Path $dll',
                '& $Runner -TestsPath $TestsPath',
                'exit $LASTEXITCODE'
            ) -join "`n"
            [System.IO.File]::WriteAllText($child, $body, [System.Text.UTF8Encoding]::new($false))

            $result = Invoke-ChildPowerShell -ScriptPath $child -ScriptArguments @('-Runner', $script:RunnerScript, '-TestsPath', $testsDir, '-Work', $work)
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'Pester\.dll 5\.7\.1'
            $result.Output | Should -Match 'new (PowerShell )?process'
        }
        finally {
            Remove-TestDirectory -Path $testsDir
            Remove-TestDirectory -Path $work
        }
    }
}

Describe 'pester-bootstrap: Windows PowerShell-safe module path' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'PesterBootstrap.TestHelpers.ps1')
        # Dot-sourcing loads the functions only (script mode is skipped); the
        # function reads $env:PSModulePath, which each It sets and restores.
        . $script:BootstrapScript
    }

    It 'Drops the PowerShell 7 module folders and keeps everything else, in order' {
        $original = $env:PSModulePath
        try {
            $env:PSModulePath = @(
                'C:\Users\u\Documents\WindowsPowerShell\Modules',
                'C:\Program Files\PowerShell\7',
                'C:\Program Files\PowerShell\7\Modules\',
                'C:\Program Files\PowerShell\Modules',
                'C:\Users\u\Documents\PowerShell\Modules',
                'C:\Program Files\WindowsPowerShell\Modules',
                '',
                'C:\Windows\system32\WindowsPowerShell\v1.0\Modules'
            ) -join ';'

            $kept = Get-WindowsPowerShellSafeModulePath

            $kept | Should -Be 'C:\Users\u\Documents\WindowsPowerShell\Modules;C:\Program Files\WindowsPowerShell\Modules;C:\Windows\system32\WindowsPowerShell\v1.0\Modules'
        }
        finally {
            $env:PSModulePath = $original
        }
    }

    It 'Is case-insensitive and does not touch lookalike paths' {
        $original = $env:PSModulePath
        try {
            $env:PSModulePath = 'c:\program files\powershell\7;D:\Tools\PowerShell7Modules;E:\Documents\PowerShell\Modules;C:\Keep\Modules'

            Get-WindowsPowerShellSafeModulePath | Should -Be 'D:\Tools\PowerShell7Modules;C:\Keep\Modules'
        }
        finally {
            $env:PSModulePath = $original
        }
    }
}
