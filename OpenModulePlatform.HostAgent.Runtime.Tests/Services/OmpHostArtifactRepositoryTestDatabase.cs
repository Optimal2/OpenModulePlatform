using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class OmpHostArtifactRepositoryTestDatabase : IDisposable
{
    private const string DatabaseNamePattern = "OmpHostAgentTests[_]%";

    /// <summary>
    /// Captured once per test process. The stale-database sweep only drops databases
    /// created before this timestamp (minus a safety margin), so a concurrently running
    /// test process never loses its own databases. <c>sys.databases.create_date</c> is
    /// server-local time and the test SQL Server is local, so a client-side
    /// <see cref="DateTime.Now"/> timestamp compares cleanly.
    /// </summary>
    private static readonly DateTime ProcessStart = DateTime.Now;

    /// <summary>Keeps databases created while this process was starting out of the sweep.</summary>
    private static readonly TimeSpan SweepSafetyMargin = TimeSpan.FromMinutes(2);

    private readonly string _databaseName;
    private readonly string _connectionString;

    static OmpHostArtifactRepositoryTestDatabase()
    {
        SweepStaleDatabases();
    }

    public OmpHostArtifactRepositoryTestDatabase()
    {
        _databaseName = $"OmpHostAgentTests_{Guid.NewGuid():N}";
        var baseConnectionString = GetBaseConnectionString();

        OmpTestDatabaseProvisioner.CreateDatabase(
            baseConnectionString,
            $"CREATE DATABASE [{_databaseName}] COLLATE Latin1_General_100_CI_AS_SC_UTF8;");

        var builder = new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = _databaseName
        };
        _connectionString = builder.ConnectionString;

        try
        {
            CreateSchema();
        }
        catch
        {
            // The constructor is throwing, so xUnit will never call Dispose(). Drop the
            // half-created database here or it leaks on every failing run.
            try
            {
                DropDatabase(baseConnectionString, _databaseName);
            }
            catch (Exception dropEx)
            {
                Console.Error.WriteLine(
                    $"[OmpHostAgentTests] Failed to drop test database '{_databaseName}' after a constructor failure. " +
                    $"Drop it manually. Drop error:{Environment.NewLine}{dropEx}");
            }

            throw;
        }
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
        var baseConnectionString = GetBaseConnectionString();
        try
        {
            DropDatabase(baseConnectionString, _databaseName);
        }
        catch (Exception ex)
        {
            // A failed drop must never fail the test run, but it must never be silent
            // either: silent drops are how stale OmpHostAgentTests_* databases pile up.
            var filePaths = GetDatabaseFilePaths(baseConnectionString, _databaseName);
            var files = filePaths.Count > 0
                ? string.Join(Environment.NewLine, filePaths.Select(p => $"  {p}"))
                : "  (database no longer visible in sys.master_files)";
            Console.Error.WriteLine(
                $"[OmpHostAgentTests] Failed to drop test database '{_databaseName}'. " +
                $"Drop it manually and delete any leftover files:{Environment.NewLine}{files}{Environment.NewLine}{ex}");
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

    public Guid InsertHost(string hostKey, bool isEnabled = true, Guid? instanceId = null, string? environment = null)
    {
        var hostId = Guid.NewGuid();
        Execute(
            "INSERT INTO omp.Hosts(HostId, InstanceId, HostKey, Environment, IsEnabled) VALUES(@hostId, @instanceId, @hostKey, @environment, @isEnabled);",
            new SqlParameter("@hostId", hostId),
            new SqlParameter("@instanceId", instanceId ?? Guid.NewGuid()),
            new SqlParameter("@hostKey", hostKey),
            new SqlParameter("@environment", environment ?? (object)DBNull.Value),
            new SqlParameter("@isEnabled", isEnabled));
        return hostId;
    }

    public Guid InsertInstance(string instanceKey, bool isEnabled = true)
    {
        var instanceId = Guid.NewGuid();
        Execute(
            "INSERT INTO omp.Instances(InstanceId, InstanceKey, DisplayName, IsEnabled) VALUES(@instanceId, @instanceKey, @displayName, @isEnabled);",
            new SqlParameter("@instanceId", instanceId),
            new SqlParameter("@instanceKey", instanceKey),
            new SqlParameter("@displayName", instanceKey),
            new SqlParameter("@isEnabled", isEnabled));
        return instanceId;
    }

    public Guid InsertAppInstance(Guid moduleInstanceId, string appInstanceKey, Guid? hostId = null)
    {
        var appInstanceId = Guid.NewGuid();
        var artifactId = EnsureArtifact(1, "web-app");
        Execute(
            "INSERT INTO omp.AppInstances(AppInstanceId, ModuleInstanceId, AppInstanceKey, HostId, ArtifactId, IsEnabled, IsAllowed, DesiredState) VALUES(@appInstanceId, @moduleInstanceId, @appInstanceKey, @hostId, @artifactId, 1, 1, 1);",
            new SqlParameter("@appInstanceId", appInstanceId),
            new SqlParameter("@moduleInstanceId", moduleInstanceId),
            new SqlParameter("@appInstanceKey", appInstanceKey),
            new SqlParameter("@hostId", hostId ?? (object)DBNull.Value),
            new SqlParameter("@artifactId", artifactId));
        return appInstanceId;
    }

    public void InsertHostArtifactRequirement(Guid hostId, string requirementKey, int artifactId = 1)
    {
        EnsureArtifact(artifactId, "host-requirement");
        Execute(
            "INSERT INTO omp.HostArtifactRequirements(HostId, ArtifactId, RequirementKey) VALUES(@hostId, @artifactId, @requirementKey);",
            new SqlParameter("@hostId", hostId),
            new SqlParameter("@artifactId", artifactId),
            new SqlParameter("@requirementKey", requirementKey));
    }

    public void InsertHostArtifactState(Guid hostId, int artifactId = 1)
    {
        EnsureArtifact(artifactId, "host-state");
        Execute(
            "INSERT INTO omp.HostArtifactStates(HostId, ArtifactId) VALUES(@hostId, @artifactId);",
            new SqlParameter("@hostId", hostId),
            new SqlParameter("@artifactId", artifactId));
    }

    private int EnsureArtifact(int artifactId, string packageType)
    {
        Execute(
            "IF NOT EXISTS (SELECT 1 FROM omp.Artifacts WHERE ArtifactId = @artifactId) INSERT INTO omp.Artifacts(ArtifactId, PackageType, IsEnabled) VALUES(@artifactId, @packageType, 1);",
            new SqlParameter("@artifactId", artifactId),
            new SqlParameter("@packageType", packageType));
        return artifactId;
    }

    public void CreateConfigOverlayTables()
    {
        Execute(@"
CREATE TABLE omp.ConfigOverlayDocuments
(
    ConfigOverlayDocumentId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    OverlayKey nvarchar(200) NOT NULL,
    OverlayVersion nvarchar(50) NOT NULL,
    HostKey nvarchar(128) NOT NULL,
    ModuleKey nvarchar(100) NULL,
    ModuleDefinitionVersion nvarchar(50) NULL,
    AppKey nvarchar(100) NULL,
    PackageType nvarchar(50) NULL,
    TargetName nvarchar(200) NULL,
    ArtifactVersion nvarchar(50) NULL,
    FormatVersion int NOT NULL DEFAULT(1),
    OverlayJson nvarchar(max) NOT NULL,
    OverlaySha256 nvarchar(128) NOT NULL,
    SourceName nvarchar(400) NULL,
    IsEnabled bit NOT NULL DEFAULT(1),
    CreatedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Test_ConfigOverlayDocuments_Key_Host_Version UNIQUE(OverlayKey, HostKey, OverlayVersion)
);");
        Execute(@"
CREATE TABLE omp.ConfigOverlayConfigurationFiles
(
    ConfigOverlayConfigurationFileId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ConfigOverlayDocumentId int NOT NULL,
    RelativePath nvarchar(500) NOT NULL,
    FileContent nvarchar(max) NOT NULL,
    IsEnabled bit NOT NULL DEFAULT(1)
);");
    }

    public IReadOnlyList<(int DocumentId, string OverlayVersion, bool IsEnabled, DateTime UpdatedUtc)> GetOverlayDocuments(
        string overlayKey,
        string hostKey)
    {
        var rows = new List<(int, string, bool, DateTime)>();
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand(
            @"
SELECT ConfigOverlayDocumentId, OverlayVersion, IsEnabled, UpdatedUtc
FROM omp.ConfigOverlayDocuments
WHERE OverlayKey = @overlayKey AND HostKey = @hostKey
ORDER BY ConfigOverlayDocumentId;",
            conn);
        cmd.Parameters.AddWithValue("@overlayKey", overlayKey);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            rows.Add((rdr.GetInt32(0), rdr.GetString(1), rdr.GetBoolean(2), rdr.GetDateTime(3)));
        }

        return rows;
    }

    public int CountOverlayConfigurationFiles(int documentId)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand(
            "SELECT COUNT(1) FROM omp.ConfigOverlayConfigurationFiles WHERE ConfigOverlayDocumentId = @documentId;",
            conn);
        cmd.Parameters.AddWithValue("@documentId", documentId);
        return (int)cmd.ExecuteScalar()!;
    }

    public void SetOverlayDocumentEnabled(int documentId, bool isEnabled)
    {
        Execute(
            "UPDATE omp.ConfigOverlayDocuments SET IsEnabled = @isEnabled WHERE ConfigOverlayDocumentId = @documentId;",
            new SqlParameter("@isEnabled", isEnabled),
            new SqlParameter("@documentId", documentId));
    }

    public void SetOverlayDocumentUpdatedUtc(int documentId, DateTime updatedUtc)
    {
        Execute(
            "UPDATE omp.ConfigOverlayDocuments SET UpdatedUtc = @updatedUtc WHERE ConfigOverlayDocumentId = @documentId;",
            new SqlParameter("@updatedUtc", updatedUtc),
            new SqlParameter("@documentId", documentId));
    }

    public void CreateConfigurationFileResolutionTables()
    {
        Execute(@"
ALTER TABLE omp.Artifacts ADD
    AppId int NULL,
    Version nvarchar(50) NULL,
    TargetName nvarchar(200) NULL,
    CreatedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME();");
        Execute(@"
CREATE TABLE omp.Modules
(
    ModuleId int NOT NULL PRIMARY KEY,
    ModuleKey nvarchar(100) NOT NULL
);");
        Execute(@"
CREATE TABLE omp.Apps
(
    AppId int NOT NULL PRIMARY KEY,
    ModuleId int NOT NULL,
    AppKey nvarchar(100) NOT NULL
);");
        Execute(@"
CREATE TABLE omp.ArtifactConfigurationFiles
(
    ArtifactConfigurationFileId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ArtifactId int NOT NULL,
    RelativePath nvarchar(500) NOT NULL,
    FileContent nvarchar(max) NOT NULL,
    PackageFileContent nvarchar(max) NULL,
    IsEnabled bit NOT NULL DEFAULT(1),
    UpdatedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
);");
        CreateConfigOverlayTables();
    }

    public int InsertArtifactWithApp(int artifactId, string packageType, string version, string moduleKey, string appKey)
    {
        Execute(
            "INSERT INTO omp.Modules(ModuleId, ModuleKey) VALUES(1, @moduleKey);",
            new SqlParameter("@moduleKey", moduleKey));
        Execute(
            "INSERT INTO omp.Apps(AppId, ModuleId, AppKey) VALUES(1, 1, @appKey);",
            new SqlParameter("@appKey", appKey));
        Execute(
            "INSERT INTO omp.Artifacts(ArtifactId, PackageType, IsEnabled, AppId, Version, TargetName) VALUES(@artifactId, @packageType, 1, 1, @version, NULL);",
            new SqlParameter("@artifactId", artifactId),
            new SqlParameter("@packageType", packageType),
            new SqlParameter("@version", version));
        return artifactId;
    }

    public int InsertArtifact(int artifactId, string packageType, string version, DateTime createdUtc)
    {
        Execute(
            "INSERT INTO omp.Artifacts(ArtifactId, PackageType, IsEnabled, AppId, Version, TargetName, CreatedUtc) VALUES(@artifactId, @packageType, 1, 1, @version, NULL, @createdUtc);",
            new SqlParameter("@artifactId", artifactId),
            new SqlParameter("@packageType", packageType),
            new SqlParameter("@version", version),
            new SqlParameter("@createdUtc", createdUtc));
        return artifactId;
    }

    public void SetArtifactConfigurationFileContent(
        int artifactId,
        string relativePath,
        string fileContent,
        bool? isEnabled = null)
    {
        Execute(
            @"
UPDATE omp.ArtifactConfigurationFiles
SET FileContent = @fileContent,
    IsEnabled = ISNULL(@isEnabled, IsEnabled),
    UpdatedUtc = SYSUTCDATETIME()
WHERE ArtifactId = @artifactId
  AND RelativePath = @relativePath;",
            new SqlParameter("@artifactId", artifactId),
            new SqlParameter("@relativePath", relativePath),
            new SqlParameter("@fileContent", fileContent),
            new SqlParameter("@isEnabled", (object?)isEnabled ?? DBNull.Value));
    }

    public void ClearArtifactConfigurationFileBaseline(int artifactId, string relativePath)
    {
        Execute(
            @"
UPDATE omp.ArtifactConfigurationFiles
SET PackageFileContent = NULL
WHERE ArtifactId = @artifactId
  AND RelativePath = @relativePath;",
            new SqlParameter("@artifactId", artifactId),
            new SqlParameter("@relativePath", relativePath));
    }

    public IReadOnlyList<(string RelativePath, string FileContent, string? PackageFileContent, bool IsEnabled)> GetArtifactConfigurationFiles(
        int artifactId)
    {
        var rows = new List<(string, string, string?, bool)>();
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand(
            @"
SELECT RelativePath, FileContent, PackageFileContent, IsEnabled
FROM omp.ArtifactConfigurationFiles
WHERE ArtifactId = @artifactId
ORDER BY RelativePath;",
            conn);
        cmd.Parameters.AddWithValue("@artifactId", artifactId);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            rows.Add((
                rdr.GetString(0),
                rdr.GetString(1),
                rdr.IsDBNull(2) ? null : rdr.GetString(2),
                rdr.GetBoolean(3)));
        }

        return rows;
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

    public bool HostExists(Guid hostId)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand("SELECT COUNT(1) FROM omp.Hosts WHERE HostId = @hostId;", conn);
        cmd.Parameters.AddWithValue("@hostId", hostId);
        return (int)cmd.ExecuteScalar()! > 0;
    }

    public int CountHostArtifactRequirements(Guid hostId)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand("SELECT COUNT(1) FROM omp.HostArtifactRequirements WHERE HostId = @hostId;", conn);
        cmd.Parameters.AddWithValue("@hostId", hostId);
        return (int)cmd.ExecuteScalar()!;
    }

    public int CountHostArtifactStates(Guid hostId)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand("SELECT COUNT(1) FROM omp.HostArtifactStates WHERE HostId = @hostId;", conn);
        cmd.Parameters.AddWithValue("@hostId", hostId);
        return (int)cmd.ExecuteScalar()!;
    }

    public long InsertMaintenanceFinding(string findingKey, Guid hostId)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand(
            "INSERT INTO omp.MaintenanceFindings(FindingKey, Scope, HostId, Category, TargetKind, TargetIdentifier, Title, DetectedByHostAgentJobId, DetectedUtc, LastSeenUtc, UpdatedUtc) " +
            "VALUES(@findingKey, N'Host', @hostId, N'Test', N'DatabaseRow', N'test', N'Test', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME()); " +
            "SELECT SCOPE_IDENTITY();",
            conn);
        cmd.Parameters.AddWithValue("@findingKey", findingKey);
        cmd.Parameters.AddWithValue("@hostId", hostId);
        return Convert.ToInt64(cmd.ExecuteScalar()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    public Guid? GetMaintenanceFindingHostId(long maintenanceFindingId)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand("SELECT HostId FROM omp.MaintenanceFindings WHERE MaintenanceFindingId = @id;", conn);
        cmd.Parameters.AddWithValue("@id", maintenanceFindingId);
        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : (Guid?)result;
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

    private static void DropDatabase(string baseConnectionString, string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = "master"
        };
        using var conn = new SqlConnection(builder.ConnectionString);
        conn.Open();
        using var cmd = new SqlCommand(
            $@"
ALTER DATABASE [{databaseName.Replace("]", "]]", StringComparison.Ordinal)}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [{databaseName.Replace("]", "]]", StringComparison.Ordinal)}];",
            conn);
        cmd.ExecuteNonQuery();
    }

    private static List<string> GetDatabaseFilePaths(string baseConnectionString, string databaseName)
    {
        var paths = new List<string>();
        try
        {
            var builder = new SqlConnectionStringBuilder(baseConnectionString)
            {
                InitialCatalog = "master"
            };
            using var conn = new SqlConnection(builder.ConnectionString);
            conn.Open();
            using var cmd = new SqlCommand(
                "SELECT physical_name FROM sys.master_files WHERE database_id = DB_ID(@databaseName);",
                conn);
            cmd.Parameters.AddWithValue("@databaseName", databaseName);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                paths.Add(rdr.GetString(0));
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // Best effort only: the drop failure itself matters more than the file list.
        }

        return paths;
    }

    private static void SweepStaleDatabases()
    {
        try
        {
            var cutoff = ProcessStart - SweepSafetyMargin;
            var baseConnectionString = GetBaseConnectionString();
            var builder = new SqlConnectionStringBuilder(baseConnectionString)
            {
                InitialCatalog = "master"
            };
            var staleNames = new List<string>();
            using (var conn = new SqlConnection(builder.ConnectionString))
            {
                conn.Open();
                using var cmd = new SqlCommand(
                    $"SELECT name FROM sys.databases WHERE name LIKE '{DatabaseNamePattern}' AND create_date < @cutoff;",
                    conn);
                cmd.Parameters.AddWithValue("@cutoff", cutoff);
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    staleNames.Add(rdr.GetString(0));
                }
            }

            foreach (var staleName in staleNames)
            {
                try
                {
                    DropDatabase(baseConnectionString, staleName);
                    Console.WriteLine($"[OmpHostAgentTests] Swept stale test database '{staleName}'.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[OmpHostAgentTests] Failed to sweep stale test database '{staleName}':{Environment.NewLine}{ex}");
                }
            }
        }
        catch (Exception ex)
        {
            // The sweep is belt-and-braces; it must never break the test run, but it
            // must never fail silently either.
            Console.Error.WriteLine($"[OmpHostAgentTests] Stale-database sweep failed:{Environment.NewLine}{ex}");
        }
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
CREATE TABLE omp.Instances
(
    InstanceId uniqueidentifier NOT NULL PRIMARY KEY,
    InstanceKey nvarchar(100) NOT NULL,
    DisplayName nvarchar(200) NOT NULL,
    IsEnabled bit NOT NULL DEFAULT(1)
);");
        Execute(@"
CREATE TABLE omp.Hosts
(
    HostId uniqueidentifier NOT NULL PRIMARY KEY,
    InstanceId uniqueidentifier NOT NULL,
    HostKey nvarchar(128) NOT NULL,
    Environment nvarchar(100) NULL,
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
    ModuleInstanceId uniqueidentifier NULL,
    AppInstanceKey nvarchar(100) NOT NULL,
    HostId uniqueidentifier NULL,
    ArtifactId int NULL,
    IsEnabled bit NOT NULL DEFAULT(1),
    IsAllowed bit NOT NULL DEFAULT(1),
    DesiredState bit NOT NULL DEFAULT(1)
);");
        Execute(@"
CREATE TABLE omp.HostArtifactRequirements
(
    HostArtifactRequirementId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    HostId uniqueidentifier NOT NULL,
    ArtifactId int NOT NULL,
    RequirementKey nvarchar(200) NOT NULL,
    DesiredLocalPath nvarchar(500) NULL,
    IsEnabled bit NOT NULL DEFAULT(1)
);");
        Execute(@"
CREATE TABLE omp.HostArtifactStates
(
    HostId uniqueidentifier NOT NULL,
    ArtifactId int NOT NULL,
    ProvisioningState tinyint NOT NULL DEFAULT(0),
    LocalPath nvarchar(500) NULL,
    ContentSha256 nvarchar(128) NULL,
    CONSTRAINT PK_HostArtifactStates PRIMARY KEY(HostId, ArtifactId)
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
