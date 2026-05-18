# File: scripts/install-local-examples.ps1
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    # E:\OMP is the documented local development runtime root for this repository.
    # Pass -RuntimeRoot explicitly when a workstation uses a different local layout.
    [string]$RuntimeRoot = 'E:\OMP',
    [string]$SqlServer = 'localhost',
    [string]$Database = 'OpenModulePlatform',
    [string]$IisSiteName = 'OpenModulePlatform',
    [int]$IisPort = 8088,
    [string]$OpenDocViewerRepositoryRoot = '',
    [string]$OpenDocViewerAppPath = 'opendocviewer',
    [switch]$SkipBuild,
    [switch]$SkipPublish,
    [switch]$SkipSql,
    [switch]$SkipIis,
    [switch]$SkipOpenDocViewer,
    [switch]$SkipOpenDocViewerBuild,
    [switch]$SkipExampleService,
    [switch]$SkipStartExampleService,
    [switch]$SkipRuntimeServices,
    [switch]$SkipStartRuntimeServices,
    [string]$RunAsUser = '',
    # Prefer leaving this blank so the script prompts with Read-Host -AsSecureString.
    # The parameter remains for non-interactive local developer runs.
    [string]$RunAsPassword = '',
    [switch]$GrantRunAsDatabaseAccess,
    [switch]$RemoveLegacyAppPoolDatabaseUsers,
    [switch]$KeepLegacyAppPoolDatabaseUsers,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:JsonSerializationDepth = 12
$script:publishRoot = Join-Path $RuntimeRoot 'Publish\OMP'
$script:webAppsRoot = Join-Path $RuntimeRoot 'WebApps'
$script:portalPath = Join-Path $RuntimeRoot 'Sites\Portal'
$script:authAppPath = Join-Path $RuntimeRoot 'WebApps\auth'
$script:servicesRoot = Join-Path $RuntimeRoot 'Services'
$script:appcmdPath = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'
$script:exampleServiceName = 'OpenModulePlatform.Service.ExampleServiceAppModule'
$script:hostAgentServiceName = 'OpenModulePlatform.HostAgent'
$script:workerManagerServiceName = 'OpenModulePlatform.WorkerManager'
$script:authAppPoolName = 'OMP_Auth'
$script:portalAppPoolName = 'OMP_Portal'
$script:openDocViewerAppPoolName = 'OMP_OpenDocViewer'
$script:exampleWebAppPoolName = 'OMP_ExampleWebAppModule'
$script:exampleWebAppBlazorPoolName = 'OMP_ExampleWebAppBlazorModule'
$script:exampleServiceWebAppPoolName = 'OMP_ExampleServiceAppModule'
$script:exampleWorkerWebAppPoolName = 'OMP_ExampleWorkerAppModule'
$script:iframeWebAppPoolName = 'OMP_iFrameWebAppModule'
$script:exampleWebAppPoolNames = @(
    $script:exampleWebAppPoolName,
    $script:exampleWebAppBlazorPoolName,
    $script:exampleServiceWebAppPoolName,
    $script:exampleWorkerWebAppPoolName,
    $script:iframeWebAppPoolName
)
$script:deploymentStopAppPoolNames = @($script:authAppPoolName, $script:portalAppPoolName) + $script:exampleWebAppPoolNames + @($script:openDocViewerAppPoolName)
$script:deploymentStartAppPoolNames = @($script:portalAppPoolName, $script:authAppPoolName) + $script:exampleWebAppPoolNames + @($script:openDocViewerAppPoolName)
$script:legacyVirtualAppPoolPrincipals = $script:deploymentStartAppPoolNames | ForEach-Object { "IIS APPPOOL\$_" }
$script:resolvedRunAsUser = ''
$script:resolvedRunAsPasswordSecure = $null
$script:resolvedRunAsCredential = $null
$script:openDocViewerVersion = ''

function Write-Step {
    param([string]$Message)
    Write-Host "`n== $Message ==" -ForegroundColor Cyan
}

function Test-IsUncPath {
    param([string]$Path)

    return -not [string]::IsNullOrWhiteSpace($Path) -and
        $Path.StartsWith('\\', [System.StringComparison]::Ordinal)
}

function Confirm-LocalAction {
    param([string]$Message)
    if ($Yes) { return $true }
    $answer = Read-Host "$Message [Y/N, default N]"
    return $answer.Trim() -ieq 'Y'
}

function Resolve-WindowsAccountName {
    param([string]$Account)

    if ([string]::IsNullOrWhiteSpace($Account)) {
        return ''
    }

    $trimmed = $Account.Trim()
    if ($trimmed.StartsWith('.\', [System.StringComparison]::Ordinal)) {
        return "$env:COMPUTERNAME\$($trimmed.Substring(2))"
    }

    if ($trimmed.IndexOf('\') -lt 0 -and $trimmed.IndexOf('@') -lt 0) {
        return "$env:COMPUTERNAME\$trimmed"
    }

    return $trimmed
}

function ConvertFrom-SecureStringToPlainText {
    param([Parameter(Mandatory = $true)][Security.SecureString]$SecureString)

    # IIS appcmd.exe and Win32_Service.Change still require plain-text passwords. Keep the
    # returned string in the narrowest possible scope and clear script references after use.
    # The managed string can remain in memory until garbage collection, so this script treats
    # the conversion as a legacy Windows API boundary rather than a secure storage mechanism.
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Initialize-RunAsIdentity {
    $account = $RunAsUser
    if ([string]::IsNullOrWhiteSpace($account) -and -not $Yes) {
        $account = Read-Host 'Windows account for IIS app pools and the example service (blank keeps the current app pool identity)'
    }

    if ([string]::IsNullOrWhiteSpace($account)) {
        return
    }

    $script:resolvedRunAsUser = Resolve-WindowsAccountName -Account $account
    if ([string]::IsNullOrWhiteSpace($RunAsPassword)) {
        $script:resolvedRunAsPasswordSecure = Read-Host "Password for $script:resolvedRunAsUser" -AsSecureString
    }
    else {
        Write-Warning 'RunAsPassword was supplied as a plain-text parameter. Prefer the interactive SecureString prompt for local runs and avoid command history/process-list exposure.'
        $script:resolvedRunAsPasswordSecure = ConvertTo-SecureString -String $RunAsPassword -AsPlainText -Force
    }

    if ($null -eq $script:resolvedRunAsPasswordSecure) {
        throw "A password is required when RunAsUser is set."
    }

    $script:resolvedRunAsCredential = New-Object -TypeName System.Management.Automation.PSCredential -ArgumentList $script:resolvedRunAsUser, $script:resolvedRunAsPasswordSecure
}

function Clear-RunAsIdentity {
    $script:resolvedRunAsPasswordSecure = $null
    $script:resolvedRunAsCredential = $null
}

function Get-RunAsPasswordPlainText {
    if ($null -eq $script:resolvedRunAsPasswordSecure) {
        return ''
    }

    return ConvertFrom-SecureStringToPlainText -SecureString $script:resolvedRunAsPasswordSecure
}

function Require-Command {
    param([string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found in PATH."
    }
}

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments
    )

    # This helper prints native command output for developer diagnostics. Do not
    # use it for commands that may echo secrets; use Invoke-NativeCheckedRedacted
    # or suppress output at the specific call site instead.
    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Command failed with exit code ${exitCode}: $FilePath $($Arguments -join ' ')"
    }
}

function Invoke-NativeCheckedRedacted {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string[]]$DisplayArguments
    )

    # DisplayArguments redacts sensitive arguments. The invoked command can still
    # write its own output, so only use this with tools that do not echo supplied
    # passwords or tokens.
    Write-Host "> $FilePath $($DisplayArguments -join ' ')"
    & $FilePath @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Command failed with exit code ${exitCode}: $FilePath $($DisplayArguments -join ' ')"
    }
}

function Invoke-RobocopyChecked {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    Assert-PathUnderRoot -Root $RuntimeRoot -Path $Destination
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    # /MIR mirrors the source and deletes destination-only files. /R:2 and /W:2 keep retries
    # short; /NFL, /NDL, and /NP reduce file, directory, and progress log noise.
    $options = @('/MIR', '/R:2', '/W:2', '/NFL', '/NDL', '/NP')
    Write-Host "> robocopy $Source $Destination $($options -join ' ')"
    & robocopy $Source $Destination @options
    $exitCode = $LASTEXITCODE
    # Robocopy uses 0-7 for successful outcomes, including copied, skipped, or mismatched
    # files. Exit code 8 and above indicate copy failures.
    if ($exitCode -ge 8) {
        throw "robocopy failed with exit code ${exitCode}: $Source -> $Destination"
    }
}

