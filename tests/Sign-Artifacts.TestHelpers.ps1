# Shared setup for Sign-Artifacts.Tests.ps1.
# Dot-sourced from each Describe block's BeforeAll: Pester 5 runs every
# container in a separate session state, so functions and variables defined
# at file scope are not visible inside It blocks.

$ErrorActionPreference = 'Stop'

$script:SignArtifactsPath = (Resolve-Path (Join-Path $PSScriptRoot '..\scripts\deployment\sign-artifacts.ps1')).Path

function New-SignArtifactsSandbox {
    <#
    .SYNOPSIS
    Creates a temporary folder with a placeholder Trusted Signing config and an
    empty folder to sign. With nothing to sign the script stops before it looks
    for signtool or downloads anything, so only the overlay handling runs.
    #>
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ('sign-artifacts-' + [Guid]::NewGuid().ToString('N'))
    $signRoot = Join-Path $root 'publish'
    $null = New-Item -ItemType Directory -Path $signRoot -Force

    $configPath = Join-Path $root 'trusted-signing.json'
    $config = '{ "Endpoint": "https://example.invalid", "CodeSigningAccountName": "example", "CertificateProfileName": "example" }'
    [System.IO.File]::WriteAllText($configPath, $config, [System.Text.Encoding]::UTF8)

    return [pscustomobject]@{
        Root = $root
        SignRoot = $signRoot
        ConfigPath = $configPath
        OverlayPath = (Join-Path $root 'omp-components.external.json')
    }
}

function Remove-SignArtifactsSandbox {
    param([Parameter(Mandatory = $true)]$Sandbox)

    if (Test-Path -LiteralPath $Sandbox.Root -PathType Container) {
        Remove-Item -LiteralPath $Sandbox.Root -Recurse -Force
    }
}

function Invoke-SignArtifacts {
    <#
    .SYNOPSIS
    Runs sign-artifacts.ps1 against a sandbox and returns its exit code and
    everything it wrote, warnings included. A terminating error propagates.
    #>
    param(
        [Parameter(Mandatory = $true)]$Sandbox,
        [Parameter(Mandatory = $false)][switch]$Strict
    )

    $arguments = @{
        Path = @($Sandbox.SignRoot)
        ConfigPath = $Sandbox.ConfigPath
        ExternalOverlayPath = $Sandbox.OverlayPath
    }
    if ($Strict) {
        $arguments['Strict'] = $true
    }

    $output = & $script:SignArtifactsPath @arguments *>&1 | Out-String -Width 4096
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}
