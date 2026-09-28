# Pester 5's 'Should -Be/-Throw' parameters are provided by the pinned Pester
# module (5.9.1), not by the inbox Pester 3.4.0 profile the compatibility rule
# measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 5 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()

# An artifact configuration file may carry ConnectionStrings only as OMP
# placeholders. A literal value is the database the artifact was packaged
# against; before this guard, build-repository-objects.ps1 packaged it and the
# HostAgent let it override the live connection on every other host.

Describe 'Artifact configuration ConnectionStrings guard' {
    BeforeAll {
        $scriptsRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\omp'
        $repositoryRoot = Split-Path -Parent $PSScriptRoot
        . (Join-Path $scriptsRoot 'runtime-configuration-files.ps1')
        $builderScript = Join-Path $scriptsRoot 'build-repository-objects.ps1'

        $literalSettings = '{ "ConnectionStrings": { "OmpDb": "Data Source=localhost;Initial Catalog=OpenModulePlatform;Integrated Security=true;" } }'
        $placeholderSettings = '{ "ConnectionStrings": { "OmpDb": "{{Omp.Json.ConnectionStrings.OmpDb}}" }, "Title": "x" }'

        function New-FakeRepository {
            param(
                [Parameter(Mandatory = $true)][string]$Root,
                [object[]]$ArtifactConfigurationFiles = @()
            )

            $null = New-Item -ItemType Directory -Path (Join-Path $Root 'artifacts') -Force
            $component = [ordered]@{
                componentKey = 'fake-web'
                moduleKey    = 'fake_module'
                appKey       = 'fake_app'
                packageType  = 'web-app'
                targetName   = 'fake-target'
                version      = '9.9.9'
            }
            if ($ArtifactConfigurationFiles.Count -gt 0) {
                $component.artifactConfigurationFiles = $ArtifactConfigurationFiles
            }

            $manifest = [ordered]@{
                manifestVersion   = 1
                repositoryKey     = 'fakerepo'
                repositoryVersion = '1.0.0'
                moduleDefinitions = @()
                components        = @($component)
            }
            ($manifest | ConvertTo-Json -Depth 6) |
                Set-Content -LiteralPath (Join-Path $Root 'omp-components.json') -Encoding UTF8
        }

        function New-FakeArtifactPackage {
            param(
                [Parameter(Mandatory = $true)][string]$WorkRoot,
                [Parameter(Mandatory = $true)][string]$ZipPath,
                [Parameter(Mandatory = $true)][string]$AppSettingsContent
            )

            $packageRoot = Join-Path $WorkRoot ('package-' + [Guid]::NewGuid().ToString('N'))
            $null = New-Item -ItemType Directory -Path (Join-Path $packageRoot 'configuration') -Force
            [System.IO.File]::WriteAllText((Join-Path $packageRoot 'omp-artifact-package.json'), '{"formatVersion":1}')
            [System.IO.File]::WriteAllText((Join-Path $packageRoot 'configuration\001-appsettings.json'), $AppSettingsContent)
            Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $ZipPath -Force
        }
    }

    BeforeEach {
        $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-connection-string-guard-' + [Guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $tempRoot -Force
    }

    AfterEach {
        if (Test-Path -LiteralPath $tempRoot -PathType Container) {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
    }

    It 'Accepts placeholders and names every literal ConnectionStrings entry, case-insensitively' {
        @(Get-OmpLiteralConnectionStringName -Content $placeholderSettings -Description 'test').Count | Should -Be 0
        @(Get-OmpLiteralConnectionStringName -Content '{ "Other": { "OmpDb": "Server=x" } }' -Description 'test').Count | Should -Be 0

        $names = @(Get-OmpLiteralConnectionStringName -Content '{ "connectionStrings": { "ompDb": "Server=x", "ModuleDb": "{{Omp.Json.ConnectionStrings.OmpDb}}", "Extra": "" } }' -Description 'test')
        $names | Should -Be @('connectionStrings:ompDb', 'connectionStrings:Extra')
    }

    It 'Fails the build for a component whose configuration source file sets a literal connection string' {
        $repo = Join-Path $tempRoot 'repo'
        $null = New-Item -ItemType Directory -Path (Join-Path $repo 'Packaging') -Force
        [System.IO.File]::WriteAllText((Join-Path $repo 'Packaging\appsettings.json'), $literalSettings)
        New-FakeRepository -Root $repo -ArtifactConfigurationFiles @(
            [ordered]@{ relativePath = 'appsettings.json'; sourcePath = 'Packaging/appsettings.json' })

        $message = ''
        try {
            & $builderScript -RepositoryRoot $repo -OmpRepositoryRoot $repositoryRoot -AllComponents -OutputRoot (Join-Path $tempRoot 'out') 3>$null
        }
        catch {
            $message = $_.Exception.Message
        }

        $message | Should -Match "component 'fake-web'"
        $message | Should -Match 'appsettings\.json'
        $message | Should -Match 'ConnectionStrings:OmpDb'
        $message | Should -Not -Match 'Initial Catalog'
    }

    It 'Fails the build for a command-line configuration mapping with a literal connection string' {
        $repo = Join-Path $tempRoot 'repo'
        New-FakeRepository -Root $repo
        $source = Join-Path $tempRoot 'secure-appsettings.json'
        [System.IO.File]::WriteAllText($source, $literalSettings)

        {
            & $builderScript -RepositoryRoot $repo -OmpRepositoryRoot $repositoryRoot -AllComponents `
                -OutputRoot (Join-Path $tempRoot 'out') `
                -ArtifactConfigurationFile "fake-web:appsettings.json=$source" 3>$null
        } | Should -Throw -ExpectedMessage "*component 'fake-web'*"
    }

    It 'Fails the build when a reused artifact package carries a literal connection string' {
        $repo = Join-Path $tempRoot 'repo'
        New-FakeRepository -Root $repo
        New-FakeArtifactPackage -WorkRoot $tempRoot `
            -ZipPath (Join-Path $repo 'artifacts\fake_module__fake_app__web-app__fake-target__9.9.9.zip') `
            -AppSettingsContent $literalSettings

        {
            & $builderScript -RepositoryRoot $repo -OmpRepositoryRoot $repositoryRoot -AllComponents -OutputRoot (Join-Path $tempRoot 'out')
        } | Should -Throw -ExpectedMessage "*configuration/001-appsettings.json*component 'fake-web'*"
    }

    It 'Bundles a reused artifact package whose configuration uses only placeholders' {
        $repo = Join-Path $tempRoot 'repo'
        New-FakeRepository -Root $repo
        New-FakeArtifactPackage -WorkRoot $tempRoot `
            -ZipPath (Join-Path $repo 'artifacts\fake_module__fake_app__web-app__fake-target__9.9.9.zip') `
            -AppSettingsContent $placeholderSettings

        & $builderScript -RepositoryRoot $repo -OmpRepositoryRoot $repositoryRoot -AllComponents -OutputRoot (Join-Path $tempRoot 'out') | Out-Null

        Test-Path -LiteralPath (Join-Path $tempRoot 'out\artifacts\fake_module__fake_app__web-app__fake-target__9.9.9.zip') -PathType Leaf |
            Should -BeTrue
    }

    It 'Accepts the platform packaging configuration files, which use placeholders only' {
        foreach ($relative in @(
                'OpenModulePlatform.Portal\Packaging\appsettings.json',
                'OpenModulePlatform.Auth\Packaging\appsettings.json',
                'OpenModulePlatform.Web.ContentWebAppModule\Packaging\appsettings.json')) {
            $content = [System.IO.File]::ReadAllText((Join-Path $repositoryRoot $relative))
            { Assert-OmpConfigurationFileHasNoLiteralConnectionStrings -ComponentKey 'platform' -RelativePath 'appsettings.json' -Content $content } |
                Should -Not -Throw
        }
    }
}
