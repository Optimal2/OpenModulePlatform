<#
.SYNOPSIS
Publishes an OMP project from two source paths and compares every byte and package hash.
.DESCRIPTION
Copies tracked build inputs (including working-tree edits), changes their timestamps,
then uses the installer publish options and the production artifact packer. No runtime
installation is performed. Evidence and build logs remain under OutputRoot.
#>
[CmdletBinding()]
param(
    [string]$OutputRoot = '',
    [switch]$UseIsolatedBuildRoots,
    [ValidateSet('WorkerProcessHost', 'Portal', 'Auth', 'Content', 'Iframe', 'Example', 'Blazor', 'ServiceWeb', 'WorkerWeb')]
    [string[]]$Project = @('WorkerProcessHost', 'Portal')
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
    $probeProject = Join-Path $repoRoot 'scripts/dev/PublishMetadataProbe/PublishMetadataProbe.csproj'
    & dotnet build $probeProject -c Release --nologo -v q *> (Join-Path $OutputRoot 'metadata-probe.log')
    if ($LASTEXITCODE -ne 0) { throw "Metadata probe build failed: $OutputRoot/metadata-probe.log" }
    $probe = Join-Path $repoRoot 'scripts/dev/PublishMetadataProbe/bin/Release/net10.0/PublishMetadataProbe.dll'
    $projectPaths = @{
        WorkerProcessHost = 'OpenModulePlatform.WorkerProcessHost/OpenModulePlatform.WorkerProcessHost.csproj'
        Portal = 'OpenModulePlatform.Portal/OpenModulePlatform.Portal.csproj'
        Auth = 'OpenModulePlatform.Auth/OpenModulePlatform.Auth.csproj'
        Content = 'OpenModulePlatform.Web.ContentWebAppModule/OpenModulePlatform.Web.ContentWebAppModule.csproj'
        Iframe = 'OpenModulePlatform.Web.iFrameWebAppModule/OpenModulePlatform.Web.iFrameWebAppModule.csproj'
        Example = 'examples/WebAppModule/WebApp/OpenModulePlatform.Web.ExampleWebAppModule.csproj'
        Blazor = 'examples/WebAppBlazorModule/WebApp/OpenModulePlatform.Web.ExampleWebAppBlazorModule.csproj'
        ServiceWeb = 'examples/ServiceAppModule/WebApp/OpenModulePlatform.Web.ExampleServiceAppModule.csproj'
        WorkerWeb = 'examples/WorkerAppModule/WebApp/OpenModulePlatform.Web.ExampleWorkerAppModule.csproj'
    }
    $tracked = @(git -C $repoRoot ls-files -- Directory.Build.props Directory.Build.targets Directory.Packages.props global.json build shared integrations tools examples 'OpenModulePlatform.*' scripts/omp/DeterministicArtifactEncoding.cs)
    # Do not copy the solution marker: these are input snapshots, not checkouts.
    $tracked = @($tracked | Where-Object { $_ -ne 'OpenModulePlatform.slnx' })
    if ($LASTEXITCODE -ne 0 -or $tracked.Count -lt 10) { throw 'Could not enumerate the tracked build inputs.' }
    $results = @()
    $snapshot = Join-Path $OutputRoot 'inputs'
    foreach ($relative in $tracked) {
        $destination = Join-Path $snapshot $relative
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force)
        Copy-Item -LiteralPath (Join-Path $repoRoot $relative) -Destination $destination
    }
    $legs = @('first', 'different length second')
    if ($UseIsolatedBuildRoots) { $legs += 'third isolated root' }
    foreach ($leg in $legs) {
        $root = Join-Path $OutputRoot $leg
        $source = Join-Path $root 'source'
        foreach ($relative in $tracked) {
            $destination = Join-Path $source $relative
            [void](New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force)
            Copy-Item -LiteralPath (Join-Path $snapshot $relative) -Destination $destination
            (Get-Item -LiteralPath $destination).LastWriteTimeUtc = if ($leg -eq 'first') {
                [datetime]'2020-01-02T03:04:06Z'
            } else { [datetime]'2024-05-06T07:08:10Z' }
        }
        foreach ($projectName in $Project) {
            Write-Host "Publishing $projectName ($leg)..."
            $publish = Join-Path $root "publish/$projectName"
            $arguments = @('publish', (Join-Path $source $projectPaths[$projectName]),
                '-c', 'Release', '-o', $publish, '--nologo', '--verbosity', 'minimal')
            if ($UseIsolatedBuildRoots -and $leg -ne 'first') {
                $arguments += '-p:OmpIsolatedBuildRoot=' + (Join-Path $root 'isolated')
            }
            & dotnet @arguments *> (Join-Path $root "$projectName.publish.log")
            if ($LASTEXITCODE -ne 0) { throw "Publish failed: $root/$projectName.publish.log" }
            $symbolMode = if ($projectName -eq 'WorkerProcessHost') { 'plain' } else { 'razor' }
            & dotnet $probe $publish $symbolMode
            if ($LASTEXITCODE -ne 0) { throw "Published symbol validation failed: $projectName ($leg)" }
            $zip = Join-Path $root "$projectName.zip"
            $packageType = if ($projectName -eq 'WorkerProcessHost') { 'service' } else { 'web-app' }
            & (Join-Path $repoRoot 'scripts/deployment/new-omp-artifact-package.ps1') `
                -ModuleKey omp_core -AppKey determinism-probe -PackageType $packageType `
                -TargetName determinism-probe -Version 1.0.0 -PayloadPath $publish -OutputPath $zip
            $hashes = [ordered]@{}
            foreach ($file in Get-ChildItem -LiteralPath $publish -File -Recurse | Sort-Object FullName) {
                $name = $file.FullName.Substring($publish.Length).TrimStart('\', '/').Replace('\', '/')
                $hashes[$name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            }
            $results += [pscustomobject]@{ Project = $projectName; Leg = $leg; Files = $hashes; PackageSha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash }
            $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputRoot 'hashes.json') -Encoding UTF8
        }
    }
    foreach ($result in $results | Where-Object { $_.Leg -ne 'first' }) {
        $baseline = $results | Where-Object { $_.Leg -eq 'first' -and $_.Project -eq $result.Project }
        $differences = @()
        $names = @(@($baseline.Files.Keys) + @($result.Files.Keys) | Sort-Object -Unique)
        foreach ($name in $names) {
            if ($baseline.Files[$name] -ne $result.Files[$name]) { $differences += $name }
        }
        Write-Host "Compared $($names.Count) $($result.Project) publish files ($($result.Leg)); differences: $($differences -join ', ')"
        Write-Host "Package SHA-256 first: $($baseline.PackageSha256)"
        Write-Host "Package SHA-256 other: $($result.PackageSha256)"
        if ($differences.Count -gt 0 -or $baseline.PackageSha256 -ne $result.PackageSha256) {
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
