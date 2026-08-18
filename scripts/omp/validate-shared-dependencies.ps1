#requires -Version 5.1
<#
.SYNOPSIS
    Check 14: cross-repository shared project cascade, for OMP consumer repositories.

.DESCRIPTION
    Consumer repositories (IbsPackager, iKrock2, LogSearch, EArkivChecker,
    Dokumentbibliotek, VajSkrivare, ...) build shared OpenModulePlatform projects
    such as OpenModulePlatform.Web.Shared straight out of this sibling repository.
    When the shared project changes, the consumer's artifact content changes too --
    and if the consumer releases the new content under an unchanged component
    version, the host rejects the artifact at import.

    This script is the single canonical implementation of the consumer-side guard.
    A consumer declares its shared dependencies in the sharedDependencies array of
    its own omp-components.json:

      "sharedDependencies": [
        {
          "repositoryKey": "openmoduleplatform",
          "repositoryPathHint": "../OpenModulePlatform",
          "projectPath": "OpenModulePlatform.Web.Shared",
          "treeId": "<git tree object id of the project directory>",
          "consumers": [ "<componentKey>", ... ]
        }
      ]

    The recorded treeId is a lockfile: git's tree object id for the shared project
    directory changes exactly when its contents change, and never otherwise. When
    the sibling has moved past what is recorded, the consumer repository must bump
    every listed consumer component and re-record the tree id in the same change
    (run the consumer's scripts/validate-component-versions.ps1 with
    -UpdateSharedDependencies, which forwards to this script).

    Consumer validators call this script instead of embedding their own copy:
    five inline copies of this rule drifted apart before, which is how the gap
    went silent in every consumer except the first one.

.PARAMETER ConsumerRepositoryRoot
    Root of the consumer repository that owns the omp-components.json being
    validated.

.PARAMETER ComponentsPath
    Path to the consumer's omp-components.json. Defaults to
    <ConsumerRepositoryRoot>\omp-components.json.

.PARAMETER BaseCommit
    Git ref in the consumer repository whose omp-components.json is the baseline
    for the consumer-bump comparison. Defaults to 'origin/main'. Pass an empty
    string to run without a baseline (only tree-id drift against the recorded
    value is reported then; consumer-bump enforcement needs the baseline).

.PARAMETER OpenModulePlatformRoot
    Root of the OpenModulePlatform sibling repository. Defaults to the
    OpenModulePlatformRoot environment variable, then to each dependency's
    repositoryPathHint relative to the consumer root.

.PARAMETER UpdateSharedDependencies
    Rewrites the recorded treeId of every drifted sharedDependencies entry to the
    sibling repository's current state. Run this together with the consumer bump
    that Check 14 asks for.

.PARAMETER Strict
    Treats "could not be checked" conditions as errors instead of warnings: a
    missing sibling repository, an unreadable tree id, or a baseline manifest
    that cannot be loaded all disable the cross-repository guard, and in CI
    that must fail the build rather than pass silently. CI (including the
    consumer's local-ci.ps1 gate) sets this switch. A plain local run without
    the OpenModulePlatform sibling can omit it and still validate versions.

    A dirty sibling working tree stays a warning even in strict mode: the
    verification itself succeeded there -- the warning only says the recorded
    treeId will go stale when the uncommitted edits are committed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConsumerRepositoryRoot,

    [Parameter(Mandatory = $false)]
    [string]$ComponentsPath = '',

    [Parameter(Mandatory = $false)]
    [AllowEmptyString()]
    [string]$BaseCommit = 'origin/main',

    [Parameter(Mandatory = $false)]
    [string]$OpenModulePlatformRoot = '',

    [Parameter(Mandatory = $false)]
    [switch]$UpdateSharedDependencies,

    [Parameter(Mandatory = $false)]
    [switch]$Strict
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Ensure git output is decoded as UTF-8 so embedded BOMs and non-ASCII
# characters are preserved exactly as stored in the repository.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$script:check14ErrorCount = 0
$script:check14WarningCount = 0

function Write-Check14Result {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message,
        [switch]$IsError,
        [switch]$IsWarning
    )

    if ($IsError) {
        $script:check14ErrorCount++
        Write-Host "ERROR: $Message" -ForegroundColor Red
    }
    elseif ($IsWarning) {
        $script:check14WarningCount++
        Write-Host "WARNING: $Message" -ForegroundColor Yellow
    }
    else {
        Write-Host $Message
    }
}

function Write-Check14Unverifiable {
    <#
    .SYNOPSIS
    Reports a condition that made the cross-repository guard impossible to run.
    These are warnings in a plain local run (a developer without the sibling
    checkout can still validate versions) but errors under -Strict, which CI
    sets: a guard that could not run must fail the build, not pass silently.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if ($Strict) {
        Write-Check14Result -Message $Message -IsError
    }
    else {
        Write-Check14Result -Message $Message -IsWarning
    }
}

