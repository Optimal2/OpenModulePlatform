# Shared setup for PesterBootstrap.Tests.ps1.
# Dot-sourced from each Describe block's BeforeAll: Pester 6 runs every
# container in a separate session state, so functions and variables defined
# at file scope are not visible inside It blocks.

$ErrorActionPreference = 'Stop'

$script:BootstrapScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/omp/pester-bootstrap.ps1'
$script:RunnerScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/omp/run-script-tests.ps1'
$script:PinnedPesterVersion = '6.1.0'
# The repository-local cache run-script-tests.ps1 restores into. It is the seed
# for every restore case below: the bootstrap copies an already available
# pinned Pester instead of downloading, so with this cache on the child's
# module path the suite never needs PSGallery.
$script:RepoModuleCache = Join-Path (Split-Path -Parent $PSScriptRoot) '.psmodules'

function Test-PinnedPesterSeedAvailable {
    <#
    .SYNOPSIS
    True when a restore can be served without the network: the pinned version
    is in the repository-local cache (the runner put it there) or is installed
    globally. Restore cases skip with a reason otherwise, instead of turning
    the script gate into a PSGallery download.
    #>
    $cached = Join-Path (Join-Path (Join-Path $script:RepoModuleCache 'Pester') $script:PinnedPesterVersion) 'Pester.psd1'
    if (Test-Path -LiteralPath $cached -PathType Leaf) {
        return $true
    }
    $global = @(Get-Module -ListAvailable -Name Pester | Where-Object { $_.Version.ToString() -eq $script:PinnedPesterVersion })
    return $global.Count -gt 0
}

function Skip-UnlessPinnedPesterSeedAvailable {
    if (-not (Test-PinnedPesterSeedAvailable)) {
        Set-ItResult -Skipped -Because "no offline source for Pester $script:PinnedPesterVersion (neither $script:RepoModuleCache nor a global install); run scripts/omp/run-script-tests.ps1 once to seed the cache"
    }
}

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

    # The child inherits the module path; putting the repository cache first
    # makes the local-copy branch of the restore deterministic rather than a
    # property of whichever process happens to host this suite.
    $originalModulePath = $env:PSModulePath
    try {
        $env:PSModulePath = $script:RepoModuleCache + ';' + $originalModulePath
        return Invoke-ChildPowerShell -ScriptPath $script:BootstrapScript `
            -ScriptArguments @('-RequiredVersion', $script:PinnedPesterVersion, '-CacheRoot', $CacheRoot)
    }
    finally {
        $env:PSModulePath = $originalModulePath
    }
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

function New-SuiteFixture {
    <#
    .SYNOPSIS
    Creates a temporary tests directory holding the given files (relative path
    -> content) and returns its path. Subdirectories are created as needed.
    #>
    param([Parameter(Mandatory = $true)][hashtable]$Files)

    $root = New-TestDirectory
    foreach ($relative in $Files.Keys) {
        $path = Join-Path $root $relative
        $parent = Split-Path -Parent $path
        if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
            $null = New-Item -ItemType Directory -Path $parent -Force
        }
        [System.IO.File]::WriteAllText($path, [string]$Files[$relative], [System.Text.UTF8Encoding]::new($false))
    }
    return $root
}

# A suite with one passing test; the building block of every inventory fixture.
$script:PassingSuiteBody = "Describe 'fixture' { It 'passes' { 1 | Should -Be 1 } }`n"

function Invoke-RunnerAgainst {
    <#
    .SYNOPSIS
    Runs run-script-tests.ps1 as a child process against a fixture directory.
    #>
    param([Parameter(Mandatory = $true)][string]$TestsPath)

    return Invoke-ChildPowerShell -ScriptPath $script:RunnerScript -ScriptArguments @('-TestsPath', $TestsPath)
}

function Set-HiddenFile {
    <#
    .SYNOPSIS
    Marks a fixture file hidden, the attribute Get-ChildItem skips without -Force.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)

    $item = Get-Item -LiteralPath $Path -Force
    $item.Attributes = $item.Attributes -bor [System.IO.FileAttributes]::Hidden
}

function Test-GitAvailable {
    return $null -ne (Get-Command -Name git -CommandType Application -ErrorAction SilentlyContinue)
}

function Initialize-GitFixture {
    <#
    .SYNOPSIS
    Turns a fixture directory into a git work tree with the given .gitignore
    body. Nothing is committed: untracked files that are not ignored must count
    exactly like tracked ones.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$GitIgnore
    )

    [System.IO.File]::WriteAllText((Join-Path $Path '.gitignore'), $GitIgnore, [System.Text.UTF8Encoding]::new($false))
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $null = & git -C $Path init -q 2>&1
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

# A suite that fails when it runs: a fixture that must NOT be run.
$script:FailingSuiteBody = "Describe 'must not run' { It 'fails' { 1 | Should -Be 2 } }`n"
# A suite whose only test is skipped.
$script:SkippedSuiteBody = "Describe 'all skipped' { It 'is skipped' -Skip { 1 | Should -Be 1 } }`n"
