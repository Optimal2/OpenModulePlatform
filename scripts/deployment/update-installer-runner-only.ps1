# File: scripts/deployment/update-installer-runner-only.ps1
<#
.SYNOPSIS
Updates only the runnable installer executable in an existing HostAgent-first package.

.DESCRIPTION
Developer/private installer packages can be kept intentionally minimal in Git:
the root OpenModulePlatform.Bootstrapper.exe plus the machine-specific host
profiles that live next to the package. This helper refreshes the runnable
installer executable from source. If the package still contains the older
tools/OpenModulePlatform.Bootstrapper runner folder, that executable is refreshed
too and stale framework-dependent bootstrapper entry files are removed so package
refreshes cannot accidentally execute an older runner. It does not rebuild module
definitions, artifact packages, SQL payloads, package manifests, or any other
generated package content.

The runner is published as a framework-dependent win-x64 executable. Machines
that run it must have the matching .NET runtime installed.

Use the installer GUI package sync action afterwards when a developer machine
needs to populate the ignored package object library before an install.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [string]$RepositoryRoot = '',

    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-RunnerSignatureStatus {
    <#
        Thin wrapper over Get-AuthenticodeSignature so the refresh script has one
        place that turns a path into a status string. 'NoTarget' is returned for a
        path that does not exist yet, and an empty string when the status could
        not be read at all - the gate treats those very differently, and it
        should: a missing file is not a signed file, but an unreadable one may be.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return 'NoTarget'
    }

    try {
        return [string](Get-AuthenticodeSignature -LiteralPath $Path).Status
    }
    catch {
        Write-Warning "Could not read the Authenticode status of '$Path': $($_.Exception.Message)"
        return ''
    }
}

function Assert-RunnerReplacementAllowed {
    <#
        Refuses to overwrite a signed runner with an unsigned or invalidly signed
        one. The decision itself lives in assert-runner-signature.ps1, which is
        covered by tests/Assert-RunnerSignature.Tests.ps1 - inline script code
        cannot be tested, and an untested gate is a gate nobody has seen fail.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$SourceExe,
        [Parameter(Mandatory = $true)][string]$TargetExe
    )

    $gate = Join-Path $PSScriptRoot 'assert-runner-signature.ps1'
    if (-not (Test-Path -LiteralPath $gate -PathType Leaf)) {
        throw "The runner signature gate is missing: $gate. Refusing to replace a runner without it."
    }

    & $gate `
        -TargetSignatureStatus (Get-RunnerSignatureStatus -Path $TargetExe) `
        -NewSignatureStatus (Get-RunnerSignatureStatus -Path $SourceExe) `
        -TargetPath $TargetExe `
        -NewPath $SourceExe
}

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Resolve-RepositoryRoot {
    param([string]$ConfiguredRoot)

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredRoot)) {
        $resolved = Resolve-FullPath -Path $ConfiguredRoot
        if (-not (Test-Path -LiteralPath (Join-Path $resolved 'OpenModulePlatform.Bootstrapper\OpenModulePlatform.Bootstrapper.csproj') -PathType Leaf)) {
            throw "RepositoryRoot does not look like an OpenModulePlatform repository: $resolved"
        }

        return $resolved
    }

    $scriptRoot = Resolve-FullPath -Path $PSScriptRoot
    $candidate = [System.IO.DirectoryInfo]$scriptRoot
    while ($null -ne $candidate) {
        $projectPath = Join-Path $candidate.FullName 'OpenModulePlatform.Bootstrapper\OpenModulePlatform.Bootstrapper.csproj'
        if (Test-Path -LiteralPath $projectPath -PathType Leaf) {
            return $candidate.FullName
        }

        $candidate = $candidate.Parent
    }

    throw 'Could not locate the OpenModulePlatform repository root. Pass -RepositoryRoot.'
}

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @()
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Command failed with exit code ${exitCode}: $FilePath $($Arguments -join ' ')"
    }
}

