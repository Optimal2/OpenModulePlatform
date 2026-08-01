# File: scripts/dev/test-content-webapp-local-install.ps1
#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$RuntimeRoot = 'E:\OMP',
    [string]$SqlServer = 'localhost',
    [string]$Database = 'OpenModulePlatform',
    [string]$AppInstanceId = '11111111-1111-1111-1111-111111111232',
    [string]$BaseUrl = 'http://localhost:8088',
    [ValidateRange(10, 300)][int]$MirrorTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)

    Write-Host ''
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

function Assert-TestCondition {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Write-Utf8NoBomFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    [System.IO.File]::WriteAllText(
        $Path,
        $Content,
        [System.Text.UTF8Encoding]::new($false))
}

function Get-ActiveHostAgentSettingsPath {
    param([Parameter(Mandatory = $true)][string]$ServicesRoot)

    $servicesRootFull = [System.IO.Path]::GetFullPath($ServicesRoot)
    $servicesRootPrefix = $servicesRootFull.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    $runningService = Get-CimInstance Win32_Service -ErrorAction Stop |
        Where-Object {
            $_.State -eq 'Running' -and
            ($_.Name -eq 'OMP.HostAgent' -or $_.Name -like 'OMP.HostAgent.*')
        } |
        Select-Object -First 1
    if ($null -eq $runningService) {
        throw 'No running OMP HostAgent Windows service was found.'
    }

    $executablePath = if ($runningService.PathName -match '^\s*"([^"]+)"') {
        $Matches[1]
    }
    else {
        ([string]$runningService.PathName -split '\s+--', 2)[0].Trim()
    }

    $executableFullPath = [System.IO.Path]::GetFullPath($executablePath)
    if (-not $executableFullPath.StartsWith(
            $servicesRootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The running HostAgent executable is outside the expected services root '$servicesRootFull'."
    }

    $settingsPath = Join-Path (Split-Path -Parent $executableFullPath) 'appsettings.Production.json'
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        throw "The active HostAgent settings file was not found: $settingsPath"
    }

    return [pscustomobject]@{
        ServiceName = [string]$runningService.Name
        SettingsPath = [System.IO.Path]::GetFullPath($settingsPath)
    }
}

function Test-FilesEqual {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    try {
        if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf) -or
            -not (Test-Path -LiteralPath $TargetPath -PathType Leaf)) {
            return $false
        }

        $sourceHash = (Get-FileHash -LiteralPath $SourcePath -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash -LiteralPath $TargetPath -Algorithm SHA256).Hash
        return $sourceHash -eq $targetHash
    }
    catch [System.IO.IOException] {
        return $false
    }
}

function Wait-TestCondition {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Condition,
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (& $Condition) {
            return
        }

        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Timed out after $TimeoutSeconds seconds while waiting for $Description."
}

$runtimeRootFull = [System.IO.Path]::GetFullPath($RuntimeRoot)
$servicesRoot = Join-Path $runtimeRootFull 'Services'
$reportsSourcePath = Join-Path $runtimeRootFull 'Data\ContentReports'
$pagesSourcePath = Join-Path $runtimeRootFull 'Data\ContentPages'
$reportsTargetPath = Join-Path $runtimeRootFull 'WebApps\content\App_Data\ContentReports'
$pagesTargetPath = Join-Path $runtimeRootFull 'WebApps\content\App_Data\ContentPages'
$reportFileName = 'content-test-status.json'
$htmlFileName = 'content-test-file.html'
$reportSourceFile = Join-Path $reportsSourcePath $reportFileName
$htmlSourceFile = Join-Path $pagesSourcePath $htmlFileName
$reportTargetFile = Join-Path $reportsTargetPath $reportFileName
$htmlTargetFile = Join-Path $pagesTargetPath $htmlFileName
$runId = [Guid]::NewGuid().ToString('D')
$staleTargetFile = Join-Path $reportsTargetPath "omp-content-mirror-stale-$runId.tmp"
$seedScript = Join-Path $PSScriptRoot 'seed-content-webapp-test-pages.ps1'
$probeProject = Join-Path $PSScriptRoot 'ContentWebAppRuntimeProbe\ContentWebAppRuntimeProbe.csproj'

Write-Step 'Preparing deterministic Content test data'
& $seedScript `
    -RuntimeRoot $runtimeRootFull `
    -SqlServer $SqlServer `
    -Database $Database `
    -AppInstanceId $AppInstanceId

Write-Step 'Verifying the active HostAgent mirror configuration'
$hostAgent = Get-ActiveHostAgentSettingsPath -ServicesRoot $servicesRoot
$settings = Get-Content -LiteralPath $hostAgent.SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$enabledMirrors = @($settings.HostAgent.FileMirrors | Where-Object { $_.IsEnabled -ne $false })

