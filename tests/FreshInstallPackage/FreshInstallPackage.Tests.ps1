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
        $script:helpers = Join-Path $script:repositoryRoot 'scripts\deployment\fresh-install-package-helpers.ps1'
        . $script:helpers
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
        $readme | Should -Match ('Det h' + [char]0x00e4 + 'r paketet')
        [BitConverter]::ToString([System.IO.File]::ReadAllBytes((Join-Path $packageRoot 'README.txt')), 0, 3) | Should -Be 'EF-BB-BF'
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

    It 'refuses a package name that escapes the output root and deletes nothing outside it' {
        # F1 regression: '..\Escape' must not make the builder write (or
        # recursively delete) outside -OutputRoot.
        $decoy = Join-Path $script:workRoot 'Escape'
        New-Item -ItemType Directory -Path $decoy -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $decoy 'keep.txt') -Value 'keep' -Encoding ASCII

        { & $script:invokeBuilder -ProfilePath $script:profileFolder -BundlePath $script:fakeBundle -PackageName '..\Escape' } | Should -Throw '*package name*'
        Test-Path -LiteralPath (Join-Path $decoy 'keep.txt') -PathType Leaf | Should -BeTrue
    }

    It 'refuses a rooted package name' {
        $rooted = Join-Path $script:workRoot 'Rooted'
        { & $script:invokeBuilder -ProfilePath $script:profileFolder -BundlePath $script:fakeBundle -PackageName $rooted } | Should -Throw '*package name*'
        Test-Path -LiteralPath $rooted | Should -BeFalse
    }

    It 'refuses an artifact source that climbs out of the payload root' {
        $outside = Join-Path $script:workRoot 'outside'
        New-Item -ItemType Directory -Path $outside -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $outside 'evil.zip') -Value 'zip' -Encoding ASCII

        $climbProfile = Join-Path $script:workRoot 'profiles\climb'
        New-Item -ItemType Directory -Path $climbProfile -Force | Out-Null
        $bootstrap = Get-Content -LiteralPath (Join-Path $script:profileFolder 'bootstrap.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $bootstrap.artifacts[0].source = '../outside/evil.zip'
        $bootstrap | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $climbProfile 'bootstrap.json') -Encoding UTF8

        { & $script:invokeBuilder -ProfilePath $climbProfile -BundlePath $script:fakeBundle -PackageName 'PkgClimb' } | Should -Throw "*'..' is not allowed*"
        Test-Path -LiteralPath (Join-Path $script:outputRoot 'PkgClimb') | Should -BeFalse
    }

    It 'refuses an absolute artifact source' {
        $absoluteProfile = Join-Path $script:workRoot 'profiles\absolute'
        New-Item -ItemType Directory -Path $absoluteProfile -Force | Out-Null
        $bootstrap = Get-Content -LiteralPath (Join-Path $script:profileFolder 'bootstrap.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $bootstrap.artifacts[0].source = Join-Path $script:payloadSource 'data\global\artifacts\testmod__testapp__web-app__testapp__0.3.1.zip'
        $bootstrap | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $absoluteProfile 'bootstrap.json') -Encoding UTF8

        { & $script:invokeBuilder -ProfilePath $absoluteProfile -BundlePath $script:fakeBundle -PackageName 'PkgAbsolute' } | Should -Throw '*absolute*'
        Test-Path -LiteralPath (Join-Path $script:outputRoot 'PkgAbsolute') | Should -BeFalse
    }

    It 'refuses the sample profile folder even without *.sample.json files' {
        $sampleFolder = Join-Path $script:workRoot 'profiles\sample'
        New-Item -ItemType Directory -Path $sampleFolder -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $script:profileFolder 'bootstrap.json') -Destination (Join-Path $sampleFolder 'bootstrap.json')

        { & $script:invokeBuilder -ProfilePath $sampleFolder -BundlePath $script:fakeBundle -PackageName 'PkgSampleFolder' } | Should -Throw '*Sample profiles*'
        Test-Path -LiteralPath (Join-Path $script:outputRoot 'PkgSampleFolder') | Should -BeFalse
    }

    It 'refuses clear-text passwords in bootstrap.json' {
        $passwordProfile = Join-Path $script:workRoot 'profiles\cleartext'
        New-Item -ItemType Directory -Path $passwordProfile -Force | Out-Null
        $bootstrap = Get-Content -LiteralPath (Join-Path $script:profileFolder 'bootstrap.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $bootstrap.hostAgent | Add-Member -NotePropertyName serviceAccountPassword -NotePropertyValue 'Secret123'
        $bootstrap.sql | Add-Member -NotePropertyName password -NotePropertyValue 'Secret123'
        $bootstrap | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $passwordProfile 'bootstrap.json') -Encoding UTF8

        { & $script:invokeBuilder -ProfilePath $passwordProfile -BundlePath $script:fakeBundle -PackageName 'PkgClearText' } | Should -Throw '*clear-text passwords*'
        Test-Path -LiteralPath (Join-Path $script:outputRoot 'PkgClearText') | Should -BeFalse
    }

    It 'allows enc:aesgcm:v1: password values' {
        $encryptedProfile = Join-Path $script:workRoot 'profiles\encrypted'
        New-Item -ItemType Directory -Path $encryptedProfile -Force | Out-Null
        $bootstrap = Get-Content -LiteralPath (Join-Path $script:profileFolder 'bootstrap.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $bootstrap.hostAgent | Add-Member -NotePropertyName serviceAccountPassword -NotePropertyValue 'enc:aesgcm:v1:AAAAAAAAAAAAAAAA:c2VjcmV0:AAAAAAAAAAAAAAAAAAAAAA=='
        $bootstrap | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $encryptedProfile 'bootstrap.json') -Encoding UTF8

        & $script:invokeBuilder -ProfilePath $encryptedProfile -BundlePath $script:fakeBundle -PackageName 'PkgEncrypted' | Out-Null
        Test-Path -LiteralPath (Join-Path $script:outputRoot 'PkgEncrypted\OpenModulePlatform.Installer.exe') -PathType Leaf | Should -BeTrue
    }

    It 'refuses clear-text *Password fields in package.psd1' {
        $psd1Profile = Join-Path $script:workRoot 'profiles\psd1clear'
        New-Item -ItemType Directory -Path $psd1Profile -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $script:profileFolder 'bootstrap.json') -Destination (Join-Path $psd1Profile 'bootstrap.json')
        $psd1 = "@{`n    SqlPassword = 'Secret123'`n    RunAsPassword = 'enc:aesgcm:v1:a:b:c'`n}"
        Set-Content -LiteralPath (Join-Path $psd1Profile 'package.psd1') -Value $psd1 -Encoding UTF8

        { & $script:invokeBuilder -ProfilePath $psd1Profile -BundlePath $script:fakeBundle -PackageName 'PkgPsd1' } | Should -Throw '*clear-text passwords*'
        Test-Path -LiteralPath (Join-Path $script:outputRoot 'PkgPsd1') | Should -BeFalse
    }

    It 'warns when the profile carries the portable encryption key' {
        $keyProfile = Join-Path $script:workRoot 'profiles\portablekey'
        New-Item -ItemType Directory -Path $keyProfile -Force | Out-Null
        $bootstrap = Get-Content -LiteralPath (Join-Path $script:profileFolder 'bootstrap.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $bootstrap | Add-Member -NotePropertyName security -NotePropertyValue ([ordered]@{ portableEncryptionKey = 'base64:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=' })
        $bootstrap | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $keyProfile 'bootstrap.json') -Encoding UTF8

        # Stream 3 is the warning stream; merge it to capture.
        $output = (& $script:invokeBuilder -ProfilePath $keyProfile -BundlePath $script:fakeBundle -PackageName 'PkgKey' 3>&1 | Out-String)
        $output | Should -Match 'portableEncryptionKey'
        Test-Path -LiteralPath (Join-Path $script:outputRoot 'PkgKey\OpenModulePlatform.Installer.exe') -PathType Leaf | Should -BeTrue
    }

    It 'detects the runtime major from the installer csproj when -RuntimeMajor is 0' {
        $fakeRepo = Join-Path $script:workRoot 'fake-repo'
        $csprojDir = Join-Path $fakeRepo 'OpenModulePlatform.Installer'
        New-Item -ItemType Directory -Path $csprojDir -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $csprojDir 'OpenModulePlatform.Installer.csproj') -Value '<Project><PropertyGroup><TargetFramework>net9.0-windows</TargetFramework></PropertyGroup></Project>' -Encoding UTF8
        $bundle9 = Join-Path $script:workRoot 'dotnet-hosting-9.0.0-win.exe'
        Set-Content -LiteralPath $bundle9 -Value 'bundle' -Encoding ASCII

        $output = (& $script:builder `
                -ProfilePath $script:profileFolder `
                -PayloadSourceRoot $script:payloadSource `
                -RepositoryRoot $fakeRepo `
                -InstallerExePath $script:fakeInstaller `
                -HostingBundlePath $bundle9 `
                -OutputRoot $script:outputRoot `
                -PackageName 'PkgMajor9' 6>&1 | Out-String)
        $output | Should -Match 'dotnet-hosting-9\.0\.0-win\.exe'
    }

    It 'fails loudly when the runtime major cannot be detected' {
        $emptyRepo = Join-Path $script:workRoot 'empty-repo'
        New-Item -ItemType Directory -Path $emptyRepo -Force | Out-Null

        { & $script:builder `
                -ProfilePath $script:profileFolder `
                -PayloadSourceRoot $script:payloadSource `
                -RepositoryRoot $emptyRepo `
                -InstallerExePath $script:fakeInstaller `
                -HostingBundlePath $script:fakeBundle `
                -OutputRoot $script:outputRoot `
                -PackageName 'PkgNoMajor' } | Should -Throw '*Cannot detect the runtime major*'
        Test-Path -LiteralPath (Join-Path $script:outputRoot 'PkgNoMajor') | Should -BeFalse
    }
}

