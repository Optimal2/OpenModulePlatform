<#
.SYNOPSIS
Tests the OpenDocViewer artifact version source in package-hostagent-first.ps1.

.DESCRIPTION
Regression test for the 2.6.9 misnaming case: the HostAgent-first packaging must
derive the OpenDocViewer artifact version from the canonical opendocviewer-web
component version in OpenDocViewer\omp-components.json, never from package.json.
The configured OpenDocViewer Version value is an explicit fallback (with a
warning) used only when the component manifest is unavailable, and a configured
value that deviates from the manifest version must trigger a warning.

The test extracts the real function definitions from package-hostagent-first.ps1
via the PowerShell AST so the tested code is the shipping code, not a copy.

Run with Windows PowerShell 5.1 or later:
    powershell.exe -File scripts\deployment\test-package-hostagent-first-odv-version.ps1
#>
[CmdletBinding()]
param(
    # Optional path to a real OpenDocViewer repository root. When omitted, the
    # sibling 'OpenDocViewer' folder next to this repository is used if present.
    [string]$OpenDocViewerRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptRoot = $PSScriptRoot
$packagingScriptPath = Join-Path $scriptRoot 'package-hostagent-first.ps1'

# Parse the packaging script (this also acts as its syntax validation).
$parseErrors = $null
$tokens = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($packagingScriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    $parseErrors | ForEach-Object { Write-Host "PARSE ERROR: $($_.Message) ($($_.Extent.StartLineNumber))" }
    throw "package-hostagent-first.ps1 has parse errors."
}

# Dot-source only the real function definitions needed by this test.
$functionNames = @(
    'Get-ManifestProperty',
    'Get-ManifestPropertyValue',
    'Get-OpenDocViewerWebComponent',
    'Resolve-OpenDocViewerArtifactVersion'
)
$functionAsts = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)
foreach ($name in $functionNames) {
    $functionAst = @($functionAsts | Where-Object { $_.Name -eq $name } | Select-Object -First 1)
    if ($functionAst.Count -eq 0) {
        throw "Function '$name' was not found in package-hostagent-first.ps1."
    }

    . ([scriptblock]::Create($functionAst[0].Extent.Text))
}

$failures = [System.Collections.Generic.List[string]]::new()
function Assert-Equal {
    param(
        [string]$Name,
        [object]$Expected,
        [object]$Actual
    )

    if ([string]::Equals([string]$Expected, [string]$Actual, [StringComparison]::Ordinal)) {
        Write-Host "PASS: $Name (got '$Actual')"
    }
    else {
        Write-Host "FAIL: $Name (expected '$Expected', got '$Actual')"
        $script:failures.Add($Name) | Out-Null
    }
}