$expectedMirrors = @(
    @{ Source = $reportsSourcePath; Target = $reportsTargetPath },
    @{ Source = $pagesSourcePath; Target = $pagesTargetPath }
)
foreach ($expectedMirror in $expectedMirrors) {
    $matchingMirror = @(
        $enabledMirrors | Where-Object {
            [System.IO.Path]::GetFullPath(([string]$_.SourcePath).Trim()) -eq
                [System.IO.Path]::GetFullPath($expectedMirror.Source) -and
            [System.IO.Path]::GetFullPath(([string]$_.TargetPath).Trim()) -eq
                [System.IO.Path]::GetFullPath($expectedMirror.Target) -and
            $_.DeleteStaleTargetEntries -ne $false
        }
    )
    Assert-TestCondition `
        -Condition ($matchingMirror.Count -eq 1) `
        -Message "Expected exactly one enabled HostAgent mirror from '$($expectedMirror.Source)' to '$($expectedMirror.Target)'."
}

Write-Step 'Forcing an observable HTML and server-report update'
$htmlContent = Get-Content -LiteralPath $htmlSourceFile -Raw -Encoding UTF8
$htmlContent = [regex]::Replace(
    $htmlContent,
    '(?m)^<!-- OMP HostAgent sync marker: .* -->\r?\n?',
    '')
$htmlContent = "<!-- OMP HostAgent sync marker: $runId -->`r`n$htmlContent"
Write-Utf8NoBomFile -Path $htmlSourceFile -Content $htmlContent

$report = Get-Content -LiteralPath $reportSourceFile -Raw -Encoding UTF8 | ConvertFrom-Json
$markerProperty = $report.PSObject.Properties['_ompHostAgentSyncMarker']
if ($null -eq $markerProperty) {
    $report | Add-Member -NotePropertyName '_ompHostAgentSyncMarker' -NotePropertyValue $runId
}
else {
    $markerProperty.Value = $runId
}
Write-Utf8NoBomFile `
    -Path $reportSourceFile `
    -Content (($report | ConvertTo-Json -Depth 20) + [Environment]::NewLine)

New-Item -ItemType Directory -Path $reportsTargetPath -Force | Out-Null
Write-Utf8NoBomFile -Path $staleTargetFile -Content "stale mirror probe $runId"

try {
    Wait-TestCondition `
        -Description 'HostAgent to copy the HTML and server-report files byte-for-byte' `
        -TimeoutSeconds $MirrorTimeoutSeconds `
        -Condition {
            (Test-FilesEqual -SourcePath $htmlSourceFile -TargetPath $htmlTargetFile) -and
            (Test-FilesEqual -SourcePath $reportSourceFile -TargetPath $reportTargetFile)
        }
    Wait-TestCondition `
        -Description 'HostAgent to delete a stale target-only file' `
        -TimeoutSeconds $MirrorTimeoutSeconds `
        -Condition { -not (Test-Path -LiteralPath $staleTargetFile) }

    Assert-TestCondition `
        -Condition ((Get-Content -LiteralPath $htmlTargetFile -Raw -Encoding UTF8).Contains($runId)) `
        -Message 'The mirrored HTML file did not contain this test run sync marker.'
    $mirroredReport = Get-Content -LiteralPath $reportTargetFile -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-TestCondition `
        -Condition ($mirroredReport._ompHostAgentSyncMarker -eq $runId) `
        -Message 'The mirrored server-report JSON did not contain this test run sync marker.'
}
finally {
    if (Test-Path -LiteralPath $staleTargetFile -PathType Leaf) {
        Remove-Item -LiteralPath $staleTargetFile -Force
    }
}

Write-Step 'Executing the Content renderer against the installed files and database'
& dotnet run `
    --project $probeProject `
    --configuration Release `
    --no-launch-profile `
    -- `
    --runtime-root $runtimeRootFull `
    --sql-server $SqlServer `
    --database $Database `
    --app-instance-id $AppInstanceId
if ($LASTEXITCODE -ne 0) {
    throw "Content runtime probe failed with exit code $LASTEXITCODE."
}

Write-Step 'Checking the installed IIS route'
$pageUrl = "$($BaseUrl.TrimEnd('/'))/content/test-html-file"
$httpHandler = [System.Net.Http.HttpClientHandler]::new()
$httpHandler.AllowAutoRedirect = $false
$httpClient = [System.Net.Http.HttpClient]::new($httpHandler)
$httpClient.Timeout = [TimeSpan]::FromSeconds(20)
try {
    $response = $httpClient.GetAsync($pageUrl).GetAwaiter().GetResult()
    $contentRouteStatus = [int]$response.StatusCode
    $response.Dispose()
}
finally {
    $httpClient.Dispose()
    $httpHandler.Dispose()
}
Assert-TestCondition `
    -Condition ($contentRouteStatus -in @(200, 302, 401, 403)) `
    -Message "Content route returned unexpected HTTP status $contentRouteStatus."

Write-Host ''
Write-Host 'Content Web App local installation test passed.' -ForegroundColor Green
[pscustomobject]@{
    Status = 'PASS'
    HostAgentService = $hostAgent.ServiceName
    HostAgentSettings = $hostAgent.SettingsPath
    SyncMarker = $runId
    HtmlSource = $htmlSourceFile
    HtmlTarget = $htmlTargetFile
    ServerReportSource = $reportSourceFile
    ServerReportTarget = $reportTargetFile
    ContentRoute = $pageUrl
    ContentRouteStatus = $contentRouteStatus
} | Format-List
