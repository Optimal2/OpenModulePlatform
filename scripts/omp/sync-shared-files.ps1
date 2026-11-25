#Requires -Version 5.1
<#
.SYNOPSIS
    Synchronizes existing copies of canonical OMP shared files in local consumer checkouts.

.DESCRIPTION
    Uses the file lists from Check 15 in validate-shared-scripts.ps1. Files
    with equivalent line endings are left untouched. Drifted copies receive
    the exact canonical bytes. Missing copies are reported as not carried.

    Exit 0 when all carried files match, 1 on an inventory, discovery, or file
    error (including any NOT MEASURED consumer), and 2 in DryRun when drift
    exists without errors.

.PARAMETER PlatformRepositoryRoot
    Canonical checkout to copy from. Defaults to this script's repository.

.PARAMETER ConsumerRoot
    Directory containing the consumer checkouts as immediate children.
    Defaults to OMP_CONSUMER_ROOT, then to the parent of the main platform checkout.

.PARAMETER DryRun
    Reports copies that would be made without writing files.

.EXAMPLE
    ./scripts/omp/sync-shared-files.ps1 -DryRun

.EXAMPLE
    ./scripts/omp/sync-shared-files.ps1
#>
[CmdletBinding()]
param(
    [string]$PlatformRepositoryRoot,
    [string]$ConsumerRoot,
    [switch]$DryRun
)

# The shared core is side-effect free when dot-sourced. Reuse its final-path
# reader so junctions, symlinks and 8.3 aliases compare as the same checkout.
. (Join-Path $PSScriptRoot 'validate-component-versions.helpers.ps1')

function Resolve-OmpSharedFilePlatformRoot {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        $Path = Join-Path $PSScriptRoot '../..'
    }
    $directory = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($directory -isnot [IO.DirectoryInfo]) {
        throw "Platform repository root '$Path' is not a directory."
    }
    return $directory.FullName
}

function Get-OmpSharedFileRepositoryKey {
    param([string]$ManifestPath)

    $json = [IO.File]::ReadAllText($ManifestPath)
    if (-not $json.TrimStart().StartsWith('{')) {
        throw "Expected a JSON object in '$ManifestPath'."
    }
    $manifest = ConvertFrom-Json -InputObject $json -ErrorAction Stop
    $key = if ($manifest.PSObject.Properties['repositoryKey']) { $manifest.repositoryKey }
    if ($key -isnot [string] -or [string]::IsNullOrWhiteSpace($key)) {
        throw "Missing or invalid repositoryKey in '$ManifestPath'."
    }
    return $key
}

function Invoke-OmpSharedFileGit {
    param([string]$Root, [string]$RevParseOption)

    # Capture Git's diagnostic as text on both PowerShell engines.
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    $git = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $output = @(& $git.Source -C $Root rev-parse $RevParseOption 2>&1)
    if ($LASTEXITCODE -ne 0 -or $output.Count -ne 1) {
        throw "git rev-parse $RevParseOption failed in '$Root' (exit $LASTEXITCODE): $($output -join ' ')"
    }
    return [string]$output[0]
}