function Remove-ArtifactRuntimeConfigurationFiles {
    param([Parameter(Mandatory = $true)][string]$Destination)

    if (-not (Test-Path -LiteralPath $Destination -PathType Container)) {
        return
    }

    Assert-PathUnderRoot -Root $RuntimeRoot -Path $Destination

    foreach ($pattern in @('appsettings.json', 'appsettings.*.json', 'odv.site.config.js')) {
        Get-ChildItem -LiteralPath $Destination -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }
}

function Assert-PathUnderRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $pathFull = [System.IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    if (-not $pathFull.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to mirror files outside runtime root. Root: $rootFull Path: $pathFull"
    }

    Assert-NoReparsePointInPath -Root $rootFull -Path $pathFull
}

function Assert-NoReparsePointInPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    # Robocopy /MIR follows existing junctions and symbolic links. Reject a
    # mirror target when any existing ancestor under the runtime root is a
    # reparse point, otherwise the path prefix check can be bypassed.
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $current = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    while ($current.Length -ge $rootFull.Length) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to mirror files through a junction or symbolic link: $current"
            }
        }

        if ([string]::Equals($current, $rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }

        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or
            [string]::Equals($parent, $current, [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }

        $current = $parent
    }
}

function Get-OmpConnectionString {
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder['Data Source'] = $SqlServer
    $builder['Initial Catalog'] = $Database
    $builder['Integrated Security'] = $true
    $builder['TrustServerCertificate'] = $true
    return $builder.ConnectionString
}

function ConvertTo-SqlUnicodeLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "N'$($Value.Replace("'", "''"))'"
}

function ConvertTo-SqlNullableUnicodeLiteral {
    param([object]$Value)

    if ($null -eq $Value) {
        return 'NULL'
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return 'NULL'
    }

    return ConvertTo-SqlUnicodeLiteral -Value $text
}

function Join-UrlPath {
    param(
        [string]$BaseUrl,
        [string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
        return $null
    }

    $base = $BaseUrl.TrimEnd('/')
    $relative = $RelativePath.Trim('/\')
    if ([string]::IsNullOrWhiteSpace($relative)) {
        return $base + '/'
    }

    return $base + '/' + $relative + '/'
}

function Invoke-SqlFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "SQL file does not exist: $Path"
    }

    Require-Command sqlcmd
    Invoke-NativeChecked sqlcmd '-S' $SqlServer '-d' $Database '-E' '-b' '-i' $Path
}

