using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.HostAgent.Runtime.Services;
using HostAgentSqlConnectionFactory = OpenModulePlatform.HostAgent.Runtime.Services.SqlConnectionFactory;
using PortalSqlConnectionFactory = OpenModulePlatform.Web.Shared.Services.SqlConnectionFactory;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Provides a local SQL Server test database with the minimal OMP tables required
/// to exercise stale-schema detection and repair in both the Portal and HostAgent
/// repositories.
/// </summary>
public sealed class StaleSchemaTestFixture : IAsyncLifetime
{
    public const string DatabaseName = "OpenModulePlatform_PortalTests_StaleSchema";

    public string ConnectionString { get; } =
        "Server=localhost;Database=OpenModulePlatform_PortalTests_StaleSchema;Integrated Security=true;TrustServerCertificate=true;";

    public async Task InitializeAsync()
    {
        await EnsureDatabaseExistsAsync();
        await EnsureSchemaAsync();
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

    public OmpHostArtifactRepository CreateHostAgentRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OmpDb"] = ConnectionString
            })
            .Build();
        return new OmpHostArtifactRepository(new HostAgentSqlConnectionFactory(configuration));
    }

    public async Task<int> InsertModuleDefinitionDocumentAsync(
        string moduleKey,
        string definitionVersion,
        string definitionJson,
        bool isApplied = false)
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
    @isApplied
);
SELECT CAST(SCOPE_IDENTITY() AS int);",
            conn);
        cmd.Parameters.AddWithValue("@moduleKey", moduleKey);
        cmd.Parameters.AddWithValue("@definitionVersion", definitionVersion);
        cmd.Parameters.AddWithValue("@definitionJson", definitionJson);
        cmd.Parameters.AddWithValue("@definitionSha256", ComputeSha256(definitionJson));
        cmd.Parameters.AddWithValue("@isApplied", isApplied);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task DropSchemaObjectsAsync(string schemaName)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            $@"
IF OBJECT_ID(N'{schemaName}.Data', N'U') IS NOT NULL DROP TABLE [{schemaName}].[Data];
IF SCHEMA_ID(N'{schemaName}') IS NOT NULL DROP SCHEMA [{schemaName}];",
            conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task CleanModuleDefinitionDocumentsAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            "DELETE FROM omp.ModuleDefinitionSqlExecutions; DELETE FROM omp.ModuleDefinitionArtifactCompatibility; DELETE FROM omp.ModuleDefinitionDocuments;",
            conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> TableExistsAsync(string schemaName, string tableName)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
SELECT 1
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = @schemaName
  AND t.name = @tableName;",
            conn);
        cmd.Parameters.AddWithValue("@schemaName", schemaName);
        cmd.Parameters.AddWithValue("@tableName", tableName);
        return await cmd.ExecuteScalarAsync() is not null;
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

        await using var schemaCmd = new SqlCommand(
            "IF SCHEMA_ID(N'omp') IS NULL EXEC(N'CREATE SCHEMA omp');",
            conn);
        await schemaCmd.ExecuteNonQueryAsync();

        await using var documentsCmd = new SqlCommand(
            @"
IF OBJECT_ID(N'omp.ModuleDefinitionDocuments', N'U') IS NULL
CREATE TABLE omp.ModuleDefinitionDocuments
(
    ModuleDefinitionDocumentId int IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_omp_ModuleDefinitionDocuments PRIMARY KEY,
    ModuleKey nvarchar(100) NOT NULL,
    DefinitionVersion nvarchar(50) NOT NULL,
    FormatVersion int NOT NULL CONSTRAINT DF_omp_ModuleDefinitionDocuments_FormatVersion DEFAULT(1),
    DefinitionJson nvarchar(max) NOT NULL,
    DefinitionSha256 nvarchar(128) NOT NULL,
    SourceName nvarchar(400) NULL,
    IsApplied bit NOT NULL CONSTRAINT DF_omp_ModuleDefinitionDocuments_IsApplied DEFAULT(0),
    AppliedUtc datetime2(3) NULL,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ModuleDefinitionDocuments_CreatedUtc DEFAULT SYSUTCDATETIME(),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ModuleDefinitionDocuments_UpdatedUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_omp_ModuleDefinitionDocuments_Module_Version UNIQUE(ModuleKey, DefinitionVersion),
    CONSTRAINT CK_omp_ModuleDefinitionDocuments_DefinitionJson CHECK(ISJSON(DefinitionJson) = 1)
);",
            conn);
        await documentsCmd.ExecuteNonQueryAsync();

        await using var compatibilityCmd = new SqlCommand(
            @"
IF OBJECT_ID(N'omp.ModuleDefinitionArtifactCompatibility', N'U') IS NULL
CREATE TABLE omp.ModuleDefinitionArtifactCompatibility
(
    ModuleDefinitionArtifactCompatibilityId int IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_omp_ModuleDefinitionArtifactCompatibility PRIMARY KEY,
    ModuleDefinitionDocumentId int NOT NULL,
    AppKey nvarchar(100) NOT NULL,
    PackageType nvarchar(50) NOT NULL,
    TargetName nvarchar(100) NULL,
    RelativePathTemplate nvarchar(400) NULL,
    MinArtifactVersion nvarchar(50) NULL,
    MaxArtifactVersion nvarchar(50) NULL,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ModuleDefinitionArtifactCompatibility_CreatedUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_omp_ModuleDefinitionArtifactCompatibility_Document
        FOREIGN KEY(ModuleDefinitionDocumentId)
        REFERENCES omp.ModuleDefinitionDocuments(ModuleDefinitionDocumentId)
        ON DELETE CASCADE,
    CONSTRAINT UQ_omp_ModuleDefinitionArtifactCompatibility_Target
        UNIQUE(ModuleDefinitionDocumentId, AppKey, PackageType, TargetName)
);",
            conn);
        await compatibilityCmd.ExecuteNonQueryAsync();

        await using var executionsCmd = new SqlCommand(
            @"
IF OBJECT_ID(N'omp.ModuleDefinitionSqlExecutions', N'U') IS NULL
CREATE TABLE omp.ModuleDefinitionSqlExecutions
(
    ModuleDefinitionSqlExecutionId bigint IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_omp_ModuleDefinitionSqlExecutions PRIMARY KEY,
    ModuleDefinitionDocumentId int NOT NULL,
    ScriptKey nvarchar(100) NOT NULL,
    ScriptPhase nvarchar(50) NOT NULL,
    ScriptOrder int NOT NULL,
    ScriptSha256 nvarchar(128) NOT NULL,
    ExecutionStatus nvarchar(30) NOT NULL,
    StartedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ModuleDefinitionSqlExecutions_StartedUtc DEFAULT SYSUTCDATETIME(),
    CompletedUtc datetime2(3) NULL,
    ErrorMessage nvarchar(max) NULL,
    ExecutedBy nvarchar(256) NULL CONSTRAINT DF_omp_ModuleDefinitionSqlExecutions_ExecutedBy DEFAULT SUSER_SNAME(),
    CONSTRAINT FK_omp_ModuleDefinitionSqlExecutions_Document
        FOREIGN KEY(ModuleDefinitionDocumentId)
        REFERENCES omp.ModuleDefinitionDocuments(ModuleDefinitionDocumentId)
        ON DELETE CASCADE,
    CONSTRAINT CK_omp_ModuleDefinitionSqlExecutions_Status
        CHECK(ExecutionStatus IN (N'Running', N'Succeeded', N'Failed'))
);",
            conn);
        await executionsCmd.ExecuteNonQueryAsync();
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

    private static string ComputeSha256(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
