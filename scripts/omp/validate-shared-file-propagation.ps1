#Requires -Version 5.1
<#
.SYNOPSIS
    Validates carried copies of canonical OMP shared files in local consumers.

.DESCRIPTION
    Uses Get-OmpSharedFileConsumers, Get-OmpSharedFileInventory and
    Get-OmpSharedFileHash from sync-shared-files.ps1. A consumer checkout that
    is behind its local remote-tracking upstream is NOT MEASURED rather than
    compared, because its old working tree must not make this gate permanently
    red. This command never fetches and never writes to a consumer.

    Exit 0: every discovered consumer was measured and all carried files match.
    Exit 1: drift or an unexpected measurement error.
    Exit 3: no drift, but one or more consumers (or consumer roots) were NOT
            MEASURED. This distinct non-failure signal lets local-ci.ps1 label
            the step honestly without parsing its output.

.PARAMETER PlatformRepositoryRoot
    Canonical checkout. Defaults to this script's repository.

.PARAMETER ConsumerRoot
    One or more directories containing consumer checkouts as immediate children.
    Defaults to OMP_CONSUMER_ROOT split on semicolons, then to the consumer root
    discovered by sync-shared-files.ps1 beside the main platform checkout.

.EXAMPLE
    ./scripts/omp/validate-shared-file-propagation.ps1

.EXAMPLE
    ./scripts/omp/validate-shared-file-propagation.ps1 -ConsumerRoot C:\src,C:\more-src
#>
[CmdletBinding()]
param(
    [string]$PlatformRepositoryRoot = '',
    [string[]]$ConsumerRoot = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:OmpPropagationScriptDirectory = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($script:OmpPropagationScriptDirectory)) {
    # Windows PowerShell 5.1 can leave PSScriptRoot empty in some invocation
    # forms. Capture MyCommand at script scope, not inside a function.
    $script:OmpPropagationScriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
}

function Invoke-OmpPropagationGit {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    # Native stderr must stay text under Windows PowerShell 5.1. In particular,
    # do not use '@{u}' revision syntax: a missing upstream writes a fatal error
    # there. for-each-ref below reports a missing upstream as empty output.
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& git -C $Root @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Text = ($output -join "`n").Trim()
    }
}

function Get-OmpSharedFileUpstreamState {
    param([Parameter(Mandatory = $true)][string]$Root)

    # symbolic-ref also works on an unborn branch. Detached HEAD has no branch
    # upstream, so it is measured with the same explicit no-upstream label.
    $branchResult = Invoke-OmpPropagationGit -Root $Root -Arguments @('symbolic-ref', '--quiet', '--short', 'HEAD')
    if ($branchResult.ExitCode -ne 0 -and $branchResult.ExitCode -ne 1) {
        return [pscustomobject]@{
            CanMeasure = $false; Upstream = ''; Reason = "could not read the current branch: $($branchResult.Text)"
        }
    }

    $upstream = ''
    if ($branchResult.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($branchResult.Text)) {
        $branch = $branchResult.Text
        $upstreamResult = Invoke-OmpPropagationGit -Root $Root -Arguments @(
            'for-each-ref', "--format=%(upstream:short)", "refs/heads/$branch"
        )
        if ($upstreamResult.ExitCode -ne 0) {
            return [pscustomobject]@{
                CanMeasure = $false; Upstream = ''; Reason = "could not read the upstream for '$branch': $($upstreamResult.Text)"
            }
        }
        $upstream = $upstreamResult.Text
    }

    if ([string]::IsNullOrWhiteSpace($upstream)) {
        return [pscustomobject]@{ CanMeasure = $true; Upstream = ''; Reason = '' }
    }

    $behindResult = Invoke-OmpPropagationGit -Root $Root -Arguments @('rev-list', '--count', "HEAD..$upstream")
    $behind = 0
    if ($behindResult.ExitCode -ne 0 -or -not [int]::TryParse($behindResult.Text, [ref]$behind)) {
        return [pscustomobject]@{
            CanMeasure = $false; Upstream = $upstream
            Reason = "could not compare HEAD with '$upstream': $($behindResult.Text)"
        }
    }
    if ($behind -gt 0) {
        return [pscustomobject]@{
            CanMeasure = $false; Upstream = $upstream
            Reason = "checkout is $behind commit(s) behind $upstream; update it to measure"
        }
    }

    return [pscustomobject]@{ CanMeasure = $true; Upstream = $upstream; Reason = '' }
}

