#Requires -Version 5.1
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Assertions and data-driven cases use the repository-pinned Pester 6.1.0 module.')]
param()

Describe 'sync-shared-files.ps1' {
    BeforeAll {
        $repository = Split-Path -Parent $PSScriptRoot

        function Initialize-FixtureRepository {
            param([string]$Path, [string]$RepositoryKey)

            $null = New-Item -ItemType Directory -Path $Path -Force
            & git -C $Path init --quiet
            if ($LASTEXITCODE -ne 0) { throw "Fixture git init failed in '$Path'." }
            Set-Content -LiteralPath (Join-Path $Path 'omp-components.json') -Encoding Ascii -Value "{`"repositoryKey`":`"$RepositoryKey`"}"
        }

        function Set-FixtureFile {
            param([string]$Path, [byte[]]$Bytes)

            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force
            [IO.File]::WriteAllBytes($Path, $Bytes)
        }

        function Invoke-FixtureSync {
            param([switch]$DryRun)

            $output = & $syncPath -ConsumerRoot $root -DryRun:$DryRun 6>&1
            $exitCode = $LASTEXITCODE
            [pscustomobject]@{ ExitCode = $exitCode; Output = ($output -join "`n") }
        }
    }

    BeforeEach {
        $fixture = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $root = Join-Path $fixture 'repositories with spaces'
        $platform = Join-Path $root 'Platform'
        $consumer = Join-Path $root 'Consumer'
        Initialize-FixtureRepository $platform 'fixture-platform'
        Initialize-FixtureRepository $consumer 'fixture-consumer'
        $toolsDirectory = Join-Path $platform 'scripts/omp'
        $null = New-Item -ItemType Directory -Path $toolsDirectory -Force
        $syncPath = Join-Path $toolsDirectory 'sync-shared-files.ps1'
        $validatorPath = Join-Path $toolsDirectory 'validate-shared-scripts.ps1'
        Copy-Item -LiteralPath (Join-Path $repository 'scripts/omp/sync-shared-files.ps1') -Destination $syncPath
        Copy-Item -LiteralPath (Join-Path $repository 'scripts/omp/validate-shared-scripts.ps1') -Destination $validatorPath
        $driftedRelative = 'scripts/omp/bump-version.ps1'
        $identicalRelative = 'build/OpenModulePlatform.DeterministicStaticWebAssets.targets'
        $missingRelative = 'scripts/omp/run-script-tests.ps1'
        $driftedPath = Join-Path $consumer $driftedRelative
        $identicalPath = Join-Path $consumer $identicalRelative
        $canonicalBytes = [byte[]](0xEF, 0xBB, 0xBF) + [Text.Encoding]::UTF8.GetBytes("canonical`r`n")
        Set-FixtureFile (Join-Path $platform $driftedRelative) $canonicalBytes
        Set-FixtureFile $driftedPath ([Text.Encoding]::UTF8.GetBytes("canonical`r`n"))

        # Check 15 treats CRLF, LF, and CR as equivalent while retaining a BOM.
        Set-FixtureFile (Join-Path $platform $identicalRelative) ([Text.Encoding]::UTF8.GetBytes("first`r`nsecond`r`n"))
        Set-FixtureFile $identicalPath ([Text.Encoding]::UTF8.GetBytes("first`nsecond`r"))
    }

    It 'copies the canonical bytes and reports the hashes without touching identical files' {
        [IO.File]::SetLastWriteTimeUtc($identicalPath, [datetime]'2020-01-01T00:00:00Z')
        $timestamp = [IO.File]::GetLastWriteTimeUtc($identicalPath)
        $identicalBytes = [Convert]::ToBase64String([IO.File]::ReadAllBytes($identicalPath))

        $result = Invoke-FixtureSync

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match 'Copied: scripts/omp/bump-version\.ps1 \([a-f0-9]{8} -> [a-f0-9]{8}\)'
        $result.Output | Should -Match 'already identical: 1'
        $result.Output | Should -Match 'Summary: 1 consumer\(s\) scanned'
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($driftedPath)) | Should -Be ([Convert]::ToBase64String($canonicalBytes))
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($identicalPath)) | Should -Be $identicalBytes
        [IO.File]::GetLastWriteTimeUtc($identicalPath) | Should -Be $timestamp
    }

    It 'reports a file the consumer does not carry without creating it' {
        $result = Invoke-FixtureSync

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match 'not carried: scripts/omp/run-script-tests\.ps1'
        Join-Path $consumer $missingRelative | Should -Not -Exist
    }

    It 'reports a copy failure and exits one' {
        $attributes = [IO.File]::GetAttributes($driftedPath)
        [IO.File]::SetAttributes($driftedPath, ($attributes -bor [IO.FileAttributes]::ReadOnly))
        try {
            $result = Invoke-FixtureSync

            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'ERROR scripts/omp/bump-version\.ps1:'
        }
        finally {
            [IO.File]::SetAttributes($driftedPath, $attributes)
        }
    }

    It 'reports drift in DryRun without writing and returns zero once the copies match' {
        $original = [Convert]::ToBase64String([IO.File]::ReadAllBytes($driftedPath))
        $timestamp = [IO.File]::GetLastWriteTimeUtc($driftedPath)

        $result = Invoke-FixtureSync -DryRun

        $result.ExitCode | Should -Be 2
        $result.Output | Should -Match 'Would copy: scripts/omp/bump-version\.ps1 \([a-f0-9]{8} -> [a-f0-9]{8}\)'
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($driftedPath)) | Should -Be $original
        [IO.File]::GetLastWriteTimeUtc($driftedPath) | Should -Be $timestamp

        Set-FixtureFile $driftedPath $canonicalBytes
        (Invoke-FixtureSync -DryRun).ExitCode | Should -Be 0
    }

    It 'reports unreadable manifests and failed Git discovery as NOT MEASURED even with drift' {
        $invalid = Join-Path $root 'InvalidManifest'
        Initialize-FixtureRepository $invalid 'invalid-manifest'
        Set-Content -LiteralPath (Join-Path $invalid 'omp-components.json') -Value '{ broken'
        $notCheckout = Join-Path $root 'NotCheckout'
        Set-FixtureFile (Join-Path $notCheckout 'omp-components.json') ([Text.Encoding]::UTF8.GetBytes('{"repositoryKey":"not-checkout"}'))

        $result = Invoke-FixtureSync -DryRun

        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match 'InvalidManifest \[.*\] NOT MEASURED:'
        $result.Output | Should -Match 'NotCheckout \[.*\] NOT MEASURED: git rev-parse'
        $result.Output | Should -Match 'Would copy:'
    }

    It 'reports a missing explicit root even when the environment provides a valid root' {
        $previousRoot = $env:OMP_CONSUMER_ROOT
        try {
            $env:OMP_CONSUMER_ROOT = $root
            $missingRoot = Join-Path $fixture 'missing'
            $output = & $syncPath -ConsumerRoot $missingRoot 6>&1
            $exitCode = $LASTEXITCODE

            $exitCode | Should -Be 1
            $output -join "`n" | Should -Match 'NOT MEASURED: Consumer root .* is missing'
        }
        finally {
            $env:OMP_CONSUMER_ROOT = $previousRoot
        }
    }

    It 'defines consumer discovery when dot-sourced without running the sync' {
        Set-StrictMode -Version Latest
        $original = [Convert]::ToBase64String([IO.File]::ReadAllBytes($driftedPath))

        $output = . $syncPath -ConsumerRoot $root 6>&1

        $output | Should -BeNullOrEmpty
        (Get-Command Get-OmpSharedFileConsumers -CommandType Function).Name | Should -Be 'Get-OmpSharedFileConsumers'
        $found = @(Get-OmpSharedFileConsumers -ConsumerRoot $root)
        $found.Count | Should -Be 1
        $found[0].Name | Should -Be 'Consumer'
        $found[0].Root | Should -Be $consumer
        $found[0].Resolved | Should -BeTrue
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($driftedPath)) | Should -Be $original
    }

    It 'finds consumers beside the main platform checkout from an external worktree' {
        $worktree = Join-Path $fixture 'platform worktree'
        $tree = & git -C $platform write-tree
        if ($LASTEXITCODE -ne 0) { throw 'Fixture git write-tree failed.' }
        $commit = & git -C $platform -c user.name=Fixture -c user.email=fixture@example.invalid commit-tree $tree -m 'Fixture checkout'
        if ($LASTEXITCODE -ne 0) { throw 'Fixture git commit-tree failed.' }
        & git -C $platform -c core.hooksPath=NUL worktree add --quiet --detach $worktree $commit
        if ($LASTEXITCODE -ne 0) { throw 'Fixture git worktree add failed.' }
        Copy-Item -LiteralPath (Join-Path $platform 'omp-components.json') -Destination $worktree
        Initialize-FixtureRepository (Join-Path $root 'OtherPlatformCheckout') 'fixture-platform'

        . $syncPath -ConsumerRoot $root
        $previousRoot = $env:OMP_CONSUMER_ROOT
        try {
            $env:OMP_CONSUMER_ROOT = $null
            $found = @(Get-OmpSharedFileConsumers -PlatformRepositoryRoot $worktree)
        }
        finally {
            $env:OMP_CONSUMER_ROOT = $previousRoot
        }

        $found.Count | Should -Be 1
        $found[0].Name | Should -Be 'Consumer'
        $found[0].Root | Should -Be $consumer
        $found[0].Resolved | Should -BeTrue
    }

    It 'uses the consumer root from the environment when no explicit root is supplied' {
        . $syncPath
        $environmentRoot = Join-Path $fixture 'environment root'
        Initialize-FixtureRepository (Join-Path $environmentRoot 'OtherConsumer') 'other-consumer'
        $previousRoot = $env:OMP_CONSUMER_ROOT
        try {
            $env:OMP_CONSUMER_ROOT = $environmentRoot
            $found = @(Get-OmpSharedFileConsumers)
        }
        finally {
            $env:OMP_CONSUMER_ROOT = $previousRoot
        }

        $found.Count | Should -Be 1
        $found[0].Name | Should -Be 'OtherConsumer'
        $found[0].Resolved | Should -BeTrue
    }

    It 'fails closed when the validator inventory <description>' -ForEach @(
        @{ description = 'has a non-literal element'; replacement = '$sharedScripts = @(''literal'', $dynamicPath)'; message = 'literal string elements' }
        @{ description = 'is only assigned in a function'; replacement = 'function Set-Inventory { $sharedScripts = @(''literal'') }'; message = 'top-level assignment' }
    ) {
        $validator = [IO.File]::ReadAllText($validatorPath)
        $validator = $validator -replace '(?ms)^\$sharedScripts = @\(.*?^\)', $replacement
        [IO.File]::WriteAllText($validatorPath, $validator)
        $original = [Convert]::ToBase64String([IO.File]::ReadAllBytes($driftedPath))

        $result = Invoke-FixtureSync

        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match ($message + '.*\$sharedScripts')
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($driftedPath)) | Should -Be $original
    }
}