function Remove-Utf8Bom {
    <#
    .SYNOPSIS
    Removes a leading UTF-8 BOM from a string so it can be parsed as JSON.

    IMPORTANT: Must use $Text[0] -eq [char]0xFEFF (not StartsWith) because
    StartsWith([char]) is culture-sensitive in Windows PowerShell 5.1 (.NET Framework)
    where U+FEFF is an "ignorable" character — it returns true for ANY string,
    silently stripping the first character.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Text
    )

    if ($Text.Length -gt 0 -and $Text[0] -eq [char]0xFEFF) {
        return $Text.Substring(1)
    }

    return $Text
}

function ConvertFrom-JsonWithDepth {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '', Justification = 'The ConvertFrom-Json -Depth call is guarded at runtime by checking Get-Command for the Depth parameter; on Windows PowerShell 5.1 the fallback branch without -Depth runs.')]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Json
    )

    $command = Get-Command ConvertFrom-Json
    if ($command.Parameters.ContainsKey('Depth')) {
        return $Json | ConvertFrom-Json -Depth 32
    }

    return $Json | ConvertFrom-Json
}

function Get-OptionalPropertyValue {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$Object,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

$consumerRoot = [System.IO.Path]::GetFullPath($ConsumerRepositoryRoot)
if ([string]::IsNullOrWhiteSpace($ComponentsPath)) {
    $ComponentsPath = Join-Path $consumerRoot 'omp-components.json'
}

if (-not (Test-Path -LiteralPath $ComponentsPath -PathType Leaf)) {
    Write-Check14Result -Message "Check 14: omp-components.json not found at '$ComponentsPath'." -IsError
    exit 1
}

$current = ConvertFrom-JsonWithDepth -Json (Remove-Utf8Bom (Get-Content -Raw -LiteralPath $ComponentsPath))

# --- Load baseline manifest ----------------------------------------------------

$base = $null
if (-not [string]::IsNullOrWhiteSpace($BaseCommit)) {
    $baseManifestText = $null
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $baseManifestText = git -C $consumerRoot show "${BaseCommit}:omp-components.json" 2>$null
        $baseExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($baseExitCode -ne 0) {
        Write-Check14Unverifiable -Message "Check 14: could not read baseline manifest from '$BaseCommit'; consumer-bump enforcement is disabled for this run."
    }
    else {
        try {
            $base = ConvertFrom-JsonWithDepth -Json (Remove-Utf8Bom ($baseManifestText | Out-String))
        }
        catch {
            Write-Check14Unverifiable -Message "Check 14: baseline omp-components.json at '$BaseCommit' is not valid JSON: $_"
            $base = $null
        }
    }
}

# --- Check 14: cross-repository shared project cascade -------------------------

$sharedDependencies = Get-OptionalPropertyValue -Object $current -Name 'sharedDependencies'
if (-not $sharedDependencies) {
    Write-Check14Result -Message "Check 14: no sharedDependencies are declared; cross-repository cascade not checked."
}
else {
    $sharedDependencyUpdates = @()

    foreach ($dependency in $sharedDependencies) {
        $projectPath = $dependency.projectPath
        $recordedTreeId = $dependency.treeId
        $consumerKeys = @($dependency.consumers)

        $siblingRoot = $OpenModulePlatformRoot
        if ([string]::IsNullOrWhiteSpace($siblingRoot)) {
            $siblingRoot = $env:OpenModulePlatformRoot
        }
        if ([string]::IsNullOrWhiteSpace($siblingRoot)) {
            $siblingRoot = Join-Path $consumerRoot $dependency.repositoryPathHint
        }

        if (-not (Test-Path -LiteralPath $siblingRoot)) {
            Write-Check14Unverifiable -Message "Check 14: sibling repository '$siblingRoot' was not found, so '$projectPath' could not be verified. A cross-repository drift will not be caught in this run."
            continue
        }

        $currentTreeId = $null
        try {
            $currentTreeId = (git -C $siblingRoot rev-parse "HEAD:$projectPath" 2>&1 | Out-String).Trim()
            if ($LASTEXITCODE -ne 0) {
                throw $currentTreeId
            }
        }
        catch {
            Write-Check14Unverifiable -Message "Check 14: could not read the tree id of '$projectPath' in '$siblingRoot': $_"
            continue
        }

        # An uncommitted edit in the sibling is invisible to the tree id, so the
        # recorded value would go stale the moment it is committed. Saying so is
        # better than a check that quietly passes on a dirty tree.
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $siblingDirty = (git -C $siblingRoot status --porcelain -- $projectPath 2>&1 | Out-String).Trim()
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        if ($siblingDirty) {
            Write-Check14Result -Message "Check 14: '$projectPath' has uncommitted changes in '$siblingRoot'. Commit them there before releasing here, or the recorded treeId will be stale." -IsWarning
        }

        if ([string]::Equals($currentTreeId, $recordedTreeId, [System.StringComparison]::OrdinalIgnoreCase)) {
            Write-Check14Result -Message "Check 14: '$projectPath' is unchanged since the recorded state ($($recordedTreeId.Substring(0, 8)))."
            continue
        }

        $sharedDependencyUpdates += [pscustomobject]@{
            ProjectPath = $projectPath
            TreeId      = $currentTreeId
        }

        # The sibling moved. Every consumer must be bumped in THIS change, and the
        # recorded id must be updated in the same commit.
        $recordedTreeIdChanged = $false
        if ($base) {
            $baseShared = Get-OptionalPropertyValue -Object $base -Name 'sharedDependencies'
            if ($baseShared) {
                $baseEntry = $baseShared | Where-Object { [string]::Equals($_.projectPath, $projectPath, [StringComparison]::Ordinal) } | Select-Object -First 1
                if ($baseEntry) {
                    $recordedTreeIdChanged = -not [string]::Equals($baseEntry.treeId, $recordedTreeId, [StringComparison]::OrdinalIgnoreCase)
                }
                else {
                    $recordedTreeIdChanged = $true
                }
            }
            else {
                $recordedTreeIdChanged = $true
            }
        }

        $missingConsumerBumps = @()
        foreach ($consumerKey in $consumerKeys) {
            $currentConsumer = $null
            if ($current.components) {
                $currentConsumer = $current.components | Where-Object { [string]::Equals($_.componentKey, $consumerKey, [StringComparison]::Ordinal) } | Select-Object -First 1
            }

            if (-not $currentConsumer) {
                $missingConsumerBumps += "component '$consumerKey' is declared as a consumer but does not exist"
                continue
            }

            $baseConsumer = $null
            if ($base -and $base.components) {
                $baseConsumer = $base.components | Where-Object { [string]::Equals($_.componentKey, $consumerKey, [StringComparison]::Ordinal) } | Select-Object -First 1
            }

            if ($baseConsumer -and [string]::Equals($baseConsumer.version, $currentConsumer.version, [StringComparison]::Ordinal)) {
                $missingConsumerBumps += "component '$consumerKey' is still at '$($currentConsumer.version)'"
            }
        }

        if ($missingConsumerBumps.Count -eq 0 -and $recordedTreeIdChanged) {
            Write-Check14Result -Message "Check 14: '$projectPath' moved in '$siblingRoot' and the consumers are bumped, but the recorded treeId is still '$recordedTreeId' (sibling is at '$currentTreeId'). Re-record with scripts/validate-component-versions.ps1 -UpdateSharedDependencies." -IsError
        }
        elseif ($missingConsumerBumps.Count -gt 0) {
            Write-Check14Result -Message "Check 14: '$projectPath' changed in '$siblingRoot' (recorded '$recordedTreeId', now '$currentTreeId') but $($missingConsumerBumps -join '; '). Bump the consumers with scripts/omp/bump-version.ps1 and re-record with -UpdateSharedDependencies, or the host will reject the artifact at import." -IsError
        }
        else {
            # No baseline: only the drift itself can be reported.
            Write-Check14Result -Message "Check 14: '$projectPath' changed in '$siblingRoot' (recorded '$recordedTreeId', now '$currentTreeId') and no baseline is available to verify consumer bumps. Bump the consumers and re-record with -UpdateSharedDependencies." -IsWarning
        }
    }

    if ($UpdateSharedDependencies -and $sharedDependencyUpdates.Count -gt 0) {
        $manifestText = Get-Content -Raw -LiteralPath $ComponentsPath
        foreach ($update in $sharedDependencyUpdates) {
            $entry = $sharedDependencies | Where-Object { [string]::Equals($_.projectPath, $update.ProjectPath, [StringComparison]::Ordinal) } | Select-Object -First 1
            if ($entry) {
                $manifestText = $manifestText.Replace("`"treeId`": `"$($entry.treeId)`"", "`"treeId`": `"$($update.TreeId)`"")
            }
        }

        # Rewriting the text rather than reserializing the object: the manifest is
        # hand-maintained and a round-trip through ConvertTo-Json would reflow the
        # whole file into an unreviewable diff.
        [System.IO.File]::WriteAllText($ComponentsPath, $manifestText, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "Updated $($sharedDependencyUpdates.Count) sharedDependencies treeId value(s) in omp-components.json." -ForegroundColor Yellow
    }
}

if ($script:check14ErrorCount -gt 0) {
    exit 1
}

exit 0
