#Requires -Version 5.1
# Pester assertions are supplied by the repository's pinned module.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 assertions are provided by the pinned module.')]
param()

Describe 'Shared script platform root resolution' {
    BeforeAll {
        $script:RepoRoot = Split-Path -Parent $PSScriptRoot
        . (Join-Path $script:RepoRoot 'scripts\omp\validate-component-versions.helpers.ps1')
        $script:GuardScript = Join-Path $script:RepoRoot 'scripts\omp\validate-shared-scripts.ps1'

        function Invoke-SharedScriptGuard {
            param(
                [Parameter(Mandatory = $true)][string]$Consumer,
                [Parameter(Mandatory = $true)][string]$Platform,
                [Parameter(Mandatory = $true)][string]$OtherPlatform
            )

            $names = @('OMP_PLATFORM_ROOT', 'OpenModulePlatformRoot')
            $saved = @{}
            foreach ($name in $names) {
                $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            }

            try {
                [Environment]::SetEnvironmentVariable('OMP_PLATFORM_ROOT', $Platform, 'Process')
                [Environment]::SetEnvironmentVariable('OpenModulePlatformRoot', $OtherPlatform, 'Process')
                $engine = (Get-Process -Id $PID).Path
                $arguments = @(
                    '-NoProfile', '-NonInteractive', '-File', $script:GuardScript,
                    '-ConsumerRepositoryRoot', $Consumer,
                    '-PlatformRepositoryRoot', $Platform
                )
                $previousErrorActionPreference = $ErrorActionPreference
                $ErrorActionPreference = 'Continue'
                try {
                    $output = & $engine @arguments 2>&1 | Out-String
                    $exitCode = $LASTEXITCODE
                }
                finally {
                    $ErrorActionPreference = $previousErrorActionPreference
                }
                return @{ ExitCode = $exitCode; Output = $output }
            }
            finally {
                foreach ($name in $names) {
                    [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
                }
            }
        }
    }

    It 'Rejects a <Name> platform root instead of resolving against process state' -TestCases @(
        @{ Name = 'drive-relative'; Path = 'C:folder' },
        @{ Name = 'root-relative'; Path = '\folder' }
    ) {
        param($Name, $Path)

        $basePath = Join-Path $TestDrive 'Consumer'
        { Get-OmpPlatformFullPath -Path $Path -BasePath $basePath } |
            Should -Throw -ExpectedMessage '*drive-relative or root-relative*fully qualified*'
    }

    It 'Reports conflicting platform roots as a guarded exit code' {
        $consumer = Join-Path $TestDrive 'Consumer'
        $platform = Join-Path $TestDrive 'Platform'
        $otherPlatform = Join-Path $TestDrive 'OtherPlatform'
        $null = New-Item -ItemType Directory -Path $consumer, $platform, $otherPlatform -Force

        $result = Invoke-SharedScriptGuard -Consumer $consumer -Platform $platform -OtherPlatform $otherPlatform

        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match 'Shared scripts: platform root resolution failed:'
        $result.Output | Should -Match 'different platform checkouts'
    }
}
