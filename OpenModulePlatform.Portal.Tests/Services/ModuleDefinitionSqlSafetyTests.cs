using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class ModuleDefinitionSqlSafetyTests
{
    private static string? ValidateMaintenanceSql(string sql) => OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql);

    // Keep this maintenance matrix identical in all three executor suites.
    public static IEnumerable<object[]> ConfigurationMaintenanceSql(bool owned)
    {
        string[] tables = ["ArtifactConfigurationFiles", "ConfigOverlayDocuments", "ConfigOverlayConfigurationFiles"];
        string[] statements =
        [
            "ENABLE TRIGGER ALL ON {0};",
            "DISABLE TRIGGER module.ConfigProbe ON {0};",
            "ALTER INDEX IX_Probe ON {0} DISABLE;",
            "ALTER INDEX ALL ON {0} REBUILD;",
            "DROP INDEX IX_Probe ON {0};",
            "DROP INDEX {0}.IX_Probe;",
            "DROP INDEX IX_Other ON module.Settings, IX_Probe ON {0};",
            "UPDATE STATISTICS {0} WITH FULLSCAN;",
            "CREATE STATISTICS ST_Probe ON {0}(Id);",
            "DBCC DBREINDEX('{0}', '', 80);",
            "DBCC CLEANTABLE(localdb, '{0}', 100);",
            "DBCC CHECKTABLE('{0}');",
            "DBCC CHECKCONSTRAINTS('{0}');",
            "DBCC SHOW_STATISTICS('{0}', IX_Probe);",
        ];
        foreach (var table in tables)
        {
            string[] names = owned
                ? [$"omp.{table}", $"[omp].[{table}]", $"\"OMP\".\"{table.ToLowerInvariant()}\"", table, $"localdb.omp.{table}"]
                : [$"module.{table}", $"omp.Module{table}Log"];
            foreach (var name in names)
            foreach (var statement in statements)
            foreach (var sql in MaintenanceExecutionPaths(statement.Replace("{0}", name)))
                yield return [sql, table];
        }
    }

    [Theory]
    [InlineData("EXEC sp_updatestats;")]
    [InlineData("EXECUTE dbo.sp_updatestats;")]
    [InlineData("eXeCuTe [dbo].[Sp_UpdateStats];")]
    [InlineData("EXEC sp_createstats;")]
    [InlineData("EXECUTE dbo.sp_createstats;")]
    [InlineData("eXeCuTe [dbo].[Sp_CreateStats];")]
    public void ConfigurationOwnership_BlocksGlobalStatisticsMaintenance(string statement)
    {
        foreach (var sql in MaintenanceExecutionPaths(statement))
        {
            var error = ValidateMaintenanceSql(sql);
            Assert.NotNull(error);
            Assert.Contains("OMP-MODULE-SQL-CONFIG-OWNERSHIP", error);
            Assert.Contains("global statistics maintenance affects platform-owned tables", error);
        }
    }

    [Theory]
    [InlineData("DBCC DBREINDEX(@target);")]
    [InlineData("DBCC CLEANTABLE(localdb, @target);")]
    [InlineData("DBCC CHECKTABLE(@target);")]
    [InlineData("DBCC CHECKCONSTRAINTS(@target);")]
    [InlineData("DBCC SHOW_STATISTICS(@target, IX_Probe);")]
    [InlineData("DECLARE @target sysname = N'module.Settings'; DBCC CHECKTABLE(@target);")]
    [InlineData("DBCC CHECKTABLE(123);")]
    [InlineData("DBCC CHECKTABLE(N'[unterminated');")]
    public void ConfigurationOwnership_RejectsUnresolvedDbccTarget(string statement)
    {
        foreach (var sql in MaintenanceExecutionPaths(statement))
        {
            var error = ValidateMaintenanceSql(sql);
            Assert.NotNull(error);
            Assert.Contains("OMP-MODULE-SQL-CONFIG-OWNERSHIP", error);
            Assert.Contains("dynamic SQL payload cannot be resolved", error);
        }
    }

    [Theory]
    [InlineData("DBCC CHECKDB;")]
    [InlineData("DBCC CHECKDB(localdb);")]
    [InlineData("DBCC SHRINKFILE(module_data, 10);")]
    [InlineData("EXEC module.sp_updatestats_log;")]
    [InlineData("EXEC module.sp_createstats_log;")]
    [InlineData("PRINT N'EXEC sp_updatestats; DBCC CHECKTABLE(''omp.ConfigOverlayDocuments'');';")]
    public void ConfigurationOwnership_AllowsNonTableDbccAndStatisticsLookalikes(string statement)
    {
        foreach (var sql in MaintenanceExecutionPaths(statement))
            Assert.Null(ValidateMaintenanceSql(sql));
    }

    private static IEnumerable<string> MaintenanceExecutionPaths(string sql)
    {
        yield return sql;
        yield return $"EXEC(N'{sql.Replace("'", "''")}');";
        yield return $"EXEC sys.sp_executesql N'{sql.Replace("'", "''")}';";
        yield return $"CREATE PROCEDURE module.ChangeConfig AS {sql}";
    }

    [Theory]
    [MemberData(nameof(ConfigurationMaintenanceSql), true)]
    public void ConfigurationOwnership_BlocksOwnedTableMaintenance(string sql, string table)
    {
        var error = ValidateMaintenanceSql(sql);
        Assert.NotNull(error);
        Assert.Contains("OMP-MODULE-SQL-CONFIG-OWNERSHIP", error);
        Assert.Contains("Module definition SQL must not write omp." + table, error);
        Assert.Contains("configuration continuity is owned by the platform (line ", error);
    }

    [Theory]
    [MemberData(nameof(ConfigurationMaintenanceSql), false)]
    public void ConfigurationOwnership_AllowsModuleTableMaintenance(string sql, string table)
    {
        Assert.Contains(table, sql);
        Assert.Null(ValidateMaintenanceSql(sql));
    }

    [Theory]
    [InlineData("CREATE INDEX IX_Probe ON omp.ConfigOverlayDocuments(Id);")]
    [InlineData("CREATE UNIQUE INDEX IX_Probe ON omp.ConfigOverlayDocuments(Id);")]
    public void ConfigurationOwnership_AllowsCreateIndexByBootstrapDecision(string statement)
    {
        // CREATE INDEX is deliberately allowed: additive bootstrap DDL uses this gate.
        // Blocking it requires moving bootstrap DDL to the compiled migration path first.
        foreach (var sql in MaintenanceExecutionPaths(statement))
            Assert.Null(ValidateMaintenanceSql(sql));
    }


    [Theory]
    [InlineData("EXEC(N'UPDATE omp.ConfigOverlayDocuments SET Id = 2 WHERE Id = 1;');")]
    [InlineData("EXEC sys.sp_executesql N'DELETE FROM omp.ConfigOverlayDocuments WHERE Id = 1;';")]
    [InlineData("DECLARE @sql nvarchar(max) = N'UPDATE omp.ConfigOverlayDocuments SET Id = 2 WHERE Id = 1;'; EXEC(@sql);")]
    [InlineData("CREATE PROCEDURE module.ChangeConfig AS UPDATE omp.ConfigOverlayDocuments SET Id = 2 WHERE Id = 1;")]
    public void ConfigurationOwnership_BlocksIndirectConfigurationSql(string sql)
    {
        Assert.Contains("omp.ConfigOverlayDocuments", OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql)!);
    }

    [Theory]
    [InlineData("DECLARE @sql nvarchar(max); EXEC(@sql);")]
    [InlineData("EXEC sys.sp_executesql @unknown;")]
    [InlineData("DECLARE @sql nvarchar(max) = N'SELECT 1;'; SET @sql = @unknown; EXEC(@sql);")]
    [InlineData("DECLARE @sql nvarchar(max) = N'UPDATE omp.' + QUOTENAME(@table) + N' SET Id = 1;'; EXEC(@sql);")]
    [InlineData("EXEC sp_rename @unknown, 'Renamed';")]
    [InlineData("DECLARE @name nvarchar(128) = N'module.Settings'; EXEC sys.sp_rename @name, 'Renamed';")]
    [InlineData("EXEC sp_rename @newname = N'Renamed', @objname = @unknown;")]
    [InlineData("CREATE PROCEDURE module.ChangeConfig @name sysname AS EXEC sp_rename @name, 'Renamed';")]
    [InlineData("EXEC sp_rename;")]
    [InlineData("EXEC sp_rename N'[unterminated', 'Renamed';")]
    public void ConfigurationOwnership_RejectsUnresolvedDynamicSql(string sql)
    {
        Assert.Contains("OMP-MODULE-SQL-CONFIG-OWNERSHIP", OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql)!);
    }

    [Fact]
    public void ConfigurationOwnership_AllowsBoundedQuotedConstraintMaintenance()
    {
        const string sql = "DECLARE @sql nvarchar(max) = N'ALTER TABLE module.Settings DROP CONSTRAINT ' + QUOTENAME(@constraint); EXEC(@sql);";
        Assert.Null(OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql));
    }

    [Theory]
    [InlineData("example_module", "UPDATE omp.ConfigOverlayDocuments SET Id = 2 WHERE Id = 1;")]
    [InlineData("omp_core", "UPDATE omp.ConfigOverlayDocuments SET Id = 2 WHERE Id = 1;")]
    [InlineData("example_module", "BULK INSERT omp.ConfigOverlayDocuments FROM 'ownership-probe.csv';")]
    [InlineData("omp_core", "BULK INSERT omp.ConfigOverlayDocuments FROM 'ownership-probe.csv';")]
    [InlineData("example_module", "ALTER TABLE omp.ConfigOverlayDocuments DROP COLUMN Content;")]
    [InlineData("omp_core", "ALTER TABLE omp.ConfigOverlayDocuments DROP COLUMN Content;")]
    [InlineData("example_module", "INSERT BULK omp.ConfigOverlayDocuments (Id int);")]
    [InlineData("omp_core", "INSERT BULK omp.ConfigOverlayDocuments (Id int);")]
    [InlineData("example_module", "EXEC sp_rename 'omp.ConfigOverlayDocuments', 'Renamed';")]
    [InlineData("omp_core", "EXEC sp_rename 'omp.ConfigOverlayDocuments', 'Renamed';")]
    [InlineData("example_module", "EXEC sp_rename 'omp.ConfigOverlayDocuments.Content', 'Renamed', 'COLUMN';")]
    [InlineData("omp_core", "EXEC sp_rename 'omp.ConfigOverlayDocuments.Content', 'Renamed', 'COLUMN';")]
    [InlineData("example_module", "CREATE TRIGGER module.ConfigProbe ON omp.ConfigOverlayDocuments AFTER INSERT AS SELECT 1;")]
    [InlineData("omp_core", "CREATE TRIGGER module.ConfigProbe ON omp.ConfigOverlayDocuments AFTER INSERT AS SELECT 1;")]
    public void ConfigurationOwnership_DocumentPreflightNamesModuleAndTable(string moduleKey, string sql)
    {
        var document = System.Text.Json.JsonSerializer.Serialize(new
        {
            moduleKey,
            sqlScripts = new[] { new { key = "initialize", contentEncoding = "base64-utf8", content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sql)) } }
        });
        var method = typeof(OmpAdminRepository).GetMethod("ReadPortableSqlScripts",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, [document]));
        Assert.Contains(moduleKey, error.InnerException!.Message);
        Assert.Contains("omp.ConfigOverlayDocuments", error.InnerException.Message);
        Assert.Contains("OMP-MODULE-SQL-CONFIG-OWNERSHIP", error.InnerException.Message);
    }


    [Theory]
    [InlineData(null, "SELECT 1;", "rot13")]
    [InlineData("SELECT 1;", "SELECT 1;", "rot13")]
    [InlineData(null, "not-base64!", "base64-utf8")]
    [InlineData(null, "/w==", "base64-utf8")]
    [InlineData(null, null, null)]
    [InlineData("SELECT 1;", "SELECT 2;", "utf-8")]
    public void ConfigurationOwnership_RejectsUnknownOrInvalidPayload(string? inlineSql, string? content, string? encoding)
    {
        Assert.Throws<System.Reflection.TargetInvocationException>(() => ResolvePayload(inlineSql, content, encoding));
    }

    [Fact]
    public void ConfigurationOwnership_DecodesBase64BeforeValidation()
    {
        const string sql = "UPDATE omp.ConfigOverlayDocuments SET Id = 2 WHERE Id = 1;";
        var decoded = ResolvePayload(null, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sql)), "base64-utf8");
        Assert.Equal(sql, decoded);
        Assert.Contains("OMP-MODULE-SQL-CONFIG-OWNERSHIP", OmpAdminRepository.ValidateSafeModuleDefinitionSql(decoded!)!);
    }

    private static string? ResolvePayload(string? inlineSql, string? content, string? encoding)
    {
        var method = typeof(OmpAdminRepository).GetMethod("ResolvePortableSqlText",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var constructor = method.GetParameters()[0].ParameterType.GetConstructors().Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.Name?.ToLowerInvariant() switch
        {
            "inlinesql" => inlineSql,
            "content" => content,
            "contentencoding" => encoding,
            _ => parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null
        }).ToArray();
        return (string?)method.Invoke(null, [constructor.Invoke(arguments)]);
    }


    // Identical configuration-ownership regression matrix in all three execution-path suites.
    public static IEnumerable<object[]> ConfigurationLayerWrites()
    {
        string[] tables = ["ArtifactConfigurationFiles", "ConfigOverlayDocuments", "ConfigOverlayConfigurationFiles"];
        string[] statements =
        [
            "INSERT INTO omp.{0}(Id) VALUES(1);",
            "INSERT omp.{0} VALUES(1);",
            "UPDATE omp.{0} SET Id = 2 WHERE Id = 1;",
            "DELETE FROM omp.{0} WHERE Id = 1;",
            "DELETE c FROM omp.{0} c WHERE c.Id = 1;",
            "MERGE omp.{0} AS t USING (SELECT 1 AS Id) s ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET Id = s.Id;",
            "UPDATE c SET Id = 2 FROM omp.{0} AS c WHERE c.Id = 1;",
            "UPDATE c SET Id = 2 FROM omp.Apps a JOIN omp.{0} c ON c.Id = a.AppId WHERE c.Id = 1;",
            ";WITH c AS (SELECT Id FROM omp.{0}) UPDATE c SET Id = 2 WHERE Id = 1;",
            ";WITH c AS (SELECT Id FROM omp.{0}) DELETE c WHERE Id = 1;",
            ";WITH c AS (SELECT Id FROM omp.{0}), d AS (SELECT Id FROM c) UPDATE d SET Id = 2 WHERE Id = 1;",
            "UPDATE omp.Apps SET DisplayName = N'x' OUTPUT inserted.AppId INTO omp.{0}(Id) WHERE AppId = 1;",
            "INSERT INTO [omp] /* ownership */ . [{0}] DEFAULT VALUES;",
            "UPDATE \"omp\".\"{0}\" SET Id = 2 WHERE Id = 1;",
            "SELECT AppId INTO omp.{0} FROM omp.Apps;",
            "BULK INSERT omp.{0} FROM 'ownership-probe.csv';",
            "BULK INSERT [omp].[{0}] FROM 'ownership-probe.csv';",
            "BULK INSERT \"omp\".\"{0}\" FROM 'ownership-probe.csv';",
            "BULK INSERT {0} FROM 'ownership-probe.csv';",
            "INSERT BULK omp.{0} (Id int);",
            "INSERT BULK [omp].[{0}] (Id int);",
            "INSERT BULK \"omp\".\"{0}\" (Id int);",
            "INSERT BULK {0} (Id int);",
            "EXEC(N'INSERT BULK omp.{0} (Id int);');",
            "CREATE PROCEDURE module.ChangeConfig AS INSERT BULK omp.{0} (Id int);",
            "EXEC sp_rename 'omp.{0}', 'Renamed';",
            "EXECUTE sys.sp_rename N'[omp].[{0}]', N'Renamed';",
            "EXEC dbo.sp_rename '\"omp\".\"{0}\"', 'Renamed';",
            "eXeCuTe [sys].[Sp_ReNaMe] N'OMP.{0}', N'Renamed';",
            "EXEC sp_rename '{0}', 'Renamed';",
            "EXEC sp_rename 'omp.{0}.Content', 'Renamed', 'COLUMN';",
            "EXEC sp_rename '{0}.Content', 'Renamed', 'COLUMN';",
            "EXEC sp_rename '[omp].[{0}].[Content.With.Dot]', 'Renamed', 'COLUMN';",
            "EXEC sp_rename 'localdb.omp.{0}.Content', 'Renamed', 'COLUMN';",
            "EXEC sys.sp_rename @newname = N'Renamed', @objname = N'omp.{0}', @objtype = N'OBJECT';",
            "EXEC sp_rename @OBJNAME = N'omp.{0}.Content', @NEWNAME = N'Renamed', @OBJTYPE = N'COLUMN';",
            "CREATE PROCEDURE module.ChangeConfig AS EXEC sp_rename 'omp.{0}', 'Renamed';",
            "CREATE PROCEDURE module.ChangeConfig AS EXEC sp_rename 'omp.{0}.Content', 'Renamed', 'COLUMN';",
            "EXEC(N'EXEC sp_rename ''omp.{0}'', ''Renamed'';');",
            "CREATE TRIGGER module.ConfigProbe ON omp.{0} AFTER INSERT AS SELECT 1;",
            "ALTER TRIGGER module.ConfigProbe ON [omp].[{0}] AFTER UPDATE AS SELECT 1;",
            "CREATE OR ALTER TRIGGER module.ConfigProbe ON \"omp\".\"{0}\" INSTEAD OF DELETE AS SELECT 1;",
            "CREATE TRIGGER module.ConfigProbe ON {0} AFTER INSERT AS SELECT 1;",
            "EXEC(N'CREATE TRIGGER module.ConfigProbe ON omp.{0} AFTER INSERT AS SELECT 1;');",
            "ALTER TABLE omp.{0} DROP COLUMN Content;",
            "ALTER TABLE [omp].[{0}] ALTER COLUMN Content nvarchar(max) NULL;",
            "ALTER TABLE \"omp\".\"{0}\" ADD Probe int NULL;",
            "ALTER TABLE {0} ADD CONSTRAINT CK_Probe CHECK (Id > 0);",
            "ALTER TABLE omp.{0} DROP CONSTRAINT CK_Probe;",
            "ALTER TABLE omp.{0} NOCHECK CONSTRAINT ALL;",
            "ALTER TABLE omp.{0} DISABLE TRIGGER ALL;",
            "ALTER TABLE omp.{0} SWITCH TO module.Settings;",
            "ALTER TABLE module.Settings SWITCH TO omp.{0};",
            "ALTER TABLE omp.{0} REBUILD;",
            "ALTER TABLE omp.{0} SET (LOCK_ESCALATION = TABLE);",
            "ALTER TABLE omp.{0} DISABLE FILETABLE_NAMESPACE;",
            "ALTER TABLE omp.{0} ENABLE CHANGE_TRACKING;",
            "ALTER TABLE omp.{0} SET (FILESTREAM_ON = \"default\");",
            "EXEC(N'ALTER TABLE omp.{0} DROP COLUMN Content;');",
            "EXEC(N'BULK INSERT omp.{0} FROM ''ownership-probe.csv'';');",
            "DECLARE @sql nvarchar(max) = N'ALTER TABLE omp.{0} DROP CONSTRAINT ' + QUOTENAME(@constraint); EXEC(@sql);",
            "CREATE PROCEDURE module.ChangeConfig AS ALTER TABLE omp.{0} DROP COLUMN Content;",
        ];
        foreach (var table in tables)
        {
            foreach (var statement in statements)
            {
                yield return [statement.Replace("{0}", table), table];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ConfigurationLayerWrites))]
    public void ConfigurationOwnership_BlocksPlatformConfigurationWrites(string sql, string table)
    {
        var error = OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql);
        Assert.NotNull(error);
        Assert.Contains("OMP-MODULE-SQL-CONFIG-OWNERSHIP", error);
        Assert.Contains("omp." + table, error);
    }

    [Theory]
    [InlineData("SELECT * FROM omp.ArtifactConfigurationFiles;")]
    [InlineData("SELECT N'UPDATE omp.ConfigOverlayDocuments SET Id = 1'; -- DELETE FROM omp.ConfigOverlayConfigurationFiles")]
    [InlineData("UPDATE module.Settings SET Value = N'x' WHERE Id = 1;")]
    [InlineData("BULK INSERT omp.ModuleArtifactConfigurationFilesLog FROM 'ownership-probe.csv';")]
    [InlineData("ALTER TABLE omp.ModuleArtifactConfigurationFilesLog DROP COLUMN Content;")]
    [InlineData("BULK INSERT module.ArtifactConfigurationFiles FROM 'ownership-probe.csv';")]
    [InlineData("ALTER TABLE module.ArtifactConfigurationFiles ADD Probe int NULL;")]
    [InlineData("PRINT N'BULK INSERT omp.ArtifactConfigurationFiles FROM ''ownership-probe.csv'';';")]
    [InlineData("PRINT N'ALTER TABLE omp.ArtifactConfigurationFiles DROP COLUMN Content;';")]
    [InlineData("INSERT BULK omp.ModuleArtifactConfigurationFilesLog (Id int);")]
    [InlineData("INSERT BULK module.ArtifactConfigurationFiles (Id int);")]
    [InlineData("EXEC sp_rename 'omp.ModuleArtifactConfigurationFilesLog', 'Renamed';")]
    [InlineData("EXEC sp_rename 'module.ArtifactConfigurationFiles', 'Renamed';")]
    [InlineData("EXEC sp_rename 'module.ArtifactConfigurationFiles.Content', 'Renamed', 'COLUMN';")]
    [InlineData("EXEC sp_rename '[module].[Settings].[ArtifactConfigurationFiles]', 'Renamed', 'COLUMN';")]
    [InlineData("EXEC sp_rename 'module.Settings', 'ArtifactConfigurationFiles';")]
    [InlineData("EXEC module.sp_rename_log 'omp.ArtifactConfigurationFiles', 'Renamed';")]
    [InlineData("CREATE TRIGGER module.ConfigProbe ON module.ArtifactConfigurationFiles AFTER INSERT AS SELECT 1;")]
    [InlineData("ALTER TRIGGER module.ConfigProbe ON omp.ModuleArtifactConfigurationFilesLog AFTER INSERT AS SELECT 1;")]
    [InlineData("CREATE OR ALTER TRIGGER module.ConfigProbe ON module.Settings AFTER INSERT AS SELECT 1;")]
    [InlineData("CREATE TRIGGER module.DdlProbe ON DATABASE FOR CREATE_TABLE AS SELECT 1;")]
    [InlineData("PRINT N'INSERT BULK omp.ArtifactConfigurationFiles (Id int);';")]
    [InlineData("PRINT N'EXEC sp_rename ''omp.ArtifactConfigurationFiles'', ''Renamed'';';")]
    [InlineData("PRINT N'CREATE TRIGGER module.ConfigProbe ON omp.ArtifactConfigurationFiles AFTER INSERT AS SELECT 1;';")]
    public void ConfigurationOwnership_AllowsReadsLiteralsAndModuleWrites(string sql)
    {
        Assert.Null(OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql));
    }

    [Theory]
    [InlineData("UPDATE [omp].[ConfigOverlayDocuments SET Id = 1;")]
    [InlineData("SELECT 'unterminated")]
    public void ConfigurationOwnership_RejectsUnparseableSql(string sql)
    {
        Assert.NotNull(OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql));
    }

    [Theory]
    [InlineData("TRUNCATE TABLE [omp].[ArtifactConfigurationFiles];", "TRUNCATE TABLE")]
    [InlineData("DROP TABLE [omp].[ArtifactConfigurationFiles];", "DROP TABLE")]
    [InlineData("TRUNCATE /* comment */ TABLE omp.ConfigOverlayDocuments;", "TRUNCATE TABLE")]
    [InlineData("DROP /* comment */ TABLE omp.ConfigOverlayConfigurationFiles;", "DROP TABLE")]
    public void ConfigurationOwnership_LegacyGuardsBlockDestructiveTableStatements(string sql, string operation)
    {
        Assert.Contains(operation, OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql)!);
    }

    private const string ArtifactWriteMessage =
        "Module definition SQL must not register or mutate omp.Artifacts; artifact registration is owned by the artifact import path.";

    private const string ArtifactPointerWriteMessage =
        "Module definition SQL must not write omp.InstanceTemplateAppInstances.DesiredArtifactId or omp.AppInstances.ArtifactId; artifact selection is owned by artifact auto-apply.";

    public static TheoryData<string, string> BlockedSql => new()
    {
        { "INSERT INTO omp.Artifacts(AppId, Version) VALUES(1, N'1.0.0');", ArtifactWriteMessage },
        { "UPDATE [omp].[Artifacts] SET IsEnabled = 0 WHERE ArtifactId = 1;", ArtifactWriteMessage },
        { "UPDATE artifact SET IsEnabled = 0 FROM omp.Artifacts AS artifact WHERE artifact.ArtifactId = 1;", ArtifactWriteMessage },
        { "INSERT INTO \"omp\".\"Artifacts\"(AppId, Version) VALUES(1, N'1.0.0');", ArtifactWriteMessage },
        { "UPDATE TOP (10) omp.Artifacts SET IsEnabled = 0 WHERE ArtifactId > 0;", ArtifactWriteMessage },
        { "INSERT TOP (1) INTO omp.Artifacts(AppId, Version) VALUES(1, N'1.0.0');", ArtifactWriteMessage },
        { "MERGE TOP (1) INTO omp.Artifacts AS target USING (SELECT 1 AS AppId) source ON 1 = 0 WHEN NOT MATCHED THEN INSERT(AppId) VALUES(source.AppId);", ArtifactWriteMessage },
        { "MERGE INTO [omp].Artifacts AS target USING (SELECT 1 AS AppId) source ON 1 = 0 WHEN NOT MATCHED THEN INSERT(AppId) VALUES(source.AppId);", ArtifactWriteMessage },
        { "INSERT INTO omp.AppInstances(AppId, ArtifactId) VALUES(1, 2);", ArtifactPointerWriteMessage },
        { "UPDATE omp.AppInstances SET ArtifactId = 2 WHERE AppInstanceId = NEWID();", ArtifactPointerWriteMessage },
        { "UPDATE ai SET ArtifactId = 2 FROM omp.AppInstances AS ai WHERE ai.AppInstanceId = NEWID();", ArtifactPointerWriteMessage },
        { "INSERT INTO [omp].[InstanceTemplateAppInstances](AppId, [DesiredArtifactId]) VALUES(1, 2);", ArtifactPointerWriteMessage },
        { "INSERT INTO \"omp\".\"AppInstances\"(AppId, \"ArtifactId\") VALUES(1, 2);", ArtifactPointerWriteMessage },
        { "INSERT INTO omp.InstanceTemplateAppInstances WITH (TABLOCK) (AppId, DesiredArtifactId) VALUES(1, 2);", ArtifactPointerWriteMessage },
        { "UPDATE TOP (10) omp.AppInstances WITH (UPDLOCK) SET ArtifactId = 2 WHERE AppInstanceId = NEWID();", ArtifactPointerWriteMessage },
        { "MERGE omp.InstanceTemplateAppInstances AS target USING (SELECT 1 AS AppId) source ON 1 = 0 WHEN MATCHED THEN UPDATE SET DesiredArtifactId = 2;", ArtifactPointerWriteMessage }
    };

    [Theory]
    [MemberData(nameof(BlockedSql))]
    public void PlatformRuntimeValidators_BlockArtifactOwnedWrites(string sql, string expectedMessage)
    {
        Assert.Equal(expectedMessage, OmpHostArtifactRepository.ValidateSafeModuleDefinitionSql(sql));
        Assert.Equal(expectedMessage, OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql));
    }

    // Probe batch for the 2026-09 hardening round. Every blocked case below was first
    // verified RED against the pre-fix validators (each one passed all three mirrors),
    // then the hole was closed. Keep this batch identical to the Bootstrapper and
    // HostAgent suites so a divergence between the mirrors shows up as a failing test.
    public static TheoryData<string, string> HardenedBlockedSql => new()
    {
        // Family 4: OUTPUT INTO writes rows without INSERT/UPDATE/MERGE in front of the table.
        { "UPDATE omp.AppInstances SET DisplayName = N'ghost' OUTPUT inserted.AppInstanceId INTO omp.Artifacts(AppId) WHERE AppInstanceId = NEWID();", ArtifactWriteMessage },
        // Family 2: a CTE over the table can be the DML target; SQL Server writes the base table.
        { ";WITH c AS (SELECT ArtifactId, IsEnabled FROM omp.Artifacts) UPDATE c SET IsEnabled = 0 WHERE ArtifactId = 1;", ArtifactWriteMessage },
        // Family 9: modules must not delete artifact rows either.
        { "DELETE FROM omp.Artifacts WHERE Version = N'1.0';", ArtifactWriteMessage },
        { "DELETE omp.Artifacts WHERE ArtifactId = 1;", ArtifactWriteMessage },
        { ";WITH c AS (SELECT ArtifactId FROM omp.Artifacts) DELETE FROM c WHERE ArtifactId = 1;", ArtifactWriteMessage },
        // Family 9, second round (independent review 2026-09-02 ran these ALLOWED against the
        // first hardening pass — a fix against the test, not against the hole):
        // the DELETE guard required the table right after DELETE/FROM, so the alias form slipped
        // through, and the CTE guard demanded DELETE **FROM** <cte> while `DELETE <cte>` is
        // equally valid T-SQL and writes the base table.
        { "DELETE a FROM omp.Artifacts AS a WHERE a.ArtifactId = 1;", ArtifactWriteMessage },
        { "DELETE a FROM omp.Artifacts a WHERE 1 = 1;", ArtifactWriteMessage },
        { "DELETE a FROM omp.Artifacts a JOIN omp.AppInstances i ON i.ArtifactId = a.ArtifactId WHERE 1 = 1;", ArtifactWriteMessage },
        { "DELETE TOP (1) a FROM omp.Artifacts a WHERE 1 = 1;", ArtifactWriteMessage },
        { ";WITH c AS (SELECT ArtifactId FROM omp.Artifacts) DELETE c WHERE 1 = 1;", ArtifactWriteMessage },
        // Mirror divergence: GO with a repeat count must still split batches everywhere.
        { "CREATE PROCEDURE omp.GhostProc AS BEGIN SELECT 1; END\nGO 2\nINSERT INTO omp.Artifacts(AppId) VALUES(1);", ArtifactWriteMessage },
        // Family 5: positional INSERT has no column list to scan.
        { "INSERT INTO omp.InstanceTemplateAppInstances VALUES(1, 1, 5);", ArtifactPointerWriteMessage },
        { "INSERT INTO omp.AppInstances DEFAULT VALUES;", ArtifactPointerWriteMessage },
        { "MERGE omp.InstanceTemplateAppInstances AS target USING (SELECT 1 AS AppId) source ON 1 = 0 WHEN NOT MATCHED THEN INSERT VALUES(1, 1, 5);", ArtifactPointerWriteMessage },
        // Family 6: compound assignment.
        { "UPDATE omp.AppInstances SET ArtifactId += 1 WHERE AppInstanceId = NEWID();", ArtifactPointerWriteMessage },
        { "UPDATE omp.AppInstances SET [ArtifactId] -= 1 WHERE AppInstanceId = NEWID();", ArtifactPointerWriteMessage },
        // Family 2: CTE write against an artifact pointer column.
        { ";WITH c AS (SELECT InstanceTemplateAppInstanceId, DesiredArtifactId FROM omp.InstanceTemplateAppInstances) UPDATE c SET DesiredArtifactId = 999 WHERE InstanceTemplateAppInstanceId = 1;", ArtifactPointerWriteMessage },
        // Family 7: the assignment scan must not stop inside a subquery, at a parameter
        // named @Where, or at the WHEN of a CASE expression.
        { "UPDATE omp.AppInstances SET DisplayName = (SELECT TOP 1 name FROM sys.tables WHERE 1 = 1), ArtifactId = 5 WHERE AppInstanceId = NEWID();", ArtifactPointerWriteMessage },
        { "UPDATE omp.AppInstances SET DisplayName = @Where, ArtifactId = 5 WHERE AppInstanceId = NEWID();", ArtifactPointerWriteMessage },
        { "MERGE omp.InstanceTemplateAppInstances AS target USING (SELECT 1 AS AppId) source ON 1 = 0 WHEN MATCHED THEN UPDATE SET DisplayName = CASE WHEN 1 = 1 THEN N'x' ELSE N'y' END, DesiredArtifactId = 5;", ArtifactPointerWriteMessage },
    };

    [Theory]
    [MemberData(nameof(HardenedBlockedSql))]
    public void PlatformRuntimeValidators_BlockKnownBypasses(string sql, string expectedMessage)
    {
        Assert.Equal(expectedMessage, OmpHostArtifactRepository.ValidateSafeModuleDefinitionSql(sql));
        Assert.Equal(expectedMessage, OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql));
    }

    public static TheoryData<string> HardenedAllowedSql => new()
    {
        // A column list that does not name the owned column remains allowed.
        "INSERT INTO omp.AppInstances(AppId, DisplayName) VALUES(1, N'x');",
        // CASE WHEN in an ordinary UPDATE assignment list.
        "UPDATE omp.AppInstances SET DisplayName = CASE WHEN 1 = 1 THEN N'x' ELSE N'y' END WHERE AppInstanceId = NEWID();",
        // A foreign key's ON DELETE clause is schema metadata, not an executable DELETE;
        // all three mirrors must agree (the line break before DELETE is deliberate).
        "CREATE TABLE omp.FkProbeChild (Id int NOT NULL PRIMARY KEY, ParentId int NULL, CONSTRAINT FK_FkProbeChild FOREIGN KEY (ParentId) REFERENCES omp.FkProbeChild(Id) ON\nDELETE CASCADE);",
        // WHEN NOT MATCHED BY SOURCE THEN DELETE is a MERGE action scoped by the merge predicate.
        "MERGE omp.Apps AS target USING (SELECT 1 AS AppKey) source ON 1 = 0 WHEN NOT MATCHED BY SOURCE THEN\nDELETE;",
    };

    [Theory]
    [MemberData(nameof(HardenedAllowedSql))]
    public void PlatformRuntimeValidators_AllowNonOwningStatements(string sql)
    {
        Assert.Null(OmpHostArtifactRepository.ValidateSafeModuleDefinitionSql(sql));
        Assert.Null(OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql));
    }

    [Fact]
    public void PlatformRuntimeValidators_AllowArtifactReadsAndUnrelatedArtifactIdColumns()
    {
        const string sql = """
SELECT ArtifactId FROM omp.Artifacts WHERE IsEnabled = 1;
UPDATE omp.WorkerInstances SET ArtifactId = 2 WHERE WorkerInstanceId = 1;
UPDATE omp.AppInstances SET DisplayName = N'ArtifactId is descriptive text' WHERE AppInstanceId = NEWID();
UPDATE omp.InstanceTemplateAppInstances SET DisplayName = N'x' WHERE DesiredArtifactId = 5;
UPDATE app SET DisplayName = N'x' FROM omp.AppInstances AS app WHERE app.ArtifactId = 5;
""";

        Assert.Null(OmpHostArtifactRepository.ValidateSafeModuleDefinitionSql(sql));
        Assert.Null(OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql));
    }

    [Fact]
    public void PlatformRuntimeValidators_AllowStoredProcedureDefinitionsThatOwnRuntimeMaterialization()
    {
        const string sql = """
ALTER PROCEDURE omp.MaterializeInstanceTemplate
AS
BEGIN
    UPDATE omp.InstanceTemplateAppInstances
    SET DesiredArtifactId = 2
    WHERE InstanceTemplateAppInstanceId = 1;
END
GO
""";

        Assert.Null(OmpHostArtifactRepository.ValidateSafeModuleDefinitionSql(sql));
        Assert.Null(OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql));
    }

    /// <summary>
    /// Red before 2026-09-02. The MERGE branch's positional-INSERT probe was
    /// <c>\bINSERT\s*(?!\()</c>; <c>\s*</c> backtracks to zero width, so ANY whitespace
    /// between INSERT and the column list read as "no column list". IbsPackager and iKrock2
    /// put <c>INSERT</c> and <c>(</c> on separate lines and were rejected for a pointer write
    /// they no longer contain; OpenDocViewer passed only because it writes <c>INSERT(</c>.
    /// The column list itself was never the problem - the previous probe caught the owned
    /// column first, which is why this never surfaced while the scripts still wrote it.
    /// </summary>
    [Fact]
    public void PlatformRuntimeValidators_AllowsMergeInsertWhoseColumnListStartsOnTheNextLine()
    {
        const string sql = """
MERGE omp.AppInstances AS target
USING (SELECT 1 AS AppId, N'x' AS DisplayName) AS source
ON target.AppId = source.AppId
WHEN NOT MATCHED THEN
    INSERT
    (
        AppId, DisplayName
    )
    VALUES (source.AppId, source.DisplayName);
""";

        Assert.Null(OmpHostArtifactRepository.ValidateSafeModuleDefinitionSql(sql));
        Assert.Null(OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql));
    }

    /// <summary>The fix must not open the hole the probe exists for.</summary>
    [Fact]
    public void PlatformRuntimeValidators_StillBlocksMergePositionalInsert()
    {
        const string sql = """
MERGE omp.AppInstances AS target
USING (SELECT 1 AS AppId) AS source
ON target.AppId = source.AppId
WHEN NOT MATCHED THEN
    INSERT VALUES (NEWID(), 1, NULL);
""";

        Assert.Equal(
            "Module definition SQL must not write omp.InstanceTemplateAppInstances.DesiredArtifactId or omp.AppInstances.ArtifactId; artifact selection is owned by artifact auto-apply.",
            OmpHostArtifactRepository.ValidateSafeModuleDefinitionSql(sql));
        Assert.Equal(
            "Module definition SQL must not write omp.InstanceTemplateAppInstances.DesiredArtifactId or omp.AppInstances.ArtifactId; artifact selection is owned by artifact auto-apply.",
            OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql));
    }
}
