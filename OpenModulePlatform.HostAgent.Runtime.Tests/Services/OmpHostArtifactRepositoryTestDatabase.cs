using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class OmpHostArtifactRepositoryTestDatabase : IDisposable
{
    private readonly string _databaseName;
    private readonly string _connectionString;

    public OmpHostArtifactRepositoryTestDatabase()
    {
        _databaseName = $"OmpHostAgentTests_{Guid.NewGuid():N}";
        var baseConnectionString = GetBaseConnectionString();

        using (var conn = new SqlConnection(baseConnectionString))
        {
            conn.Open();
            using var cmd = new SqlCommand(
                $"CREATE DATABASE [{_databaseName}] COLLATE Latin1_General_100_CI_AS_SC_UTF8;",
                conn);
            cmd.ExecuteNonQuery();
        }

        var builder = new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = _databaseName
        };
        _connectionString = builder.ConnectionString;

        CreateSchema();
    }

    public string ConnectionString => _connectionString;

    public ISqlConnectionFactory CreateFactory()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OmpDb"] = _connectionString
            })
            .Build();
        return new SqlConnectionFactory(configuration);
    }

    public void Dispose()
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(_connectionString)
            {
                InitialCatalog = "master"
            };
            using var conn = new SqlConnection(builder.ConnectionString);
            conn.Open();
            using var cmd = new SqlCommand(
                $@"
ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [{_databaseName}];",
                conn);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Best-effort cleanup; do not fail tests because cleanup failed.
        }
    }

    public void CreateMaterializeProcedure()
    {
        Execute(@"
CREATE PROCEDURE omp.MaterializeInstanceTemplate
    @HostKey nvarchar(128) = NULL,
    @HostTemplateId int = NULL,
    @RequestedBy nvarchar(256) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT 5 AS ModuleInstanceChanges, 3 AS AppInstanceChanges;
END;");
    }

    public void CreateMaterializeProcedureThatThrows()
    {
        Execute(@"
CREATE PROCEDURE omp.MaterializeInstanceTemplate
    @HostKey nvarchar(128) = NULL,
    @HostTemplateId int = NULL,
    @RequestedBy nvarchar(256) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51099, 'Simulated materialization failure.', 1;
END;");
    }

    public Guid InsertHost(string hostKey, bool isEnabled = true)
    {
        var hostId = Guid.NewGuid();
        Execute(
            "INSERT INTO omp.Hosts(HostId, HostKey, IsEnabled) VALUES(@hostId, @hostKey, @isEnabled);",
            new SqlParameter("@hostId", hostId),
            new SqlParameter("@hostKey", hostKey),
            new SqlParameter("@isEnabled", isEnabled));
        return hostId;
    }

    public void CreateMaintenanceFindingsTable()
    {
        Execute(@"
CREATE TABLE omp.MaintenanceFindings
(
    MaintenanceFindingId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    FindingKey nvarchar(450) NOT NULL UNIQUE,
    Scope nvarchar(20) NOT NULL,
    HostId uniqueidentifier NULL,
    Category nvarchar(100) NOT NULL,
    TargetKind nvarchar(80) NOT NULL,
    TargetIdentifier nvarchar(1000) NOT NULL,
    Title nvarchar(300) NOT NULL,
    Detail nvarchar(max) NULL,
    RecommendedAction nvarchar(300) NULL,
    SafetyNotes nvarchar(max) NULL,
    ActionJson nvarchar(max) NULL,
    Status tinyint NOT NULL DEFAULT(0),
    Severity tinyint NOT NULL DEFAULT(1),
    Confidence tinyint NOT NULL DEFAULT(80),
    DetectedByHostAgentJobId bigint NOT NULL,
    ResultMessage nvarchar(max) NULL,
    DetectedUtc datetime2(3) NOT NULL,
    LastSeenUtc datetime2(3) NOT NULL,
    UpdatedUtc datetime2(3) NOT NULL
);");
    }

    public int CountFindings()
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand("SELECT COUNT(1) FROM omp.MaintenanceFindings;", conn);
        return (int)cmd.ExecuteScalar()!;
    }

    public void InsertRecoveryCandidate(
        Guid hostId,
        string appInstanceKey,
        string targetPath,
        string runtimeName,
        string packageType = "web-app")
    {
        var appInstanceId = Guid.NewGuid();
        var artifactId = 1;

        Execute(
            "IF NOT EXISTS (SELECT 1 FROM omp.Artifacts WHERE ArtifactId = @artifactId) INSERT INTO omp.Artifacts(ArtifactId, PackageType, IsEnabled) VALUES(@artifactId, @packageType, 1);",
            new SqlParameter("@artifactId", artifactId),
            new SqlParameter("@packageType", packageType));

        Execute(
            "INSERT INTO omp.AppInstances(AppInstanceId, AppInstanceKey, ArtifactId, IsEnabled, IsAllowed, DesiredState) VALUES(@appInstanceId, @appInstanceKey, @artifactId, 1, 1, 1);",
            new SqlParameter("@appInstanceId", appInstanceId),
            new SqlParameter("@appInstanceKey", appInstanceKey),
            new SqlParameter("@artifactId", artifactId));

        Execute(
            "INSERT INTO omp.HostAppDeploymentStates(HostId, AppInstanceId, TargetPath, RuntimeName, ArtifactId) VALUES(@hostId, @appInstanceId, @targetPath, @runtimeName, @artifactId);",
            new SqlParameter("@hostId", hostId),
            new SqlParameter("@appInstanceId", appInstanceId),
            new SqlParameter("@targetPath", targetPath),
            new SqlParameter("@runtimeName", runtimeName),
            new SqlParameter("@artifactId", artifactId));
    }

    private static string GetBaseConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("OMP_TEST_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = ""
            };
            return builder.ConnectionString;
        }

        return "Server=(local);Integrated Security=true;TrustServerCertificate=true";
    }

    private void CreateSchema()
    {
        Execute("CREATE SCHEMA [omp];");
        Execute(@"
CREATE TABLE omp.Hosts
(
    HostId uniqueidentifier NOT NULL PRIMARY KEY,
    HostKey nvarchar(128) NOT NULL,
    IsEnabled bit NOT NULL DEFAULT(1)
);");
        Execute(@"
CREATE TABLE omp.Artifacts
(
    ArtifactId int NOT NULL PRIMARY KEY,
    PackageType nvarchar(50) NOT NULL,
    IsEnabled bit NOT NULL DEFAULT(1)
);");
        Execute(@"
CREATE TABLE omp.AppInstances
(
    AppInstanceId uniqueidentifier NOT NULL PRIMARY KEY,
    AppInstanceKey nvarchar(100) NOT NULL,
    ArtifactId int NULL,
    IsEnabled bit NOT NULL DEFAULT(1),
    IsAllowed bit NOT NULL DEFAULT(1),
    DesiredState bit NOT NULL DEFAULT(1)
);");
        Execute(@"
CREATE TABLE omp.HostAppDeploymentStates
(
    HostId uniqueidentifier NOT NULL,
    AppInstanceId uniqueidentifier NOT NULL,
    TargetPath nvarchar(500) NULL,
    RuntimeName nvarchar(200) NULL,
    ArtifactId int NULL,
    CONSTRAINT PK_HostAppDeploymentStates PRIMARY KEY(HostId, AppInstanceId)
);");
    }

    private void Execute(string sql, params SqlParameter[] parameters)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn);
        if (parameters.Length > 0)
        {
            cmd.Parameters.AddRange(parameters);
        }

        cmd.ExecuteNonQuery();
    }
}
