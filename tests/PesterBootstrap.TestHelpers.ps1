# Shared setup for PesterBootstrap.Tests.ps1.
# Dot-sourced from each Describe block's BeforeAll: Pester 5 runs every
# container in a separate session state, so functions and variables defined
# at file scope are not visible inside It blocks.

$ErrorActionPreference = 'Stop'

$script:BootstrapScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/omp/pester-bootstrap.ps1'
$script:RunnerScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/omp/run-script-tests.ps1'
$script:PinnedPesterVersion = '5.9.1'

function Invoke-ChildPowerShell {
    <#
    .SYNOPSIS
    Runs a script as its own child powershell.exe process and captures the
    exit code, which is the contract under test. Module-path state and the
    loaded Pester module are per-process, so the bootstrap must be measured in
    a child, not in the Pester host.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $false)][string[]]$ScriptArguments = @()
    )

    $arguments = @('-NoProfile', '-File', $ScriptPath) + $ScriptArguments

    # ErrorActionPreference 'Stop' would turn the child's redirected stderr
    # into a throwing ErrorRecord, so relax it locally for the capture.
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & powershell.exe $arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return @{ ExitCode = $exitCode; Output = $output }
}

function Invoke-PesterBootstrap {
    <#
    .SYNOPSIS
    Runs pester-bootstrap.ps1 as a child process against the given cache
    root. The script-mode main ensures the pinned Pester, imports it, and
    prints 'Loaded Pester <version> from <moduleBase>'.
    #>
    param([Parameter(Mandatory = $true)][string]$CacheRoot)

    return Invoke-ChildPowerShell -ScriptPath $script:BootstrapScript `
        -ScriptArguments @('-RequiredVersion', $script:PinnedPesterVersion, '-CacheRoot', $CacheRoot)
}

function New-TestDirectory {
    <#
    .SYNOPSIS
    Creates an empty temporary directory and returns its path.
    #>
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-pester-bootstrap-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $path -Force
    return $path
}

function Remove-TestDirectory {
    param([Parameter(Mandatory = $false)][AllowNull()][AllowEmptyString()][string]$Path)
    if (-not [string]::IsNullOrWhiteSpace($Path) -and (Test-Path -LiteralPath $Path -PathType Container)) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}