function Invoke-SqlText {
    param([Parameter(Mandatory = $true)][string]$Query)

    Require-Command sqlcmd
    $temp = [System.IO.Path]::GetTempFileName()
    Set-Content -LiteralPath $temp -Value $Query -Encoding UTF8
    try {
        Invoke-NativeChecked sqlcmd '-S' $SqlServer '-d' $Database '-E' '-b' '-i' $temp
    }
    finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

function Test-SqlIntegratedAccess {
    if ($SkipSql) { return }

    Write-Step 'Validating SQL integrated authentication'
    Require-Command sqlcmd
    Invoke-NativeChecked sqlcmd '-S' $SqlServer '-d' $Database '-E' '-b' '-Q' 'SET NOCOUNT ON; SELECT 1;'
}

function Test-IsWindowsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-OpenDocViewerRoot {
    if ($SkipOpenDocViewer) { return '' }

    if (-not [string]::IsNullOrWhiteSpace($OpenDocViewerRepositoryRoot)) {
        return [System.IO.Path]::GetFullPath($OpenDocViewerRepositoryRoot)
    }

    $workspaceRoot = Split-Path -Parent $RepositoryRoot
    $candidate = Join-Path $workspaceRoot 'OpenDocViewer'
    if (Test-Path -LiteralPath (Join-Path $candidate 'package.json')) {
        return [System.IO.Path]::GetFullPath($candidate)
    }

    if ($Yes) {
        Write-Warning 'OpenDocViewer repo was not found next to OpenModulePlatform. Skipping ODV deployment.'
        return ''
    }

    $entered = Read-Host 'OpenDocViewer repository path (blank skips ODV deployment)'
    if ([string]::IsNullOrWhiteSpace($entered)) {
        return ''
    }

    return [System.IO.Path]::GetFullPath($entered)
}

function Publish-OpenModulePlatform {
    if ($SkipBuild -and $SkipPublish) { return }

    Push-Location $RepositoryRoot
    try {
        # Prefer Visual Studio's newer .slnx format when the repository has it, while keeping
        # compatibility with older checkouts that still use the traditional .sln file.
        $solution = Join-Path $RepositoryRoot 'OpenModulePlatform.slnx'
        if (-not (Test-Path -LiteralPath $solution)) {
            $solution = Join-Path $RepositoryRoot 'OpenModulePlatform.sln'
        }

        if (-not $SkipBuild) {
            Write-Step 'Building OpenModulePlatform'
            Invoke-NativeChecked dotnet 'restore' $solution
            Invoke-NativeChecked dotnet 'build' $solution '-c' 'Release' '--no-restore'
        }

        if (-not $SkipPublish) {
            Write-Step 'Publishing OpenModulePlatform web apps and modules'
            & (Join-Path $RepositoryRoot 'publish-all.ps1') `
                -Configuration Release `
                -OutputRoot $script:publishRoot `
                -Restore `
                -CleanOutput

            $exitCode = $LASTEXITCODE
            if ($exitCode -ne 0) {
                throw "publish-all.ps1 failed with exit code ${exitCode}."
            }
        }
    }
    finally {
        Pop-Location
    }
}

function Publish-OpenDocViewer {
    param([string]$OpenDocViewerRoot)

    if ($SkipOpenDocViewer -or [string]::IsNullOrWhiteSpace($OpenDocViewerRoot)) {
        return ''
    }

    if (-not (Test-Path -LiteralPath (Join-Path $OpenDocViewerRoot 'package.json'))) {
        throw "OpenDocViewer package.json was not found in: $OpenDocViewerRoot"
    }

    $packageJson = Get-Content -LiteralPath (Join-Path $OpenDocViewerRoot 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $versionProperty = $packageJson.PSObject.Properties['version']
    if ($null -eq $versionProperty -or [string]::IsNullOrWhiteSpace([string]$versionProperty.Value)) {
        throw "OpenDocViewer package.json does not contain a version."
    }
    $script:openDocViewerVersion = ([string]$versionProperty.Value).Trim()

    if (-not $SkipOpenDocViewerBuild) {
        Write-Step 'Building OpenDocViewer'
        Require-Command npm
        Push-Location $OpenDocViewerRoot
        try {
            if (-not (Test-Path -LiteralPath (Join-Path $OpenDocViewerRoot 'node_modules'))) {
                Invoke-NativeChecked npm 'ci'
            }

            Invoke-NativeChecked npm 'run' 'build'
        }
        finally {
            Pop-Location
        }
    }

    $distPath = Join-Path $OpenDocViewerRoot 'dist'
    if (-not (Test-Path -LiteralPath $distPath)) {
        throw "OpenDocViewer dist folder was not found. Build ODV first or remove -SkipOpenDocViewerBuild. Path: $distPath"
    }

    return $distPath
}

function Deploy-PublishedOutputs {
    param([string]$OpenDocViewerDistPath)

    if ($SkipPublish) { return }

    Write-Step 'Deploying published web applications'
    Stop-IisAppPoolsForDeployment

    $deployments = @(
        @{ Source = 'OpenModulePlatform.Auth'; Destination = 'WebApps\auth' },
        @{ Source = 'OpenModulePlatform.Portal'; Destination = 'Sites\Portal' },
        @{ Source = 'OpenModulePlatform.Web.ExampleWebAppModule'; Destination = 'WebApps\ExampleWebAppModule' },
        @{ Source = 'OpenModulePlatform.Web.ExampleWebAppBlazorModule'; Destination = 'WebApps\ExampleWebAppBlazorModule' },
        @{ Source = 'OpenModulePlatform.Web.ExampleServiceAppModule'; Destination = 'WebApps\ExampleServiceAppModule' },
        @{ Source = 'OpenModulePlatform.Web.ExampleWorkerAppModule'; Destination = 'WebApps\ExampleWorkerAppModule' },
        @{ Source = 'OpenModulePlatform.Web.iFrameWebAppModule'; Destination = 'WebApps\iFrameWebAppModule' }
    )

    foreach ($deployment in $deployments) {
        $sourcePath = Join-Path $script:publishRoot $deployment.Source
        if (-not (Test-Path -LiteralPath $sourcePath)) {
            throw "Published output was not found: $sourcePath"
        }

        $destinationPath = Join-Path $RuntimeRoot $deployment.Destination
        Invoke-RobocopyChecked -Source $sourcePath -Destination $destinationPath
    }

    $webArtifactDeployments = @(
        @{ Source = 'OpenModulePlatform.Portal'; Destination = 'ArtifactStore\omp-portal\web\0.3.3' },
        @{ Source = 'OpenModulePlatform.Web.ExampleWebAppModule'; Destination = 'ArtifactStore\example-webapp\web\1.0.0' },
        @{ Source = 'OpenModulePlatform.Web.ExampleWebAppBlazorModule'; Destination = 'ArtifactStore\example-webapp-blazor\web\1.0.0' },
        @{ Source = 'OpenModulePlatform.Web.ExampleServiceAppModule'; Destination = 'ArtifactStore\example-serviceapp\web\1.0.0' },
        @{ Source = 'OpenModulePlatform.Web.ExampleWorkerAppModule'; Destination = 'ArtifactStore\example-workerapp\web\1.0.0' },
        @{ Source = 'OpenModulePlatform.Web.iFrameWebAppModule'; Destination = 'ArtifactStore\iframe-webapp\web\0.3.3' }
    )

    foreach ($deployment in $webArtifactDeployments) {
        $sourcePath = Join-Path $script:publishRoot $deployment.Source
        if (-not (Test-Path -LiteralPath $sourcePath)) {
            throw "Published artifact output was not found: $sourcePath"
        }

        $destinationPath = Join-Path $RuntimeRoot $deployment.Destination
        Invoke-RobocopyChecked -Source $sourcePath -Destination $destinationPath
        Remove-ArtifactRuntimeConfigurationFiles -Destination $destinationPath
    }

    $serviceArtifactSourcePath = Join-Path $script:publishRoot 'OpenModulePlatform.Service.ExampleServiceAppModule'
    if (-not (Test-Path -LiteralPath $serviceArtifactSourcePath)) {
        throw "Published example service artifact output was not found: $serviceArtifactSourcePath"
    }

    $serviceArtifactDestinationPath = Join-Path $RuntimeRoot 'ArtifactStore\example-serviceapp\service\1.0.0'
    Invoke-RobocopyChecked -Source $serviceArtifactSourcePath -Destination $serviceArtifactDestinationPath
    Remove-ArtifactRuntimeConfigurationFiles -Destination $serviceArtifactDestinationPath

    $workerArtifactSourcePath = Join-Path $script:publishRoot 'OpenModulePlatform.Worker.ExampleWorkerAppModule'
    if (-not (Test-Path -LiteralPath $workerArtifactSourcePath)) {
        throw "Published example worker artifact output was not found: $workerArtifactSourcePath"
    }

    $workerArtifactDestinationPath = Join-Path $RuntimeRoot 'ArtifactStore\example-workerapp\worker\1.0.0'
    Invoke-RobocopyChecked -Source $workerArtifactSourcePath -Destination $workerArtifactDestinationPath
    Remove-ArtifactRuntimeConfigurationFiles -Destination $workerArtifactDestinationPath

    if (-not $SkipRuntimeServices) {
        Stop-WindowsServiceIfInstalled -Name $script:workerManagerServiceName
        Stop-WindowsServiceIfInstalled -Name $script:hostAgentServiceName

        $runtimeServiceDeployments = @(
            @{ Source = 'OpenModulePlatform.HostAgent.WindowsService'; Destination = 'Services\HostAgent' },
            @{ Source = 'OpenModulePlatform.WorkerManager.WindowsService'; Destination = 'Services\WorkerManager' },
            @{ Source = 'OpenModulePlatform.WorkerProcessHost'; Destination = 'Services\WorkerProcessHost' }
        )

        foreach ($deployment in $runtimeServiceDeployments) {
            $sourcePath = Join-Path $script:publishRoot $deployment.Source
            if (-not (Test-Path -LiteralPath $sourcePath)) {
                throw "Published runtime service output was not found: $sourcePath"
            }

            $destinationPath = Join-Path $RuntimeRoot $deployment.Destination
            Invoke-RobocopyChecked -Source $sourcePath -Destination $destinationPath
        }
    }

    if (-not $SkipExampleService) {
        Stop-WindowsServiceIfInstalled -Name $script:exampleServiceName

        $serviceSourcePath = Join-Path $script:publishRoot $script:exampleServiceName
        if (-not (Test-Path -LiteralPath $serviceSourcePath)) {
            throw "Published example service output was not found: $serviceSourcePath"
        }

        $serviceDestinationPath = Join-Path $script:servicesRoot 'ExampleServiceAppModule'
        Invoke-RobocopyChecked -Source $serviceSourcePath -Destination $serviceDestinationPath
    }

    if (-not [string]::IsNullOrWhiteSpace($OpenDocViewerDistPath)) {
        $odvDestination = Join-Path $script:webAppsRoot $OpenDocViewerAppPath
        Invoke-RobocopyChecked -Source $OpenDocViewerDistPath -Destination $odvDestination

        if ([string]::IsNullOrWhiteSpace($script:openDocViewerVersion)) {
            throw 'OpenDocViewer version was not resolved from package.json.'
        }

        $odvArtifactDestination = Join-Path $RuntimeRoot "ArtifactStore\opendocviewer\web\$($script:openDocViewerVersion)"
        Invoke-RobocopyChecked -Source $OpenDocViewerDistPath -Destination $odvArtifactDestination
        Remove-ArtifactRuntimeConfigurationFiles -Destination $odvArtifactDestination
    }
}

function Stop-IisAppPoolsForDeployment {
    if ($SkipIis -or -not (Test-Path -LiteralPath $script:appcmdPath)) {
        return
    }

    foreach ($pool in $script:deploymentStopAppPoolNames) {
        if (Test-IisAppPool -Name $pool) {
            Invoke-AppCmdOptional stop apppool "/apppool.name:$pool"
        }
    }
}

function Write-ExampleRuntimeConfig {
    Write-Step 'Writing example runtime configuration overrides'

    $connectionString = Get-OmpConnectionString
    $odvBaseUrl = '/' + $OpenDocViewerAppPath.Trim('/') + '/'
    $odvSampleUrl = $odvBaseUrl + 'sample.pdf'
    $dataProtectionKeyPath = Join-Path $RuntimeRoot 'DataProtectionKeys'
    New-Item -ItemType Directory -Path $dataProtectionKeyPath -Force | Out-Null

    $ompAuth = [ordered]@{
        CookieName = '.OpenModulePlatform.Auth'
        LoginPath = '/auth/login'
        LogoutPath = '/auth/logout'
        AccessDeniedPath = '/status/403'
        ApplicationName = 'OpenModulePlatform'
        DataProtectionKeyPath = $dataProtectionKeyPath
    }

    if (Test-Path -LiteralPath $script:portalPath) {
        $portalOverride = [ordered]@{
            ConnectionStrings = [ordered]@{
                OmpDb = $connectionString
            }
            OmpAuth = $ompAuth
            Portal = [ordered]@{
                PortalTopBar = [ordered]@{
                    PortalBaseUrl = '/'
                }
            }
        }

        $target = Join-Path $script:portalPath 'appsettings.Production.json'
        $json = $portalOverride | ConvertTo-Json -Depth $script:JsonSerializationDepth
        Set-Content -LiteralPath $target -Value $json -Encoding UTF8
        Write-Host "Wrote: $target"
    }

    if (Test-Path -LiteralPath $script:authAppPath) {
        $authOverride = [ordered]@{
            ConnectionStrings = [ordered]@{
                OmpDb = $connectionString
            }
            OmpAuth = $ompAuth
        }

        $target = Join-Path $script:authAppPath 'appsettings.Production.json'
        $json = $authOverride | ConvertTo-Json -Depth $script:JsonSerializationDepth
        Set-Content -LiteralPath $target -Value $json -Encoding UTF8
        Write-Host "Wrote: $target"
    }

    $appFolders = @(
        'ExampleWebAppModule',
        'ExampleWebAppBlazorModule',
        'ExampleServiceAppModule',
        'ExampleWorkerAppModule'
    )

    foreach ($appFolder in $appFolders) {
        $appPath = Join-Path $script:webAppsRoot $appFolder
        if (-not (Test-Path -LiteralPath $appPath)) {
            continue
        }

        $override = [ordered]@{
            ConnectionStrings = [ordered]@{
                OmpDb = $connectionString
            }
            OmpAuth = $ompAuth
            Portal = [ordered]@{
                PortalTopBar = [ordered]@{
                    PortalBaseUrl = '/'
                }
            }
            OpenDocViewer = [ordered]@{
                BaseUrl = $odvBaseUrl
                SampleFileUrl = $odvSampleUrl
            }
        }

        $target = Join-Path $appPath 'appsettings.Production.json'
        $json = $override | ConvertTo-Json -Depth $script:JsonSerializationDepth
        Set-Content -LiteralPath $target -Value $json -Encoding UTF8
        Write-Host "Wrote: $target"
    }

    $iframeAppPath = Join-Path $script:webAppsRoot 'iFrameWebAppModule'
    if (Test-Path -LiteralPath $iframeAppPath) {
        $iframeOverride = [ordered]@{
            ConnectionStrings = [ordered]@{
                OmpDb = $connectionString
            }
            OmpAuth = $ompAuth
            Portal = [ordered]@{
                PortalTopBar = [ordered]@{
                    PortalBaseUrl = '/'
                }
            }
        }

        $target = Join-Path $iframeAppPath 'appsettings.Production.json'
        $json = $iframeOverride | ConvertTo-Json -Depth $script:JsonSerializationDepth
        Set-Content -LiteralPath $target -Value $json -Encoding UTF8
        Write-Host "Wrote: $target"
    }

    if (-not $SkipExampleService) {
        $servicePath = Join-Path $script:servicesRoot 'ExampleServiceAppModule'
        if (Test-Path -LiteralPath $servicePath) {
            $override = [ordered]@{
                ConnectionStrings = [ordered]@{
                    OmpDb = $connectionString
                }
            }

            $target = Join-Path $servicePath 'appsettings.Production.json'
            $json = $override | ConvertTo-Json -Depth $script:JsonSerializationDepth
            Set-Content -LiteralPath $target -Value $json -Encoding UTF8
            Write-Host "Wrote: $target"
        }
    }

    if (-not $SkipRuntimeServices) {
        $hostAgentPath = Join-Path $script:servicesRoot 'HostAgent'
        if (Test-Path -LiteralPath $hostAgentPath) {
            $hostAgentConfig = [ordered]@{
                ConnectionStrings = [ordered]@{
                    OmpDb = $connectionString
                }
                HostAgent = [ordered]@{
                    HostKey = 'sample-host'
                    HostName = $env:COMPUTERNAME
                    RefreshSeconds = 30
                    CentralArtifactRoot = (Join-Path $RuntimeRoot 'ArtifactStore')
                    LocalArtifactCacheRoot = (Join-Path $RuntimeRoot 'ArtifactCache')
                    MaterializeTemplates = $true
                    ProcessHostDeployments = $true
                    ProvisionAppInstanceArtifacts = $true
                    ProvisionExplicitRequirements = $true
                    DeployWebApps = $true
                    IisSiteName = 'OpenModulePlatform'
                    WebAppsRoot = $script:webAppsRoot
                    PortalPhysicalPath = $script:portalPath
                    UseAppOfflineForWebAppDeployment = $true
                    AppOfflineShutdownDelayMilliseconds = 1500
                    StopIisAppPoolForWebAppDeployment = $false
                    StartIisAppPoolAfterWebAppDeployment = $false
                    IisAppPoolStopTimeoutSeconds = 30
                    WebAppDeploymentExcludedEntries = @('appsettings.json', 'appsettings.*.json', 'logs', 'App_Data')
                    DeployServiceApps = $true
                    ServicesRoot = $script:servicesRoot
                    StopServiceForServiceAppDeployment = $true
                    StartServiceAfterServiceAppDeployment = $true
                    ServiceAppStopTimeoutSeconds = 30
                    ServiceAppStartTimeoutSeconds = 30
                    ServiceAppDeploymentExcludedEntries = @('appsettings.json', 'appsettings.*.json', 'logs', 'App_Data')
                    MaxArtifactsPerCycle = 100
                    EnableRpc = $true
                    RpcPipeName = ''
                    RpcRequestTimeoutSeconds = 60
                }
            }

            $target = Join-Path $hostAgentPath 'appsettings.Production.json'
            $json = $hostAgentConfig | ConvertTo-Json -Depth $script:JsonSerializationDepth
            Set-Content -LiteralPath $target -Value $json -Encoding UTF8
            Write-Host "Wrote: $target"
        }

        $workerManagerPath = Join-Path $script:servicesRoot 'WorkerManager'
        if (Test-Path -LiteralPath $workerManagerPath) {
            $workerManagerConfig = [ordered]@{
                ConnectionStrings = [ordered]@{
                    OmpDb = $connectionString
                }
                WorkerManager = [ordered]@{
                    CatalogMode = 'OmpDatabase'
                    HostKey = 'sample-host'
                    HostName = $env:COMPUTERNAME
                    RefreshSeconds = 15
                    WorkerProcessPath = (Join-Path $script:servicesRoot 'WorkerProcessHost\OpenModulePlatform.WorkerProcessHost.exe')
                    StopTimeoutSeconds = 15
                    RestartDelaySeconds = 5
                    RestartWindowSeconds = 300
                    MaxRestartsPerWindow = 5
                    OmpDatabase = [ordered]@{
                        RuntimeKind = 'windows-worker-plugin'
                        RunningDesiredState = 1
                        UseHostArtifactCache = $true
                    }
                    HostAgentRpc = [ordered]@{
                        Enabled = $true
                        PipeName = ''
                        TimeoutSeconds = 60
                    }
                    Workers = @()
                }
            }

            $target = Join-Path $workerManagerPath 'appsettings.Production.json'
            $json = $workerManagerConfig | ConvertTo-Json -Depth $script:JsonSerializationDepth
            Set-Content -LiteralPath $target -Value $json -Encoding UTF8
            Write-Host "Wrote: $target"
        }

        $workerProcessHostPath = Join-Path $script:servicesRoot 'WorkerProcessHost'
        if (Test-Path -LiteralPath $workerProcessHostPath) {
            $workerProcessHostConfig = [ordered]@{
                ConnectionStrings = [ordered]@{
                    OmpDb = $connectionString
                }
                WorkerProcess = [ordered]@{
                    AppInstanceId = '00000000-0000-0000-0000-000000000000'
                    WorkerInstanceId = '00000000-0000-0000-0000-000000000000'
                    WorkerInstanceKey = ''
                    WorkerTypeKey = ''
                    PluginAssemblyPath = ''
                    ShutdownEventName = ''
                    ConfigurationJson = ''
                }
            }

            $target = Join-Path $workerProcessHostPath 'appsettings.Production.json'
            $json = $workerProcessHostConfig | ConvertTo-Json -Depth $script:JsonSerializationDepth
            Set-Content -LiteralPath $target -Value $json -Encoding UTF8
            Write-Host "Wrote: $target"
        }
    }
}

function Ensure-OpenDocViewerMetadata {
    if ($SkipSql -or $SkipOpenDocViewer -or [string]::IsNullOrWhiteSpace($script:openDocViewerVersion)) {
        return
    }

    Write-Step 'Ensuring OpenDocViewer OMP metadata'

    $routePath = $OpenDocViewerAppPath.Trim('/\')
    if ([string]::IsNullOrWhiteSpace($routePath)) {
        throw 'OpenDocViewerAppPath must not be empty when OpenDocViewer is installed.'
    }

    $installPath = Join-Path $script:webAppsRoot $routePath
    $publicUrl = Join-UrlPath -BaseUrl "http://localhost:$IisPort/" -RelativePath $routePath

    $versionLiteral = ConvertTo-SqlUnicodeLiteral -Value $script:openDocViewerVersion
    $routePathLiteral = ConvertTo-SqlUnicodeLiteral -Value $routePath
    $publicUrlLiteral = ConvertTo-SqlNullableUnicodeLiteral -Value $publicUrl
    $installPathLiteral = ConvertTo-SqlUnicodeLiteral -Value $installPath

    Invoke-SqlText -Query @"
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_opendocviewer')
BEGIN
    EXEC(N'CREATE SCHEMA [omp_opendocviewer]');
END

DECLARE @InstanceId uniqueidentifier;
DECLARE @InstanceTemplateId int;
DECLARE @OpenDocViewerModuleId int;
DECLARE @OpenDocViewerAppId int;
DECLARE @OpenDocViewerArtifactId int;
DECLARE @SeedModuleInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111241';
DECLARE @SeedAppInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111242';
DECLARE @OpenDocViewerModuleInstanceId uniqueidentifier;
DECLARE @OpenDocViewerTemplateModuleInstanceId int;
DECLARE @ArtifactVersion nvarchar(50) = $versionLiteral;

SELECT @InstanceId = InstanceId,
       @InstanceTemplateId = InstanceTemplateId
FROM omp.Instances
WHERE InstanceKey = N'default';

IF @InstanceId IS NULL
BEGIN
    THROW 51013, 'Default OMP instance not found. Run the core SQL setup/init scripts first.', 1;
END

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'opendocviewer')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'OpenDocViewer',
        ModuleType = N'WebAppModule',
        SchemaName = N'omp_opendocviewer',
        Description = N'First-party OMP registration for the OpenDocViewer static web application',
        IsEnabled = 1,
        SortOrder = 310,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'opendocviewer';
END
ELSE
BEGIN
    INSERT INTO omp.Modules(ModuleKey, DisplayName, ModuleType, SchemaName, Description, IsEnabled, SortOrder)
    VALUES(N'opendocviewer', N'OpenDocViewer', N'WebAppModule', N'omp_opendocviewer', N'First-party OMP registration for the OpenDocViewer static web application', 1, 310);
END

SELECT @OpenDocViewerModuleId = ModuleId
FROM omp.Modules
WHERE ModuleKey = N'opendocviewer';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @OpenDocViewerModuleId AND AppKey = N'opendocviewer_webapp')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'OpenDocViewer',
        AppType = N'WebApp',
        Description = N'Static web application definition for OpenDocViewer',
        IsEnabled = 1,
        SortOrder = 310,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @OpenDocViewerModuleId
      AND AppKey = N'opendocviewer_webapp';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, Description, IsEnabled, SortOrder)
    VALUES(@OpenDocViewerModuleId, N'opendocviewer_webapp', N'OpenDocViewer', N'WebApp', N'Static web application definition for OpenDocViewer', 1, 310);
END

SELECT @OpenDocViewerAppId = AppId
FROM omp.Apps
WHERE ModuleId = @OpenDocViewerModuleId
  AND AppKey = N'opendocviewer_webapp';

MERGE omp.Artifacts AS target
USING
(
    SELECT @OpenDocViewerAppId AS AppId,
           @ArtifactVersion AS Version,
           N'web-app' AS PackageType,
           N'opendocviewer' AS TargetName,
           N'opendocviewer/web/' + @ArtifactVersion AS RelativePath,
           CAST(1 AS bit) AS IsEnabled
) AS source
ON target.AppId = source.AppId
AND target.Version = source.Version
AND target.PackageType = source.PackageType
AND target.TargetName = source.TargetName
WHEN MATCHED THEN
    UPDATE SET RelativePath = source.RelativePath,
               IsEnabled = source.IsEnabled,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(AppId, Version, PackageType, TargetName, RelativePath, IsEnabled)
    VALUES(source.AppId, source.Version, source.PackageType, source.TargetName, source.RelativePath, source.IsEnabled);

SELECT @OpenDocViewerArtifactId = ArtifactId
FROM omp.Artifacts
WHERE AppId = @OpenDocViewerAppId
  AND Version = @ArtifactVersion
  AND PackageType = N'web-app'
  AND TargetName = N'opendocviewer';

MERGE omp.ModuleInstances AS target
USING
(
    SELECT @SeedModuleInstanceId AS ModuleInstanceId,
           @InstanceId AS InstanceId,
           @OpenDocViewerModuleId AS ModuleId,
           N'opendocviewer' AS ModuleInstanceKey,
           N'OpenDocViewer' AS DisplayName,
           N'OpenDocViewer module instance for the default OMP instance' AS Description,
           CAST(1 AS bit) AS IsEnabled,
           CAST(310 AS int) AS SortOrder
) AS source
ON target.ModuleInstanceId = source.ModuleInstanceId
OR (target.InstanceId = source.InstanceId AND target.ModuleInstanceKey = source.ModuleInstanceKey)
WHEN MATCHED THEN
    UPDATE SET ModuleId = source.ModuleId,
               DisplayName = source.DisplayName,
               Description = source.Description,
               IsEnabled = source.IsEnabled,
               SortOrder = source.SortOrder,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(ModuleInstanceId, InstanceId, ModuleId, ModuleInstanceKey, DisplayName, Description, IsEnabled, SortOrder)
    VALUES(source.ModuleInstanceId, source.InstanceId, source.ModuleId, source.ModuleInstanceKey, source.DisplayName, source.Description, source.IsEnabled, source.SortOrder);

SELECT @OpenDocViewerModuleInstanceId = ModuleInstanceId
FROM omp.ModuleInstances
WHERE InstanceId = @InstanceId
  AND ModuleInstanceKey = N'opendocviewer';

MERGE omp.InstanceTemplateModuleInstances AS target
USING
(
    SELECT @InstanceTemplateId AS InstanceTemplateId,
           @OpenDocViewerModuleId AS ModuleId,
           N'opendocviewer' AS ModuleInstanceKey,
           N'OpenDocViewer' AS DisplayName,
           N'OpenDocViewer module instance in the default template' AS Description,
           CAST(310 AS int) AS SortOrder,
           CAST(1 AS bit) AS IsEnabled
) AS source
ON target.InstanceTemplateId = source.InstanceTemplateId
AND target.ModuleInstanceKey = source.ModuleInstanceKey
WHEN MATCHED THEN
    UPDATE SET ModuleId = source.ModuleId,
               DisplayName = source.DisplayName,
               Description = source.Description,
               SortOrder = source.SortOrder,
               IsEnabled = source.IsEnabled,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(InstanceTemplateId, ModuleId, ModuleInstanceKey, DisplayName, Description, SortOrder, IsEnabled)
    VALUES(source.InstanceTemplateId, source.ModuleId, source.ModuleInstanceKey, source.DisplayName, source.Description, source.SortOrder, source.IsEnabled);

SELECT @OpenDocViewerTemplateModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @InstanceTemplateId
  AND ModuleInstanceKey = N'opendocviewer';

MERGE omp.AppInstances AS target
USING
(
    SELECT @SeedAppInstanceId AS AppInstanceId,
           @OpenDocViewerModuleInstanceId AS ModuleInstanceId,
           CAST(NULL AS uniqueidentifier) AS HostId,
           @OpenDocViewerAppId AS AppId,
           N'opendocviewer_webapp' AS AppInstanceKey,
           N'OpenDocViewer' AS DisplayName,
           N'OpenDocViewer static web app managed by OMP Host Agent' AS Description,
           $routePathLiteral AS RoutePath,
           $publicUrlLiteral AS PublicUrl,
           $installPathLiteral AS InstallPath,
           N'opendocviewer' AS InstallationName,
           @OpenDocViewerArtifactId AS ArtifactId,
           CAST(1 AS bit) AS IsEnabled,
           CAST(1 AS bit) AS IsAllowed,
           CAST(1 AS tinyint) AS DesiredState,
           CAST(310 AS int) AS SortOrder
) AS source
ON target.AppInstanceId = source.AppInstanceId
OR (target.ModuleInstanceId = source.ModuleInstanceId AND target.AppInstanceKey = source.AppInstanceKey)
WHEN MATCHED THEN
    UPDATE SET ModuleInstanceId = source.ModuleInstanceId,
               HostId = source.HostId,
               AppId = source.AppId,
               DisplayName = source.DisplayName,
               Description = source.Description,
               RoutePath = source.RoutePath,
               PublicUrl = source.PublicUrl,
               InstallPath = source.InstallPath,
               InstallationName = source.InstallationName,
               ArtifactId = source.ArtifactId,
               IsEnabled = source.IsEnabled,
               IsAllowed = source.IsAllowed,
               DesiredState = source.DesiredState,
               SortOrder = source.SortOrder,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(AppInstanceId, ModuleInstanceId, HostId, AppId, AppInstanceKey, DisplayName, Description, RoutePath, PublicUrl, InstallPath, InstallationName, ArtifactId, IsEnabled, IsAllowed, DesiredState, SortOrder)
    VALUES(source.AppInstanceId, source.ModuleInstanceId, source.HostId, source.AppId, source.AppInstanceKey, source.DisplayName, source.Description, source.RoutePath, source.PublicUrl, source.InstallPath, source.InstallationName, source.ArtifactId, source.IsEnabled, source.IsAllowed, source.DesiredState, source.SortOrder);

MERGE omp.InstanceTemplateAppInstances AS target
USING
(
    SELECT @OpenDocViewerTemplateModuleInstanceId AS InstanceTemplateModuleInstanceId,
           CAST(NULL AS int) AS InstanceTemplateHostId,
           @OpenDocViewerAppId AS AppId,
           N'opendocviewer_webapp' AS AppInstanceKey,
           N'OpenDocViewer' AS DisplayName,
           N'OpenDocViewer static web app managed by OMP Host Agent' AS Description,
           $routePathLiteral AS RoutePath,
           $publicUrlLiteral AS PublicUrl,
           $installPathLiteral AS InstallPath,
           N'opendocviewer' AS InstallationName,
           @OpenDocViewerArtifactId AS DesiredArtifactId,
           CAST(1 AS tinyint) AS DesiredState,
           CAST(310 AS int) AS SortOrder,
           CAST(1 AS bit) AS IsEnabled
) AS source
ON target.InstanceTemplateModuleInstanceId = source.InstanceTemplateModuleInstanceId
AND target.AppInstanceKey = source.AppInstanceKey
WHEN MATCHED THEN
    UPDATE SET InstanceTemplateHostId = source.InstanceTemplateHostId,
               AppId = source.AppId,
               DisplayName = source.DisplayName,
               Description = source.Description,
               RoutePath = source.RoutePath,
               PublicUrl = source.PublicUrl,
               InstallPath = source.InstallPath,
               InstallationName = source.InstallationName,
               DesiredArtifactId = source.DesiredArtifactId,
               DesiredState = source.DesiredState,
               SortOrder = source.SortOrder,
               IsEnabled = source.IsEnabled,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(InstanceTemplateModuleInstanceId, InstanceTemplateHostId, AppId, AppInstanceKey, DisplayName, Description, RoutePath, PublicUrl, InstallPath, InstallationName, DesiredArtifactId, DesiredState, SortOrder, IsEnabled)
    VALUES(source.InstanceTemplateModuleInstanceId, source.InstanceTemplateHostId, source.AppId, source.AppInstanceKey, source.DisplayName, source.Description, source.RoutePath, source.PublicUrl, source.InstallPath, source.InstallationName, source.DesiredArtifactId, source.DesiredState, source.SortOrder, source.IsEnabled);
"@
}

