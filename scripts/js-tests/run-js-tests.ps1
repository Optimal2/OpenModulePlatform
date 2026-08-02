# File: scripts/js-tests/run-js-tests.ps1
# Headless test runner for the shared browser JavaScript in
# OpenModulePlatform.Web.Shared/wwwroot/js. Installs the jsdom dev dependency
# into scripts/js-tests/node_modules on first run (git-ignored), then executes
# every *.tests.js file with Node.
#Requires -Version 7.0
[CmdletBinding()]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '', Justification = 'The script requires PowerShell 7 (see #Requires) and never runs under Windows PowerShell 5.1. Node.js is an intentional external dependency: its absence is checked at runtime with a clear error before any node invocation.')]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$testRoot = $PSScriptRoot

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw 'Node.js is required to run the shared browser JS tests.'
}

if (-not (Test-Path -LiteralPath (Join-Path $testRoot 'node_modules\jsdom') -PathType Container)) {
    Write-Host '[js-tests] Installing test dependencies (first run)...'
    Push-Location $testRoot
    try {
        npm install --no-audit --no-fund --loglevel=error | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "npm install failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

$failed = 0
foreach ($testFile in Get-ChildItem -LiteralPath $testRoot -Filter '*.tests.js' -File) {
    Write-Host "[js-tests] $($testFile.Name)"
    node $testFile.FullName
    if ($LASTEXITCODE -ne 0) {
        $failed += 1
    }
}

if ($failed -gt 0) {
    throw "$failed JS test file(s) failed."
}

Write-Host '[js-tests] All JS test files passed.'
