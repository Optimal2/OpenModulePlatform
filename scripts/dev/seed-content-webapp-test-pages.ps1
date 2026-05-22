# File: scripts/dev/seed-content-webapp-test-pages.ps1
[CmdletBinding()]
param(
    [string]$RuntimeRoot = 'E:\OMP',
    [string]$SqlServer = 'localhost',
    [string]$Database = 'OpenModulePlatform',
    [string]$AppInstanceId = '11111111-1111-1111-1111-111111111232',
    [switch]$RunHostAgentOnce
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

function Write-Utf8NoBomFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    [System.IO.File]::WriteAllText(
        $Path,
        $Content,
        [System.Text.UTF8Encoding]::new($false))
}

function Set-JsonProperty {
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][object]$Value
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
        return
    }

    $property.Value = $Value
}

function Update-HostAgentFileMirrors {
    param(
        [Parameter(Mandatory = $true)][string]$HostAgentSettingsPath,
        [Parameter(Mandatory = $true)][string]$ReportsSourcePath,
        [Parameter(Mandatory = $true)][string]$ReportsTargetPath,
        [Parameter(Mandatory = $true)][string]$PagesSourcePath,
        [Parameter(Mandatory = $true)][string]$PagesTargetPath
    )

    if (-not (Test-Path -LiteralPath $HostAgentSettingsPath -PathType Leaf)) {
        throw "HostAgent settings file was not found: $HostAgentSettingsPath"
    }

    $json = Get-Content -LiteralPath $HostAgentSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $json.HostAgent) {
        throw "HostAgent settings file has no HostAgent section: $HostAgentSettingsPath"
    }

    $mirrors = @(
        [ordered]@{
            IsEnabled = $true
            SourcePath = $ReportsSourcePath
            TargetPath = $ReportsTargetPath
            DeleteStaleTargetEntries = $true
            ExcludedEntries = @()
        },
        [ordered]@{
            IsEnabled = $true
            SourcePath = $PagesSourcePath
            TargetPath = $PagesTargetPath
            DeleteStaleTargetEntries = $true
            ExcludedEntries = @()
        }
    )

    Set-JsonProperty -Object $json.HostAgent -Name 'FileMirrors' -Value $mirrors
    $json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $HostAgentSettingsPath -Encoding UTF8
}

function Invoke-LocalSqlFile {
    param(
        [Parameter(Mandatory = $true)][string]$SqlPath,
        [Parameter(Mandatory = $true)][string]$Server,
        [Parameter(Mandatory = $true)][string]$DatabaseName
    )

    & sqlcmd -S $Server -d $DatabaseName -E -b -i $SqlPath
    if ($LASTEXITCODE -ne 0) {
        throw "sqlcmd failed with exit code $LASTEXITCODE."
    }
}

$runtimeRootFull = [System.IO.Path]::GetFullPath($RuntimeRoot)
$reportsSourcePath = Join-Path $runtimeRootFull 'Data\ContentReports'
$pagesSourcePath = Join-Path $runtimeRootFull 'Data\ContentPages'
$reportsTargetPath = Join-Path $runtimeRootFull 'WebApps\content\App_Data\ContentReports'
$pagesTargetPath = Join-Path $runtimeRootFull 'WebApps\content\App_Data\ContentPages'
$hostAgentSettingsPath = Join-Path $runtimeRootFull 'Services\HostAgent\appsettings.Production.json'

Write-Step 'Writing shared Content test files'
$reportJson = @'
{
  "title": "Content Web App test report",
  "queries": [
    {
      "name": "content_pages",
      "title": "Content pages",
      "sql": "select top 50 slug, title, content_type, server_report_key from omp_content.contents order by slug",
      "renderer": "table",
      "maxRows": 50
    },
    {
      "name": "roles",
      "title": "Roles",
      "sql": "select top 20 Name as role_name from omp.Roles order by Name",
      "renderer": "table",
      "maxRows": 20
    }
  ]
}
'@
Write-Utf8NoBomFile -Path (Join-Path $reportsSourcePath 'content-test-status.json') -Content $reportJson

$htmlFile = @'
<section class="content-test-page">
  <h1>HTML file content test</h1>
  <p>This page is loaded from a mirrored HTML file and expands both table and JavaScript shortcodes.</p>

  [DB_JSON_SCRIPT="content-test-status"]

  <div class="content-test-summary" id="content-file-summary">Loading report rows...</div>

  <script>
    const rows = window.content_test_status || [];
    document.getElementById('content-file-summary').textContent =
      `${rows.length} flattened report row(s) were loaded from the default DB_JSON_SCRIPT variable.`;
  </script>

  [DB_JSON="content-test-status"]
</section>
'@
Write-Utf8NoBomFile -Path (Join-Path $pagesSourcePath 'content-test-file.html') -Content $htmlFile