function Run-ExampleSql {
    if ($SkipSql) { return }

    Write-Step 'Running module and example SQL scripts'

    $sqlFiles = @(
        'examples\WebAppModule\Sql\1-setup-example-webapp.sql',
        'examples\WebAppModule\Sql\2-initialize-example-webapp.sql',
        'examples\WebAppBlazorModule\Sql\1-setup-example-webapp-blazor.sql',
        'examples\WebAppBlazorModule\Sql\2-initialize-example-webapp-blazor.sql',
        'examples\ServiceAppModule\Sql\1-setup-example-serviceapp.sql',
        'examples\ServiceAppModule\Sql\2-initialize-example-serviceapp.sql',
        'examples\WorkerAppModule\Sql\1-setup-example-workerapp.sql',
        'examples\WorkerAppModule\Sql\2-initialize-example-workerapp.sql',
        'OpenModulePlatform.Web.iFrameWebAppModule\Sql\1-setup-iframe-webapp.sql',
        'OpenModulePlatform.Web.iFrameWebAppModule\Sql\2-initialize-iframe-webapp.sql'
    )

    foreach ($relativePath in $sqlFiles) {
        Invoke-SqlFile -Path (Join-Path $RepositoryRoot $relativePath)
    }

    $exampleServicePath = (Join-Path $script:servicesRoot 'ExampleServiceAppModule').Replace("'", "''")
    $exampleServiceName = $script:exampleServiceName.Replace("'", "''")
    Invoke-SqlText -Query @"
UPDATE omp.AppInstances
SET InstallPath = N'$exampleServicePath',
    InstallationName = N'$exampleServiceName',
    UpdatedUtc = SYSUTCDATETIME()
WHERE AppInstanceKey = N'example_serviceapp_service';

UPDATE omp.InstanceTemplateAppInstances
SET InstallPath = N'$exampleServicePath',
    InstallationName = N'$exampleServiceName',
    UpdatedUtc = SYSUTCDATETIME()
WHERE AppInstanceKey = N'example_serviceapp_service';
"@

    Ensure-OpenDocViewerMetadata
}

