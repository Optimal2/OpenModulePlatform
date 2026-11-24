# Pester 6's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (6.1.0), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
Pester tests for scripts/omp/validate-webshared-contracts.ps1.

.DESCRIPTION
The scanner reads omp-components.json and validates every sharedProjects
entry plus its consumers. The fixture is a synthetic repository under
$TestDrive: one source-directory shared project ('shared'), one .csproj
shared project (OpenModulePlatform.Web.Shared), one fully compliant Web.Shared
consumer ('web-app', also listed under 'shared') and one non-Web.Shared
consumer ('service', listed only under 'shared').

Covered contract points, each proven by breaking it:

- A shared projectPath that is a source directory is accepted, and each
  Web.Shared consumer is scanned exactly once: 'web-app' passes once even
  though it is listed under two sharedProjects entries, and 'service' gets
  no Web.Shared warnings.
- A missing source directory is a hard error naming the projectPath.
- A missing .csproj file is a hard error naming the projectPath.
- A directory sitting at a declared .csproj path is a hard error: a .csproj
  entry must be a file.

The validator script ends with 'exit', so like the other script suites it is
invoked in-process with the call operator (exit then sets $LASTEXITCODE
without ending the test process) and 6>&1 merges its Write-Host contract
lines into the captured output.
#>

Describe 'validate-webshared-contracts.ps1' {
    BeforeAll {
        $script:validator = Join-Path $PSScriptRoot '..\scripts\omp\validate-webshared-contracts.ps1'
        $script:webSharedProjectPath = 'OpenModulePlatform.Web.Shared/OpenModulePlatform.Web.Shared.csproj'
        $script:utf8NoBom = [System.Text.UTF8Encoding]::new($false)

        function Save-TextFile {
            param(
                [Parameter(Mandatory = $true)][string]$Path,
                [Parameter(Mandatory = $true)][string]$Content
            )

            $parent = Split-Path -Parent $Path
            if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
                $null = New-Item -ItemType Directory -Path $parent -Force
            }
            [System.IO.File]::WriteAllText($Path, $Content, $script:utf8NoBom)
        }

        function New-WebSharedFixture {
            $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
            $null = New-Item -ItemType Directory -Path (Join-Path $root 'shared')
            $null = New-Item -ItemType Directory -Path (Join-Path $root 'OpenModulePlatform.Web.Shared')
            $null = New-Item -ItemType Directory -Path (Join-Path $root 'WebApp')
            $null = New-Item -ItemType Directory -Path (Join-Path $root 'Service')
            Save-TextFile -Path (Join-Path $root $script:webSharedProjectPath) -Content '<Project />'
            Save-TextFile -Path (Join-Path $root 'WebApp\Program.cs') -Content 'AddOmpWebDefaults(); UseOmpWebDefaults();'
            Save-TextFile -Path (Join-Path $root 'WebApp\_ViewImports.cshtml') -Content '@using OpenModulePlatform.Web.Shared'
            Save-TextFile -Path (Join-Path $root 'WebApp\_Layout.cshtml') -Content 'PortalTopBar'
            $manifest = @{
                sharedProjects = @(
                    @{ projectPath = 'shared'; consumers = @('web-app', 'service') }
                    @{ projectPath = $script:webSharedProjectPath; consumers = @('web-app') }
                )
                components = @(
                    @{ componentKey = 'web-app'; projectPath = 'WebApp' }
                    @{ componentKey = 'service'; projectPath = 'Service' }
                )
            }
            Save-TextFile -Path (Join-Path $root 'omp-components.json') -Content ($manifest | ConvertTo-Json -Depth 5)
            return $root
        }

        function Invoke-WebSharedValidator {
            param([Parameter(Mandatory = $true)][string]$Root)

            $global:LASTEXITCODE = 0
            $records = & $script:validator -RepositoryRoot $Root 6>&1
            $exitCode = $LASTEXITCODE
            # Join the record strings, not Out-String: Windows PowerShell 5.1
            # wraps the rendered output at the console width, splitting the
            # matched lines.
            $output = (@($records | ForEach-Object { "$_" })) -join "`n"
            return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
        }
    }

    It 'accepts a source directory and scans each Web.Shared consumer once' {
        $root = New-WebSharedFixture
        $result = Invoke-WebSharedValidator -Root $root
        $result.ExitCode | Should -Be 0 -Because $result.Output
        @($result.Output -split "`n" | Where-Object { $_ -eq '[PASS] web-app' }).Count | Should -Be 1
        $result.Output | Should -Not -Match '\[WARN\]'
    }

    It 'fails when a declared source directory is missing' {
        $root = New-WebSharedFixture
        Remove-Item -LiteralPath (Join-Path $root 'shared') -Force
        $result = Invoke-WebSharedValidator -Root $root
        $result.ExitCode | Should -Be 1 -Because $result.Output
        $result.Output | Should -Match '(?m)^ - Shared project path is missing or has the wrong type: shared$'
    }

    It 'fails when a declared shared project file is missing' {
        $root = New-WebSharedFixture
        Remove-Item -LiteralPath (Join-Path $root $script:webSharedProjectPath) -Force
        $result = Invoke-WebSharedValidator -Root $root
        $result.ExitCode | Should -Be 1 -Because $result.Output
        $result.Output | Should -Match ('(?m)^ - Shared project path is missing or has the wrong type: ' + [regex]::Escape($script:webSharedProjectPath) + '$')
    }

    It 'requires a file at a declared csproj path' {
        $root = New-WebSharedFixture
        $projectPath = Join-Path $root $script:webSharedProjectPath
        Remove-Item -LiteralPath $projectPath -Force
        $null = New-Item -ItemType Directory -Path $projectPath
        $result = Invoke-WebSharedValidator -Root $root
        $result.ExitCode | Should -Be 1 -Because $result.Output
        $result.Output | Should -Match ('(?m)^ - Shared project path is missing or has the wrong type: ' + [regex]::Escape($script:webSharedProjectPath) + '$')
    }
}
