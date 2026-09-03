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
        [switch] $OmitPlatformRoot
    )

    $root = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
    $consumer = Join-Path $root 'Consumer'
    $platform = Join-Path $root 'OpenModulePlatform'

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
    param([hashtable] $Pair, [switch] $Strict)

    $argsLista = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:GuardScript,
        '-ConsumerRepositoryRoot', $Pair.Consumer,
        '-PlatformRepositoryRoot', $Pair.Platform
    )
    if ($Strict) { $argsLista += '-Strict' }

    $out = & powershell.exe @argsLista 2>&1 | Out-String
    return @{ Threw = ($LASTEXITCODE -ne 0); Kod = $LASTEXITCODE; Output = $out }
}

function Remove-Pair {
    param([hashtable] $Pair)
    try { Remove-Item -LiteralPath $Pair.Root -Recurse -Force -ErrorAction Stop } catch { }
}
