#Requires -Version 5.1
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Assertions and data-driven cases use the repository-pinned Pester 6.1.0 module.')]
param()

Describe 'local-ci shared file propagation' {
    BeforeAll {
        $repository = Split-Path -Parent $PSScriptRoot
        . (Join-Path $repository 'scripts/omp/sync-shared-files.ps1')
        $canonicalInventory = @(Get-OmpSharedFileInventory -ValidatorPath (Join-Path $repository 'scripts/omp/validate-shared-scripts.ps1') | Sort-Object -Unique)

        function Initialize-FixtureRepository {
            param([string]$Path, [string]$RepositoryKey)

            $null = New-Item -ItemType Directory -Path $Path -Force
            & git -C $Path init --quiet
            if ($LASTEXITCODE -ne 0) { throw "Fixture git init failed in '$Path'." }
            & git -C $Path config user.name Fixture
            & git -C $Path config user.email fixture@example.invalid
            & git -C $Path config commit.gpgsign false
            $manifest = "{`"repositoryKey`":`"$RepositoryKey`",`"repositoryVersion`":`"1.0.0`"}"
            Set-Content -LiteralPath (Join-Path $Path 'omp-components.json') -Encoding Ascii -Value $manifest
        }

        function Copy-FixtureFile {
            param([string]$SourceRoot, [string]$DestinationRoot, [string]$Relative)

            $destination = Join-Path $DestinationRoot $Relative
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
            Copy-Item -LiteralPath (Join-Path $SourceRoot $Relative) -Destination $destination
        }

        function Complete-FixtureCommit {
            param([string]$Path, [string]$Message = 'Fixture')

            & git -C $Path add -- .
            if ($LASTEXITCODE -ne 0) { throw "Fixture git add failed in '$Path'." }
            & git -C $Path commit --quiet -m $Message
            if ($LASTEXITCODE -ne 0) { throw "Fixture commit failed in '$Path'." }
        }

        function Invoke-FixtureValidation {
            param([string[]]$Roots)

            if ($null -eq $Roots) { $Roots = @($root) }
            $output = & $validatorPath -PlatformRepositoryRoot $platform -ConsumerRoot $Roots 6>&1
            $exitCode = $LASTEXITCODE
            [pscustomobject]@{ ExitCode = $exitCode; Output = ($output -join "`n") }
        }
    }

    BeforeEach {
        $previousGitAllowProtocol = $env:GIT_ALLOW_PROTOCOL
        $allowedProtocols = @($env:GIT_ALLOW_PROTOCOL -split ':' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($allowedProtocols -notcontains 'file') {
            $allowedProtocols += 'file'
        }
        $env:GIT_ALLOW_PROTOCOL = $allowedProtocols -join ':'

        $fixture = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $root = Join-Path $fixture 'repositories with spaces'
        $platform = Join-Path $root 'Platform'
        $consumer = Join-Path $root 'Consumer'
        Initialize-FixtureRepository $platform 'fixture-platform'

        $tools = @(
            'scripts/omp/sync-shared-files.ps1',
            'scripts/omp/validate-shared-scripts.ps1',
            'scripts/omp/validate-shared-file-propagation.ps1'
        )
        foreach ($relative in $tools) {
            Copy-FixtureFile $repository $platform $relative
        }
        foreach ($relative in $canonicalInventory) {
            Copy-FixtureFile $repository $platform $relative
        }
        Complete-FixtureCommit $platform 'Platform fixture'

        Initialize-FixtureRepository $consumer 'fixture-consumer'
        foreach ($relative in $canonicalInventory) {
            Copy-FixtureFile $platform $consumer $relative
        }
        Complete-FixtureCommit $consumer 'Consumer fixture'
        $validatorPath = Join-Path $platform 'scripts/omp/validate-shared-file-propagation.ps1'
    }

    AfterEach {
        $env:GIT_ALLOW_PROTOCOL = $previousGitAllowProtocol
    }

    It 'exits 0 when the consumer copies are identical' {
        $result = Invoke-FixtureValidation

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match 'Consumer: OK \([1-9][0-9]* files identical; no upstream configured\)'
        $result.Output | Should -Match 'Shared file propagation: OK for 1 consumer\(s\)'
    }

    It 'fails closed when the canonical inventory is empty' {
        $inventoryValidatorPath = Join-Path $platform 'scripts/omp/validate-shared-scripts.ps1'
        $validator = [IO.File]::ReadAllText($inventoryValidatorPath)
        $validator = $validator -replace '(?ms)^\$sharedScripts = @\(.*?^\)', '$sharedScripts = @()'
        $validator = $validator -replace '(?ms)^\$sharedWebBuildFiles = @\(.*?^\)', '$sharedWebBuildFiles = @()'
        $validator = $validator -replace '(?ms)^\$sharedPesterStepFiles = @\(.*?^\)', '$sharedPesterStepFiles = @()'
        [IO.File]::WriteAllText($inventoryValidatorPath, $validator, [Text.Encoding]::ASCII)

        $result = Invoke-FixtureValidation

        $result.ExitCode | Should -Be 1 -Because $result.Output
        $result.Output | Should -Match 'inventory.*empty'
        $result.Output | Should -Not -Match 'Shared file propagation: OK'
    }

    It 'exits non-zero when a consumer copy differs' {
        $drifted = Join-Path $consumer 'scripts/omp/bump-version.ps1'
        [IO.File]::AppendAllText($drifted, "`n# Fixture drift`n", [Text.Encoding]::ASCII)

        $result = Invoke-FixtureValidation

        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match 'scripts/omp/bump-version\.ps1 differs \(platform [a-f0-9]{12}, consumer [a-f0-9]{12}\)'
        $result.Output | Should -Match 'Consumer: DRIFT'
        $result.Output | Should -Match 'Run scripts/omp/sync-shared-files\.ps1, then commit the copies in each consumer in the same change\.'
    }

    It 'measures only the canonical files a consumer carries' {
        Remove-Item -LiteralPath (Join-Path $consumer 'scripts/omp/run-script-tests.ps1')
        Remove-Item -LiteralPath (Join-Path $consumer 'build/DeterministicRazor/DeterministicRazorGenerator.cs')

        $result = Invoke-FixtureValidation

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match 'Consumer: OK'
        $result.Output | Should -Not -Match 'NOT MEASURED'
    }

    It 'reports a consumer behind its upstream as NOT MEASURED without failing' {
        $remote = Join-Path $fixture 'remote.git'
        & git init --bare --quiet $remote
        if ($LASTEXITCODE -ne 0) { throw 'Fixture bare git init failed.' }
        $branch = (& git -C $consumer symbolic-ref --quiet --short HEAD).Trim()
        & git -C $consumer remote add origin $remote
        & git -C $consumer push --quiet --set-upstream origin $branch
        if ($LASTEXITCODE -ne 0) { throw 'Fixture initial push failed.' }
        & git -C $remote symbolic-ref HEAD "refs/heads/$branch"

        $other = Join-Path $fixture 'upstream-work'
        & git clone --quiet $remote $other
        if ($LASTEXITCODE -ne 0) { throw 'Fixture upstream clone failed.' }
        & git -C $other config user.name Fixture
        & git -C $other config user.email fixture@example.invalid
        & git -C $other config commit.gpgsign false
        Set-Content -LiteralPath (Join-Path $other 'upstream.txt') -Encoding Ascii -Value 'new upstream commit'
        Complete-FixtureCommit $other 'Advance upstream'
        & git -C $other push --quiet origin $branch
        if ($LASTEXITCODE -ne 0) { throw 'Fixture upstream push failed.' }
        & git -C $consumer fetch --quiet origin
        if ($LASTEXITCODE -ne 0) { throw 'Fixture fetch failed.' }

        $result = Invoke-FixtureValidation

        $result.ExitCode | Should -Be 3
        $result.Output | Should -Match 'Consumer: NOT MEASURED \(checkout is 1 commit\(s\) behind origin/.+; update it to measure\)'
        $result.Output | Should -Match 'Shared file propagation: NOT MEASURED for 1 consumer\(s\)'
        $result.Output | Should -Not -Match 'Shared file propagation: OK'
    }

    It 'reports an unresolvable consumer without claiming synchronization' {
        $invalid = Join-Path $root 'NotCheckout'
        $null = New-Item -ItemType Directory -Path $invalid -Force
        Set-Content -LiteralPath (Join-Path $invalid 'omp-components.json') -Encoding Ascii -Value '{"repositoryKey":"not-checkout","repositoryVersion":"1.0.0"}'

        $result = Invoke-FixtureValidation

        $result.ExitCode | Should -Be 3
        $result.Output | Should -Match 'NotCheckout: NOT MEASURED'
        $result.Output | Should -Match 'Shared file propagation: NOT MEASURED for 1 consumer\(s\)'
        $result.Output | Should -Not -Match 'in sync'
    }

    It 'measures a consumer only once when the same root is supplied twice' {
        $result = Invoke-FixtureValidation -Roots @($root, $root)

        $result.ExitCode | Should -Be 0
        [regex]::Matches($result.Output, '(?m)^Consumer: OK ').Count | Should -Be 1
        $result.Output | Should -Match 'Shared file propagation: OK for 1 consumer\(s\)'
    }

    It 'measures a consumer only once when a second root reaches it through a junction' {
        $aliasRoot = Join-Path $fixture 'alias root'
        $null = New-Item -ItemType Directory -Path $aliasRoot -Force
        try {
            $null = New-Item -ItemType Junction -Path (Join-Path $aliasRoot 'ConsumerAlias') -Value $consumer -ErrorAction Stop
        }
        catch {
            Set-ItResult -Skipped -Because 'this process cannot create a directory junction'
            return
        }

        $result = Invoke-FixtureValidation -Roots @($root, $aliasRoot)

        $result.ExitCode | Should -Be 0
        [regex]::Matches($result.Output, '(?m)^Consumer(?:Alias)?: OK ').Count | Should -Be 1
        $result.Output | Should -Match 'Shared file propagation: OK for 1 consumer\(s\)'
    }

    It 'returns NOT MEASURED when no consumer checkout exists' {
        $emptyRoot = Join-Path $fixture 'empty root'
        $null = New-Item -ItemType Directory -Path $emptyRoot -Force

        $result = Invoke-FixtureValidation -Roots @($emptyRoot)

        $result.ExitCode | Should -Be 3 -Because $result.Output
        $result.Output | Should -Match 'Shared file propagation: NOT MEASURED for 1 consumer\(s\)'
        $result.Output | Should -Not -Match 'Shared file propagation: OK'
    }

    It 'wires the validator into local-ci as a NOT MEASURED-aware Invoke-Step' {
        $localCi = [IO.File]::ReadAllText((Join-Path $repository 'scripts/local-ci.ps1'))

        $localCi | Should -Match "Invoke-Step 'Validate shared file propagation' -NotMeasuredExitCode 3 \{"
        $localCi | Should -Match '& \(Join-Path \$repoRoot ''scripts\\omp\\validate-shared-file-propagation\.ps1''\)'
        $localCi | Should -Match 'Write-Host "NOT MEASURED: \$Name"'
    }
}
