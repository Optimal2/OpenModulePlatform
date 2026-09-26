# Shared setup for Validate-SharedScripts.Tests.ps1.
# Dot-sourced from each Describe block's BeforeAll: Pester 5 runs every
# container in a separate session state, so functions and variables defined
# at file scope are not visible inside It blocks.

$ErrorActionPreference = 'Stop'

$script:GuardScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/omp/validate-shared-scripts.ps1'

function New-Pair {
    <#
        Creates a consumer root and a platform root with the given script bodies.

        The platform root is a complete OpenModulePlatform checkout as far as the
        guard can tell: both canonical shared scripts plus an omp-components.json
        carrying repositoryKey and repositoryVersion. The helpers file has the
        same body on both sides, so bump-version.ps1 is the file under test.
    #>
    param(
        [string] $ConsumerBody,
        [string] $PlatformBody,
        [switch] $OmitConsumerScript,
        [switch] $OmitPlatformRoot,
        # Creates the platform directory but none of the canonical scripts.
        [switch] $OmitPlatformScripts,
        # Replaces the platform manifest; an empty string leaves omp-components.json out.
        [string] $PlatformManifest = '{ "manifestVersion": 1, "repositoryKey": "openmoduleplatform", "repositoryVersion": "0.0.1", "components": [] }',
        # A name other than OpenModulePlatform puts the platform checkout where
        # the sibling assumption cannot find it, as in a worktree under another root.
        [string] $PlatformDirectoryName = 'OpenModulePlatform'
    )

    $root = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
    $consumer = Join-Path $root 'Consumer'
    $platform = Join-Path $root $PlatformDirectoryName
    $helpersBody = 'shared helpers'

    New-Item -ItemType Directory -Path (Join-Path $consumer 'scripts\omp') -Force | Out-Null
    if (-not $OmitConsumerScript) {
        [IO.File]::WriteAllText((Join-Path $consumer 'scripts\omp\bump-version.ps1'), $ConsumerBody)
    }
    [IO.File]::WriteAllText((Join-Path $consumer 'scripts\omp\validate-component-versions.helpers.ps1'), $helpersBody)

    if (-not $OmitPlatformRoot) {
        New-Item -ItemType Directory -Path (Join-Path $platform 'scripts\omp') -Force | Out-Null
        if (-not $OmitPlatformScripts) {
            [IO.File]::WriteAllText((Join-Path $platform 'scripts\omp\bump-version.ps1'), $PlatformBody)
            [IO.File]::WriteAllText((Join-Path $platform 'scripts\omp\validate-component-versions.helpers.ps1'), $helpersBody)
        }
        if (-not [string]::IsNullOrEmpty($PlatformManifest)) {
            [IO.File]::WriteAllText((Join-Path $platform 'omp-components.json'), $PlatformManifest)
        }
    }

    return @{ Root = $root; Consumer = $consumer; Platform = $platform }
}

function Invoke-Guard {
    <#
        Kor vakten som ETT EGET SKRIPT och mater dess SLUTKOD.

        Kontraktet ar exitkod, inte throw. Ett throw dodade den anropande
        validatorn innan den nadde sin egen felgren, sa den kopplade
        felhanteringen var dod kod - korningen blev rod genom att KRASCHA, vilket
        fick det ursprungliga beviset att se overtygande ut. Uppmatt i granskning
        2026-09-02. Harnesset maste darfor mata slutkoden, annars provar testet
        fortfarande fel sak.

        3>&1 fangar WARNING-strommen, dar noten om en omatbar kontroll skrivs.
    #>
    param(
        [hashtable] $Pair,
        [switch] $Strict,
        # Leaves -PlatformRepositoryRoot out so the guard resolves the root itself.
        [switch] $OmitPlatformArgument,
        # Passed as -PlatformRepositoryRoot instead of $Pair.Platform, for
        # example a relative path.
        [string] $PlatformArgument = '',
        # Environment for the child process only; restored afterwards. A $null
        # value removes the variable, so an ambient value cannot leak into the proof.
        [hashtable] $Environment = @{},
        # Current directory of the child process; defaults to the caller's.
        [string] $WorkingDirectory = ''
    )

    $argsLista = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:GuardScript,
        '-ConsumerRepositoryRoot', $Pair.Consumer
    )
    if (-not $OmitPlatformArgument) {
        $plattform = if ([string]::IsNullOrEmpty($PlatformArgument)) { $Pair.Platform } else { $PlatformArgument }
        $argsLista += @('-PlatformRepositoryRoot', $plattform)
    }
    if ($Strict) { $argsLista += '-Strict' }

    $sparat = @{}
    foreach ($namn in $Environment.Keys) {
        $sparat[$namn] = [Environment]::GetEnvironmentVariable($namn, 'Process')
        [Environment]::SetEnvironmentVariable($namn, $Environment[$namn], 'Process')
    }
    $bytKatalog = -not [string]::IsNullOrEmpty($WorkingDirectory)
    if ($bytKatalog) { Push-Location -LiteralPath $WorkingDirectory }
    try {
        $out = & powershell.exe @argsLista 2>&1 | Out-String
        $kod = $LASTEXITCODE
    }
    finally {
        if ($bytKatalog) { Pop-Location }
        foreach ($namn in $sparat.Keys) {
            [Environment]::SetEnvironmentVariable($namn, $sparat[$namn], 'Process')
        }
    }
    return @{ Threw = ($kod -ne 0); Kod = $kod; Output = $out }
}

function Remove-Pair {
    param([hashtable] $Pair)
    try { Remove-Item -LiteralPath $Pair.Root -Recurse -Force -ErrorAction Stop } catch { }
}