function Assert-True {
    param(
        [string]$Name,
        [bool]$Condition
    )

    if ($Condition) {
        Write-Host "PASS: $Name"
    }
    else {
        Write-Host "FAIL: $Name"
        $script:failures.Add($Name) | Out-Null
    }
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-odv-version-test-' + [Guid]::NewGuid().ToString('N'))
try {
    # Scenario 1 (the 2.6.9 case): manifest version 2.4.56, package.json 2.6.9.
    # The artifact version must come from omp-components.json, not package.json.
    $odvRoot = Join-Path $testRoot 'odv-manifest'
    New-Item -ItemType Directory -Path $odvRoot -Force | Out-Null
    @'
{
  "components": [
    {
      "componentKey": "opendocviewer-web",
      "moduleKey": "opendocviewer",
      "appKey": "opendocviewer_webapp",
      "packageType": "web-app",
      "targetName": "opendocviewer",
      "version": "2.4.56"
    }
  ]
}
'@ | Set-Content -LiteralPath (Join-Path $odvRoot 'omp-components.json') -Encoding UTF8
    '{ "name": "opendocviewer", "version": "2.6.9" }' | Set-Content -LiteralPath (Join-Path $odvRoot 'package.json') -Encoding UTF8

    $component = Get-OpenDocViewerWebComponent -OpenDocViewerRoot $odvRoot
    Assert-True -Name 'opendocviewer-web component is found in omp-components.json' -Condition ($null -ne $component)

    $warnings = @()
    $version = Resolve-OpenDocViewerArtifactVersion -Component $component -ConfiguredVersion '' -OpenDocViewerRoot $odvRoot -WarningVariable warnings -WarningAction SilentlyContinue
    Assert-Equal -Name '2.6.9 case: artifact version comes from omp-components.json, not package.json' -Expected '2.4.56' -Actual $version
    Assert-Equal -Name '2.6.9 case: no warning when no config override is set' -Expected '0' -Actual $warnings.Count

    # Scenario 2: config override that deviates from the manifest version.
    $warnings = @()
    $version = Resolve-OpenDocViewerArtifactVersion -Component $component -ConfiguredVersion '2.6.9' -OpenDocViewerRoot $odvRoot -WarningVariable warnings -WarningAction SilentlyContinue
    Assert-Equal -Name 'deviating config override: manifest version still wins' -Expected '2.4.56' -Actual $version
    Assert-True -Name 'deviating config override: warning is emitted' -Condition ($warnings.Count -eq 1 -and ([string]$warnings[0]).Contains('differs'))

    # Scenario 3: manifest unavailable, config override is the explicit fallback.
    $emptyRoot = Join-Path $testRoot 'odv-no-manifest'
    New-Item -ItemType Directory -Path $emptyRoot -Force | Out-Null
    $component = Get-OpenDocViewerWebComponent -OpenDocViewerRoot $emptyRoot
    Assert-True -Name 'missing manifest: no component is found' -Condition ($null -eq $component)

    $warnings = @()
    $version = Resolve-OpenDocViewerArtifactVersion -Component $component -ConfiguredVersion '9.9.9' -OpenDocViewerRoot $emptyRoot -WarningVariable warnings -WarningAction SilentlyContinue
    Assert-Equal -Name 'missing manifest: configured version is the explicit fallback' -Expected '9.9.9' -Actual $version
    Assert-True -Name 'missing manifest: fallback warning is emitted' -Condition ($warnings.Count -eq 1 -and ([string]$warnings[0]).Contains('fallback'))

    # Scenario 4: manifest unavailable and no config override -> hard failure.
    $threw = $false
    try {
        Resolve-OpenDocViewerArtifactVersion -Component $null -ConfiguredVersion '' -OpenDocViewerRoot $emptyRoot -WarningAction SilentlyContinue | Out-Null
    }
    catch {
        $threw = $true
    }
    Assert-True -Name 'missing manifest and no override: version resolution fails loudly' -Condition $threw
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# Live proof against a real OpenDocViewer checkout when available.
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $scriptRoot)
if ([string]::IsNullOrWhiteSpace($OpenDocViewerRoot)) {
    $OpenDocViewerRoot = Join-Path (Split-Path -Parent $repositoryRoot) 'OpenDocViewer'
}
if (Test-Path -LiteralPath (Join-Path $OpenDocViewerRoot 'omp-components.json') -PathType Leaf) {
    $component = Get-OpenDocViewerWebComponent -OpenDocViewerRoot $OpenDocViewerRoot
    $warnings = @()
    $version = Resolve-OpenDocViewerArtifactVersion -Component $component -ConfiguredVersion '' -OpenDocViewerRoot $OpenDocViewerRoot -WarningVariable warnings -WarningAction SilentlyContinue

    $packageJsonVersion = ''
    $packageJsonPath = Join-Path $OpenDocViewerRoot 'package.json'
    if (Test-Path -LiteralPath $packageJsonPath -PathType Leaf) {
        $packageJsonVersion = [string]((Get-Content -LiteralPath $packageJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json).version)
    }

    Write-Host "Live OpenDocViewer checkout: omp-components.json version = '$version', package.json version = '$packageJsonVersion'"
    Assert-True -Name 'live checkout: artifact version matches opendocviewer-web component version' -Condition ($version -eq [string](Get-ManifestPropertyValue -Object $component -Name 'version'))
}
else {
    Write-Host "No live OpenDocViewer checkout found at '$OpenDocViewerRoot'; skipping live proof."
}

if ($failures.Count -gt 0) {
    Write-Host "`n$($failures.Count) test(s) failed."
    exit 1
}

Write-Host "`nAll OpenDocViewer artifact version tests passed."
exit 0