function Ensure-RunAsDatabaseAccess {
    if ($SkipSql -or -not $GrantRunAsDatabaseAccess -or [string]::IsNullOrWhiteSpace($script:resolvedRunAsUser)) { return }

    Write-Step 'Ensuring database access for configured run-as account'

    $principal = $script:resolvedRunAsUser.Replace("'", "''")
    Invoke-SqlText -Query @"
DECLARE @principal sysname = N'$principal';
DECLARE @sql nvarchar(max);

IF SUSER_ID(@principal) IS NULL
BEGIN
    SET @sql = N'CREATE LOGIN ' + QUOTENAME(@principal) + N' FROM WINDOWS;';
    EXEC sys.sp_executesql @sql;
END

IF DATABASE_PRINCIPAL_ID(@principal) IS NULL
BEGIN
    SET @sql = N'CREATE USER ' + QUOTENAME(@principal) + N' FOR LOGIN ' + QUOTENAME(@principal) + N';';
    EXEC sys.sp_executesql @sql;
END

IF IS_ROLEMEMBER(N'db_owner', @principal) <> 1
BEGIN
    SET @sql = N'ALTER ROLE [db_owner] ADD MEMBER ' + QUOTENAME(@principal) + N';';
    EXEC sys.sp_executesql @sql;
END
"@
}

