# Shared setup for ArtifactPackageWorkerHostRoundTrip.Tests.ps1.
# Dot-sourced from each Describe block's BeforeAll: Pester 5 runs every
# container in a separate session state, so functions and variables defined
# at file scope are not visible inside It blocks.

$ErrorActionPreference = 'Stop'

$packageScript = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\deployment\new-omp-artifact-package.ps1')
$hostAgentPackageScript = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\deployment\package-hostagent-first.ps1')

function Get-PackageManifest {
    param([Parameter(Mandatory = $true)][string]$PackagePath)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.Entries | Where-Object {
            [string]::Equals(
                $_.FullName.Replace('\', '/'),
                'omp-artifact-package.json',
                [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
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
        $entry = $archive.Entries | Where-Object {
            [string]::Equals(
                $_.FullName.Replace('\', '/'),
                'payload/artifact.zip',
                [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
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
