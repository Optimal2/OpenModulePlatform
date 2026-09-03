# Pester 5's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (5.9.1), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 5 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
# Pester 5 runs every container in a separate session state, so the shared
# harness (script paths + zip helpers) lives in
# ArtifactPackageWorkerHostRoundTrip.TestHelpers.ps1 and is dot-sourced from
# each Describe block's BeforeAll.

Describe 'Worker-host compatibility survives PowerShell artifact package round-trip' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'ArtifactPackageWorkerHostRoundTrip.TestHelpers.ps1')
    }

    It 'Preserves workerHost in the envelope when an embedded compatible payload is repackaged' {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-worker-host-roundtrip-' + [Guid]::NewGuid().ToString('N'))
        try {
            $payload = Join-Path $root 'payload'
            $null = New-Item -ItemType Directory -Path $payload -Force
            [System.IO.File]::WriteAllText((Join-Path $payload 'Plugin.dll'), 'fixture')

            $firstPackage = Join-Path $root 'first.zip'
            & $packageScript `
                -ModuleKey 'example_workerapp' `
                -AppKey 'example_workerapp_worker' `
                -PackageType 'worker' `
                -TargetName 'example-workerapp' `
                -Version '0.3.74' `
                -PayloadPath $payload `
                -OutputPath $firstPackage `
                -MinWorkerHostVersion '0.3.46' | Out-Null

            $nestedPayload = Join-Path $root 'first-payload.zip'
            Export-PackagePayload -PackagePath $firstPackage -DestinationPath $nestedPayload

            $secondPackage = Join-Path $root 'second.zip'
            & $packageScript `
                -ModuleKey 'example_workerapp' `
                -AppKey 'example_workerapp_worker' `
                -PackageType 'worker' `
                -TargetName 'example-workerapp' `
                -Version '0.3.75' `
                -PayloadPath $nestedPayload `
                -OutputPath $secondPackage | Out-Null

            $manifest = Get-PackageManifest -PackagePath $secondPackage
            $manifest.workerHost.componentKey | Should -Be 'omp-workerprocesshost'
            $manifest.workerHost.minVersion | Should -Be '0.3.46'
        }
        finally {
            if (Test-Path -LiteralPath $root -PathType Container) {
                Remove-Item -LiteralPath $root -Recurse -Force
            }
        }
    }

    It 'Preserves workerHost through the HostAgent-first artifact repackager' {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        Import-HostAgentArtifactPackageFunctions
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-worker-host-installer-roundtrip-' + [Guid]::NewGuid().ToString('N'))
        try {
            $payload = Join-Path $root 'payload'
            $null = New-Item -ItemType Directory -Path $payload -Force
            [System.IO.File]::WriteAllText((Join-Path $payload 'Plugin.dll'), 'fixture')

            $firstPackage = Join-Path $root 'first.zip'
            & $packageScript `
                -ModuleKey 'example_workerapp' `
                -AppKey 'example_workerapp_worker' `
                -PackageType 'worker' `
                -TargetName 'example-workerapp' `
                -Version '0.3.74' `
                -PayloadPath $payload `
                -OutputPath $firstPackage `
                -MinWorkerHostVersion '0.3.46' | Out-Null

            $nestedPayload = Join-Path $root 'first-payload.zip'
            Export-PackagePayload -PackagePath $firstPackage -DestinationPath $nestedPayload

            $secondPackage = Join-Path $root 'second.zip'
            New-ArtifactPackage `
                -PayloadZip $nestedPayload `
                -Destination $secondPackage `
                -BuildRoot (Join-Path $root 'build')

            $manifest = Get-PackageManifest -PackagePath $secondPackage
            $manifest.workerHost.componentKey | Should -Be 'omp-workerprocesshost'
            $manifest.workerHost.minVersion | Should -Be '0.3.46'
        }
        finally {
            if (Test-Path -LiteralPath $root -PathType Container) {
                Remove-Item -LiteralPath $root -Recurse -Force
            }
        }
    }
}
