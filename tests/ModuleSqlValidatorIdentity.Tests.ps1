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
        Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $script:IdentitySource) 'module-sql-validator-support.ps1') -Destination $script:Scripts
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

    Context 'Copied on its own into a consumer fixture' {
        # Consumer test suites build a stand-in platform checkout and copy this one script
        # into it: beside stand-ins for the two validator scripts, with or without a tool
        # folder, and never with module-sql-validator-support.ps1. The script has to work
        # there, so it must not depend on any neighbouring file.
        BeforeEach {
            $script:Alone = Join-Path $TestDrive ('alone-' + [Guid]::NewGuid().ToString('N'))
            $script:AloneScripts = Join-Path $script:Alone 'scripts\omp'
            $null = New-Item -ItemType Directory -Path $script:AloneScripts -Force
            Set-Content -LiteralPath (Join-Path $script:AloneScripts 'Test-ModuleSqlGuards.ps1') -Value '# guard stand-in' -Encoding ASCII
            Set-Content -LiteralPath (Join-Path $script:AloneScripts 'validate-module-definitions.ps1') -Value '# definition validator stand-in' -Encoding ASCII
            Copy-Item -LiteralPath $script:IdentitySource -Destination $script:AloneScripts

            function Complete-AloneFixture {
                & git -C $script:Alone -c init.defaultBranch=main init --quiet
                & git -C $script:Alone add -A
                & git -C $script:Alone -c user.name=omp-test -c user.email=omp-test@example.invalid commit --quiet -m 'fixture commit'
                if ($LASTEXITCODE -ne 0) { throw 'Could not create the stand-alone identity fixture.' }
            }
        }

        It 'Produces an identity with only the two validator stand-ins beside it' {
            Complete-AloneFixture
            @(Get-ChildItem -LiteralPath $script:AloneScripts -File).Name | Sort-Object |
                Should -Be @('get-module-sql-validator-identity.ps1', 'Test-ModuleSqlGuards.ps1', 'validate-module-definitions.ps1')

            $identity = Invoke-ValidatorIdentity -Root $script:Alone

            $identity.schemaVersion | Should -Be 1
            $identity.platformRoot | Should -Be $script:Alone
            $identity.contentSha256 | Should -Match '^[0-9a-f]{64}$'
            $identity.cacheKey | Should -Match '^[0-9a-f]{64}$'
            (Invoke-ValidatorIdentity -Root $script:Alone).cacheKey | Should -Be $identity.cacheKey

            [IO.File]::AppendAllText((Join-Path $script:AloneScripts 'Test-ModuleSqlGuards.ps1'), 'dirty')
            (Invoke-ValidatorIdentity -Root $script:Alone).cacheKey | Should -Not -Be $identity.cacheKey
        }

        It 'Produces an identity with a tool folder that has sources but no project file' {
            $tool = Join-Path $script:Alone 'tools\ModuleSqlGuard'
            $null = New-Item -ItemType Directory -Path $tool -Force
            [IO.File]::WriteAllText((Join-Path $tool 'Guard.cs'), 'clean fixture')
            Complete-AloneFixture

            $identity = Invoke-ValidatorIdentity -Root $script:Alone
            $identity.cacheKey | Should -Match '^[0-9a-f]{64}$'

            [IO.File]::AppendAllText((Join-Path $tool 'Guard.cs'), 'dirty')
            (Invoke-ValidatorIdentity -Root $script:Alone).cacheKey | Should -Not -Be $identity.cacheKey
            & git -C $script:Alone checkout -- 'tools/ModuleSqlGuard/Guard.cs'
            (Invoke-ValidatorIdentity -Root $script:Alone).cacheKey | Should -Be $identity.cacheKey
        }

        It 'Has no reference to a neighbouring script file' {
            # The cheap guard against the dependency coming back: no dot-sourcing and no
            # use of its own folder to find another file.
            $source = [IO.File]::ReadAllText($script:IdentitySource)
            $source | Should -Not -Match '(?m)^\s*\.\s+\('
            $source | Should -Not -Match 'Join-Path\s+\$PSScriptRoot\s+''[^''.]'
        }
    }

    Context 'A validator built from referenced projects and linked rule sources' {
        BeforeEach {
            # The shape of the real validator: the tool project references a runtime
            # project, which references a second project and compiles the rule in from a
            # folder that belongs to no project.
            $script:Runtime = Join-Path $script:Root 'Runtime'
            $script:Contracts = Join-Path $script:Root 'Contracts'
            $script:Shared = Join-Path $script:Root 'shared'
            $script:Unrelated = Join-Path $script:Root 'Portal'
            foreach ($folder in @($script:Runtime, $script:Contracts, $script:Shared, $script:Unrelated)) {
                $null = New-Item -ItemType Directory -Path $folder -Force
            }
            [IO.File]::WriteAllText((Join-Path $script:Tools 'ModuleSqlGuard.csproj'),
                '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../../Runtime/Runtime.csproj" /></ItemGroup></Project>')
            [IO.File]::WriteAllText((Join-Path $script:Runtime 'Runtime.csproj'),
                '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\Contracts\Contracts.csproj" /><Compile Include="..\shared\OwnershipRule.cs" Link="Rules\OwnershipRule.cs" /></ItemGroup></Project>')
            [IO.File]::WriteAllText((Join-Path $script:Runtime 'Service.cs'), '// runtime v1')
            [IO.File]::WriteAllText((Join-Path $script:Contracts 'Contracts.csproj'), '<Project Sdk="Microsoft.NET.Sdk" />')
            [IO.File]::WriteAllText((Join-Path $script:Contracts 'Contract.cs'), '// contract v1')
            [IO.File]::WriteAllText((Join-Path $script:Shared 'OwnershipRule.cs'), '// rule v1')
            [IO.File]::WriteAllText((Join-Path $script:Shared 'NotCompiledIn.cs'), '// not part of the validator')
            [IO.File]::WriteAllText((Join-Path $script:Unrelated 'Page.cs'), '// portal v1')
            [IO.File]::WriteAllText((Join-Path $script:Root 'Directory.Build.props'), '<Project />')
        }

        It 'Changes when <Name> changes' -ForEach @(
            @{ Name = 'the rule source compiled in from outside every project'; Path = 'shared\OwnershipRule.cs' }
            @{ Name = 'a source of the directly referenced project'; Path = 'Runtime\Service.cs' }
            @{ Name = 'a source of a transitively referenced project'; Path = 'Contracts\Contract.cs' }
            @{ Name = 'a referenced project file'; Path = 'Runtime\Runtime.csproj' }
            @{ Name = 'the repository-wide build property file'; Path = 'Directory.Build.props' }
            @{ Name = 'the helper both validator scripts share'; Path = 'scripts\omp\module-sql-validator-support.ps1' }
        ) {
            $original = Invoke-ValidatorIdentity -Root $script:Root
            [IO.File]::AppendAllText((Join-Path $script:Root $Path), ' ')
            $changed = Invoke-ValidatorIdentity -Root $script:Root

            $changed.contentSha256 | Should -Not -Be $original.contentSha256
            $changed.cacheKey | Should -Not -Be $original.cacheKey
        }

        It 'Stays the same when <Name> changes' -ForEach @(
            @{ Name = 'a project the validator does not reference'; Path = 'Portal\Page.cs' }
            @{ Name = 'a file beside the rule that no project compiles in'; Path = 'shared\NotCompiledIn.cs' }
            @{ Name = 'build output of a referenced project'; Path = 'Runtime\obj\generated.cs' }
            @{ Name = 'package output staged below a referenced project'; Path = 'Runtime\artifacts\staged\Copy.cs' }
            @{ Name = 'an editor folder below a referenced project'; Path = 'Runtime\.vs\cache.cs' }
        ) {
            $original = Invoke-ValidatorIdentity -Root $script:Root
            $target = Join-Path $script:Root $Path
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force
            [IO.File]::AppendAllText($target, '// changed')
            $changed = Invoke-ValidatorIdentity -Root $script:Root

            $changed.contentSha256 | Should -Be $original.contentSha256
        }

        It 'Fails instead of describing a validator whose project list cannot be followed' {
            Remove-Item -LiteralPath (Join-Path $script:Contracts 'Contracts.csproj')

            { Invoke-ValidatorIdentity -Root $script:Root } | Should -Throw '*references a project that does not exist*'
        }
    }
}
