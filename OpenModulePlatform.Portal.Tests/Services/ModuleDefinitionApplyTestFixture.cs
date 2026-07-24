using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.Portal.Services;
using PortalSqlConnectionFactory = OpenModulePlatform.Web.Shared.Services.SqlConnectionFactory;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Provides a local SQL Server test database with the minimal OMP tables required
/// to exercise <see cref="OmpAdminRepository.ApplyModuleDefinitionDocumentAsync"/>,
/// specifically the seed MERGE against omp.InstanceTemplateAppInstances driven by
/// integrity.requiredOmpRows.instanceTemplateAppInstances.
/// </summary>
public sealed class ModuleDefinitionApplyTestFixture : IAsyncLifetime
{
    public const string DatabaseName = "OpenModulePlatform_PortalTests_ModuleDefinitionApply";

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public async Task InitializeAsync()
    {
        await EnsureDatabaseExistsAsync();
        await EnsureSchemaAsync();
        await ResetAsync();
    }

    public async Task DisposeAsync()
    {
        await DropDatabaseAsync();
    }

    public OmpAdminRepository CreatePortalRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OmpDb"] = ConnectionString
            })
            .Build();
        return new OmpAdminRepository(new PortalSqlConnectionFactory(configuration));
    }

    public async Task<int> InsertModuleDefinitionDocumentAsync(
        string moduleKey,
        string definitionVersion,
        string definitionJson)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.ModuleDefinitionDocuments
(
    ModuleKey,
    DefinitionVersion,
    FormatVersion,
    DefinitionJson,
    DefinitionSha256,
    SourceName,
    IsApplied
)
VALUES
(
    @moduleKey,
    @definitionVersion,
    1,
    @definitionJson,
    @definitionSha256,
    N'test',
    0
);
SELECT CAST(SCOPE_IDENTITY() AS int);",
            conn);
        cmd.Parameters.AddWithValue("@moduleKey", moduleKey);
        cmd.Parameters.AddWithValue("@definitionVersion", definitionVersion);
        cmd.Parameters.AddWithValue("@definitionJson", definitionJson);
        cmd.Parameters.AddWithValue("@definitionSha256", ComputeSha256(definitionJson));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<TemplateAppInstanceRow?> GetTemplateAppInstanceAsync(
        string moduleKey,
        string appInstanceKey)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
SELECT tai.DisplayName,
       tai.Description,
       tai.RoutePath,
       tai.InstallPath,
       tai.InstallationName,
       tai.DesiredState,
       tai.SortOrder,
       tai.IsEnabled,
       tai.IsAllowed
FROM omp.InstanceTemplateAppInstances tai
INNER JOIN omp.InstanceTemplateModuleInstances tmi
    ON tmi.InstanceTemplateModuleInstanceId = tai.InstanceTemplateModuleInstanceId
INNER JOIN omp.Modules m
    ON m.ModuleId = tmi.ModuleId