function Close-IdleInstallerGui {
    <#
    .SYNOPSIS
    Asks an installer GUI running out of the package root to close before the runner is
    replaced.

    .DESCRIPTION
    R5-G4. The manual GUI runs from the package root and holds that directory for its whole
    idle lifetime, so a forgotten window blocks every runner update -- which is the everyday
    cause of a stuck deployment here.

    The finding also proposed relaunching the GUI from a temporary copy. That is not done:
    it changes how the installer starts, and getting it wrong disables the one tool needed
    to ship the correction. Asking an idle window to close is the half that removes the
    blocker without touching launch semantics, and CloseMainWindow is a request -- a GUI with
    unsaved work refuses it and the copy below then fails loudly, exactly as before.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot
    )

    $rootFull = [System.IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
    $candidates = @(Get-Process -Name 'OpenModulePlatform.Bootstrapper' -ErrorAction SilentlyContinue)
    foreach ($process in $candidates) {
        $processPath = $null
        try { $processPath = $process.Path } catch { continue }
        if ([string]::IsNullOrWhiteSpace($processPath)) { continue }

        $processDir = [System.IO.Path]::GetFullPath((Split-Path -Parent $processPath)).TrimEnd('\')
        if (-not $processDir.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { continue }

        # Only a window can be asked; a CLI run has no main window and must not be touched.
        if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
            Write-Host "A Bootstrapper CLI process is running from the package root (PID $($process.Id)); leaving it alone."
            continue
        }

        Write-Host "Asking the installer GUI running from the package root to close (PID $($process.Id))..."
        [void]$process.CloseMainWindow()
        if ($process.WaitForExit(10000)) {
            Write-Host "  closed."
        }
        else {
            Write-Host "  still open; the runner replacement below will fail if it keeps the file locked."
        }
    }
}

function Update-PackageToolRunner {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)][string]$SourceExe
    )

    $toolRoot = Join-Path $PackageRoot 'tools\OpenModulePlatform.Bootstrapper'
    if (-not (Test-Path -LiteralPath $toolRoot -PathType Container)) {
        return
    }

    $staleBootstrapperFiles = @(
        'OpenModulePlatform.Bootstrapper.dll',
        'OpenModulePlatform.Bootstrapper.deps.json',
        'OpenModulePlatform.Bootstrapper.runtimeconfig.json',
        'OpenModulePlatform.Bootstrapper.pdb'
    )

    foreach ($fileName in $staleBootstrapperFiles) {
        $filePath = Join-Path $toolRoot $fileName
        if (Test-Path -LiteralPath $filePath -PathType Leaf) {
            Remove-Item -LiteralPath $filePath -Force
        }
    }

    $targetExe = Join-Path $toolRoot 'OpenModulePlatform.Bootstrapper.exe'
    Copy-Item -LiteralPath $SourceExe -Destination $targetExe -Force
    Write-Host "Updated package tools runner: $targetExe"

    $toolZip = Join-Path (Join-Path $PackageRoot 'tools') 'OpenModulePlatform.Bootstrapper.zip'
    if (Test-Path -LiteralPath $toolZip -PathType Leaf) {
        $zipStage = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-installer-tool-runner-' + [guid]::NewGuid().ToString('N'))
        try {
            New-Item -ItemType Directory -Path $zipStage | Out-Null
            Copy-Item -LiteralPath $SourceExe -Destination (Join-Path $zipStage 'OpenModulePlatform.Bootstrapper.exe') -Force
            Compress-Archive -Path (Join-Path $zipStage '*') -DestinationPath $toolZip -Force
            Write-Host "Updated package tools runner archive: $toolZip"
        }
        finally {
            if (Test-Path -LiteralPath $zipStage -PathType Container) {
                Remove-Item -LiteralPath $zipStage -Recurse -Force
            }
        }
    }
}

$packageRootPath = Resolve-FullPath -Path $PackageRoot
if (-not (Test-Path -LiteralPath $packageRootPath -PathType Container)) {
    throw "PackageRoot does not exist: $packageRootPath"
}

