<#
.SYNOPSIS
Publishes WorkerProcessHost from two source paths and compares every byte and package hash.
.DESCRIPTION
Copies tracked build inputs (including working-tree edits), changes their timestamps,
then uses the installer publish options and the production artifact packer. No runtime
installation is performed. Evidence and build logs remain under OutputRoot.
#>
[CmdletBinding()]
param(
    [string]$OutputRoot = '',
    [switch]$UseIsolatedBuildRoots
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $OutputRoot) {
    $OutputRoot = Join-Path ([IO.Path]::GetTempPath()) ('omp-determinism-' + [Guid]::NewGuid().ToString('N'))
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw "Use a fresh output directory: $OutputRoot" }
[void](New-Item -ItemType Directory -Path $OutputRoot)
$savedTemp = $env:TEMP
$savedTmp = $env:TMP
$savedGitHubActions = $env:GITHUB_ACTIONS
try {
    # Package staging must stay in the test's workspace, too.
    $env:TEMP = Join-Path $OutputRoot 'temp'
    $env:TMP = $env:TEMP
    [void](New-Item -ItemType Directory -Path $env:TEMP)
    # Exercise the local/installer contract even on a GitHub Actions runner.
    $env:GITHUB_ACTIONS = 'false'
    $projects = @('OpenModulePlatform.WorkerProcessHost', 'OpenModulePlatform.Artifacts',
        'OpenModulePlatform.Worker.Abstractions', 'OpenModulePlatform.EventPublisher.Abstractions')
    $tracked = @(git -C $repoRoot ls-files -- Directory.Build.props Directory.Build.targets Directory.Packages.props global.json build scripts/omp/DeterministicArtifactEncoding.cs @projects)
    if ($LASTEXITCODE -ne 0 -or $tracked.Count -lt 10) { throw 'Could not enumerate the tracked build inputs.' }
    $results = @()
    $legs = @('first', 'different length second')
    if ($UseIsolatedBuildRoots) { $legs += 'third isolated root' }
    foreach ($leg in $legs) {
        $root = Join-Path $OutputRoot $leg
        $source = Join-Path $root 'source'
        foreach ($relative in $tracked) {
            $destination = Join-Path $source $relative
            [void](New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force)
            Copy-Item -LiteralPath (Join-Path $repoRoot $relative) -Destination $destination
            (Get-Item -LiteralPath $destination).LastWriteTimeUtc = if ($leg -eq 'first') {
                [datetime]'2020-01-02T03:04:06Z'
            } else { [datetime]'2024-05-06T07:08:10Z' }
        }
        $publish = Join-Path $root 'publish'
        $arguments = @('publish', (Join-Path $source 'OpenModulePlatform.WorkerProcessHost/OpenModulePlatform.WorkerProcessHost.csproj'),
            '-c', 'Release', '-o', $publish, '--nologo', '--verbosity', 'minimal')
        if ($UseIsolatedBuildRoots -and $leg -ne 'first') {
            $arguments += '-p:OmpIsolatedBuildRoot=' + (Join-Path $root 'isolated')
        }
        & dotnet @arguments *> (Join-Path $root 'publish.log')
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $root/publish.log" }
        $zip = Join-Path $root 'workerprocesshost.zip'
        & (Join-Path $repoRoot 'scripts/deployment/new-omp-artifact-package.ps1') `
            -ModuleKey omp_core -AppKey omp-workerprocesshost -PackageType service `
            -TargetName omp-workerprocesshost -Version 1.0.0 -PayloadPath $publish -OutputPath $zip
        $hashes = [ordered]@{}
        foreach ($file in Get-ChildItem -LiteralPath $publish -File -Recurse | Sort-Object FullName) {
            $name = $file.FullName.Substring($publish.Length).TrimStart('\', '/').Replace('\', '/')
            $hashes[$name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
        $results += [pscustomobject]@{ Leg = $leg; Files = $hashes; PackageSha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash }
    }
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputRoot 'hashes.json') -Encoding UTF8
    foreach ($result in $results | Select-Object -Skip 1) {
        $differences = @()
        $names = @(@($results[0].Files.Keys) + @($result.Files.Keys) | Sort-Object -Unique)
        foreach ($name in $names) {
            if ($results[0].Files[$name] -ne $result.Files[$name]) { $differences += $name }
        }
        Write-Host "Compared $($names.Count) publish files ($($result.Leg)); differences: $($differences -join ', ')"
        Write-Host "Package SHA-256 first: $($results[0].PackageSha256)"
        Write-Host "Package SHA-256 other: $($result.PackageSha256)"
        if ($differences.Count -gt 0 -or $results[0].PackageSha256 -ne $result.PackageSha256) {
            throw "Non-deterministic publish/package. Evidence: $OutputRoot/hashes.json"
        }
    }
    Write-Host 'PASS: all publish files and the artifact package are byte-identical.'
}
finally {
    $env:TEMP = $savedTemp
    $env:TMP = $savedTmp
    $env:GITHUB_ACTIONS = $savedGitHubActions
}