function Remove-LegacyAppPoolDatabaseUsers {
    if ($SkipSql -or -not $RemoveLegacyAppPoolDatabaseUsers -or $KeepLegacyAppPoolDatabaseUsers -or [string]::IsNullOrWhiteSpace($script:resolvedRunAsUser)) { return }

    Write-Step 'Removing legacy virtual app pool database users'

    $principals = $script:legacyVirtualAppPoolPrincipals

    $values = @()
    foreach ($principal in $principals) {
        $values += "(N'$($principal.Replace("'", "''"))')"
    }

    Invoke-SqlText -Query @"
DECLARE @Principals table(Principal sysname NOT NULL PRIMARY KEY);
INSERT INTO @Principals(Principal)
VALUES
$(($values -join ",`r`n"));

DECLARE @principal sysname;
DECLARE @sql nvarchar(max);

DECLARE principal_cursor CURSOR LOCAL FAST_FORWARD FOR
SELECT Principal FROM @Principals;

OPEN principal_cursor;
FETCH NEXT FROM principal_cursor INTO @principal;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF DATABASE_PRINCIPAL_ID(@principal) IS NOT NULL
    BEGIN
        IF IS_ROLEMEMBER(N'db_owner', @principal) = 1
        BEGIN
            EXEC sys.sp_droprolemember N'db_owner', @principal;
        END

        SET @sql = N'DROP USER ' + QUOTENAME(@principal) + N';';
        EXEC sys.sp_executesql @sql;
    END

    IF SUSER_ID(@principal) IS NOT NULL
    BEGIN
        SET @sql = N'DROP LOGIN ' + QUOTENAME(@principal) + N';';
        EXEC sys.sp_executesql @sql;
    END

    FETCH NEXT FROM principal_cursor INTO @principal;
END

CLOSE principal_cursor;
DEALLOCATE principal_cursor;
"@
}

function Require-AppCmd {
    if (-not (Test-Path -LiteralPath $script:appcmdPath)) {
        throw "IIS appcmd.exe was not found: $script:appcmdPath"
    }
}

function Invoke-AppCmdChecked {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    Require-AppCmd
    Invoke-NativeChecked $script:appcmdPath @Arguments
}

function Invoke-AppCmdOptional {
    param(
        [Parameter(ValueFromRemainingArguments = $true, Position = 0)][string[]]$Arguments,
        [int[]]$IgnoredExitCodes = @()
    )

    Require-AppCmd
    Write-Host "> $script:appcmdPath $($Arguments -join ' ')"
    $output = & $script:appcmdPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0 -and $IgnoredExitCodes -notcontains $exitCode) {
        if ($null -ne $output) {
            $output | ForEach-Object { Write-Host $_ }
        }

        Write-Warning "appcmd failed with exit code ${exitCode}: $($Arguments -join ' ')"
        return
    }

    if ($exitCode -eq 0 -and $null -ne $output) {
        $output | ForEach-Object { Write-Host $_ }
    }
}

function Test-IisAppPool {
    param([string]$Name)

    Require-AppCmd
    & $script:appcmdPath list apppool "/name:$Name" 2>&1 | Out-Null
    return $LASTEXITCODE -eq 0
}

function Test-IisSite {
    param([string]$Name)

    Require-AppCmd
    & $script:appcmdPath list site "/name:$Name" 2>&1 | Out-Null
    return $LASTEXITCODE -eq 0
}

