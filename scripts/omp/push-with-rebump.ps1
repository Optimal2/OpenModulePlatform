<#
.SYNOPSIS
Push that survives being overtaken: rebase onto origin, recompute the version bump from the
new base, gate once, push. Repeat only if someone landed while the gate ran.

.DESCRIPTION
The problem this exists for
---------------------------
A version number is a decision that belongs at merge time, but we store it as data in the
commit. Two nodes that bump in parallel pick the SAME number, git merges the identical lines
without a conflict, and the rebase turns the loser's bump into a no-op. Nothing announces it.
The error surfaces minutes later in the component validator ("references changed project(s)
... but its version was not bumped") or, worse, at import time when HostAgent refuses an
artifact whose content changed under an identity it already knows.

Measured on OpenModulePlatform: since 2026-08-01, 218 of 357 commits touch
omp-components.json -- 61 % of all commits are candidates for this collision. Races are
documented on 25 Aug (twice), 31 Aug and 4 Sep.

Why this is not just a retry loop
---------------------------------
The obvious fix is to notice the validator error and bump again. That works, but it pays a
full gate run (~8 minutes) for every race, because the number was chosen BEFORE the gate ran
and only checked afterwards.

This script inverts the order: fetch and rebase FIRST, then compute the bump from the base
that is actually current, then gate once. The number is therefore always derived from what is
published, and the window in which someone can overtake you shrinks from "the whole gate" to
"the gate plus a push". A retry is still needed for that remainder -- but it is the exception,
not the normal path.

What it does NOT do
-------------------
It does not remove the race. Only deriving the number at build time from git height (the
"option B" in `AI-System/Förslag till Linus — versionsnummer utan race.md`) does that, and
that is a campaign: 18 C# files and 13 scripts read these fields, and maxVersion pins,
consistentArtifactSets and HostAgent's identity guard are all built on numbers stored in
files. This script is the cheap 90 %, deliberately.

.PARAMETER Remote
Remote to push to. Default 'origin'.

.PARAMETER MaxAttempts
How many times to recompute after being overtaken. Default 5. Each attempt costs a gate run,
so this is a guard against an infinite loop, not a target.

.PARAMETER DryRun
Do everything except the actual push and the amend: print what would change. Use this the
first few times.

.EXAMPLE
pwsh -File scripts/omp/push-with-rebump.ps1
#>
[CmdletBinding()]
param(
    [string]$Remote = 'origin',
    [int]$MaxAttempts = 5,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step { param([string]$Text) Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Note { param([string]$Text) Write-Host "    $Text" -ForegroundColor DarkGray }
function Write-Good { param([string]$Text) Write-Host "    $Text" -ForegroundColor Green }
function Write-Warn { param([string]$Text) Write-Host "    $Text" -ForegroundColor Yellow }

function Invoke-Git {
    <#
    Runs git and returns stdout. Throws with the real stderr on failure.

    Why not just call git: under PowerShell 5.1 a native command writing to stderr can turn
    into a terminating error when $ErrorActionPreference is Stop, so a perfectly normal
    "everything up to date" on stderr would abort the script. Capture, then judge on the exit
    code. (See the house note on PS 5.1 stderr behaviour.)
    #>
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$AllowFailure)

    # EAP maste sankas RUNT anropet, inte bara beskrivas i kommentaren ovan. Med
    # $ErrorActionPreference = 'Stop' pa skriptniva gor `2>&1` att varje rad git skriver
    # till stderr blir ett TERMINERANDE fel — och git skriver sin normala "To <url>" dit
    # vid en LYCKAD push. Skriptet dog darfor precis efter att ha pushat, forsta gangen
    # det kordes skarpt. Domen ska falla pa $LASTEXITCODE, inget annat.
    $tidigareEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $stdout = & git @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $tidigareEap
    }
    $text = ($stdout | Out-String).TrimEnd()
    if ($code -ne 0 -and -not $AllowFailure) {
        throw "git $($Arguments -join ' ') failed with exit code $code`n$text"
    }
    return [pscustomobject]@{ Output = $text; ExitCode = $code }
}

$repoRoot = (Invoke-Git -Arguments @('rev-parse', '--show-toplevel')).Output.Trim()
if (-not $repoRoot) { throw 'Not inside a git repository.' }
Set-Location $repoRoot

$manifestPath = Join-Path $repoRoot 'omp-components.json'
if (-not (Test-Path $manifestPath)) {
    throw "omp-components.json not found in $repoRoot. This script is for OMP-compatible repositories."
}

$bumpScript = Join-Path $repoRoot 'scripts\omp\bump-version.ps1'
if (-not (Test-Path $bumpScript)) { throw "scripts/omp/bump-version.ps1 not found." }
$localCi = Join-Path $repoRoot 'scripts\local-ci.ps1'
if (-not (Test-Path $localCi)) { throw "scripts/local-ci.ps1 not found." }

# A dirty tree makes every step below ambiguous: we cannot tell the commit's own bump from an
# uncommitted edit, and a rebase would refuse anyway. Refuse early with a clear reason rather
# than failing three steps in.
#
# NEVER stash to work around this. In the DEV repository `git stash` deletes the .git junction
# (measured, reproduced three times on 2026-08-30); the failure then looks like repository
# corruption. Commit out of the way instead.
$dirty = (Invoke-Git -Arguments @('status', '--porcelain')).Output
if ($dirty) {
    Write-Host 'Working tree is not clean:' -ForegroundColor Red
    Write-Host $dirty
    throw 'Commit or set aside your changes first. Do not stash: in DEV that removes the .git junction.'
}

function Get-BumpedComponentKeys {
    <#
    Which component keys did THIS commit bump? Read it out of the commit's own diff against
    its parent, so nothing has to be passed by hand and the answer stays right after a rebase.

    We look at the "componentKey" of every object in omp-components.json whose "version" line
    the commit touched. Parsing JSON at both ends and diffing the values is more robust than
    reading +/- lines: a reformat would defeat the line-based reading, and the value comparison
    survives it.
    #>
    param([string]$CommitRef = 'HEAD')

    $parent = (Invoke-Git -Arguments @('rev-parse', "$CommitRef^") -AllowFailure)
    if ($parent.ExitCode -ne 0) { return @() }   # root commit: nothing to compare against

    $beforeText = (Invoke-Git -Arguments @('show', "$($parent.Output.Trim()):omp-components.json") -AllowFailure)
    if ($beforeText.ExitCode -ne 0) { return @() }
    $afterText = (Invoke-Git -Arguments @('show', "$CommitRef`:omp-components.json") -AllowFailure)
    if ($afterText.ExitCode -ne 0) { return @() }

    $before = $beforeText.Output | ConvertFrom-Json
    $after = $afterText.Output | ConvertFrom-Json

    $beforeByKey = @{}
    foreach ($c in @($before.components)) { $beforeByKey[[string]$c.componentKey] = [string]$c.version }

    $moved = [System.Collections.Generic.List[string]]::new()
    foreach ($c in @($after.components)) {
        $key = [string]$c.componentKey
        if (-not $beforeByKey.ContainsKey($key)) { continue }
        if ($beforeByKey[$key] -ne [string]$c.version) { [void]$moved.Add($key) }
    }
    return $moved.ToArray()
}

function Invoke-Rebump {
    <#
    Re-runs the repository's own bump tool for the given component keys.

    Two things that look like details and are not:

    * ONE KEY PER CALL. `-ComponentKey a,b` passes the single string "a,b" when the script is
      invoked through -File, which matches no component and silently bumps nothing. Measured.
    * The tool reads the CURRENT number out of the file and adds one, so after a rebase it
      automatically produces "theirs + 1". That is the whole reason to call the tool instead of
      editing the manifest: it also raises repositoryVersion and the maxVersion pins in the
      module definitions, which a hand-edit forgets. A hand-edited manifest shipped new content
      under an old package identity on 2026-09-04.
    #>
    param([Parameter(Mandatory)][string[]]$ComponentKeys)

    foreach ($key in @($ComponentKeys)) {
        Write-Note "bump-version.ps1 -ComponentKey $key"
        if (-not $DryRun) {
            & powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File $bumpScript -ComponentKey $key | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "bump-version.ps1 failed for component '$key'." }
        }
    }
}

