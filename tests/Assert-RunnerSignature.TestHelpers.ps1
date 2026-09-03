# Shared setup for Assert-RunnerSignature.Tests.ps1.
# Dot-sourced from each Describe block's BeforeAll: Pester 5 runs every
# container in a separate session state, so functions and variables defined
# at file scope are not visible inside It blocks.

$ErrorActionPreference = 'Stop'

$script:GateScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/deployment/assert-runner-signature.ps1'

function Invoke-Gate {
    param(
        [string] $TargetStatus,
        [string] $NewStatus,
        [string] $TargetPath = 'C:\pkg\OpenModulePlatform.Bootstrapper.exe',
        [string] $NewPath = 'C:\temp\OpenModulePlatform.Bootstrapper.exe'
    )

    try {
        & $script:GateScript `
            -TargetSignatureStatus $TargetStatus `
            -NewSignatureStatus $NewStatus `
            -TargetPath $TargetPath `
            -NewPath $NewPath | Out-Null
        return @{ Threw = $false; ErrorMessage = '' }
    }
    catch {
        return @{ Threw = $true; ErrorMessage = $_.Exception.Message }
    }
}
