<#
.SYNOPSIS
Helper functions for validate-component-versions.ps1 that are shared with
Pester tests. Keep this file free of side effects so it can be dot-sourced
safely in test contexts.
#>

function Get-FileSha256Hex {
    <#
    .SYNOPSIS
    Computes the lower-case hex SHA-256 hash of a file.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "File not found for SHA-256 computation: $Path"
    }

    $stream = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        $hash = $sha256.ComputeHash($stream)
        return ([System.BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Compare-WebSharedBinaryIdentity {
    <#
    .SYNOPSIS
    Decides whether a Web.Shared binary change between parent and HEAD is
    acceptable given the consumer cascade-bump state.

    .DESCRIPTION
    This function is intentionally environment-agnostic: it receives two
    hashes and a flag and returns a structured verdict. Tests drive it with
    injected hash pairs so the check is never coupled to a committed absolute
    baseline or to the reproducibility of a .NET build across machines.

    .OUTPUTS
    Hashtable with keys:
      Result  - 'Pass', 'Fail', or 'Skip'
      Message - human-readable explanation
    #>
    param(
        [Parameter(Mandatory = $false)]
        [string]$ParentHash = '',

        [Parameter(Mandatory = $false)]
        [string]$HeadHash = '',

        [Parameter(Mandatory = $false)]
        [bool]$CascadeBumped = $false
    )

    if ([string]::IsNullOrWhiteSpace($ParentHash) -or [string]::IsNullOrWhiteSpace($HeadHash)) {
        return @{
            Result  = 'Skip'
            Message = 'Cannot compare Web.Shared binary identity because one or both hashes are missing.'
        }
    }

    if ([string]::Equals($ParentHash, $HeadHash, [StringComparison]::OrdinalIgnoreCase)) {
        return @{
            Result  = 'Pass'
            Message = "Web.Shared binary is unchanged between parent and HEAD ($HeadHash)."
        }
    }

    if (-not $CascadeBumped) {
        return @{
            Result  = 'Fail'
            Message = "Web.Shared binary changed between parent and HEAD ($ParentHash -> $HeadHash) but no consumer cascade-bump was detected. Run `.\\scripts\\omp\\bump-version.ps1 -CascadeFrom 'OpenModulePlatform.Web.Shared/OpenModulePlatform.Web.Shared.csproj'`."
        }
    }

    return @{
        Result  = 'Pass'
        Message = "Web.Shared binary changed between parent and HEAD ($ParentHash -> $HeadHash) and consumers were cascade-bumped."
    }
}
