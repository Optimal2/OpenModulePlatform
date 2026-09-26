# Shared setup for Validate-SharedScripts.Tests.ps1.
# Dot-sourced from each Describe block's BeforeAll: Pester 5 runs every
# container in a separate session state, so functions and variables defined
# at file scope are not visible inside It blocks.

$ErrorActionPreference = 'Stop'

$script:GuardScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/omp/validate-shared-scripts.ps1'

function New-Pair {
    <# Creates a consumer root and a platform root with the given script bodies. #>
    param(
        [string] $ConsumerBody,
        [string] $PlatformBody,
        [switch] $OmitConsumerScript,
        [switch] $OmitPlatformRoot,
        # A name other than OpenModulePlatform puts the platform checkout where
        # the sibling assumption cannot find it, as in a worktree under another root.
        [string] $PlatformDirectoryName = 'OpenModulePlatform'
    )

    $root = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
    $consumer = Join-Path $root 'Consumer'
    $platform = Join-Path $root $PlatformDirectoryName

    New-Item -ItemType Directory -Path (Join-Path $consumer 'scripts\omp') -Force | Out-Null
    if (-not $OmitConsumerScript) {
        [IO.File]::WriteAllText((Join-Path $consumer 'scripts\omp\bump-version.ps1'), $ConsumerBody)
    }

    if (-not $OmitPlatformRoot) {
        New-Item -ItemType Directory -Path (Join-Path $platform 'scripts\omp') -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $platform 'scripts\omp\bump-version.ps1'), $PlatformBody)
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
        # Environment for the child process only; restored afterwards. A $null
        # value removes the variable, so an ambient value cannot leak into the proof.
        [hashtable] $Environment = @{}
    )

    $argsLista = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:GuardScript,
        '-ConsumerRepositoryRoot', $Pair.Consumer
    )
    if (-not $OmitPlatformArgument) { $argsLista += @('-PlatformRepositoryRoot', $Pair.Platform) }
    if ($Strict) { $argsLista += '-Strict' }

    $sparat = @{}
    foreach ($namn in $Environment.Keys) {
        $sparat[$namn] = [Environment]::GetEnvironmentVariable($namn, 'Process')
        [Environment]::SetEnvironmentVariable($namn, $Environment[$namn], 'Process')
    }
    try {
        $out = & powershell.exe @argsLista 2>&1 | Out-String
        $kod = $LASTEXITCODE
    }
    finally {
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
