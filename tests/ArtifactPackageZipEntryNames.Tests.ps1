# Pester 6's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (6.1.0), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
Pester tests for zip entry name separators in
scripts/deployment/new-omp-artifact-package.ps1.

.DESCRIPTION
Under Windows PowerShell 5.1's inbox Archive module 1.0.1.0, Compress-Archive
writes BACKSLASH zip entry names (configuration\001-..., payload\artifact.zip,
bin\app.dll). The consumers of these packages read entries by their forward-
slash names, so a package built on a clean 5.1 host was unreadable (measured
2026-10-07). These tests build a package through BOTH hosts and require every
entry name -- outer package and nested payload zip -- to use '/'.

The 5.1 leg is red on the unfixed script on any host whose Archive module is
the inbox 1.0.1.0 (a clean Windows PowerShell 5.1 install); it is green once
the script writes entries through System.IO.Compression with explicit names.
#>

Describe 'new-omp-artifact-package.ps1 zip entry names' {
    BeforeAll {
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $script:packageScript = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\deployment\new-omp-artifact-package.ps1')

        function New-PackageFixture {
            <#
            .SYNOPSIS
            Creates a minimal payload directory (including a SUBDIRECTORY
            file, so at least one entry name needs a separator) and a
            configuration file, in a throwaway temp root.
            #>
            param([Parameter(Mandatory = $true)][string]$RootPath)

            if (Test-Path -LiteralPath $RootPath -PathType Container) {
                Remove-Item -LiteralPath $RootPath -Recurse -Force
            }

            $payloadDir = Join-Path $RootPath 'payload-src'
            $null = New-Item -ItemType Directory -Path (Join-Path $payloadDir 'bin') -Force
            [System.IO.File]::WriteAllText((Join-Path $payloadDir 'web.config'), '<configuration />', [System.Text.UTF8Encoding]::new($false))
            [System.IO.File]::WriteAllText((Join-Path $payloadDir 'bin\app.dll'), 'fake-dll-bytes', [System.Text.UTF8Encoding]::new($false))

            $configSource = Join-Path $RootPath 'odv.site.config.js'
            [System.IO.File]::WriteAllText($configSource, 'window.odv = {};', [System.Text.UTF8Encoding]::new($false))

            return [pscustomobject]@{
                PayloadDir   = $payloadDir
                ConfigSource = $configSource
                OutputDir    = (Join-Path $RootPath 'out')
            }
        }

        function Invoke-PackageBuild {
            <#
            .SYNOPSIS
            Runs the packaging script as a child process under the given host
            executable, returning exit code and output.
            #>
            param(
                [Parameter(Mandatory = $true)][string]$HostExecutable,
                [Parameter(Mandatory = $true)][object]$Fixture
            )

            $arguments = @(
                '-NoProfile', '-File', $script:packageScript,
                '-ModuleKey', 'test_module',
                '-AppKey', 'test_app',
                '-PackageType', 'web-app',
                '-TargetName', 'test-app',
                '-Version', '1.0.0',
                '-PayloadPath', $Fixture.PayloadDir,
                '-OutputPath', $Fixture.OutputDir,
                '-ConfigurationFile', ('config/odv.site.config.js=' + $Fixture.ConfigSource)
            )

            $output = ''
            $exitCode = $null
            $previousErrorActionPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $output = & $HostExecutable @arguments 2>&1 | Out-String -Width 4096
            }
            catch {
                $output += $_.Exception.Message
            }
            finally {
                $exitCode = $LASTEXITCODE
                $ErrorActionPreference = $previousErrorActionPreference
            }

            return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
        }

        function Get-ZipEntryNames {
            param([Parameter(Mandatory = $true)][string]$ZipPath)

            $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
            try {
                return @($archive.Entries | ForEach-Object { $_.FullName })
            }
            finally {
                $archive.Dispose()
            }
        }

        function Get-NestedPayloadEntryNames {
            <#
            .SYNOPSIS
            Entry names of the nested payload/artifact.zip inside the package.
            #>
            param([Parameter(Mandatory = $true)][string]$PackagePath)

            $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
            try {
                $payloadEntry = $archive.Entries | Where-Object {
                    [string]::Equals($_.FullName.Replace('\', '/'), 'payload/artifact.zip', [StringComparison]::OrdinalIgnoreCase)
                } | Select-Object -First 1
                if ($null -eq $payloadEntry) {
                    throw "Nested payload zip is missing from '$PackagePath'."
                }

                $memory = [System.IO.MemoryStream]::new()
                $stream = $payloadEntry.Open()
                try {
                    $stream.CopyTo($memory)
                }
                finally {
                    $stream.Dispose()
                }
                $memory.Position = 0

                $payloadArchive = [System.IO.Compression.ZipArchive]::new($memory, [System.IO.Compression.ZipArchiveMode]::Read)
                try {
                    return @($payloadArchive.Entries | ForEach-Object { $_.FullName })
                }
                finally {
                    $payloadArchive.Dispose()
                    $memory.Dispose()
                }
            }
            finally {
                $archive.Dispose()
            }
        }

        function Test-PackageEntryNames {
            <#
            .SYNOPSIS
            The shared assertions: every entry name in the outer package and
            in the nested payload zip uses '/' separators, and the entries the
            consumers read by name are present under their forward-slash paths.
            #>
            param([Parameter(Mandatory = $true)][string]$PackagePath)

            $outerNames = @(Get-ZipEntryNames -ZipPath $PackagePath)
            $outerNames.Count | Should -BeGreaterThan 0
            foreach ($name in $outerNames) {
                $name.Contains('\') | Should -BeFalse -Because "outer entry '$name' must use '/' separators"
            }
            $outerNames | Should -Contain 'omp-artifact-package.json'
            $outerNames | Should -Contain 'payload/artifact.zip'
            $outerNames | Should -Contain 'configuration/001-odv.site.config.js'

            $payloadNames = @(Get-NestedPayloadEntryNames -PackagePath $PackagePath)
            $payloadNames.Count | Should -BeGreaterThan 0
            foreach ($name in $payloadNames) {
                $name.Contains('\') | Should -BeFalse -Because "payload entry '$name' must use '/' separators"
            }
            $payloadNames | Should -Contain 'web.config'
            $payloadNames | Should -Contain 'bin/app.dll'
        }
    }

    It 'Writes forward-slash entry names under Windows PowerShell 5.1 (inbox Archive 1.0.1.0)' {
        $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $fixture = New-PackageFixture -RootPath $fixtureRoot
            $powershellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

            $result = Invoke-PackageBuild -HostExecutable $powershellExe -Fixture $fixture
            $result.ExitCode | Should -Be 0 -Because $result.Output

            $packagePath = Join-Path $fixture.OutputDir 'test_module__test_app__web-app__test-app__1.0.0.zip'
            Test-PackageEntryNames -PackagePath $packagePath
        }
        finally {
            if (Test-Path -LiteralPath $fixtureRoot -PathType Container) {
                Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
            }
        }
    }

    It 'Writes forward-slash entry names under PowerShell 7 (pwsh)' {
        $pwsh = Get-Command -Name pwsh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $pwsh) {
            Set-ItResult -Skipped -Because 'pwsh is not installed on this machine.'
            return
        }

        $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $fixture = New-PackageFixture -RootPath $fixtureRoot

            $result = Invoke-PackageBuild -HostExecutable $pwsh.Source -Fixture $fixture
            $result.ExitCode | Should -Be 0 -Because $result.Output

            $packagePath = Join-Path $fixture.OutputDir 'test_module__test_app__web-app__test-app__1.0.0.zip'
            Test-PackageEntryNames -PackagePath $packagePath
        }
        finally {
            if (Test-Path -LiteralPath $fixtureRoot -PathType Container) {
                Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
            }
        }
    }
}
