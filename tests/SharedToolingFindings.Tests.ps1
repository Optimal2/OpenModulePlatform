[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Assertions use the repository-pinned Pester 6.1.0 module.')]
param()

Describe 'Shared tooling version bump regressions' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Bump-Version.TestHelpers.ps1')

        # Hash via .NET rather than Get-FileHash: on a workstation whose PSModulePath also lists
        # PowerShell 7 module folders, Windows PowerShell 5.1 loads a hybrid Utility module without
        # Get-FileHash and the gate fails for an environmental reason (same as HostAgentFirstModuleSeed.Tests.ps1).
        function Get-TestFileHash {
            param([string]$Path)
            [Convert]::ToBase64String([System.Security.Cryptography.SHA256]::Create().ComputeHash([System.IO.File]::ReadAllBytes($Path)))
        }

        function Invoke-IsolatedBump {
            param([string]$Path, [hashtable]$Parameters)
            # A separate runspace contains the script's exit without starting a shell.
            $engine = [PowerShell]::Create()
            try {
                $null = $engine.AddCommand($Path).AddParameters($Parameters)
                $output = @($engine.Invoke())
                [pscustomobject]@{
                    Failed = $engine.HadErrors
                    Errors = ($engine.Streams.Error | Out-String)
                    Verbose = @($engine.Streams.Verbose | ForEach-Object { $_.Message })
                    Output = $output
                }
            }
            finally { $engine.Dispose() }
        }
    }

    BeforeEach {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bump = New-TemporaryBumpRepository -RootPath $repoRoot
        $manifestPath = Join-Path $repoRoot 'omp-components.json'
        $definitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'
    }

    It 'Bumps a repeated component key once, including case variants' {
        $result = Invoke-IsolatedBump $bump @{ ComponentKey = @('test_app', 'TEST_APP', 'test_app') }
        $result.Failed | Should -BeFalse
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $manifest.components[0].version | Should -Be '1.0.1'
    }

    It 'Processes a repeated widget path only once' {
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $manifest | Add-Member widgetFiles @([pscustomobject]@{ path = 'widget.json'; widgetVersion = '1.0.0' })
        $manifest | ConvertTo-Json -Depth 8 | Set-Content $manifestPath -Encoding UTF8
        $widgetPath = Join-Path $repoRoot 'widget.json'
        '{"packageVersion":"1.0.0","widgets":[{"widgetVersion":"1.0.0"}]}' | Set-Content $widgetPath -Encoding UTF8
        $result = Invoke-IsolatedBump $bump @{ WidgetFile = @('widget.json', 'WIDGET.JSON', 'widget.json'); Verbose = $true }
        $result.Failed | Should -BeFalse
        @($result.Output | Where-Object {
            $_.GetType().Name -eq 'FormatEntryData' -and
            $_.formatEntryInfo.formatPropertyFieldList.propertyValue -contains 'dashboard-widget'
        }).Count | Should -Be 1
        (Get-Content $widgetPath -Raw | ConvertFrom-Json).packageVersion | Should -Be '1.0.1'
    }

    It 'Refuses explicit version <Version> before writing' -ForEach @(
        @{ Version = '1.2.3-beta' }
        @{ Version = '1.2.3-01a' }
    ) {
        $before = Get-TestFileHash $manifestPath
        $result = Invoke-IsolatedBump $bump @{ RepositoryOnly = $true; Version = $Version }
        $result.Failed | Should -BeTrue
        ($result.Errors -replace '\s+', '') | Should -Match ([regex]::Escape("Version'$Version'isnotallowed:OMPversionsmustbeplain"))
        (Get-TestFileHash $manifestPath) | Should -Be $before
    }

    It 'Refuses an existing suffixed repositoryVersion even when skipping the repository bump' {
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $manifest.repositoryVersion = '1.2.3+z'
        $manifest | ConvertTo-Json -Depth 8 | Set-Content $manifestPath -Encoding UTF8
        $before = Get-TestFileHash $manifestPath
        $result = Invoke-IsolatedBump $bump @{ ComponentKey = @('test_app'); SkipRepositoryVersion = $true }
        $result.Failed | Should -BeTrue
        ($result.Errors -replace '\s+', '') | Should -Match "Version'1\.2\.3\+z'isnotallowed:OMPversionsmustbeplain"
        (Get-TestFileHash $manifestPath) | Should -Be $before
    }

    It 'Rejects a missing selected definition before writing any selected file' {
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $manifest.moduleDefinitions += [pscustomobject]@{ moduleKey = 'missing'; definitionVersion = '1.0.0'; path = 'missing.json' }
        $manifest | ConvertTo-Json -Depth 8 | Set-Content $manifestPath -Encoding UTF8
        $manifestBefore = (Get-TestFileHash $manifestPath)
        $definitionBefore = (Get-TestFileHash $definitionPath)
        $result = Invoke-IsolatedBump $bump @{ AllModuleDefinitions = $true; ComponentKey = @('test_app') }
        $result.Failed | Should -BeTrue
        # Windows PowerShell 5.1 wraps Out-String at the console width, so match across line breaks.
        ($result.Errors -replace '\s+', '') | Should -Match "Moduledefinition'missing'filewasnotfound:.*missing\.json"
        (Get-TestFileHash $manifestPath) | Should -Be $manifestBefore
        (Get-TestFileHash $definitionPath) | Should -Be $definitionBefore
    }

    It 'Refuses a suffixed compatible artifact cap before writing' {
        $definition = Get-Content $definitionPath -Raw | ConvertFrom-Json
        $definition.compatibleArtifacts | ForEach-Object { $_ | Add-Member maxVersion '1.2.3-beta.2' }
        $definition | ConvertTo-Json -Depth 8 | Set-Content $definitionPath -Encoding UTF8
        $manifestBefore = Get-TestFileHash $manifestPath
        $definitionBefore = Get-TestFileHash $definitionPath
        $result = Invoke-IsolatedBump $bump @{ ComponentKey = @('test_app'); Version = '1.2.3' }
        $result.Failed | Should -BeTrue
        ($result.Errors -replace '\s+', '') | Should -Match "Version'1\.2\.3-beta\.2'isnotallowed:.*compatibleArtifacts\.maxVersion"
        (Get-TestFileHash $manifestPath) | Should -Be $manifestBefore
        (Get-TestFileHash $definitionPath) | Should -Be $definitionBefore
    }

    It 'Preserves the bytes and version of a canonical BOM-bearing definition' {
        # First produce canonical JSON with a cap high enough to remain unchanged.
        $definition = Get-Content $definitionPath -Raw | ConvertFrom-Json
        $definition.compatibleArtifacts | ForEach-Object { $_ | Add-Member maxVersion '9.0.0' }
        $definition | ConvertTo-Json -Depth 8 | Set-Content $definitionPath -Encoding UTF8
        (Invoke-IsolatedBump $bump @{ ComponentKey = @('test_app') }).Failed | Should -BeFalse
        $before = Get-Content $definitionPath -Raw | ConvertFrom-Json
        [IO.File]::WriteAllText($definitionPath, [IO.File]::ReadAllText($definitionPath), [Text.UTF8Encoding]::new($true))
        $hashBefore = (Get-TestFileHash $definitionPath)
        (Invoke-IsolatedBump $bump @{ ComponentKey = @('test_app') }).Failed | Should -BeFalse
        $after = Get-Content $definitionPath -Raw | ConvertFrom-Json
        $after.definitionVersion | Should -Be $before.definitionVersion
        (Get-Content $manifestPath -Raw | ConvertFrom-Json).moduleDefinitions[0].definitionVersion | Should -Be $after.definitionVersion
        (Get-TestFileHash $definitionPath) | Should -Be $hashBefore
    }
}

Describe 'Wrapper summary success stream' {
    It 'Returns result objects including diagnostic fields while rendering the compact table to the host' {
        $source = Get-Content (Join-Path $PSScriptRoot '../scripts/omp/test-cmd-wrappers.ps1') -Raw
        # Exercise the actual final summary, without building packages or starting wrappers.
        $summary = [scriptblock]::Create($source.Substring($source.LastIndexOf('$results | Format-Table', [StringComparison]::Ordinal)))
        $ValidationSummaryColumns = @('Repository', 'Status', 'ExitCode', 'Package')
        $results = @(
            [pscustomobject]@{ Repository = 'sample'; Status = 'OK'; ExitCode = 0; Package = 'sample.zip'; StdoutLog = 'out.log'; StderrLog = 'err.log'; Detail = 'validated' }
        )
        Mock Out-Host {}
        $captured = @(& $summary)
        $captured.Count | Should -Be 1
        $captured[0].StdoutLog | Should -Be 'out.log'
        $captured[0].StderrLog | Should -Be 'err.log'
        $captured[0].Detail | Should -Be 'validated'
        Should -Invoke Out-Host -Times 1 -Exactly -ParameterFilter { $InputObject.GetType().Name -eq 'FormatEntryData' }
    }
}
