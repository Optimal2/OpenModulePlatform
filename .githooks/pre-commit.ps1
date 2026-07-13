<#
.SYNOPSIS
OMP lightweight pre-commit gate — fast static checks only.

.DESCRIPTION
Runs only quick static checks on staged files. No build or test here; those
belong in the pre-push hook.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    $staged = @(& git diff --cached --name-only --diff-filter=ACM)
    if ($LASTEXITCODE -ne 0) {
        throw 'git diff --cached failed.'
    }
}
finally {
    Pop-Location
}

$errors = New-Object System.Collections.Generic.List[string]

foreach ($file in $staged) {
    $fullPath = Join-Path $repoRoot $file

    # Fast JSON sanity check for the component manifest.
    if ($file -eq 'omp-components.json') {
        try {
            $jsonText = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8
            $null = $jsonText | ConvertFrom-Json
        }
        catch {
            [void]$errors.Add("omp-components.json is not valid JSON: $_")
        }
    }

    # No tab characters in PowerShell source.
    if ($file -like '*.ps1') {
        try {
            $content = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8
            if ([System.Text.RegularExpressions.Regex]::IsMatch($content, "`t")) {
                [void]$errors.Add("Tab characters found in '$file'. Please use spaces.")
            }
        }
        catch {
            [void]$errors.Add("Could not read '$file' for tab check: $_")
        }
    }
}

if ($errors.Count -gt 0) {
    Write-Host 'PRE-COMMIT GATE FAILED:'
    foreach ($message in $errors) {
        Write-Host " - $message"
    }
    exit 1
}

Write-Host 'Pre-commit gate passed.'
exit 0
