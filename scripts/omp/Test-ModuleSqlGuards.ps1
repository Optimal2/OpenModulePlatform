#requires -Version 5.1
<#
.SYNOPSIS
    Checks module-definition SQL against the four rules the import path enforces.

.DESCRIPTION
    R12. Mirrors ValidateReadOnlyModuleDefinitionSql in
    OpenModulePlatform.HostAgent.Runtime/Services/OmpHostArtifactRepository.cs so a blocked
    script is found here instead of in a failed import.

    That distinction is not academic. A module definition whose SQL is refused takes the whole
    definition with it, and every artifact that requires that definition version fails in the
    same package: one bad line in one script cost five of forty-five package items and left the
    host on the previous build. The feedback arrives minutes later, in an error file, naming a
    rule rather than a line.

    The rules, all four applied to the script AFTER the local 'USE [database]' header is
    stripped, exactly as the import does:

      1. No USE directives.
      2. Nothing that discards a data-bearing root: a database, a schema or a table.
         Indexes, constraints and columns are allowed -- bounded schema maintenance is the
         point of these scripts.
      3. No TRUNCATE.
      4. No row removal without a predicate.

    IMPORTANT, and the reason this script exists: the rules are regular expressions over the
    RAW text and do NOT strip comments. A comment that names a forbidden statement blocks the
    import just as surely as code that performs it. Rules 2 and 4 both fired on prose while
    this was being written.

    CONSERVATIVE BY DESIGN, and not yet a gate. Rule 4's statement splitting is an
    approximation of the production one, and it currently reports IbsPackager's setup and
    initialize scripts -- which the import demonstrably accepts (measured 2026-08-16: the
    ibs_packager definition imported in the same package where omp_core was refused). So a
    BLOCKED verdict means "look at this line", not "this will certainly fail", and the script
    is deliberately NOT wired into CI or the pre-push hook: a gate that fails on working code
    teaches people to ignore it. Wiring it in requires reusing the production splitter rather
    than approximating it; until then this is a fast local check, and it earned its keep by
    catching two real blocks before the second failed import.

.PARAMETER Path
    SQL files to check. Defaults to every .sql file in the repository's sql/ directory.

.EXAMPLE
    pwsh -File scripts/omp/Test-ModuleSqlGuards.ps1
    powershell -File scripts/omp/Test-ModuleSqlGuards.ps1 -Path ..\IbsPackager\sql\1-setup-ibspackager.sql
#>
[CmdletBinding()]
param(
    [string[]]$Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Path -or $Path.Count -eq 0) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $sqlDir = Join-Path $repoRoot 'sql'
    if (-not (Test-Path -LiteralPath $sqlDir)) {
        Write-Host "No sql/ directory under '$repoRoot' and no -Path given; nothing to check."
        exit 0
    }
    $Path = @(Get-ChildItem -LiteralPath $sqlDir -Filter '*.sql' -File | Select-Object -ExpandProperty FullName)
}

function Remove-LocalUseHeader {
    param([string]$SqlText)

    # Same transformation the packaging step applies before embedding, so this script sees what
    # the import sees rather than what is on disk.
    return [regex]::Replace(
        $SqlText,
        '(?im)^\s*USE\s+\[OpenModulePlatform\]\s*;\s*\r?\n\s*GO\s*(?:--.*)?\s*(?:\r?\n)?',
        '')
}

$blocked = 0
$checked = 0

foreach ($file in $Path) {
    if (-not (Test-Path -LiteralPath $file)) {
        Write-Host "  MISSING $file" -ForegroundColor Red
        $blocked++
        continue
    }

    $checked++
    $name = Split-Path $file -Leaf
    $sql = Remove-LocalUseHeader -SqlText (Get-Content -LiteralPath $file -Raw -Encoding UTF8)
    $problems = New-Object 'System.Collections.Generic.List[string]'

    if ([regex]::IsMatch($sql, '(?im)^\s*USE\s+(?:\[[^\]]+\]|[A-Za-z0-9_]+)\s*;?\s*$')) {
        $problems.Add('Rule 1: a USE directive remains after the local header is stripped.')
    }

    if ([regex]::IsMatch($sql, '(?is)\bDROP\s+(?:DATABASE|SCHEMA|TABLE)\b')) {
        $problems.Add('Rule 2: discards a data-bearing root. Comments count -- check prose too.')
    }

    if ([regex]::IsMatch($sql, '(?is)\bTRUNCATE\s+TABLE\b')) {
        $problems.Add('Rule 3: TRUNCATE.')
    }

    foreach ($batch in ($sql -split '(?im)^\s*GO\s*$')) {
        $matches = [regex]::Matches(
            $batch,
            '(?ims)\bDELETE\b(?<statement>.*?)(?=;|^\s*(?:GO|INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC(?:UTE)?|GRANT|REVOKE|DENY|SELECT)\b|\z)')

        foreach ($match in $matches) {
            # A foreign key's ON DELETE clause and a MERGE's THEN DELETE action are not
            # statements and cannot carry a predicate; the import skips them too.
            $before = $batch.Substring(0, $match.Index).TrimEnd()
            if ([regex]::IsMatch($before, '(?is)\b(?:ON|THEN)$')) { continue }

            $statement = ('DELETE' + $match.Groups['statement'].Value).Trim()
            if ($statement -and -not [regex]::IsMatch($statement, '(?is)\bWHERE\b')) {
                $preview = $statement -replace '\s+', ' '
                if ($preview.Length -gt 90) { $preview = $preview.Substring(0, 90) + '...' }
                $problems.Add("Rule 4: row removal without a predicate -- '$preview'")
            }
        }
    }

    if ($problems.Count -eq 0) {
        Write-Host ("  OK      {0}" -f $name) -ForegroundColor Green
    }
    else {
        $blocked++
        Write-Host ("  BLOCKED {0}" -f $name) -ForegroundColor Red
        foreach ($problem in $problems) {
            Write-Host ("            {0}" -f $problem) -ForegroundColor Yellow
        }
    }
}

Write-Host ''
if ($blocked -gt 0) {
    Write-Host "$blocked of $checked script(s) would be refused by the import path." -ForegroundColor Red
    exit 1
}

Write-Host "$checked script(s) checked; all pass the import path's guards."
exit 0
