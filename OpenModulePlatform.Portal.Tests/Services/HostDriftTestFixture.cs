using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.Portal.Services;
using PortalSqlConnectionFactory = OpenModulePlatform.Web.Shared.Services.SqlConnectionFactory;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Provides a local SQL Server test database with the minimal OMP tables required
/// to exercise the host drift queries in <see cref="OmpAdminRepository"/>
/// (GetHostDriftDetailsAsync / GetHostDriftSummariesAsync).
/// </summary>
public sealed class HostDriftTestFixture : IAsyncLifetime
{
    public const string DatabaseName = "OpenModulePlatform_PortalTests_HostDrift";

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public Guid HostId { get; } = Guid.NewGuid();

    public int DesiredArtifactId { get; } = 1;

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

    /// <summary>
    /// Seeds one host with one desired web-app whose runtime deployment looks healthy
    /// (DeploymentState = 2 / Succeeded, no error) while the desired artifact has the
    /// given provisioning state on the host. This mirrors the incident shape where a
    /// failed artifact provisioning was hidden behind a stale successful deployment.
    /// </summary>
    public async Task SeedHostWithDesiredArtifactProvisioningStateAsync(
        byte provisioningState,
        string? provisioningError)
    {
        await ResetAsync();

        var instanceId = Guid.NewGuid();
        var moduleInstanceId = Guid.NewGuid();
        var appInstanceId = Guid.NewGuid();

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await ExecuteAsync(conn, $@"
INSERT INTO omp.InstanceTemplates (InstanceTemplateId, TemplateKey, DisplayName) VALUES (1, N'test-template', N'Test template');
INSERT INTO omp.Instances (InstanceId, InstanceKey, DisplayName, InstanceTemplateId)
VALUES ('{instanceId}', N'test-instance', N'Test instance', 1);
INSERT INTO omp.Hosts (HostId, InstanceId, HostKey, DisplayName)
VALUES ('{HostId}', '{instanceId}', N'test-host', N'Test host');
INSERT INTO omp.InstanceTemplateModuleInstances (InstanceTemplateModuleInstanceId, InstanceTemplateId, ModuleId, ModuleInstanceKey, DisplayName)
VALUES (1, 1, 1, N'test-module', N'Test module');
INSERT INTO omp.ModuleInstances (ModuleInstanceId, InstanceId, ModuleId, ModuleInstanceKey, DisplayName)
VALUES ('{moduleInstanceId}', '{instanceId}', 1, N'test-module', N'Test module');
INSERT INTO omp.Apps (AppId, ModuleId, AppKey, DisplayName, AppType) VALUES (1, 1, N'test-app', N'Test app', N'WebApp');
INSERT INTO omp.Artifacts (ArtifactId, AppId, Version, PackageType, TargetName)
VALUES ({DesiredArtifactId}, 1, N'1.0.0', N'web-app', N'test-target');
INSERT INTO omp.InstanceTemplateAppInstances
    (InstanceTemplateModuleInstanceId, InstanceTemplateHostId, TargetHostTemplateId, AppId, AppInstanceKey, DisplayName, DesiredArtifactId)
VALUES (1, NULL, NULL, 1, N'default', N'Test app instance', {DesiredArtifactId});
INSERT INTO omp.AppInstances
    (AppInstanceId, ModuleInstanceId, HostId, TargetHostTemplateId, AppId, AppInstanceKey, DisplayName, ArtifactId)
VALUES ('{appInstanceId}', '{moduleInstanceId}', NULL, NULL, 1, N'default', N'Test app instance', {DesiredArtifactId});
INSERT INTO omp.HostAppDeploymentStates (HostId, AppInstanceId, ArtifactId, DeploymentState, LastCheckedUtc, LastAppliedUtc, LastError)
VALUES ('{HostId}', '{appInstanceId}', {DesiredArtifactId}, 2, SYSUTCDATETIME(), SYSUTCDATETIME(), NULL);");

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.HostArtifactStates (HostId, ArtifactId, ProvisioningState, LastError)
VALUES (@hostId, @artifactId, @provisioningState, @provisioningError);",
            conn);
        cmd.Parameters.AddWithValue("@hostId", HostId);
        cmd.Parameters.AddWithValue("@artifactId", DesiredArtifactId);
        cmd.Parameters.AddWithValue("@provisioningState", provisioningState);
        cmd.Parameters.AddWithValue("@provisioningError", (object?)provisioningError ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ResetAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await ExecuteAsync(conn, @"
DELETE FROM omp.HostArtifactStates;
DELETE FROM omp.HostAppDeploymentStates;
DELETE FROM omp.HostAgentRuntimeStates;
DELETE FROM omp.HostAgentDesiredStates;
DELETE FROM omp.AppInstances;
DELETE FROM omp.InstanceTemplateAppInstances;
DELETE FROM omp.Artifacts;
DELETE FROM omp.Apps;
DELETE FROM omp.ModuleInstances;
DELETE FROM omp.InstanceTemplateModuleInstances;
DELETE FROM omp.Hosts;
DELETE FROM omp.Instances;
DELETE FROM omp.InstanceTemplates;
DELETE FROM omp.HostDeploymentAssignments;
DELETE FROM omp.HostTemplates;
DELETE FROM omp.InstanceTemplateHosts;");
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
IF OBJECT_ID(N'omp.InstanceTemplates', N'U') IS NULL
CREATE TABLE omp.InstanceTemplates
(
    InstanceTemplateId int NOT NULL PRIMARY KEY,
    TemplateKey nvarchar(100) NOT NULL,
    DisplayName nvarchar(200) NOT NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_Drift_InstanceTemplates_IsEnabled DEFAULT(1)
);

IF OBJECT_ID(N'omp.Instances', N'U') IS NULL
CREATE TABLE omp.Instances
(
    InstanceId uniqueidentifier NOT NULL PRIMARY KEY,
    InstanceKey nvarchar(100) NOT NULL,
    DisplayName nvarchar(200) NOT NULL,
    InstanceTemplateId int NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_Drift_Instances_IsEnabled DEFAULT(1)
);

IF OBJECT_ID(N'omp.Hosts', N'U') IS NULL
CREATE TABLE omp.Hosts
(
    HostId uniqueidentifier NOT NULL PRIMARY KEY,
    InstanceId uniqueidentifier NOT NULL,
    HostKey nvarchar(128) NOT NULL,
    DisplayName nvarchar(200) NULL,
    LastSeenUtc datetime2(3) NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_Drift_Hosts_IsEnabled DEFAULT(1)
);

IF OBJECT_ID(N'omp.InstanceTemplateModuleInstances', N'U') IS NULL
CREATE TABLE omp.InstanceTemplateModuleInstances
(
    InstanceTemplateModuleInstanceId int NOT NULL PRIMARY KEY,
    InstanceTemplateId int NOT NULL,
    ModuleId int NOT NULL,
    ModuleInstanceKey nvarchar(100) NOT NULL,
    DisplayName nvarchar(200) NOT NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_Drift_TemplateModuleInstances_IsEnabled DEFAULT(1)
);

IF OBJECT_ID(N'omp.ModuleInstances', N'U') IS NULL
CREATE TABLE omp.ModuleInstances
(
    ModuleInstanceId uniqueidentifier NOT NULL PRIMARY KEY,
    InstanceId uniqueidentifier NOT NULL,
    ModuleId int NOT NULL,
    ModuleInstanceKey nvarchar(100) NOT NULL,
    DisplayName nvarchar(200) NOT NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_Drift_ModuleInstances_IsEnabled DEFAULT(1)
);

IF OBJECT_ID(N'omp.Apps', N'U') IS NULL
CREATE TABLE omp.Apps
(
    AppId int NOT NULL PRIMARY KEY,
    ModuleId int NOT NULL,
    AppKey nvarchar(100) NOT NULL,
    DisplayName nvarchar(200) NOT NULL,
    AppType nvarchar(50) NOT NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_Drift_Apps_IsEnabled DEFAULT(1)
);

IF OBJECT_ID(N'omp.Artifacts', N'U') IS NULL
CREATE TABLE omp.Artifacts
(
    ArtifactId int NOT NULL PRIMARY KEY,
    AppId int NOT NULL,
    Version nvarchar(50) NOT NULL,
    PackageType nvarchar(50) NOT NULL,
    TargetName nvarchar(100) NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_Drift_Artifacts_IsEnabled DEFAULT(1)
);

IF OBJECT_ID(N'omp.InstanceTemplateHosts', N'U') IS NULL
CREATE TABLE omp.InstanceTemplateHosts
(
    InstanceTemplateHostId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    HostTemplateId int NULL,
    HostKey nvarchar(128) NOT NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_Drift_InstanceTemplateHosts_IsEnabled DEFAULT(1)
);

IF OBJECT_ID(N'omp.HostTemplates', N'U') IS NULL
CREATE TABLE omp.HostTemplates
(
    HostTemplateId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    TemplateKey nvarchar(100) NOT NULL
);

IF OBJECT_ID(N'omp.HostDeploymentAssignments', N'U') IS NULL
CREATE TABLE omp.HostDeploymentAssignments
(
    HostId uniqueidentifier NOT NULL,
    HostTemplateId int NOT NULL,
    IsActive bit NOT NULL CONSTRAINT DF_Drift_HostDeploymentAssignments_IsActive DEFAULT(1)
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
    DisplayName nvarchar(200) NOT NULL,
    DesiredArtifactId int NOT NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_Drift_TemplateAppInstances_IsEnabled DEFAULT(1),
    IsAllowed bit NOT NULL CONSTRAINT DF_Drift_TemplateAppInstances_IsAllowed DEFAULT(1),
    DesiredState tinyint NOT NULL CONSTRAINT DF_Drift_TemplateAppInstances_DesiredState DEFAULT(1)
);

IF OBJECT_ID(N'omp.AppInstances', N'U') IS NULL
CREATE TABLE omp.AppInstances
(
    AppInstanceId uniqueidentifier NOT NULL PRIMARY KEY,
    ModuleInstanceId uniqueidentifier NOT NULL,
    HostId uniqueidentifier NULL,
    TargetHostTemplateId int NULL,
    AppId int NOT NULL,
    AppInstanceKey nvarchar(100) NOT NULL,
    DisplayName nvarchar(200) NOT NULL,
    ArtifactId int NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_Drift_AppInstances_IsEnabled DEFAULT(1),
    IsAllowed bit NOT NULL CONSTRAINT DF_Drift_AppInstances_IsAllowed DEFAULT(1),
    DesiredState tinyint NOT NULL CONSTRAINT DF_Drift_AppInstances_DesiredState DEFAULT(1)
);

IF OBJECT_ID(N'omp.HostAppDeploymentStates', N'U') IS NULL
CREATE TABLE omp.HostAppDeploymentStates
(
    HostId uniqueidentifier NOT NULL,
    AppInstanceId uniqueidentifier NOT NULL,
    ArtifactId int NULL,
    DeploymentState tinyint NOT NULL CONSTRAINT DF_Drift_HostAppDeploymentStates_DeploymentState DEFAULT(0),
    LastCheckedUtc datetime2(3) NULL,
    LastAppliedUtc datetime2(3) NULL,
    LastError nvarchar(max) NULL,
    CONSTRAINT PK_Drift_HostAppDeploymentStates PRIMARY KEY(HostId, AppInstanceId)
);

IF OBJECT_ID(N'omp.HostArtifactStates', N'U') IS NULL
CREATE TABLE omp.HostArtifactStates
(
    HostId uniqueidentifier NOT NULL,
    ArtifactId int NOT NULL,
    ProvisioningState tinyint NOT NULL CONSTRAINT DF_Drift_HostArtifactStates_ProvisioningState DEFAULT(0),
    LocalPath nvarchar(500) NULL,
    LastCheckedUtc datetime2(3) NULL,
    LastProvisionedUtc datetime2(3) NULL,
    LastError nvarchar(max) NULL,
    CONSTRAINT PK_Drift_HostArtifactStates PRIMARY KEY(HostId, ArtifactId)
);

IF OBJECT_ID(N'omp.HostAgentDesiredStates', N'U') IS NULL
CREATE TABLE omp.HostAgentDesiredStates
(
    HostId uniqueidentifier NOT NULL PRIMARY KEY,
    ArtifactId int NOT NULL
);

IF OBJECT_ID(N'omp.HostAgentRuntimeStates', N'U') IS NULL
CREATE TABLE omp.HostAgentRuntimeStates
(
    HostId uniqueidentifier NOT NULL,
    ServiceName nvarchar(200) NOT NULL,
    Version nvarchar(50) NULL,
    IsActive bit NOT NULL CONSTRAINT DF_Drift_HostAgentRuntimeStates_IsActive DEFAULT(1),
    LastSeenUtc datetime2(3) NULL,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Drift_HostAgentRuntimeStates_CreatedUtc DEFAULT SYSUTCDATETIME(),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Drift_HostAgentRuntimeStates_UpdatedUtc DEFAULT SYSUTCDATETIME()
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
}