function Get-OmpSharedFileConsumers {
    [CmdletBinding()]
    param(
        [string]$PlatformRepositoryRoot,
        [string]$ConsumerRoot
    )

    if ([string]::IsNullOrWhiteSpace($ConsumerRoot)) {
        $ConsumerRoot = $env:OMP_CONSUMER_ROOT
    }

    $mainCheckout = $null
    try {
        $platform = Get-OmpCanonicalDirectoryPath -Path (Resolve-OmpSharedFilePlatformRoot -Path $PlatformRepositoryRoot)
        $platformKey = Get-OmpSharedFileRepositoryKey (Join-Path $platform 'omp-components.json')
        if ([string]::IsNullOrWhiteSpace($ConsumerRoot)) {
            $commonDir = Invoke-OmpSharedFileGit -Root $platform -RevParseOption '--git-common-dir'
            # IsPathRooted accepts drive-relative (C:x) and root-relative (\x)
            # paths. The shared resolver joins only genuinely relative paths and
            # rejects those two process-dependent forms on both PowerShell engines.
            $commonDir = Get-OmpPlatformFullPath -Path $commonDir -BasePath $platform
            $mainCheckout = Split-Path -Parent $commonDir
            $ConsumerRoot = Split-Path -Parent $mainCheckout
        }
        $mainCheckoutRealPath = if ($mainCheckout) { Get-OmpCanonicalDirectoryPath -Path $mainCheckout } else { $null }
        if (-not (Test-Path -LiteralPath $ConsumerRoot -PathType Container -ErrorAction Stop)) {
            throw "Consumer root '$ConsumerRoot' is missing or is not a directory."
        }
        $children = @(Get-ChildItem -LiteralPath $ConsumerRoot -Directory -Force -ErrorAction Stop | Sort-Object Name)
    }
    catch {
        return [pscustomobject]@{
            Name = 'Consumer discovery'; Root = $ConsumerRoot; DiscoveredRoot = $ConsumerRoot
            Resolved = $false; Reason = $_.Exception.Message
        }
    }

    foreach ($child in $children) {
        $reason = $null
        $childRealPath = $child.FullName
        try {
            $childRealPath = Get-OmpCanonicalDirectoryPath -Path $child.FullName
            $isPlatform = [string]::Equals($childRealPath, $platform, [StringComparison]::OrdinalIgnoreCase)
            $isMainCheckout = $mainCheckoutRealPath -and
                [string]::Equals($childRealPath, $mainCheckoutRealPath, [StringComparison]::OrdinalIgnoreCase)
            if ($isPlatform -or $isMainCheckout) {
                continue
            }

            $manifestPath = Join-Path $child.FullName 'omp-components.json'
            try {
                $null = Get-Item -LiteralPath $manifestPath -Force -ErrorAction Stop
            }
            catch [System.Management.Automation.ItemNotFoundException] {
                continue
            }

            if ((Get-OmpSharedFileRepositoryKey $manifestPath) -eq $platformKey) {
                continue
            }
            $gitRoot = Invoke-OmpSharedFileGit -Root $child.FullName -RevParseOption '--show-toplevel'
            $gitRootRealPath = Get-OmpCanonicalDirectoryPath -Path $gitRoot
            if (-not [string]::Equals($gitRootRealPath, $childRealPath, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Git checkout is rooted at '$gitRootRealPath' rather than '$childRealPath'."
            }
        }
        catch {
            $reason = $_.Exception.Message
        }

        [pscustomobject]@{
            Name = $child.Name; Root = $childRealPath; DiscoveredRoot = $child.FullName
            Resolved = ($null -eq $reason); Reason = $reason
        }
    }
}

function Get-OmpSharedFileInventory {
    param([string]$ValidatorPath)

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($ValidatorPath, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw "Cannot parse shared-file inventory in '$ValidatorPath': $($parseErrors[0].Message)"
    }

    foreach ($name in 'sharedScripts', 'sharedWebBuildFiles', 'sharedPesterStepFiles') {
        $assignments = @($ast.EndBlock.Statements | Where-Object {
            $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $_.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $_.Left.VariablePath.UserPath -eq $name
        })
        if ($assignments.Count -ne 1 -or $assignments[0].Operator -ne 'Equals') {
            throw "Expected one top-level assignment to `$$name in '$ValidatorPath'."
        }

        $right = $assignments[0].Right
        if ($right -isnot [System.Management.Automation.Language.CommandExpressionAst] -or $right.Redirections.Count -ne 0) {
            throw "Expected a literal string array for `$$name in '$ValidatorPath'."
        }
        $array = $right.Expression
        $elements = if ($array -is [System.Management.Automation.Language.ArrayExpressionAst]) {
            foreach ($statement in $array.SubExpression.Statements) {
                if ($statement -isnot [System.Management.Automation.Language.PipelineAst] -or
                    $statement.PipelineElements.Count -ne 1 -or
                    $statement.PipelineElements[0] -isnot [System.Management.Automation.Language.CommandExpressionAst] -or
                    $statement.PipelineElements[0].Redirections.Count -ne 0) {
                    throw "Expected literal string elements in `$$name in '$ValidatorPath'."
                }
                $expression = $statement.PipelineElements[0].Expression
                if ($expression -is [System.Management.Automation.Language.ArrayLiteralAst]) {
                    $expression.Elements
                }
                else {
                    $expression
                }
            }
        }
        elseif ($array -is [System.Management.Automation.Language.ArrayLiteralAst]) {
            $array.Elements
        }
        else {
            throw "Expected a literal string array for `$$name in '$ValidatorPath'."
        }

        foreach ($element in $elements) {
            if ($element -isnot [System.Management.Automation.Language.StringConstantExpressionAst]) {
                throw "Expected literal string elements in `$$name in '$ValidatorPath'."
            }
            if ([string]::IsNullOrWhiteSpace($element.Value) -or $element.Value -match '(^[/\\]|:|(^|[/\\])\.\.([/\\]|$))') {
                throw "Invalid repository-relative path '$($element.Value)' in `$$name in '$ValidatorPath'."
            }
            $element.Value
        }
    }
}

function Get-OmpSharedFileHash {
    param([string]$Path)

    # Match Check 15: normalize CRLF and CR, retaining BOMs and trailing newlines.
    $text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($Path))
    $normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n")
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($normalized))) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Invoke-OmpSharedFileSync {
    param([string]$PlatformRepositoryRoot, [string]$ConsumerRoot, [switch]$DryRun)

    $failed = 0
    $changed = 0
    $measured = 0
    $unresolved = 0
    $action = if ($DryRun) { 'Would copy' } else { 'Copied' }
    try {
        $platform = Resolve-OmpSharedFilePlatformRoot -Path $PlatformRepositoryRoot
        $validator = Join-Path $platform 'scripts/omp/validate-shared-scripts.ps1'
        $inventory = @(Get-OmpSharedFileInventory -ValidatorPath $validator | Sort-Object -Unique)
        if ($inventory.Count -eq 0) {
            throw "Shared file inventory in '$validator' is empty; expected at least one canonical file."
        }
        $canonical = @{}

        foreach ($consumer in (Get-OmpSharedFileConsumers -PlatformRepositoryRoot $platform -ConsumerRoot $ConsumerRoot)) {
            if (-not $consumer.Resolved) {
                Write-Host "$($consumer.Name) [$($consumer.Root)] NOT MEASURED: $($consumer.Reason)"
                $unresolved++
                continue
            }

            $measured++
            Write-Host "$($consumer.Name) [$($consumer.Root)]"
            $identical = 0
            foreach ($relative in $inventory) {
                $destination = Join-Path $consumer.Root $relative
                try {
                    try {
                        $null = Get-Item -LiteralPath $destination -Force -ErrorAction Stop
                    }
                    catch [System.Management.Automation.ItemNotFoundException] {
                        Write-Host "  not carried: $relative"
                        continue
                    }

                    $source = Join-Path $platform $relative
                    if (-not $canonical.ContainsKey($relative)) {
                        $canonical[$relative] = Get-OmpSharedFileHash $source
                    }
                    $oldHash = Get-OmpSharedFileHash $destination
                    $newHash = $canonical[$relative]
                    if ($oldHash -eq $newHash) {
                        $identical++
                        continue
                    }

                    if (-not $DryRun) {
                        [IO.File]::Copy($source, $destination, $true)
                    }
                    $changed++
                    Write-Host "  ${action}: $relative ($($oldHash.Substring(0, 8)) -> $($newHash.Substring(0, 8)))"
                }
                catch {
                    $failed++
                    Write-Host "  ERROR ${relative}: $($_.Exception.Message)"
                }
            }
            Write-Host "  already identical: $identical"
        }
    }
    catch {
        $failed++
        Write-Host "ERROR: $($_.Exception.Message)"
    }

    $changeResult = if ($DryRun) { 'would be copied' } else { 'copied' }
    Write-Host "Summary: $measured consumer(s) scanned, $unresolved NOT MEASURED, $changed file(s) $changeResult, $failed error(s)."
    if ($failed -gt 0 -or $unresolved -gt 0) { return 1 }
    if ($DryRun -and $changed -gt 0) { return 2 }
    return 0
}

if ($MyInvocation.InvocationName -ne '.') {
    exit (Invoke-OmpSharedFileSync -PlatformRepositoryRoot $PlatformRepositoryRoot -ConsumerRoot $ConsumerRoot -DryRun:$DryRun)
}