function Invoke-OmpSharedFilePropagationValidation {
    param(
        [string]$PlatformRepositoryRoot,
        [string[]]$ConsumerRoot
    )

    # Dot-sourcing WP2 binds its own param block in this scope. Preserve our
    # parameters first, or its empty ConsumerRoot silently selects the default.
    $requestedPlatformRoot = $PlatformRepositoryRoot
    $requestedConsumerRoots = @($ConsumerRoot)
    . (Join-Path $script:OmpPropagationScriptDirectory 'sync-shared-files.ps1')

    $platform = Resolve-OmpSharedFilePlatformRoot -Path $requestedPlatformRoot
    $validatorPath = Join-Path $platform 'scripts/omp/validate-shared-scripts.ps1'
    $inventory = @(Get-OmpSharedFileInventory -ValidatorPath $validatorPath | Sort-Object -Unique)
    $canonicalHashes = @{}

    $roots = @($requestedConsumerRoots | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($roots.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($env:OMP_CONSUMER_ROOT)) {
        $roots = @($env:OMP_CONSUMER_ROOT -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    if ($roots.Count -eq 0) {
        # Empty means WP2's default: beside the main platform checkout.
        $roots = @('')
    }

    $consumersByRealPath = [ordered]@{}
    foreach ($root in $roots) {
        $found = @(Get-OmpSharedFileConsumers -PlatformRepositoryRoot $platform -ConsumerRoot $root)
        if ($found.Count -eq 0) {
            $label = if ([string]::IsNullOrWhiteSpace($root)) { '<default>' } else { $root }
            $found = @([pscustomobject]@{
                Name = 'Consumer discovery'; Root = $root; DiscoveredRoot = $root; Resolved = $false
                Reason = "No consumer checkouts were found under '$label'."
            })
        }
        foreach ($consumer in $found) {
            # WP2 returns Root as the resolved real path and DiscoveredRoot as
            # the path through which it was found. OrderedDictionary string keys
            # are case-insensitive on both supported PowerShell engines.
            $key = [string]$consumer.Root
            if ([string]::IsNullOrWhiteSpace($key)) {
                $key = "discovery:$root"
            }
            if (-not $consumersByRealPath.Contains($key)) {
                $consumersByRealPath[$key] = $consumer
            }
            elseif (-not $consumersByRealPath[$key].Resolved -and $consumer.Resolved) {
                $consumersByRealPath[$key] = $consumer
            }
        }
    }

    $measured = 0
    $drifted = 0
    $notMeasured = 0
    $errors = 0
    foreach ($consumer in $consumersByRealPath.Values) {
        if (-not $consumer.Resolved) {
            $notMeasured++
            Write-Host "$($consumer.Name): NOT MEASURED ($($consumer.Reason))"
            continue
        }

        try {
            $upstreamState = Get-OmpSharedFileUpstreamState -Root $consumer.Root
        }
        catch {
            $upstreamState = [pscustomobject]@{
                CanMeasure = $false; Upstream = ''; Reason = "could not inspect the upstream: $($_.Exception.Message)"
            }
        }
        if (-not $upstreamState.CanMeasure) {
            $notMeasured++
            Write-Host "$($consumer.Name): NOT MEASURED ($($upstreamState.Reason))"
            continue
        }

        try {
            $identical = 0
            $different = 0
            $noUpstreamSuffix = if ([string]::IsNullOrWhiteSpace($upstreamState.Upstream)) { '; no upstream configured' } else { '' }
            foreach ($relative in $inventory) {
                $consumerPath = Join-Path $consumer.Root $relative
                if (-not (Test-Path -LiteralPath $consumerPath -PathType Leaf)) {
                    # Check 15 in the consumer decides whether a missing file is
                    # required. This gate compares only files actually carried.
                    continue
                }
                if (-not $canonicalHashes.ContainsKey($relative)) {
                    $canonicalHashes[$relative] = Get-OmpSharedFileHash (Join-Path $platform $relative)
                }
                $platformHash = $canonicalHashes[$relative]
                $consumerHash = Get-OmpSharedFileHash $consumerPath
                if ($consumerHash -eq $platformHash) {
                    $identical++
                    continue
                }

                $different++
                Write-Host "  $relative differs (platform $($platformHash.Substring(0, 12)), consumer $($consumerHash.Substring(0, 12)))."
            }

            $measured++
            if ($different -gt 0) {
                $drifted++
                Write-Host "$($consumer.Name): DRIFT ($different file(s) differ$noUpstreamSuffix)"
            }
            else {
                Write-Host "$($consumer.Name): OK ($identical files identical$noUpstreamSuffix)"
            }
        }
        catch {
            $errors++
            $notMeasured++
            Write-Host "$($consumer.Name): NOT MEASURED (measurement failed: $($_.Exception.Message))"
        }
    }

    if ($notMeasured -gt 0) {
        Write-Warning "Shared file propagation: NOT MEASURED for $notMeasured consumer(s)"
        Write-Host "Shared file propagation: NOT MEASURED for $notMeasured consumer(s)"
    }
    elseif ($measured -eq 0) {
        Write-Warning 'Shared file propagation: NOT MEASURED for 0 consumer(s)'
        Write-Host 'Shared file propagation: NOT MEASURED for 0 consumer(s)'
    }

    if ($errors -gt 0) {
        Write-Host "Shared file propagation: ERROR during $errors consumer measurement(s)."
        return 1
    }
    if ($drifted -gt 0) {
        Write-Host "Shared file propagation: DRIFT for $drifted consumer(s)."
        Write-Host 'Run scripts/omp/sync-shared-files.ps1, then commit the copies in each consumer in the same change.'
        return 1
    }
    if ($notMeasured -gt 0 -or $measured -eq 0) {
        return 3
    }

    Write-Host "Shared file propagation: OK for $measured consumer(s)."
    return 0
}

try {
    exit (Invoke-OmpSharedFilePropagationValidation -PlatformRepositoryRoot $PlatformRepositoryRoot -ConsumerRoot $ConsumerRoot)
}
catch {
    Write-Host "Shared file propagation: ERROR: $($_.Exception.Message)"
    exit 1
}