function Get-IisAppName {
    param([string]$AppPath)

    Require-AppCmd
    $expected = "$IisSiteName/$AppPath"
    $output = & $script:appcmdPath list app 2>&1
    foreach ($line in @($output)) {
        $text = $line.ToString()
        if ($text -match '^APP "([^"]+)"') {
            $candidate = $Matches[1]
            if ([string]::Equals($candidate, $expected, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $candidate
            }
        }
    }

    return ''
}

function Ensure-IisAppPool {
    param([string]$Name)

    if (-not (Test-IisAppPool -Name $Name)) {
        Invoke-AppCmdChecked add apppool "/name:$Name"
    }

    Invoke-AppCmdChecked set apppool "/apppool.name:$Name" '/managedRuntimeVersion:'
    Invoke-AppCmdChecked set apppool "/apppool.name:$Name" '/processModel.loadUserProfile:true'

    if (-not [string]::IsNullOrWhiteSpace($script:resolvedRunAsUser)) {
        $runAsPasswordPlain = Get-RunAsPasswordPlainText
        try {
            $arguments = @(
                'set',
                'apppool',
                "/apppool.name:$Name",
                '/processModel.identityType:SpecificUser',
                "/processModel.userName:$script:resolvedRunAsUser",
                "/processModel.password:$runAsPasswordPlain"
            )
            $displayArguments = @(
                'set',
                'apppool',
                "/apppool.name:$Name",
                '/processModel.identityType:SpecificUser',
                "/processModel.userName:$script:resolvedRunAsUser",
                '/processModel.password:***'
            )
            Invoke-NativeCheckedRedacted -FilePath $script:appcmdPath -Arguments $arguments -DisplayArguments $displayArguments
        }
        finally {
            # This drops the script reference only. Managed strings cannot be reliably zeroed
            # and may remain until garbage collection, so the plain-text password scope is
            # intentionally kept short.
            $runAsPasswordPlain = ''
        }
    }
}

function Ensure-IisWebApplication {
    param(
        [string]$AppPath,
        [string]$PhysicalPath,
        [string]$AppPoolName,
        [bool]$AnonymousEnabled
    )

    Ensure-IisAppPool -Name $AppPoolName
    Invoke-AppCmdOptional stop apppool "/apppool.name:$AppPoolName"

    $existingAppName = Get-IisAppName -AppPath $AppPath
    if (-not [string]::IsNullOrWhiteSpace($existingAppName)) {
        Invoke-AppCmdChecked delete app $existingAppName
    }

    Invoke-AppCmdChecked add app `
        "/site.name:$IisSiteName" `
        "/path:/$AppPath" `
        "/physicalPath:$PhysicalPath" `
        "/applicationPool:$AppPoolName"

    Set-IisAuthentication -Location "$IisSiteName/$AppPath" -AnonymousEnabled $AnonymousEnabled
}

function Set-IisAuthentication {
    param(
        [string]$Location,
        [bool]$AnonymousEnabled,
        [object]$WindowsEnabled = $null
    )

    $anonymousValue = $AnonymousEnabled.ToString().ToLowerInvariant()
    if ($null -eq $WindowsEnabled) {
        $WindowsEnabled = -not $AnonymousEnabled
    }

    $windowsEnabledBool = [bool]$WindowsEnabled
    $windowsValue = $windowsEnabledBool.ToString().ToLowerInvariant()

    Invoke-AppCmdOptional set config $Location `
        '/section:system.webServer/security/authentication/anonymousAuthentication' `
        "/enabled:$anonymousValue" `
        '/userName:' `
        '/password:' `
        '/commit:apphost'

    Invoke-AppCmdOptional set config $Location `
        '/section:system.webServer/security/authentication/windowsAuthentication' `
        "/enabled:$windowsValue" `
        '/commit:apphost'

    if ($windowsEnabledBool) {
        Set-IisWindowsAuthenticationProviders -Location $Location
    }
}

function Set-IisWindowsAuthenticationProviders {
    param([string]$Location)

    # Keep Windows-auth enabled locations on the same provider list. Mixing
    # Negotiate+NTLM with NTLM-only child apps can make browsers issue a new
    # Windows-auth challenge when navigating between apps.
    # Removing providers is intentionally idempotent: 183 means the target
    # collection already has the requested shape, and 4312 is returned by IIS
    # when the provider entry is absent at this configuration level.
    Invoke-AppCmdOptional -IgnoredExitCodes @(183, 4312) set config $Location `
        '/section:system.webServer/security/authentication/windowsAuthentication' `
        "/-providers.[value='Negotiate']" `
        '/commit:apphost'

    Invoke-AppCmdOptional -IgnoredExitCodes @(183, 4312) set config $Location `
        '/section:system.webServer/security/authentication/windowsAuthentication' `
        "/-providers.[value='NTLM']" `
        '/commit:apphost'

    Invoke-AppCmdChecked set config $Location `
        '/section:system.webServer/security/authentication/windowsAuthentication' `
        "/+providers.[value='Negotiate']" `
        '/commit:apphost'

    Invoke-AppCmdChecked set config $Location `
        '/section:system.webServer/security/authentication/windowsAuthentication' `
        "/+providers.[value='NTLM']" `
        '/commit:apphost'
}

function Grant-RunAsRuntimeAccess {
    if ([string]::IsNullOrWhiteSpace($script:resolvedRunAsUser)) { return }

    Write-Step 'Granting runtime folder access to configured run-as account'

    New-Item -ItemType Directory -Path $RuntimeRoot -Force | Out-Null
    if (Test-IsUncPath -Path $RuntimeRoot) {
        Write-Warning "Skipping ACL grant on UNC path. Ensure the run-as account has access: $RuntimeRoot"
        return
    }

    Invoke-NativeChecked icacls $RuntimeRoot '/grant' ('{0}:(OI)(CI)M' -f $script:resolvedRunAsUser) '/T' '/C' '/Q'
}

function Wait-ForWindowsServiceStatus {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][System.ServiceProcess.ServiceControllerStatus]$Status,
        [int]$TimeoutSeconds = 30
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($null -ne $service -and $service.Status -eq $Status) {
            return
        }

        Start-Sleep -Seconds 1
    }

    throw "Timed out waiting for service '$Name' to reach status '$Status'."
}

function Stop-WindowsServiceIfInstalled {
    param([Parameter(Mandatory = $true)][string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $service -or $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        return
    }

    Write-Host "Stopping service before deployment: $Name"
    Stop-Service -Name $Name -Force -ErrorAction Stop
    Wait-ForWindowsServiceStatus -Name $Name -Status Stopped -TimeoutSeconds 30
}

function Invoke-ScChecked {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string[]]$DisplayArguments = $null
    )

    $scPath = Join-Path $env:windir 'System32\sc.exe'
    if ($null -eq $DisplayArguments) {
        $DisplayArguments = $Arguments
    }

    Invoke-NativeCheckedRedacted -FilePath $scPath -Arguments $Arguments -DisplayArguments $DisplayArguments
}