Write-Step 'Configuring HostAgent file mirrors'
Update-HostAgentFileMirrors `
    -HostAgentSettingsPath $hostAgentSettingsPath `
    -ReportsSourcePath $reportsSourcePath `
    -ReportsTargetPath $reportsTargetPath `
    -PagesSourcePath $pagesSourcePath `
    -PagesTargetPath $pagesTargetPath

if ($RunHostAgentOnce) {
    Write-Step 'Running HostAgent once'
    $hostAgentExe = Join-Path $runtimeRootFull 'Services\HostAgent\OpenModulePlatform.HostAgent.WindowsService.exe'
    if (-not (Test-Path -LiteralPath $hostAgentExe -PathType Leaf)) {
        throw "HostAgent executable was not found: $hostAgentExe"
    }

    $hostAgentServiceName = 'OMP.HostAgent'
    $service = Get-Service -Name $hostAgentServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        $hostAgentServiceName = 'OpenModulePlatform.HostAgent'
        $service = Get-Service -Name $hostAgentServiceName -ErrorAction SilentlyContinue
    }

    $restartService = $false
    if ($null -ne $service -and $service.Status -eq 'Running') {
        Write-Host "Stopping $hostAgentServiceName before run-once to avoid concurrent IIS configuration writes."
        Stop-Service -Name $hostAgentServiceName
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        $restartService = $true
    }

    Push-Location (Split-Path -Parent $hostAgentExe)
    try {
        & $hostAgentExe --run-once
        if ($LASTEXITCODE -ne 0) {
            throw "HostAgent run-once failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
        if ($restartService) {
            Start-Service -Name $hostAgentServiceName
        }
    }
}

Write-Step 'Seeding Content test pages'
$sqlPath = Join-Path ([System.IO.Path]::GetTempPath()) "omp-content-test-pages-$([Guid]::NewGuid().ToString('N')).sql"
$sql = @"
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @AppInstanceId uniqueidentifier = '$AppInstanceId';
DECLARE @Actor nvarchar(256) = N'local-content-test-seed';

IF NOT EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = @AppInstanceId)
BEGIN
    THROW 53001, 'The configured Content app instance was not found.', 1;
END;

DECLARE @Pages table
(
    ContentId uniqueidentifier NOT NULL PRIMARY KEY,
    Slug nvarchar(256) NOT NULL,
    Title nvarchar(200) NOT NULL,
    ContentType nvarchar(20) NOT NULL,
    Body nvarchar(max) NOT NULL,
    ServerReportKey nvarchar(128) NULL,
    SortOrder int NOT NULL
);

INSERT INTO @Pages(ContentId, Slug, Title, ContentType, Body, ServerReportKey, SortOrder)
VALUES
(
    '22222222-2222-2222-2222-222222222301',
    N'test-markdown-shortcodes',
    N'Test: Markdown shortcodes',
    N'markdown',
    N'# Markdown shortcode test

This page validates the table shortcode and the escaped Markdown editor variant.

[DB_JSON="content-test-status"]

Some Markdown editors escape underscores. This should still render:

[DB\_JSON="content-test-status"]',
    NULL,
    910
),
(
    '22222222-2222-2222-2222-222222222302',
    N'test-html-shortcodes',
    N'Test: HTML shortcodes',
    N'html',
    N'<section class="content-test-page">
  <h1>Inline HTML shortcode test</h1>
  <p>This page validates inline HTML, table shortcodes, and explicit JavaScript variable names.</p>

  [DB_JSON_SCRIPT="content-test-status" variable="contentTestRows"]

  <div class="content-test-summary" id="content-inline-summary">Loading report rows...</div>
  <script>
    const rows = window.contentTestRows || [];
    document.getElementById(''content-inline-summary'').textContent =
      `${rows.length} flattened report row(s) were loaded through an explicit DB_JSON_SCRIPT variable.`;
  </script>

  [DB_JSON="content-test-status"]
</section>',
    NULL,
    920
),
(
    '22222222-2222-2222-2222-222222222303',
    N'test-html-file',
    N'Test: HTML file page',
    N'html',
    N'',
    N'content-test-file',
    930
),
(
    '22222222-2222-2222-2222-222222222304',
    N'test-server-report',
    N'Test: Server report page',
    N'server_report',
    N'',
    N'content-test-status',
    940
);

BEGIN TRANSACTION;

MERGE omp_content.contents AS target
USING @Pages AS source
ON target.content_id = source.ContentId
WHEN MATCHED THEN
    UPDATE SET
        app_instance_id = @AppInstanceId,
        slug = source.Slug,
        title = source.Title,
        content_type = source.ContentType,
        body = source.Body,
        server_report_key = source.ServerReportKey,
        is_enabled = 1,
        sort_order = source.SortOrder,
        updated_at = SYSUTCDATETIME(),
        updated_by = @Actor
WHEN NOT MATCHED THEN
    INSERT(content_id, app_instance_id, slug, title, content_type, body, server_report_key, is_enabled, sort_order, created_by, updated_by)
    VALUES(source.ContentId, @AppInstanceId, source.Slug, source.Title, source.ContentType, source.Body, source.ServerReportKey, 1, source.SortOrder, @Actor, @Actor);

DELETE accessRows
FROM omp_content.content_role_access accessRows
INNER JOIN @Pages pages ON pages.ContentId = accessRows.content_id;

INSERT INTO omp_content.content_role_access(content_id, role_id, can_read, can_write)
SELECT pages.ContentId,
       roles.RoleId,
       CAST(1 AS bit),
       CAST(CASE WHEN roles.Name = N'PortalAdmins' THEN 1 ELSE 0 END AS bit)
FROM @Pages pages
CROSS JOIN omp.Roles roles
WHERE roles.Name IN (N'PortalAdmins', N'Everyone', N'AuthenticatedUsers');

COMMIT TRANSACTION;
"@
Write-Utf8NoBomFile -Path $sqlPath -Content $sql
try {
    Invoke-LocalSqlFile -SqlPath $sqlPath -Server $SqlServer -DatabaseName $Database
}
finally {
    Remove-Item -LiteralPath $sqlPath -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'Content Web App test pages are ready.' -ForegroundColor Green
Write-Host "Reports source: $reportsSourcePath"
Write-Host "Pages source:   $pagesSourcePath"
Write-Host "Reports target: $reportsTargetPath"
Write-Host "Pages target:   $pagesTargetPath"
