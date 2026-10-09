[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][ValidateSet('installer', 'canonical')][string]$Producer
)
$ErrorActionPreference = 'Stop'
[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('sv-SE')
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ($Producer -eq 'installer') {
    # Load the real refresh packer's functions without executing its installation/build body.
    $path = Join-Path $repo 'scripts/deployment/package-hostagent-first.ps1'
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'Installer packer did not parse.' }
    foreach ($statement in $ast.EndBlock.Statements) {
        if ($statement -is [Management.Automation.Language.FunctionDefinitionAst]) {
            Set-Item -Path ("Function:\{0}" -f $statement.Name) -Value $statement.Body.GetScriptBlock()
        }
    }
    # The functions resolve script dependencies relative to the production script.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    . (Join-Path $repo 'scripts/omp/runtime-configuration-files.ps1')
    $helper = Join-Path $repo 'scripts/omp/deterministic-artifact.ps1'
    if (Test-Path -LiteralPath $helper) { . $helper }
    Compress-ArtifactPayloadFolderToZip -Source (Join-Path $Root 'payload') -Destination (Join-Path $Root 'payload.zip') -BuildRoot $Root
    New-ArtifactPackage -PayloadZip (Join-Path $Root 'payload.zip') -Destination (Join-Path $Root 'package.zip') -BuildRoot $Root `
        -MinModuleDefinitionVersion '1.2.3' -MinWorkerHostVersion '1.0.0' -ConfigurationFiles @(
            @{ RelativePath = 'settings/config.txt'; SourcePath = (Join-Path $Root 'config.txt'); PackageSourcePath = 'configuration/001-config.txt' }
        )
}
else {
    & (Join-Path $repo 'scripts/deployment/new-omp-artifact-package.ps1') -ModuleKey test -AppKey worker -PackageType worker `
        -TargetName worker -Version 1.0.0 -PayloadPath (Join-Path $Root 'payload') -OutputPath (Join-Path $Root 'package.zip') `
        -MinModuleDefinitionVersion '1.2.3' -MinWorkerHostVersion '1.0.0' -ConfigurationFile "settings/config.txt=$(Join-Path $Root 'config.txt')"
}