Describe 'fresh-install-package-helpers.ps1' {
    BeforeAll {
        $script:repositoryRoot = $PSScriptRoot
        while ($script:repositoryRoot -and -not (Test-Path -LiteralPath (Join-Path $script:repositoryRoot 'scripts\deployment\fresh-install-package-helpers.ps1') -PathType Leaf)) {
            $script:repositoryRoot = Split-Path -Parent $script:repositoryRoot
        }
        if ([string]::IsNullOrWhiteSpace($script:repositoryRoot)) {
            throw 'Repository root not found above ' + $PSScriptRoot
        }

        . (Join-Path $script:repositoryRoot 'scripts\deployment\fresh-install-package-helpers.ps1')
    }

    It 'Test-MicrosoftDownloadUrl allows the official Microsoft hosts over https' {
        Test-MicrosoftDownloadUrl -Url 'https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/10.0.0/dotnet-hosting-10.0.0-win.exe' | Should -BeTrue
        Test-MicrosoftDownloadUrl -Url 'https://download.visualstudio.microsoft.com/download/pr/abc/dotnet-hosting-10.0.0-win.exe' | Should -BeTrue
        Test-MicrosoftDownloadUrl -Url 'https://dotnetcli.azureedge.net/dotnet/aspnetcore/Runtime/10.0.0/dotnet-hosting-10.0.0-win.exe' | Should -BeTrue
    }

    It 'Test-MicrosoftDownloadUrl refuses other hosts, http and non-URLs' {
        Test-MicrosoftDownloadUrl -Url 'https://evil.example.com/dotnet-hosting-10.0.0-win.exe' | Should -BeFalse
        Test-MicrosoftDownloadUrl -Url 'https://builds.dotnet.microsoft.com.evil.example.com/dotnet-hosting-10.0.0-win.exe' | Should -BeFalse
        Test-MicrosoftDownloadUrl -Url 'http://builds.dotnet.microsoft.com/dotnet-hosting-10.0.0-win.exe' | Should -BeFalse
        Test-MicrosoftDownloadUrl -Url 'not a url' | Should -BeFalse
    }

    It 'Resolve-PackageRoot keeps a plain name directly under the output root' {
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-pkgroot-' + [Guid]::NewGuid().ToString('N'))
        $resolved = Resolve-PackageRoot -OutputRoot $root -PackageName 'Pkg'
        $resolved | Should -Be ([System.IO.Path]::GetFullPath((Join-Path $root 'Pkg')))
    }

    It 'Resolve-PackageRoot refuses traversal, rooted, separated and invalid names' {
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-pkgroot-' + [Guid]::NewGuid().ToString('N'))
        { Resolve-PackageRoot -OutputRoot $root -PackageName '..\X' } | Should -Throw '*package name*'
        { Resolve-PackageRoot -OutputRoot $root -PackageName 'sub\X' } | Should -Throw '*package name*'
        { Resolve-PackageRoot -OutputRoot $root -PackageName 'a..b' } | Should -Throw '*package name*'
        { Resolve-PackageRoot -OutputRoot $root -PackageName 'a<b' } | Should -Throw '*package name*'
        { Resolve-PackageRoot -OutputRoot $root -PackageName (Join-Path $root 'X') } | Should -Throw '*package name*'
    }

    It 'Assert-SafePayloadReference accepts relative paths and refuses climbing or rooted ones' {
        { Assert-SafePayloadReference -Reference 'data/global/artifacts/x.zip' } | Should -Not -Throw
        { Assert-SafePayloadReference -Reference 'payload\OpenModulePlatform.HostAgent.WindowsService.zip' } | Should -Not -Throw
        { Assert-SafePayloadReference -Reference '../x.zip' } | Should -Throw "*'..' is not allowed*"
        { Assert-SafePayloadReference -Reference 'data/../../x.zip' } | Should -Throw "*'..' is not allowed*"
        { Assert-SafePayloadReference -Reference 'C:\temp\x.zip' } | Should -Throw '*absolute*'
        { Assert-SafePayloadReference -Reference '\\server\share\x.zip' } | Should -Throw '*absolute*'
    }

    It 'Test-ClearTextSecret allows empty and enc:aesgcm:v1: values and refuses clear text' {
        Test-ClearTextSecret -Value '' | Should -BeFalse
        Test-ClearTextSecret -Value '   ' | Should -BeFalse
        Test-ClearTextSecret -Value 'enc:aesgcm:v1:AAAAAAAAAAAAAAAA:c2VjcmV0:AAAAAAAAAAAAAAAAAAAAAA==' | Should -BeFalse
        Test-ClearTextSecret -Value 'Secret123' | Should -BeTrue
    }

    It 'Find-PasswordFields walks nested package.psd1 data and skips enc values' {
        $data = @{
            SqlPassword = 'Secret123'
            RunAsPassword = 'enc:aesgcm:v1:AAAAAAAAAAAAAAAA:c2VjcmV0:AAAAAAAAAAAAAAAAAAAAAA=='
            Nested = @{ AppPoolPassword = 'clear' }
            Items = @(@{ ServicePassword = 'clear2' }, @{ Name = 'no password here' })
        }
        $bad = Find-PasswordFields -Node $data -Path 'package.psd1'
        $bad.Count | Should -Be 3
        ($bad -join ',') | Should -Match 'SqlPassword'
        ($bad -join ',') | Should -Match 'AppPoolPassword'
        ($bad -join ',') | Should -Match 'ServicePassword'
        ($bad -join ',') | Should -Not -Match 'RunAsPassword'
    }

    It 'Get-RepositoryRuntimeMajor parses the TargetFramework from the installer csproj' {
        $fakeRepo = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-major-' + [Guid]::NewGuid().ToString('N'))
        $csprojDir = Join-Path $fakeRepo 'OpenModulePlatform.Installer'
        New-Item -ItemType Directory -Path $csprojDir -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $csprojDir 'OpenModulePlatform.Installer.csproj') -Value '<Project><PropertyGroup><TargetFramework>net9.0-windows</TargetFramework></PropertyGroup></Project>' -Encoding UTF8

        Get-RepositoryRuntimeMajor -RepositoryRootPath $fakeRepo | Should -Be 9
    }

    It 'Get-RepositoryRuntimeMajor fails loudly without a csproj or a TargetFramework' {
        $missingRepo = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-major-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $missingRepo -Force | Out-Null
        { Get-RepositoryRuntimeMajor -RepositoryRootPath $missingRepo } | Should -Throw '*Cannot detect the runtime major*'

        $noTfmRepo = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-major-' + [Guid]::NewGuid().ToString('N'))
        $csprojDir = Join-Path $noTfmRepo 'OpenModulePlatform.Installer'
        New-Item -ItemType Directory -Path $csprojDir -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $csprojDir 'OpenModulePlatform.Installer.csproj') -Value '<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>' -Encoding UTF8
        { Get-RepositoryRuntimeMajor -RepositoryRootPath $noTfmRepo } | Should -Throw '*Cannot detect the runtime major*'
    }
}
