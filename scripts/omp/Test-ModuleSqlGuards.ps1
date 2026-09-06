#requires -Version 5.1
<#
.SYNOPSIS
Validates portable module SQL with the production validator, without a database connection.
.DESCRIPTION
Enforces OMP-MODULE-SQL-CONFIG-OWNERSHIP and the existing module SQL guards.
Defaults to the module definitions declared in omp-components.json, including decoded
base64-utf8 content. Explicit SQL paths use the portable USE-header transformation.
Unknown encodings, missing payloads, parse errors and blocked writes fail the command.
.PARAMETER Path
SQL files or module-definition JSON files to validate.
.PARAMETER RepositoryRoot
Repository containing omp-components.json. Defaults to this repository.
.PARAMETER NoBuild
Use the Release validator already built by the solution build.
.PARAMETER SelfTest
Run ownership regression probes against temporary SQL files before validating inputs.
#>
[CmdletBinding()]
param(
    [string[]]$Path = @(),
    [string]$RepositoryRoot = '',
    [switch]$NoBuild,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ompRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $ompRoot 'tools\ModuleSqlGuard\ModuleSqlGuard.csproj'
$validator = Join-Path $ompRoot 'tools\ModuleSqlGuard\bin\Release\net10.0\ModuleSqlGuard.dll'

if (-not $NoBuild) {
    & dotnet build $project --configuration Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Module SQL validator build failed ($LASTEXITCODE)." }
}
if (-not (Test-Path -LiteralPath $validator -PathType Leaf)) { throw 'Module SQL validator is missing. Build it first.' }

if ($SelfTest) {
    $probeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('module-sql-guards-' + [Guid]::NewGuid().ToString('N'))
    [void][System.IO.Directory]::CreateDirectory($probeRoot)
    $probeFiles = @()
    try {
        $tables = @('ArtifactConfigurationFiles', 'ConfigOverlayDocuments', 'ConfigOverlayConfigurationFiles')
        $statements = @(
            'BULK INSERT omp.{0} FROM ''ownership-probe.csv'';'
            'BULK INSERT [omp].[{0}] FROM ''ownership-probe.csv'';'
            'BULK INSERT "omp"."{0}" FROM ''ownership-probe.csv'';'
            'BULK INSERT {0} FROM ''ownership-probe.csv'';'
            'INSERT BULK omp.{0} (Id int);'
            'INSERT BULK [omp].[{0}] (Id int);'
            'INSERT BULK "omp"."{0}" (Id int);'
            'INSERT BULK {0} (Id int);'
            'EXEC(N''INSERT BULK omp.{0} (Id int);'');'
            'CREATE PROCEDURE module.ChangeConfig AS INSERT BULK omp.{0} (Id int);'
            'EXEC sp_rename ''omp.{0}'', ''Renamed'';'
            'EXECUTE sys.sp_rename N''[omp].[{0}]'', N''Renamed'';'
            'EXEC dbo.sp_rename ''"omp"."{0}"'', ''Renamed'';'
            'eXeCuTe [sys].[Sp_ReNaMe] N''OMP.{0}'', N''Renamed'';'
            'EXEC sp_rename ''{0}'', ''Renamed'';'
            'EXEC sp_rename ''omp.{0}.Content'', ''Renamed'', ''COLUMN'';'
            'EXEC sp_rename ''{0}.Content'', ''Renamed'', ''COLUMN'';'
            'EXEC sp_rename ''[omp].[{0}].[Content.With.Dot]'', ''Renamed'', ''COLUMN'';'
            'EXEC sp_rename ''localdb.omp.{0}.Content'', ''Renamed'', ''COLUMN'';'
            'EXEC sys.sp_rename @newname = N''Renamed'', @objname = N''omp.{0}'', @objtype = N''OBJECT'';'
            'EXEC sp_rename @OBJNAME = N''omp.{0}.Content'', @NEWNAME = N''Renamed'', @OBJTYPE = N''COLUMN'';'
            'CREATE PROCEDURE module.ChangeConfig AS EXEC sp_rename ''omp.{0}'', ''Renamed'';'
            'CREATE PROCEDURE module.ChangeConfig AS EXEC sp_rename ''omp.{0}.Content'', ''Renamed'', ''COLUMN'';'
            'EXEC(N''EXEC sp_rename ''''omp.{0}'''', ''''Renamed'''';'');'
            'CREATE TRIGGER module.ConfigProbe ON omp.{0} AFTER INSERT AS SELECT 1;'
            'ALTER TRIGGER module.ConfigProbe ON [omp].[{0}] AFTER UPDATE AS SELECT 1;'
            'CREATE OR ALTER TRIGGER module.ConfigProbe ON "omp"."{0}" INSTEAD OF DELETE AS SELECT 1;'
            'CREATE TRIGGER module.ConfigProbe ON {0} AFTER INSERT AS SELECT 1;'
            'EXEC(N''CREATE TRIGGER module.ConfigProbe ON omp.{0} AFTER INSERT AS SELECT 1;'');'
            'ALTER TABLE omp.{0} DROP COLUMN Content;'
            'ALTER TABLE [omp].[{0}] ALTER COLUMN Content nvarchar(max) NULL;'
            'ALTER TABLE "omp"."{0}" ADD Probe int NULL;'
            'ALTER TABLE {0} ADD CONSTRAINT CK_Probe CHECK (Id > 0);'
            'ALTER TABLE omp.{0} DROP CONSTRAINT CK_Probe;'
            'ALTER TABLE omp.{0} NOCHECK CONSTRAINT ALL;'
            'ALTER TABLE omp.{0} DISABLE TRIGGER ALL;'
            'ALTER TABLE omp.{0} SWITCH TO module.Settings;'
            'ALTER TABLE module.Settings SWITCH TO omp.{0};'
            'ALTER TABLE omp.{0} REBUILD;'
            'ALTER TABLE omp.{0} SET (LOCK_ESCALATION = TABLE);'
            'ALTER TABLE omp.{0} DISABLE FILETABLE_NAMESPACE;'
            'ALTER TABLE omp.{0} ENABLE CHANGE_TRACKING;'
            'ALTER TABLE omp.{0} SET (FILESTREAM_ON = "default");'
            'EXEC(N''ALTER TABLE omp.{0} DROP COLUMN Content;'');'
            'EXEC(N''BULK INSERT omp.{0} FROM ''''ownership-probe.csv'''';'');'
            'DECLARE @sql nvarchar(max) = N''ALTER TABLE omp.{0} DROP CONSTRAINT '' + QUOTENAME(@constraint); EXEC(@sql);'
            'CREATE PROCEDURE module.ChangeConfig AS ALTER TABLE omp.{0} DROP COLUMN Content;'
        )
        $blocked = @()
        foreach ($table in $tables) {
            foreach ($statement in $statements) {
                $blocked += $statement.Replace('{0}', $table) + [Environment]::NewLine + 'GO'
            }
        }
        $allowed = @(
            'INSERT BULK omp.ModuleArtifactConfigurationFilesLog (Id int);'
            'INSERT BULK module.ArtifactConfigurationFiles (Id int);'
            'EXEC sp_rename ''omp.ModuleArtifactConfigurationFilesLog'', ''Renamed'';'
            'EXEC sp_rename ''module.ArtifactConfigurationFiles'', ''Renamed'';'
            'EXEC sp_rename ''module.ArtifactConfigurationFiles.Content'', ''Renamed'', ''COLUMN'';'
            'EXEC sp_rename ''[module].[Settings].[ArtifactConfigurationFiles]'', ''Renamed'', ''COLUMN'';'
            'EXEC sp_rename ''module.Settings'', ''ArtifactConfigurationFiles'';'
            'EXEC module.sp_rename_log ''omp.ArtifactConfigurationFiles'', ''Renamed'';'
            'CREATE TRIGGER module.ConfigProbe ON module.ArtifactConfigurationFiles AFTER INSERT AS SELECT 1;'
            'ALTER TRIGGER module.ConfigProbe ON omp.ModuleArtifactConfigurationFilesLog AFTER INSERT AS SELECT 1;'
            'CREATE OR ALTER TRIGGER module.ConfigProbe ON module.Settings AFTER INSERT AS SELECT 1;'
            'CREATE TRIGGER module.DdlProbe ON DATABASE FOR CREATE_TABLE AS SELECT 1;'
            'PRINT N''INSERT BULK omp.ArtifactConfigurationFiles (Id int);'';'
            'PRINT N''EXEC sp_rename ''''omp.ArtifactConfigurationFiles'''', ''''Renamed'''';'';'
            'PRINT N''CREATE TRIGGER module.ConfigProbe ON omp.ArtifactConfigurationFiles AFTER INSERT AS SELECT 1;'';'
            'BULK INSERT omp.ModuleArtifactConfigurationFilesLog FROM ''ownership-probe.csv'';'
            'ALTER TABLE omp.ModuleArtifactConfigurationFilesLog DROP COLUMN Content;'
            'BULK INSERT module.ArtifactConfigurationFiles FROM ''ownership-probe.csv'';'
            'ALTER TABLE module.ArtifactConfigurationFiles ADD Probe int NULL;'
            'PRINT N''BULK INSERT omp.ArtifactConfigurationFiles FROM ''''ownership-probe.csv'''';'';'
            'PRINT N''ALTER TABLE omp.ArtifactConfigurationFiles DROP COLUMN Content;'';'
        )
        $probes = @(
            @{ Name = 'unresolved-rename-0'; Sql = 'EXEC sp_rename @unknown, ''Renamed'';'; Count = 1; Rule = 'OMP-MODULE-SQL-CONFIG-OWNERSHIP' }
            @{ Name = 'unresolved-rename-1'; Sql = 'DECLARE @name nvarchar(128) = N''module.Settings''; EXEC sys.sp_rename @name, ''Renamed'';'; Count = 1; Rule = 'OMP-MODULE-SQL-CONFIG-OWNERSHIP' }
            @{ Name = 'unresolved-rename-2'; Sql = 'EXEC sp_rename @newname = N''Renamed'', @objname = @unknown;'; Count = 1; Rule = 'OMP-MODULE-SQL-CONFIG-OWNERSHIP' }
            @{ Name = 'unresolved-rename-3'; Sql = 'CREATE PROCEDURE module.ChangeConfig @name sysname AS EXEC sp_rename @name, ''Renamed'';'; Count = 1; Rule = 'OMP-MODULE-SQL-CONFIG-OWNERSHIP' }
            @{ Name = 'unresolved-rename-4'; Sql = 'EXEC sp_rename;'; Count = 1; Rule = 'OMP-MODULE-SQL-CONFIG-OWNERSHIP' }
            @{ Name = 'unresolved-rename-5'; Sql = 'EXEC sp_rename N''[unterminated'', ''Renamed'';'; Count = 1; Rule = 'OMP-MODULE-SQL-CONFIG-OWNERSHIP' }
            @{ Name = 'owned-writes'; Sql = ($blocked -join [Environment]::NewLine); Count = $blocked.Count; Rule = 'OMP-MODULE-SQL-CONFIG-OWNERSHIP' }
            @{ Name = 'allowed'; Sql = ($allowed -join ([Environment]::NewLine + 'GO' + [Environment]::NewLine)); Count = 0; Rule = '' }
            @{ Name = 'legacy-truncate'; Sql = 'TRUNCATE TABLE omp.ArtifactConfigurationFiles;'; Count = 1; Rule = 'OMP-MODULE-SQL-GUARD' }
            @{ Name = 'legacy-drop'; Sql = 'DROP TABLE omp.ArtifactConfigurationFiles;'; Count = 1; Rule = 'OMP-MODULE-SQL-GUARD' }
        )
        foreach ($probe in $probes) {
            $probePath = Join-Path $probeRoot ($probe.Name + '.sql')
            $probeFiles += $probePath
            [System.IO.File]::WriteAllText($probePath, $probe.Sql)
            $output = & dotnet $validator --json $probePath
            $probeExit = $LASTEXITCODE
            $result = $output | ConvertFrom-Json
            $expectedExit = if ($probe.Count -eq 0) { 0 } else { 1 }
            if ($probeExit -ne $expectedExit -or @($result.diagnostics).Count -ne $probe.Count) {
                throw "Probe '$($probe.Name)' failed: exit $probeExit; $(@($result.diagnostics).Count) violations, expected $($probe.Count)."
            }
            foreach ($diagnostic in $result.diagnostics) {
                if ($diagnostic.RuleId -ne $probe.Rule) { throw "Probe '$($probe.Name)' returned the wrong rule." }
                if ($probe.Name -eq 'owned-writes') {
                    $tableIndex = [int][Math]::Floor(($diagnostic.Line - 1) / (2 * $statements.Count))
                    if ($diagnostic.Table -ne ('omp.' + $tables[$tableIndex])) {
                        throw "Probe '$($probe.Name)' did not resolve the owned table."
                    }
                }
            }
            Write-Host "PASS: $($probe.Name) ($($probe.Count) violations; exit $probeExit)"
        }
    }
    finally {
        foreach ($probePath in $probeFiles) { [System.IO.File]::Delete($probePath) }
        [System.IO.Directory]::Delete($probeRoot)
    }
}

$arguments = @()
if ($Path.Count -gt 0) {
    $arguments = $Path
}
else {
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = $ompRoot }
    $arguments = @('--repository', $RepositoryRoot)
}

& dotnet $validator @arguments
if ($LASTEXITCODE -ne 0) { throw "Module SQL validation failed ($LASTEXITCODE)." }