function Test-HasBootstrapProfiles {
    param([Parameter(Mandatory = $true)][string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return $false
    }

    if (Get-ChildItem -LiteralPath $Root -Filter '*.json' -File -ErrorAction SilentlyContinue | Select-Object -First 1) {
        return $true
    }

    return (Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bootstrap.json') -PathType Leaf } |
        Select-Object -First 1) -ne $null
}

$profileRoots = @(
    (Join-Path $packageRootPath 'configs'),
    (Join-Path $packageRootPath 'hosts'),
    (Join-Path (Split-Path -Parent $packageRootPath) 'hosts'),
    (Join-Path (Split-Path -Parent (Split-Path -Parent $packageRootPath)) 'hosts')
)

if (-not ($profileRoots | Where-Object { Test-HasBootstrapProfiles -Root $_ } | Select-Object -First 1)) {
    throw "Minimal installer packages must be accompanied by bootstrap profiles in a package 'configs' folder or a 'hosts\<profile>\bootstrap.json' tree under PackageRoot, beside PackageRoot, or up to two parent levels above it."
}

$repositoryRootPath = Resolve-RepositoryRoot -ConfiguredRoot $RepositoryRoot
$projectPath = Join-Path $repositoryRootPath 'OpenModulePlatform.Bootstrapper\OpenModulePlatform.Bootstrapper.csproj'
$publishRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-installer-runner-only-' + [guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $publishRoot | Out-Null

    Invoke-NativeChecked -FilePath dotnet -Arguments @(
        'publish',
        $projectPath,
        '-c',
        $Configuration,
        '-o',
        $publishRoot,
        '-r',
        'win-x64',
        '--self-contained',
        'false',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '--nologo',
        '--verbosity',
        'minimal'
    )

    $sourceExe = Join-Path $publishRoot 'OpenModulePlatform.Bootstrapper.exe'
    if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
        throw "Publish did not produce OpenModulePlatform.Bootstrapper.exe: $publishRoot"
    }

    # Sign the fresh runner through the canonical path before it goes anywhere.
    # This is a no-op unless signing is configured (see sign-artifacts.ps1), so
    # unsigned developer packaging keeps working exactly as before.
    $signScript = Join-Path $PSScriptRoot 'sign-artifacts.ps1'
    if (Test-Path -LiteralPath $signScript -PathType Leaf) {
        & $signScript -Path $publishRoot -SkipIfUnconfigured
        if ($LASTEXITCODE -ne 0) {
            throw "Code signing failed for '$publishRoot' with exit code $LASTEXITCODE. The package was not modified."
        }
    }

    $targetExe = Join-Path $packageRootPath 'OpenModulePlatform.Bootstrapper.exe'
    # Samma rot som Update-PackageToolRunner anvander (rad ~182); en gissad
    # sokvag hade grindat fel fil och latit den riktiga passera ogrindad.
    $toolExe = Join-Path (Join-Path $packageRootPath 'tools\OpenModulePlatform.Bootstrapper') 'OpenModulePlatform.Bootstrapper.exe'

    # Gate EVERY target before replacing ANY of them. Checking as we go would
    # leave a package with a new root runner and an old tools runner when the
    # second check fails - a partially replaced package is worse than a refused
    # one, because nothing reports it.
    Assert-RunnerReplacementAllowed -SourceExe $sourceExe -TargetExe $targetExe
    Assert-RunnerReplacementAllowed -SourceExe $sourceExe -TargetExe $toolExe

    Close-IdleInstallerGui -PackageRoot $packageRootPath
    Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force
    Update-PackageToolRunner -PackageRoot $packageRootPath -SourceExe $sourceExe

    Write-Host "Updated installer runner: $targetExe"
    Write-Host 'Package object libraries were not rebuilt.'
}
finally {
    if (Test-Path -LiteralPath $publishRoot -PathType Container) {
        Remove-Item -LiteralPath $publishRoot -Recurse -Force
    }
}
