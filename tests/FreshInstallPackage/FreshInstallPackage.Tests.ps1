# Pester 6's 'Should -Be/-Not -Be/-Match/-Throw' parameters are provided by the
# pinned Pester module (6.1.0), not by the inbox Pester 3.4.0 profile the
# compatibility rule measures against; suppress for the whole file, not per
# assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
Pester tests for scripts/deployment/build-fresh-install-package.ps1.

.DESCRIPTION
Builds a throwaway fixture (a generic host profile plus a minimal gathered
payload source) and runs the builder against it with a fake installer exe and
a fake hosting bundle, so no dotnet publish and no Microsoft download happen
in the gate. Verifies the package layout, the sample-profile refusal, the
missing-payload failure, the hosting-bundle major check and the summary.
#>

Set-StrictMode -Version Latest

Describe 'build-fresh-install-package.ps1' {
    BeforeAll {
        # The suite may run from tests\ (full gate) or tests\FreshInstallPackage\
        # (focused run); walk up to the repository root.
        $script:repositoryRoot = $PSScriptRoot
        while ($script:repositoryRoot -and -not (Test-Path -LiteralPath (Join-Path $script:repositoryRoot 'scripts\deployment\build-fresh-install-package.ps1') -PathType Leaf)) {
            $script:repositoryRoot = Split-Path -Parent $script:repositoryRoot
        }
        if ([string]::IsNullOrWhiteSpace($script:repositoryRoot)) {
            throw 'Repository root not found above ' + $PSScriptRoot
        }

        $script:builder = Join-Path $script:repositoryRoot 'scripts\deployment\build-fresh-install-package.ps1'
        $script:workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-fresh-install-tests-' + [Guid]::NewGuid().ToString('N'))

        $script:profileFolder = Join-Path $script:workRoot 'profiles\web-server-01'
        New-Item -ItemType Directory -Path $script:profileFolder -Force | Out-Null
        $bootstrap = [ordered]@{
            schema = 'OpenModulePlatform.HostAgentFirstBootstrap.v1'
            profile = [ordered]@{ machineNames = @('WEB-SERVER-01') }
            sql = [ordered]@{ enabled = $false }
            artifactStoreRoot = 'C:\OMP\ArtifactStore'
            artifacts = @(
                [ordered]@{
                    source = 'data/global/artifacts/testmod__testapp__web-app__testapp__0.3.1.zip'
                    target = 'testmod/web/0.3.1'
                }
            )
            hostAgent = [ordered]@{
                enabled = $false
                packagePath = 'payload/OpenModulePlatform.HostAgent.WindowsService.zip'
                hostKey = 'WEB-SERVER-01'
            }
        }
        $bootstrap | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $script:profileFolder 'bootstrap.json') -Encoding UTF8

        $script:payloadSource = Join-Path $script:workRoot 'payload-source'
        New-Item -ItemType Directory -Path (Join-Path $script:payloadSource 'data\global\artifacts') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $script:payloadSource 'data\global\module-definitions') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $script:payloadSource 'payload') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $script:payloadSource 'data\global\artifacts\testmod__testapp__web-app__testapp__0.3.1.zip') -Value 'zip' -Encoding ASCII
        Set-Content -LiteralPath (Join-Path $script:payloadSource 'data\global\module-definitions\testmod.module-definition.json') -Value '{}' -Encoding ASCII
        Set-Content -LiteralPath (Join-Path $script:payloadSource 'payload\OpenModulePlatform.HostAgent.WindowsService.zip') -Value 'zip' -Encoding ASCII

        $script:fakeInstaller = Join-Path $script:workRoot 'OpenModulePlatform.Installer.exe'
        Set-Content -LiteralPath $script:fakeInstaller -Value 'exe' -Encoding ASCII

        $script:fakeBundle = Join-Path $script:workRoot 'dotnet-hosting-10.0.3-win.exe'
        Set-Content -LiteralPath $script:fakeBundle -Value 'bundle' -Encoding ASCII

        $script:outputRoot = Join-Path $script:workRoot 'out'

        $script:invokeBuilder = {
            param([string]$ProfilePath, [string]$BundlePath, [string]$PackageName)
            & $script:builder `
                -ProfilePath $ProfilePath `
                -PayloadSourceRoot $script:payloadSource `
                -InstallerExePath $script:fakeInstaller `
                -HostingBundlePath $BundlePath `
                -RuntimeMajor 10 `
                -OutputRoot $script:outputRoot `
                -PackageName $PackageName
        }
    }

    AfterAll {
        Remove-Item -LiteralPath $script:workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'builds the folder and the zip with the documented layout' {
        & $script:invokeBuilder -ProfilePath $script:profileFolder -BundlePath $script:fakeBundle -PackageName 'PkgOk' | Out-Null

        $packageRoot = Join-Path $script:outputRoot 'PkgOk'
        Test-Path -LiteralPath (Join-Path $packageRoot 'OpenModulePlatform.Installer.exe') -PathType Leaf | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $packageRoot 'README.txt') -PathType Leaf | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $packageRoot 'hosts\web-server-01\bootstrap.json') -PathType Leaf | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $packageRoot 'prereqs\dotnet-hosting-10.0.3-win.exe') -PathType Leaf | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $packageRoot 'data\global\artifacts\testmod__testapp__web-app__testapp__0.3.1.zip') -PathType Leaf | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $packageRoot 'data\global\module-definitions\testmod.module-definition.json') -PathType Leaf | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $packageRoot 'payload\OpenModulePlatform.HostAgent.WindowsService.zip') -PathType Leaf | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $script:outputRoot 'PkgOk.zip') -PathType Leaf | Should -BeTrue

        $readme = Get-Content -LiteralPath (Join-Path $packageRoot 'README.txt') -Raw -Encoding UTF8
        $readme | Should -Match '--dry-run'
        $readme | Should -Match 'SVENSKA'
        $readme | Should -Match 'ENGLISH'
    }

    It 'prints the summary with profile, machine names, bundle version and hash' {
        # Write-Host goes to the information stream; merge it to capture.
        $output = (& $script:invokeBuilder -ProfilePath $script:profileFolder -BundlePath $script:fakeBundle -PackageName 'PkgSummary' 6>&1 | Out-String)

        $output | Should -Match 'web-server-01'
        $output | Should -Match 'WEB-SERVER-01'
        $output | Should -Match 'dotnet-hosting-10\.0\.3-win\.exe'
        $output | Should -Match 'SHA-512'
    }

    It 'refuses a profile folder that still contains a sample config' {
        $sampleProfile = Join-Path $script:workRoot 'profiles\sample-profile'
        New-Item -ItemType Directory -Path $sampleProfile -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $script:profileFolder 'bootstrap.json') -Destination (Join-Path $sampleProfile 'bootstrap.json')
        Set-Content -LiteralPath (Join-Path $sampleProfile 'bootstrap.local.sample.json') -Value '{}' -Encoding UTF8

        { & $script:invokeBuilder -ProfilePath $sampleProfile -BundlePath $script:fakeBundle -PackageName 'PkgSample' } | Should -Throw '*Sample profiles*'
        Test-Path -LiteralPath (Join-Path $script:outputRoot 'PkgSample') | Should -BeFalse
    }

    It 'fails when a referenced artifact payload is missing' {
        $brokenProfile = Join-Path $script:workRoot 'profiles\broken'
        New-Item -ItemType Directory -Path $brokenProfile -Force | Out-Null
        $bootstrap = Get-Content -LiteralPath (Join-Path $script:profileFolder 'bootstrap.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $bootstrap.artifacts[0].source = 'data/global/artifacts/missing__app__web-app__app__9.9.9.zip'
        $bootstrap | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $brokenProfile 'bootstrap.json') -Encoding UTF8

        { & $script:invokeBuilder -ProfilePath $brokenProfile -BundlePath $script:fakeBundle -PackageName 'PkgBroken' } | Should -Throw '*Required file not found*'
    }

    It 'rejects a hosting bundle for the wrong runtime major' {
        $wrongBundle = Join-Path $script:workRoot 'dotnet-hosting-8.0.11-win.exe'
        Set-Content -LiteralPath $wrongBundle -Value 'bundle' -Encoding ASCII

        { & $script:invokeBuilder -ProfilePath $script:profileFolder -BundlePath $wrongBundle -PackageName 'PkgWrongMajor' } | Should -Throw '*no roll-forward across majors*'
    }

    It 'refuses a profile that names no machine' {
        $namelessProfile = Join-Path $script:workRoot 'profiles\nameless'
        New-Item -ItemType Directory -Path $namelessProfile -Force | Out-Null
        $bootstrap = Get-Content -LiteralPath (Join-Path $script:profileFolder 'bootstrap.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $bootstrap.profile.machineNames = @()
        $bootstrap.hostAgent.hostKey = ''
        $bootstrap | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $namelessProfile 'bootstrap.json') -Encoding UTF8

        { & $script:invokeBuilder -ProfilePath $namelessProfile -BundlePath $script:fakeBundle -PackageName 'PkgNameless' } | Should -Throw '*names no machine*'
    }
}
