#Requires -Version 5.1
<#
.SYNOPSIS
    Proves the shared-script drift guard fails loudly on divergence and stays
    quiet-but-visible when it cannot measure.

.DESCRIPTION
    scripts/omp/bump-version.ps1 is copied verbatim into all nine repositories.
    Keeping the copies identical was a manual act twice (2026-08-25 and
    2026-08-28), and nothing held them that way: the next fix to the canonical
    file recreated the drift the day it landed, silently. A repository running a
    stale copy looks green locally; the difference surfaces only when someone
    runs a bump that behaves differently from the neighbouring repository -
    typically mid-incident, which is exactly what happened on 2026-08-23. It
    happened again on 2026-09-02, when this very campaign's fix to the canonical
    file left the other eight behind for as long as it took to notice.

    The guard follows the pattern validate-shared-dependencies.ps1 already
    established: one canonical implementation in OpenModulePlatform, CALLED by
    the consumers rather than copied into them. A guard that were itself copied
    would be subject to the drift it exists to detect - as the Check 14 code puts
    it, two implementations of the same rule are how the original gap went silent.
#>

Set-StrictMode -Version Latest
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
    param([hashtable] $Pair, [switch] $Strict)

    try {
        # 3>&1 fangar WARNING-strommen. Utan den missas just det som testet
        # "en omatbar kontroll ska SYNAS" ska bevisa - noten skrivs med
        # Write-Warning, som inte gar via stderr.
        $out = & $script:GuardScript -ConsumerRepositoryRoot $Pair.Consumer `
            -PlatformRepositoryRoot $Pair.Platform -Strict:$Strict 3>&1 2>&1 | Out-String
        return @{ Threw = $false; Output = $out }
    }
    catch {
        return @{ Threw = $true; Output = $_.Exception.Message }
    }
}

function Remove-Pair {
    param([hashtable] $Pair)
    try { Remove-Item -LiteralPath $Pair.Root -Recurse -Force -ErrorAction Stop } catch { }
}

Describe 'validate-shared-scripts: drift is loud' {
    It 'Fails when the consumer copy differs from the canonical one' {
        $pair = New-Pair -ConsumerBody 'stale content' -PlatformBody 'canonical content'
        try {
            $result = Invoke-Guard -Pair $pair
            $result.Threw | Should Be $true
            ($result.Output -match 'bump-version\.ps1') | Should Be $true
        }
        finally { Remove-Pair -Pair $pair }
    }

    It 'Names both hashes so the divergence can be identified' {
        $pair = New-Pair -ConsumerBody 'stale content' -PlatformBody 'canonical content'
        try {
            $result = Invoke-Guard -Pair $pair
            # Two different SHA-256 prefixes must appear in the message.
            ([regex]::Matches($result.Output, '[0-9a-f]{16}')).Count -ge 2 | Should Be $true
        }
        finally { Remove-Pair -Pair $pair }
    }

    It 'Says how to resolve the drift rather than only reporting it' {
        $pair = New-Pair -ConsumerBody 'stale content' -PlatformBody 'canonical content'
        try {
            $result = Invoke-Guard -Pair $pair
            ($result.Output -match 'Copy') | Should Be $true
        }
        finally { Remove-Pair -Pair $pair }
    }

    It 'Passes when the copies are byte-identical' {
        $pair = New-Pair -ConsumerBody 'same content' -PlatformBody 'same content'
        try {
            $result = Invoke-Guard -Pair $pair
            $result.Threw | Should Be $false
        }
        finally { Remove-Pair -Pair $pair }
    }

    It 'Treats a trailing-newline difference as drift, not as equal' {
        # Byte-identical means byte-identical: a copy that differs only in its
        # final newline is still a different file, and pretending otherwise is
        # how a guard starts having opinions.
        $pair = New-Pair -ConsumerBody "same content`n" -PlatformBody 'same content'
        try {
            $result = Invoke-Guard -Pair $pair
            $result.Threw | Should Be $true
        }
        finally { Remove-Pair -Pair $pair }
    }
}

Describe 'validate-shared-scripts: cannot-measure is visible, never silently green' {
    It 'Skips with a visible note when the platform repository is not beside the consumer' {
        # CI checks out one repository, so the neighbour is usually absent. That
        # must not fail the build - but it must not read as a passing check either.
        $pair = New-Pair -ConsumerBody 'anything' -PlatformBody '' -OmitPlatformRoot
        try {
            $result = Invoke-Guard -Pair $pair
            $result.Threw | Should Be $false
            ($result.Output -match 'not verified|skipp') | Should Be $true
        }
        finally { Remove-Pair -Pair $pair }
    }

    It 'Fails on a missing neighbour when -Strict is passed' {
        # The opt-in form for environments that DO have the neighbour and want
        # its absence treated as a problem.
        $pair = New-Pair -ConsumerBody 'anything' -PlatformBody '' -OmitPlatformRoot
        try {
            $result = Invoke-Guard -Pair $pair -Strict
            $result.Threw | Should Be $true
        }
        finally { Remove-Pair -Pair $pair }
    }

    It 'Fails when the consumer has no copy of a script the platform ships' {
        # A missing shared script is drift of the most complete kind.
        $pair = New-Pair -ConsumerBody '' -PlatformBody 'canonical content' -OmitConsumerScript
        try {
            $result = Invoke-Guard -Pair $pair
            $result.Threw | Should Be $true
            ($result.Output -match 'missing|saknas') | Should Be $true
        }
        finally { Remove-Pair -Pair $pair }
    }
}