function Set-WindowsServiceConfiguration {
    param(
        [Parameter(Mandatory = $true)][string]$ServiceName,
        [Parameter(Mandatory = $true)][string]$BinaryPath,
        [Parameter(Mandatory = $true)][string]$DisplayName,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $serviceCim = Get-CimInstance -ClassName Win32_Service -Filter ("Name='{0}'" -f $ServiceName)
    if ($null -eq $serviceCim) {
        throw "Windows service exists in Service Control Manager but could not be loaded through CIM: $ServiceName"
    }

    $runAsPasswordPlain = $null
    if (-not [string]::IsNullOrWhiteSpace($script:resolvedRunAsUser)) {
        $runAsPasswordPlain = Get-RunAsPasswordPlainText
    }

    try {
        $serviceAccount = if ([string]::IsNullOrWhiteSpace($script:resolvedRunAsUser)) { $null } else { $script:resolvedRunAsUser }
        $changeArguments = @{
            DisplayName = $DisplayName
            PathName = $BinaryPath
            StartMode = 'Automatic'
            DesktopInteract = $false
        }

        if (-not [string]::IsNullOrWhiteSpace($serviceAccount)) {
            $changeArguments['StartName'] = $serviceAccount
            $changeArguments['StartPassword'] = $runAsPasswordPlain
        }

        $changeResult = Invoke-CimMethod -InputObject $serviceCim -MethodName Change -Arguments $changeArguments

        if ($changeResult.ReturnValue -ne 0) {
            throw "Failed to update Windows service '$ServiceName'. Win32_Service.Change returned $($changeResult.ReturnValue)."
        }

        Invoke-ScChecked -Arguments @('description', $ServiceName, $Description)
    }
    finally {
            # This drops the script reference only. Managed strings cannot be reliably zeroed
            # and may remain until garbage collection, so the plain-text password scope is
            # intentionally kept short.
            $runAsPasswordPlain = ''
    }
}

function Ensure-WindowsService {
    param(
        [Parameter(Mandatory = $true)][string]$ServiceName,
        [Parameter(Mandatory = $true)][string]$DisplayName,
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [switch]$SkipStart
    )

    if (-not (Test-IsWindowsAdministrator)) {
        throw "Windows service installation requires an elevated PowerShell session. Re-run as Administrator or skip service setup."
    }

    if (-not (Test-Path -LiteralPath $ExecutablePath)) {
        throw "Service executable was not found for '$ServiceName': $ExecutablePath"
    }

    $binaryPath = '"{0}"' -f $ExecutablePath
    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

    if ($null -ne $existingService -and $existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-Host "Stopping existing service: $ServiceName"
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        Wait-ForWindowsServiceStatus -Name $ServiceName -Status Stopped -TimeoutSeconds 30
    }

    if ($null -eq $existingService) {
        Write-Host "Creating Windows service: $ServiceName"
        if ($null -ne $script:resolvedRunAsCredential) {
            New-Service `
                -Name $ServiceName `
                -BinaryPathName $binaryPath `
                -DisplayName $DisplayName `
                -Description $Description `
                -StartupType Automatic `
                -Credential $script:resolvedRunAsCredential | Out-Null
        }
        else {
            New-Service `
                -Name $ServiceName `
                -BinaryPathName $binaryPath `
                -DisplayName $DisplayName `
                -Description $Description `
                -StartupType Automatic | Out-Null
        }
    }

    Set-WindowsServiceConfiguration `
        -ServiceName $ServiceName `
        -BinaryPath $binaryPath `
        -DisplayName $DisplayName `
        -Description $Description

    if (-not $SkipStart) {
        Write-Host "Starting service: $ServiceName"
        Start-Service -Name $ServiceName -ErrorAction Stop
        Wait-ForWindowsServiceStatus -Name $ServiceName -Status Running -TimeoutSeconds 30
    }
}

function Ensure-RuntimeWindowsServices {
    if ($SkipRuntimeServices) { return }

    Write-Step 'Ensuring OMP runtime Windows services'

    Ensure-WindowsService `
        -ServiceName $script:hostAgentServiceName `
        -DisplayName 'OpenModulePlatform HostAgent' `
        -Description 'Local OMP HostAgent for artifact provisioning.' `
        -ExecutablePath (Join-Path $script:servicesRoot 'HostAgent\OpenModulePlatform.HostAgent.WindowsService.exe') `
        -SkipStart:$SkipStartRuntimeServices

    Ensure-WindowsService `
        -ServiceName $script:workerManagerServiceName `
        -DisplayName 'OpenModulePlatform WorkerManager' `
        -Description 'Local OMP WorkerManager for manager-driven worker plugins.' `
        -ExecutablePath (Join-Path $script:servicesRoot 'WorkerManager\OpenModulePlatform.WorkerManager.WindowsService.exe') `
        -SkipStart:$SkipStartRuntimeServices
}

function Ensure-ExampleWindowsService {
    if ($SkipExampleService) { return }

    Write-Step 'Ensuring example Windows service'

    Ensure-WindowsService `
        -ServiceName $script:exampleServiceName `
        -DisplayName 'OpenModulePlatform Service - ExampleServiceAppModule' `
        -Description 'Example OMP service app installed by scripts/install-local-examples.ps1.' `
        -ExecutablePath (Join-Path $script:servicesRoot "ExampleServiceAppModule\$script:exampleServiceName.exe") `
        -SkipStart:$SkipStartExampleService
}

function Ensure-IisExamples {
    if ($SkipIis) { return }

    if (-not (Test-IsWindowsAdministrator)) {
        throw 'IIS installation requires an elevated PowerShell session. Re-run as Administrator or use -SkipIis.'
    }

    Write-Step 'Ensuring IIS site, module applications, and example applications'
    Require-AppCmd

    New-Item -ItemType Directory -Path $script:portalPath -Force | Out-Null
    New-Item -ItemType Directory -Path $script:webAppsRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $script:authAppPath -Force | Out-Null

    Ensure-IisAppPool -Name 'OMP_Portal'

    if (-not (Test-IisSite -Name $IisSiteName)) {
        Invoke-AppCmdChecked add site `
            "/name:$IisSiteName" `
            ("/bindings:http/*:{0}:" -f $IisPort) `
            "/physicalPath:$script:portalPath"
    }
    else {
        Invoke-AppCmdChecked set vdir "$IisSiteName/" "/physicalPath:$script:portalPath"
    }

    Invoke-AppCmdChecked set app "$IisSiteName/" ('/applicationPool:{0}' -f $script:portalAppPoolName)
    Set-IisAuthentication -Location $IisSiteName -AnonymousEnabled $true

    Ensure-IisWebApplication `
        -AppPath 'auth' `
        -PhysicalPath $script:authAppPath `
        -AppPoolName $script:authAppPoolName `
        -AnonymousEnabled $true

    Set-IisAuthentication -Location "$IisSiteName/auth" -AnonymousEnabled $true -WindowsEnabled $true

    $apps = @(
        @{ Path = 'ExampleWebAppModule'; Pool = $script:exampleWebAppPoolName; Anonymous = $true },
        @{ Path = 'ExampleWebAppBlazorModule'; Pool = $script:exampleWebAppBlazorPoolName; Anonymous = $true },
        @{ Path = 'ExampleServiceAppModule'; Pool = $script:exampleServiceWebAppPoolName; Anonymous = $true },
        @{ Path = 'ExampleWorkerAppModule'; Pool = $script:exampleWorkerWebAppPoolName; Anonymous = $true },
        @{ Path = 'iFrameWebAppModule'; Pool = $script:iframeWebAppPoolName; Anonymous = $true }
    )

    foreach ($app in $apps) {
        $physicalPath = Join-Path $script:webAppsRoot $app.Path
        Ensure-IisWebApplication `
            -AppPath $app.Path `
            -PhysicalPath $physicalPath `
            -AppPoolName $app.Pool `
            -AnonymousEnabled ([bool]$app.Anonymous)
    }

    if (-not $SkipOpenDocViewer) {
        $odvPhysicalPath = Join-Path $script:webAppsRoot $OpenDocViewerAppPath
        if (Test-Path -LiteralPath $odvPhysicalPath) {
            Ensure-IisWebApplication `
                -AppPath $OpenDocViewerAppPath `
                -PhysicalPath $odvPhysicalPath `
                -AppPoolName $script:openDocViewerAppPoolName `
                -AnonymousEnabled $true
        }
    }

    foreach ($pool in $script:deploymentStartAppPoolNames) {
        if (Test-IisAppPool -Name $pool) {
            Invoke-AppCmdOptional start apppool "/apppool.name:$pool"
        }
    }
}

$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$RuntimeRoot = [System.IO.Path]::GetFullPath($RuntimeRoot)
$script:publishRoot = Join-Path $RuntimeRoot 'Publish\OMP'
$script:webAppsRoot = Join-Path $RuntimeRoot 'WebApps'
$script:portalPath = Join-Path $RuntimeRoot 'Sites\Portal'
$script:authAppPath = Join-Path $RuntimeRoot 'WebApps\auth'
$script:servicesRoot = Join-Path $RuntimeRoot 'Services'

Initialize-RunAsIdentity

try {
    Test-SqlIntegratedAccess
    $openDocViewerRoot = Resolve-OpenDocViewerRoot
    $openDocViewerDist = Publish-OpenDocViewer -OpenDocViewerRoot $openDocViewerRoot
    Publish-OpenModulePlatform
    Grant-RunAsRuntimeAccess
    Deploy-PublishedOutputs -OpenDocViewerDistPath $openDocViewerDist
    Write-ExampleRuntimeConfig
    Run-ExampleSql
    Ensure-RunAsDatabaseAccess
    Remove-LegacyAppPoolDatabaseUsers
    Ensure-IisExamples
    Ensure-RuntimeWindowsServices
    Ensure-ExampleWindowsService

    Write-Host ''
    Write-Host 'Local OMP modules and examples are installed.' -ForegroundColor Green
    Write-Host "Portal: http://localhost:$IisPort/"
    Write-Host "OMP Auth: http://localhost:$IisPort/auth/login"
    Write-Host "OpenDocViewer: http://localhost:$IisPort/$OpenDocViewerAppPath/"
    Write-Host "Example WebApp: http://localhost:$IisPort/ExampleWebAppModule/"
    Write-Host "Example Blazor WebApp: http://localhost:$IisPort/ExampleWebAppBlazorModule/"
    Write-Host "Example Service WebApp: http://localhost:$IisPort/ExampleServiceAppModule/"
    Write-Host "Example Worker WebApp: http://localhost:$IisPort/ExampleWorkerAppModule/"
    Write-Host "iFrame WebApp: http://localhost:$IisPort/iFrameWebAppModule/"
    if (-not $SkipExampleService) {
        Write-Host "Example Service: $script:exampleServiceName"
    }
    if (-not $SkipRuntimeServices) {
        Write-Host "HostAgent Service: $script:hostAgentServiceName"
        Write-Host "WorkerManager Service: $script:workerManagerServiceName"
    }
}
finally {
    Clear-RunAsIdentity
}
