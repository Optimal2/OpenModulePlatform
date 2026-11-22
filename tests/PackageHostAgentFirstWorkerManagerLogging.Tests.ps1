# Pester 6's 'Should -Be' parameters are provided by the pinned Pester module
# (6.1.0), not by the inbox Pester 3.4.0 profile the compatibility rule
# measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()

# package-hostagent-first.ps1 registers an appsettings.json on the WorkerManager artifact.
# A registered file replaces the template HostAgent has built in, so this file is the
# configuration an installed WorkerManager runs with, and its file target needs the same
# 14-day limit as every other platform log file.

Describe 'package-hostagent-first.ps1: WorkerManager artifact configuration' {
    BeforeAll {
        $scriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\deployment\package-hostagent-first.ps1'
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$null)
        $definition = $ast.Find({ param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq 'New-WorkerManagerArtifactConfigurationFile'
        }, $true)
        $definition | Should -Not -BeNullOrEmpty -Because 'the packaging script must define the WorkerManager configuration file'
        . ([scriptblock]::Create($definition.Extent.Text))

        # The real writer serializes to a file; here the value is kept as it was built.
        function Write-OmpCanonicalJson {
            param([string]$Path, $Value)
            $script:WrittenPath = $Path
            $script:WrittenConfiguration = $Value
        }

        $script:Registration = New-WorkerManagerArtifactConfigurationFile -BuildRoot $TestDrive
        $script:CheckedIn = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'OpenModulePlatform.WorkerManager.WindowsService\appsettings.json') -Raw |
            ConvertFrom-Json
    }

    It 'Registers the file as the artifact appsettings.json' {
        $script:Registration.RelativePath | Should -Be 'appsettings.json'
        $script:WrittenPath | Should -Be $script:Registration.SourcePath
    }

    It 'Keeps 14 days of log files, like the checked-in configuration' {
        $logFile = $script:WrittenConfiguration.NLog.targets.logfile

        $logFile.type | Should -Be 'File'
        $logFile.maxArchiveDays | Should -Be 14
        $logFile.maxArchiveDays | Should -Be $script:CheckedIn.NLog.targets.logfile.maxArchiveDays
        $logFile.fileName | Should -Be $script:CheckedIn.NLog.targets.logfile.fileName
    }
}