$branch = (Invoke-Git -Arguments @('rev-parse', '--abbrev-ref', 'HEAD')).Output.Trim()
if ($branch -eq 'HEAD') { throw 'Detached HEAD; check out a branch first.' }

# @() runt anropet ar inte kosmetik: under Set-StrictMode -Version Latest packar
# PowerShell upp en enelementslista till en skalar, och da finns ingen .Count.
# Ett enda bumpat komponentnamn hade alltsa kraschat skriptet — hittat av torrkorningen.
$bumpedKeys = @(Get-BumpedComponentKeys)
if ($bumpedKeys.Count -gt 0) {
    Write-Note "This commit bumps: $($bumpedKeys -join ', ')"
}
else {
    Write-Note 'This commit bumps no component versions; the rebump step will be skipped.'
}

for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    Write-Step "Attempt $attempt of $MaxAttempts"

    # 1. Find out where the remote actually is, before deciding anything.
    Write-Note "git fetch $Remote"
    $null = Invoke-Git -Arguments @('fetch', $Remote, '--quiet')

    $upstream = "$Remote/$branch"
    $behind = [int](Invoke-Git -Arguments @('rev-list', '--count', "HEAD..$upstream")).Output.Trim()

    if ($behind -gt 0) {
        Write-Warn "$upstream moved ahead by $behind commit(s) -- rebasing onto it."

        # No --autostash: the tree is clean (checked above), and in DEV the internal stash
        # would take the .git junction with it.
        $rebase = Invoke-Git -Arguments @('rebase', $upstream) -AllowFailure
        if ($rebase.ExitCode -ne 0) {
            Write-Host $rebase.Output
            Write-Host ''
            Write-Host 'The rebase stopped. This is the one case the script will not guess its way' -ForegroundColor Red
            Write-Host 'through: a real conflict needs a human. Resolve it, finish the rebase, then' -ForegroundColor Red
            Write-Host 'run this script again -- it will recompute the bump from the new base.' -ForegroundColor Red
            exit 2
        }

        # 2. The bump is recomputed HERE, after the rebase, which is the point of the script.
        #    A version bump merges cleanly against an identical bump, so the rebase may have
        #    silently turned ours into a no-op. Re-running the tool against the new base always
        #    produces a number above what is published.
        if ($bumpedKeys.Count -gt 0) {
            Write-Note 'Recomputing the version bump against the new base.'
            Invoke-Rebump -ComponentKeys $bumpedKeys

            $changed = (Invoke-Git -Arguments @('status', '--porcelain')).Output
            if ($changed) {
                Write-Note 'Folding the recomputed numbers into the commit (amend, message unchanged).'
                if (-not $DryRun) {
                    $null = Invoke-Git -Arguments @('add', '--all', '--', 'omp-components.json', '*.module-definition.json')
                    $null = Invoke-Git -Arguments @('commit', '--amend', '--no-edit')
                }
            }
            else {
                Write-Note 'Numbers were already above the new base; nothing to amend.'
            }
        }
    }
    else {
        Write-Good "$upstream has not moved; no rebase needed."
    }

    # 3. Gate once, against the baseline that is now correct.
    #
    #    Letting local-ci.ps1 resolve the baseline itself matters: passing -BaseCommit by hand
    #    is how you compare a commit with ITSELF and get a green that means nothing. Measured
    #    three times on 2026-09-04, each time followed by the gate catching what the hand-run
    #    had approved.
    Write-Step 'Running the local gate'
    if ($DryRun) {
        Write-Note '(dry run: gate skipped)'
    }
    else {
        & powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File $localCi
        if ($LASTEXITCODE -ne 0) {
            Write-Host ''
            Write-Host 'The gate failed. That is a real finding about this change, not a race --' -ForegroundColor Red
            Write-Host 'a race shows up as "not bumped", and the rebump above has already handled' -ForegroundColor Red
            Write-Host 'that case. Read what it said.' -ForegroundColor Red
            exit 1
        }
    }

    # 4. Push. The gate cache in .githooks/pre-push.ps1 recognises this exact tree and
    #    baseline, so the hook does not run the gate a second time.
    Write-Step "Pushing to $Remote/$branch"
    if ($DryRun) {
        Write-Note '(dry run: push skipped)'
        Write-Good 'Dry run complete.'
        exit 0
    }

    $push = Invoke-Git -Arguments @('push', $Remote, "HEAD:$branch") -AllowFailure
    if ($push.ExitCode -eq 0) {
        Write-Good "Pushed $(((Invoke-Git -Arguments @('rev-parse','--short','HEAD')).Output.Trim())) to $Remote/$branch."
        exit 0
    }

    # A non-fast-forward here means someone landed while the gate ran. That is the remaining
    # window, and it is exactly what the loop is for. Anything else is a real push failure.
    if ($push.Output -notmatch 'non-fast-forward|fetch first|behind its remote') {
        Write-Host $push.Output
        throw 'Push failed for a reason that is not a race. Stopping rather than retrying blindly.'
    }

    Write-Warn 'Overtaken during the gate run. Recomputing against the new base.'
}

Write-Host ''
Write-Host "Gave up after $MaxAttempts attempts -- something is landing on $Remote/$branch faster" -ForegroundColor Red
Write-Host 'than a gate run takes. That is worth looking at rather than retrying further.' -ForegroundColor Red
exit 3
