$ErrorActionPreference = 'Stop'

$packageScript = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\deployment\new-omp-artifact-package.ps1')
$hostAgentPackageScript = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\deployment\package-hostagent-first.ps1')

function Get-PackageManifest {
    param([Parameter(Mandatory = $true)][string]$PackagePath)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.GetEntry('omp-artifact-package.json')
        if ($null -eq $entry) {
            throw "Package manifest is missing from '$PackagePath'."
        }

        $reader = [System.IO.StreamReader]::new($entry.Open())
        try {
            return ($reader.ReadToEnd() | ConvertFrom-Json)
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Export-PackagePayload {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.GetEntry('payload/artifact.zip')
        if ($null -eq $entry) {
            throw "Nested artifact payload is missing from '$PackagePath'."
        }

        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $DestinationPath, $true)
    }
    finally {
        $archive.Dispose()
    }
}

function Import-HostAgentArtifactPackageFunctions {
    $parseErrors = $null
    $tokens = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $hostAgentPackageScript,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw 'package-hostagent-first.ps1 has parse errors.'
    }

    $functionNames = @(
        'Copy-RequiredFile',
        'Compress-FolderToZip',
        'Get-EmbeddedWorkerHostMinVersion',
        'New-ArtifactPackage'
    )
    $functionAsts = $ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
        }, $true)
    foreach ($name in $functionNames) {
        $functionAst = @($functionAsts | Where-Object { $_.Name -eq $name } | Select-Object -First 1)
        if ($functionAst.Count -eq 0) {
            throw "Function '$name' was not found in package-hostagent-first.ps1."
        }

        $definition = $functionAst[0].Body.GetScriptBlock()
        Set-Item -Path ("Function:\script:{0}" -f $name) -Value $definition
    }
}

Describe 'Worker-host compatibility survives PowerShell artifact package round-trip' {
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
            $manifest.workerHost.componentKey | Should Be 'omp-workerprocesshost'
            $manifest.workerHost.minVersion | Should Be '0.3.46'
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
            $manifest.workerHost.componentKey | Should Be 'omp-workerprocesshost'
            $manifest.workerHost.minVersion | Should Be '0.3.46'
        }
        finally {
            if (Test-Path -LiteralPath $root -PathType Container) {
                Remove-Item -LiteralPath $root -Recurse -Force
            }
        }
    }
}
