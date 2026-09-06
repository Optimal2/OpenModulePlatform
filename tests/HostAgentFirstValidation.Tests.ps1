# Pester assertions are supplied by the repository's pinned module.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 5 assertions are provided by the pinned module.')]
param()

Describe 'HostAgent-first validation before copying' {
    BeforeAll {
        $sourcePath = Join-Path $PSScriptRoot '..\scripts\deployment\package-hostagent-first.ps1'
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$errors)
        $errors.Count | Should -Be 0
        $functionDefinitions = foreach ($name in @('Test-ModuleDefinitionSources', 'Resolve-DeploymentPath', 'Get-ProjectNameFromComponent')) {
            $function = $ast.Find({ param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
            }, $true)
            $function.Extent.Text
        }
    }
    BeforeEach {
        $fixture = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $validatorRoot = Join-Path $fixture 'omp'
        $null = New-Item -ItemType Directory -Path $validatorRoot -Force
        $fixtureScriptRoot = Join-Path $fixture 'deployment'
        $null = New-Item -ItemType Directory -Path $fixtureScriptRoot -Force
        # Load the unchanged production functions from the fixture so their
        # PSScriptRoot resolves the fixture validators, never the real scripts.
        $fixtureScript = Join-Path $fixtureScriptRoot 'functions.ps1'
        [System.IO.File]::WriteAllText($fixtureScript, ($functionDefinitions -join [Environment]::NewLine))
        . $fixtureScript
        $definitionValidator = Join-Path $validatorRoot 'validate-module-definitions.ps1'
        $sqlValidator = Join-Path $validatorRoot 'Test-ModuleSqlGuards.ps1'
        [System.IO.File]::WriteAllText($definitionValidator, 'param($RepositoryRoot) $global:LASTEXITCODE = 0')
        [System.IO.File]::WriteAllText($sqlValidator, 'param([string[]]$Path) $global:LASTEXITCODE = 0; "SQL INPUT COUNT: $($Path.Count)"')
        $roots = @(Join-Path $fixture 'first'; Join-Path $fixture 'second')
        foreach ($root in $roots) {
            $null = New-Item -ItemType Directory -Path (Join-Path $root 'scripts\omp') -Force
            [System.IO.File]::WriteAllText((Join-Path $root 'omp-components.json'), '{"moduleDefinitions":[{"path":"module.json"}]}')
            [System.IO.File]::WriteAllText((Join-Path $root 'scripts\omp\validate-component-versions.ps1'), '$global:LASTEXITCODE = 0')
        }
    }

    It 'runs the SQL validator once with inputs from every source' {
        $output = @(Test-ModuleDefinitionSources -RepositoryRoots $roots)
        $output.Count | Should -Be 1
        $output[0] | Should -Be 'SQL INPUT COUNT: 2'
    }

    It 'fails on a nonzero definition validator exit even without a thrown error' {
        [System.IO.File]::WriteAllText($definitionValidator, 'param($RepositoryRoot) $global:LASTEXITCODE = 7')
        { Test-ModuleDefinitionSources -RepositoryRoots $roots } | Should -Throw '*Module definition validation failed*7*'
    }

    It 'fails on a nonzero SQL validator exit even without a thrown error' {
        [System.IO.File]::WriteAllText($sqlValidator, 'param([string[]]$Path) $global:LASTEXITCODE = 8')
        { Test-ModuleDefinitionSources -RepositoryRoots $roots } | Should -Throw '*Module SQL guard validation failed*8*'
    }

    It 'still requires each owning repository version validator' {
        [System.IO.File]::Delete((Join-Path $roots[1] 'scripts\omp\validate-component-versions.ps1'))
        { Test-ModuleDefinitionSources -RepositoryRoots $roots } | Should -Throw '*owning repository version validator is required*'
    }

    It 'still rejects an empty source even when another source has definitions' {
        [System.IO.File]::WriteAllText((Join-Path $roots[1] 'omp-components.json'), '{"moduleDefinitions":[]}')
        { Test-ModuleDefinitionSources -RepositoryRoots $roots } | Should -Throw '*No module SQL inputs selected*second*'
    }

    It 'still fails on a version rejection in a later source' {
        [System.IO.File]::WriteAllText((Join-Path $roots[1] 'scripts\omp\validate-component-versions.ps1'), '$global:LASTEXITCODE = 9')
        { Test-ModuleDefinitionSources -RepositoryRoots $roots } | Should -Throw '*Component version validation failed*second*9*'
    }

    It 'reports a missing project directory with the component-specific message' {
        { Get-ProjectNameFromComponent -RepositoryRoot $fixture -Component @{ componentKey = 'probe'; projectPath = 'missing' } } |
            Should -Throw '*Component ''probe'' project file was not found below*missing*'
    }
}
