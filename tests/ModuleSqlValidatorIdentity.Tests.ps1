# Pester assertions are supplied by the repository's pinned module.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 assertions are provided by the pinned module.')]
param()

Describe 'Module SQL validator cache identity' {
    BeforeAll {
        $script:IdentitySource = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\omp\get-module-sql-validator-identity.ps1'

        function Invoke-ValidatorIdentity {
            param([Parameter(Mandatory = $true)][string]$Root)

            $scriptPath = Join-Path $Root 'scripts\omp\get-module-sql-validator-identity.ps1'
            $json = (& $scriptPath | Out-String).Trim()
            if ($LASTEXITCODE -ne 0) { throw "Identity script failed ($LASTEXITCODE): $json" }
            return $json | ConvertFrom-Json
        }
    }

    BeforeEach {
        $script:Root = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $script:Scripts = Join-Path $script:Root 'scripts\omp'
        $script:Tools = Join-Path $script:Root 'tools\ModuleSqlGuard'
        $null = New-Item -ItemType Directory -Path $script:Scripts -Force
        $null = New-Item -ItemType Directory -Path $script:Tools -Force
        Copy-Item -LiteralPath $script:IdentitySource -Destination (Join-Path $script:Scripts 'get-module-sql-validator-identity.ps1')
        [IO.File]::WriteAllText((Join-Path $script:Scripts 'Test-ModuleSqlGuards.ps1'), '# guard v1')
        [IO.File]::WriteAllText((Join-Path $script:Scripts 'validate-module-definitions.ps1'), '# definitions v1')
        [IO.File]::WriteAllText((Join-Path $script:Tools 'Program.cs'), '// validator v1')
        [IO.File]::WriteAllText((Join-Path $script:Tools 'ModuleSqlGuard.csproj'), '<Project Sdk="Microsoft.NET.Sdk" />')
        & git -C $script:Root init --quiet
        & git -C $script:Root add -A
        & git -C $script:Root -c user.name=omp-test -c user.email=omp-test@example.invalid commit --quiet -m 'validator fixture'
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the validator identity fixture.' }
        $script:Head = (& git -C $script:Root rev-parse HEAD).Trim()
    }

    It 'Returns the resolved checkout and HEAD and is stable without an input change' {
        $first = Invoke-ValidatorIdentity -Root $script:Root
        $second = Invoke-ValidatorIdentity -Root $script:Root

        $first.schemaVersion | Should -Be 1
        $first.platformRoot | Should -Be $script:Root
        $first.headCommit | Should -Be $script:Head
        $first.contentSha256 | Should -Match '^[0-9a-f]{64}$'
        $first.cacheKey | Should -Match '^[0-9a-f]{64}$'
        $second.cacheKey | Should -Be $first.cacheKey
    }

    It 'Changes for uncommitted edits to either validator or the tool source' {
        $original = Invoke-ValidatorIdentity -Root $script:Root
        $inputs = @(
            (Join-Path $script:Scripts 'Test-ModuleSqlGuards.ps1'),
            (Join-Path $script:Scripts 'validate-module-definitions.ps1'),
            (Join-Path $script:Tools 'Program.cs')
        )
        foreach ($input in $inputs) {
            [IO.File]::AppendAllText($input, [Environment]::NewLine + '# uncommitted guard change')
            $changed = Invoke-ValidatorIdentity -Root $script:Root
            $changed.contentSha256 | Should -Not -Be $original.contentSha256
            $changed.cacheKey | Should -Not -Be $original.cacheKey
        }
    }

    It 'Includes untracked tool sources and excludes generated bin and obj content' {
        $original = Invoke-ValidatorIdentity -Root $script:Root
        [IO.File]::WriteAllText((Join-Path $script:Tools 'NewGuard.cs'), '// new uncommitted source')
        $withSource = Invoke-ValidatorIdentity -Root $script:Root
        $withSource.contentSha256 | Should -Not -Be $original.contentSha256

        foreach ($folder in @('bin', 'obj')) {
            $output = Join-Path $script:Tools $folder
            $null = New-Item -ItemType Directory -Path $output -Force
            [IO.File]::WriteAllText((Join-Path $output 'generated.txt'), 'not a validator input')
        }
        $withOutput = Invoke-ValidatorIdentity -Root $script:Root
        $withOutput.contentSha256 | Should -Be $withSource.contentSha256
    }
}