WHERE m.ModuleKey = @moduleKey
  AND tai.AppInstanceKey = @appInstanceKey;",
            conn);
        cmd.Parameters.AddWithValue("@moduleKey", moduleKey);
        cmd.Parameters.AddWithValue("@appInstanceKey", appInstanceKey);
        await using var rdr = await cmd.ExecuteReaderAsync();
        if (!await rdr.ReadAsync())
        {
            return null;
        }

        return new TemplateAppInstanceRow
        {
            DisplayName = rdr.IsDBNull(0) ? null : rdr.GetString(0),
            Description = rdr.IsDBNull(1) ? null : rdr.GetString(1),
            RoutePath = rdr.IsDBNull(2) ? null : rdr.GetString(2),
            InstallPath = rdr.IsDBNull(3) ? null : rdr.GetString(3),
            InstallationName = rdr.IsDBNull(4) ? null : rdr.GetString(4),
            DesiredState = rdr.GetByte(5),
            SortOrder = rdr.GetInt32(6),
            IsEnabled = rdr.GetBoolean(7),
            IsAllowed = rdr.GetBoolean(8)
        };
    }

    public async Task UpdateTemplateAppInstanceAsync(
        string moduleKey,
        string appInstanceKey,
        string setSql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            $@"
UPDATE tai
SET {setSql}
FROM omp.InstanceTemplateAppInstances tai
INNER JOIN omp.InstanceTemplateModuleInstances tmi
    ON tmi.InstanceTemplateModuleInstanceId = tai.InstanceTemplateModuleInstanceId
INNER JOIN omp.Modules m
    ON m.ModuleId = tmi.ModuleId
WHERE m.ModuleKey = @moduleKey
  AND tai.AppInstanceKey = @appInstanceKey;",
            conn);
        cmd.Parameters.AddWithValue("@moduleKey", moduleKey);
        cmd.Parameters.AddWithValue("@appInstanceKey", appInstanceKey);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ResetAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await ExecuteAsync(conn, @"
DELETE FROM omp.InstanceTemplateAppInstances;
DELETE FROM omp.AppInstances;
DELETE FROM omp.WorkerInstances;
DELETE FROM omp.InstanceTemplateModuleInstances;
DELETE FROM omp.ModuleInstances;
DELETE FROM omp.Artifacts;
DELETE FROM omp.Apps;
DELETE FROM omp.ModuleDefinitionArtifactCompatibility;
DELETE FROM omp.ModuleDefinitionDocuments;
DELETE FROM omp.Modules;
DELETE FROM omp.InstanceTemplateHosts;
DELETE FROM omp.InstanceTemplates;
DELETE FROM omp.HostTemplates;
DELETE FROM omp.Hosts;
DELETE FROM omp.Instances;

INSERT INTO omp.InstanceTemplates (TemplateKey) VALUES (N'default');");
    }

    private async Task EnsureDatabaseExistsAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = "master"
        };

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];",
            conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task EnsureSchemaAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await ExecuteAsync(conn, "IF SCHEMA_ID(N'omp') IS NULL EXEC(N'CREATE SCHEMA omp');");

        await ExecuteAsync(conn, @"
IF OBJECT_ID(N'omp.ModuleDefinitionDocuments', N'U') IS NULL
CREATE TABLE omp.ModuleDefinitionDocuments
(
    ModuleDefinitionDocumentId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ModuleKey nvarchar(100) NOT NULL,
    DefinitionVersion nvarchar(50) NOT NULL,
    FormatVersion int NOT NULL CONSTRAINT DF_Mda_ModuleDefinitionDocuments_FormatVersion DEFAULT(1),
    DefinitionJson nvarchar(max) NOT NULL,
    DefinitionSha256 nvarchar(128) NOT NULL,
    SourceName nvarchar(400) NULL,
    IsApplied bit NOT NULL CONSTRAINT DF_Mda_ModuleDefinitionDocuments_IsApplied DEFAULT(0),
    AppliedUtc datetime2(3) NULL,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Mda_ModuleDefinitionDocuments_CreatedUtc DEFAULT SYSUTCDATETIME(),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Mda_ModuleDefinitionDocuments_UpdatedUtc DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID(N'omp.ModuleDefinitionArtifactCompatibility', N'U') IS NULL
CREATE TABLE omp.ModuleDefinitionArtifactCompatibility
(
    ModuleDefinitionArtifactCompatibilityId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ModuleDefinitionDocumentId int NOT NULL,
    AppKey nvarchar(100) NOT NULL,
    PackageType nvarchar(50) NOT NULL,
    TargetName nvarchar(100) NULL,
    RelativePathTemplate nvarchar(400) NULL,
    MinArtifactVersion nvarchar(50) NULL,
    MaxArtifactVersion nvarchar(50) NULL
);

IF OBJECT_ID(N'omp.Modules', N'U') IS NULL
CREATE TABLE omp.Modules
(
    ModuleId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ModuleKey nvarchar(100) NOT NULL,
    DisplayName nvarchar(200) NOT NULL,
    ModuleType nvarchar(50) NOT NULL,
    SchemaName nvarchar(128) NOT NULL,
    Description nvarchar(500) NULL,
    SortOrder int NOT NULL CONSTRAINT DF_Mda_Modules_SortOrder DEFAULT(0),
    IsEnabled bit NOT NULL CONSTRAINT DF_Mda_Modules_IsEnabled DEFAULT(1),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Mda_Modules_UpdatedUtc DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID(N'omp.Apps', N'U') IS NULL
CREATE TABLE omp.Apps
(
    AppId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ModuleId int NOT NULL,
    AppKey nvarchar(100) NOT NULL,
    DisplayName nvarchar(200) NOT NULL,
    AppType nvarchar(50) NOT NULL,
    AllowMultipleActiveInstances bit NOT NULL CONSTRAINT DF_Mda_Apps_AllowMultiple DEFAULT(0),
    Description nvarchar(500) NULL,
    SortOrder int NOT NULL CONSTRAINT DF_Mda_Apps_SortOrder DEFAULT(0),
    IsEnabled bit NOT NULL CONSTRAINT DF_Mda_Apps_IsEnabled DEFAULT(1),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Mda_Apps_UpdatedUtc DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID(N'omp.Instances', N'U') IS NULL
CREATE TABLE omp.Instances
(
    InstanceId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    InstanceKey nvarchar(100) NOT NULL
);

IF OBJECT_ID(N'omp.ModuleInstances', N'U') IS NULL
CREATE TABLE omp.ModuleInstances
(
    ModuleInstanceId uniqueidentifier NOT NULL CONSTRAINT DF_Mda_ModuleInstances_Id DEFAULT NEWID() PRIMARY KEY,
    InstanceId int NOT NULL,
    ModuleId int NOT NULL,
    ModuleInstanceKey nvarchar(100) NOT NULL,
    DisplayName nvarchar(200) NULL,
    Description nvarchar(500) NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_Mda_ModuleInstances_IsEnabled DEFAULT(1),
    SortOrder int NOT NULL CONSTRAINT DF_Mda_ModuleInstances_SortOrder DEFAULT(0),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Mda_ModuleInstances_UpdatedUtc DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID(N'omp.InstanceTemplates', N'U') IS NULL
CREATE TABLE omp.InstanceTemplates
(
    InstanceTemplateId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    TemplateKey nvarchar(100) NOT NULL
);

IF OBJECT_ID(N'omp.InstanceTemplateModuleInstances', N'U') IS NULL
CREATE TABLE omp.InstanceTemplateModuleInstances
(
    InstanceTemplateModuleInstanceId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    InstanceTemplateId int NOT NULL,
    ModuleId int NOT NULL,
    ModuleInstanceKey nvarchar(100) NOT NULL,
    DisplayName nvarchar(200) NULL,
    Description nvarchar(500) NULL,
    SortOrder int NOT NULL CONSTRAINT DF_Mda_TemplateModuleInstances_SortOrder DEFAULT(0),
    IsEnabled bit NOT NULL CONSTRAINT DF_Mda_TemplateModuleInstances_IsEnabled DEFAULT(1),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Mda_TemplateModuleInstances_UpdatedUtc DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID(N'omp.InstanceTemplateHosts', N'U') IS NULL
CREATE TABLE omp.InstanceTemplateHosts
(
    InstanceTemplateHostId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    InstanceTemplateId int NOT NULL,
    HostKey nvarchar(128) NOT NULL
);

IF OBJECT_ID(N'omp.HostTemplates', N'U') IS NULL
CREATE TABLE omp.HostTemplates
(
    HostTemplateId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    TemplateKey nvarchar(100) NOT NULL
);

IF OBJECT_ID(N'omp.Hosts', N'U') IS NULL
CREATE TABLE omp.Hosts
(
    HostId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    InstanceId int NOT NULL,
    HostKey nvarchar(128) NOT NULL
);

IF OBJECT_ID(N'omp.Artifacts', N'U') IS NULL
CREATE TABLE omp.Artifacts
(
    ArtifactId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    AppId int NOT NULL,
    Version nvarchar(50) NOT NULL,
    PackageType nvarchar(50) NOT NULL,
    TargetName nvarchar(100) NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_Mda_Artifacts_IsEnabled DEFAULT(1)
);

IF OBJECT_ID(N'omp.AppInstances', N'U') IS NULL
CREATE TABLE omp.AppInstances
(
    AppInstanceId uniqueidentifier NOT NULL CONSTRAINT DF_Mda_AppInstances_Id DEFAULT NEWID() PRIMARY KEY,
    ModuleInstanceId uniqueidentifier NOT NULL,
    HostId int NULL,
    TargetHostTemplateId int NULL,
    AppId int NOT NULL,
    AppInstanceKey nvarchar(100) NOT NULL,
    DisplayName nvarchar(200) NULL,
    Description nvarchar(500) NULL,
    RoutePath nvarchar(256) NULL,
    PublicUrl nvarchar(500) NULL,
    InstallPath nvarchar(500) NULL,
    InstallationName nvarchar(150) NULL,
    ArtifactId int NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_Mda_AppInstances_IsEnabled DEFAULT(1),
    IsAllowed bit NOT NULL CONSTRAINT DF_Mda_AppInstances_IsAllowed DEFAULT(1),
    DesiredState tinyint NOT NULL CONSTRAINT DF_Mda_AppInstances_DesiredState DEFAULT(1),
    SortOrder int NOT NULL CONSTRAINT DF_Mda_AppInstances_SortOrder DEFAULT(0),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Mda_AppInstances_UpdatedUtc DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID(N'omp.WorkerInstances', N'U') IS NULL
CREATE TABLE omp.WorkerInstances
(
    WorkerInstanceId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ArtifactId int NULL
);

IF OBJECT_ID(N'omp.InstanceTemplateAppInstances', N'U') IS NULL
CREATE TABLE omp.InstanceTemplateAppInstances
(
    InstanceTemplateAppInstanceId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    InstanceTemplateModuleInstanceId int NOT NULL,
    InstanceTemplateHostId int NULL,
    TargetHostTemplateId int NULL,
    AppId int NOT NULL,
    AppInstanceKey nvarchar(100) NOT NULL,
    DisplayName nvarchar(200) NULL,
    Description nvarchar(500) NULL,
    RoutePath nvarchar(256) NULL,
    PublicUrl nvarchar(500) NULL,
    InstallPath nvarchar(500) NULL,
    InstallationName nvarchar(150) NULL,
    DesiredArtifactId int NULL,
    DesiredState tinyint NOT NULL CONSTRAINT DF_Mda_TemplateAppInstances_DesiredState DEFAULT(1),
    SortOrder int NOT NULL CONSTRAINT DF_Mda_TemplateAppInstances_SortOrder DEFAULT(0),
    IsEnabled bit NOT NULL CONSTRAINT DF_Mda_TemplateAppInstances_IsEnabled DEFAULT(1),
    IsAllowed bit NOT NULL CONSTRAINT DF_Mda_TemplateAppInstances_IsAllowed DEFAULT(1),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Mda_TemplateAppInstances_UpdatedUtc DEFAULT SYSUTCDATETIME()
);");
    }

    private async Task DropDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = "master"
        };

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            $@"
ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [{DatabaseName}];",
            conn);
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException)
        {
            // Best-effort cleanup.
        }
    }

    private static async Task ExecuteAsync(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string ComputeSha256(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public sealed class TemplateAppInstanceRow
    {
        public string? DisplayName { get; init; }
        public string? Description { get; init; }
        public string? RoutePath { get; init; }
        public string? InstallPath { get; init; }
        public string? InstallationName { get; init; }
        public byte DesiredState { get; init; }
        public int SortOrder { get; init; }
        public bool IsEnabled { get; init; }
        public bool IsAllowed { get; init; }
    }
}
