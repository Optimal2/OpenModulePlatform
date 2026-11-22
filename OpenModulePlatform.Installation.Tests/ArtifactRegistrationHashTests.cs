using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Installation.Tests;

/// <summary>
/// The installer registers an artifact version that is already registered: the stored
/// Sha256 must survive as long as the content is unchanged, so that no app is redeployed
/// for a change of hash definition alone.
/// </summary>
/// <remarks>
/// Each test follows the value all the way to the deployment decision. HostAgent reports
/// the provisioned artifact's content hash (omp.HostArtifactStates.ContentSha256) and skips
/// a deployment only when that equals the hash recorded when the app was deployed
/// (omp.HostAppDeploymentStates.ContentSha256, compared by
/// ArtifactHash.MatchesDeployedContent). The "registered before the ordering changed" value
/// comes from PreOrdinalDirectoryHash, an independent copy of the old algorithm, and the
/// tree is one for which the two orderings really differ.
/// </remarks>
[Collection(InstallOutputTestCollection.Name)]
public sealed class ArtifactRegistrationHashTests : IClassFixture<ArtifactRegistrationHashFixture>, IDisposable
{
    private const string Version = "1.0.0";
    private const string PackageType = "web-app";
    private const string TargetName = "site";

    private readonly ArtifactRegistrationHashFixture _fixture;
    private readonly string _root = Path.Join(Path.GetTempPath(), "omp-registration-hash-" + Guid.NewGuid().ToString("N"));
    private readonly string _storeRoot;
    private readonly string _cacheRoot;

    public ArtifactRegistrationHashTests(ArtifactRegistrationHashFixture fixture)
    {
        _fixture = fixture;
        _storeRoot = Path.Join(_root, "store");
        _cacheRoot = Path.Join(_root, "cache");
        Directory.CreateDirectory(_storeRoot);
        Directory.CreateDirectory(_cacheRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp work root.
        }
    }

    [Fact]
    public async Task Reregistering_unchanged_content_keeps_the_hash_registered_with_the_old_path_ordering_and_nothing_redeploys()
    {
        var (relativePath, storePath) = WriteArtifactInStore();
        var registeredHash = PreOrdinalDirectoryHash.Compute(storePath);
        Assert.NotEqual(registeredHash, InstallationEngine.ComputeDirectorySha256(storePath));
        var (appId, artifactId) = await InsertRegisteredArtifactAsync(relativePath, registeredHash);
        // What HostAgent recorded when it deployed the app, before the installer ran again.
        var deployedHash = registeredHash;

        var reregisteredId = await RegisterAsync(appId, relativePath, storePath);

        Assert.Equal(artifactId, reregisteredId);
        var storedHash = await ReadSha256Async(artifactId);
        Assert.Equal(registeredHash, storedHash);
        var provisioned = await ProvisionAsync(artifactId, relativePath, storedHash);
        Assert.True(provisioned.IsSuccess, provisioned.ErrorMessage);
        Assert.True(
            ArtifactHash.MatchesDeployedContent(provisioned.ContentHash, deployedHash),
            "Unchanged content must not be redeployed after the installer registers it again.");
    }

    [Fact]
    public async Task Reregistering_changed_content_writes_the_canonical_hash_and_redeploys()
    {
        var (relativePath, storePath) = WriteArtifactInStore();
        var registeredHash = PreOrdinalDirectoryHash.Compute(storePath);
        var (appId, artifactId) = await InsertRegisteredArtifactAsync(relativePath, registeredHash);
        var deployedHash = registeredHash;
        await File.WriteAllTextAsync(Path.Join(storePath, "B.txt"), "BRAVO");

        await RegisterAsync(appId, relativePath, storePath);

        var storedHash = await ReadSha256Async(artifactId);
        Assert.Equal(InstallationEngine.ComputeDirectorySha256(storePath), storedHash);
        Assert.NotEqual(registeredHash, storedHash);
        Assert.NotEqual(PreOrdinalDirectoryHash.Compute(storePath), storedHash);
        var provisioned = await ProvisionAsync(artifactId, relativePath, storedHash);
        Assert.True(provisioned.IsSuccess, provisioned.ErrorMessage);
        Assert.False(
            ArtifactHash.MatchesDeployedContent(provisioned.ContentHash, deployedHash),
            "Changed content under the same version must be redeployed.");
    }

    [Fact]
    public async Task Registering_a_new_artifact_writes_the_canonical_hash()
    {
        var (relativePath, storePath) = WriteArtifactInStore();
        var appId = await InsertAppAsync();

        var artifactId = await RegisterAsync(appId, relativePath, storePath);

        var storedHash = await ReadSha256Async(artifactId);
        Assert.Equal(InstallationEngine.ComputeDirectorySha256(storePath), storedHash);
        Assert.NotEqual(PreOrdinalDirectoryHash.Compute(storePath), storedHash);
    }

    [Fact]
    public async Task Reregistering_unchanged_content_keeps_a_hash_registered_with_the_canonical_ordering()
    {
        var (relativePath, storePath) = WriteArtifactInStore();
        // Stored in upper case on purpose: "kept" means the stored text is left alone, not
        // replaced by an equal hash in another spelling.
        var registeredHash = InstallationEngine.ComputeDirectorySha256(storePath).ToUpperInvariant();
        var (appId, artifactId) = await InsertRegisteredArtifactAsync(relativePath, registeredHash);

        await RegisterAsync(appId, relativePath, storePath);

        Assert.Equal(registeredHash, await ReadSha256Async(artifactId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_row_without_a_registered_hash_gets_the_canonical_hash(string? registered)
    {
        var (_, storePath) = WriteArtifactInStore();
        var canonical = InstallationEngine.ComputeDirectorySha256(storePath);

        var (sha256, match) = await InstallationEngine.ResolveSha256ToRegisterAsync(storePath, canonical, registered);

        Assert.Equal(canonical, sha256);
        Assert.Equal(ArtifactRegisteredHashMatch.Mismatch, match);
    }

    private (string RelativePath, string StorePath) WriteArtifactInStore()
    {
        var relativePath = $"web/{Guid.NewGuid():N}/{Version}";
        var storePath = Path.Join(_storeRoot, relativePath);
        PreOrdinalDirectoryHash.WriteOrderSensitiveTree(storePath);
        return (relativePath, storePath);
    }

    private static async Task<int> RegisterAsync(int appId, string relativePath, string storePath)
    {
        await using var conn = new SqlConnection(ArtifactRegistrationHashFixture.ConnectionString);
        await conn.OpenAsync();
        return await InstallationEngine.RegisterArtifactContentAsync(
            conn,
            appId,
            Version,
            PackageType,
            TargetName,
            relativePath,
            storePath);
    }

    /// <summary>
    /// Provisions the artifact the way HostAgent does, from the store into an empty local
    /// cache, against the hash the catalog now holds.
    /// </summary>
    private async Task<ArtifactProvisioningResult> ProvisionAsync(int artifactId, string relativePath, string? sha256)
    {
        var settings = new HostAgentSettings
        {
            CentralArtifactRoot = _storeRoot,
            LocalArtifactCacheRoot = Path.Join(_cacheRoot, Guid.NewGuid().ToString("N"))
        };
        Directory.CreateDirectory(settings.LocalArtifactCacheRoot);
        var provisioner = new ArtifactProvisioner(
            new StaticOptionsMonitor(settings),
            NullLogger<ArtifactProvisioner>.Instance);
        return await provisioner.EnsureAsync(
            new ArtifactDescriptor
            {
                HostId = Guid.NewGuid(),
                ArtifactId = artifactId,
                Version = Version,
                PackageType = PackageType,
                TargetName = TargetName,
                RelativePath = relativePath,
                Sha256 = sha256,
                RequirementKey = "registration-hash"
            },
            CancellationToken.None);
    }

    private async Task<(int AppId, int ArtifactId)> InsertRegisteredArtifactAsync(string relativePath, string sha256)
    {
        var appId = await InsertAppAsync();
        await using var conn = new SqlConnection(ArtifactRegistrationHashFixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.Artifacts (AppId, Version, PackageType, TargetName, RelativePath, Sha256, IsEnabled)
VALUES (@appId, @version, @packageType, @targetName, @relativePath, @sha256, 1);
SELECT CAST(SCOPE_IDENTITY() AS int);",
            conn);
        cmd.Parameters.AddWithValue("@appId", appId);
        cmd.Parameters.AddWithValue("@version", Version);
        cmd.Parameters.AddWithValue("@packageType", PackageType);
        cmd.Parameters.AddWithValue("@targetName", TargetName);
        cmd.Parameters.AddWithValue("@relativePath", relativePath);
        cmd.Parameters.AddWithValue("@sha256", sha256);
        return (appId, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
    }

    private Task<int> InsertAppAsync()
        => _fixture.InsertAppAsync("registration-hash-" + Guid.NewGuid().ToString("N"));

    private static async Task<string?> ReadSha256Async(int artifactId)
    {
        await using var conn = new SqlConnection(ArtifactRegistrationHashFixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT Sha256 FROM omp.Artifacts WHERE ArtifactId = @artifactId;", conn);
        cmd.Parameters.AddWithValue("@artifactId", artifactId);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private sealed class StaticOptionsMonitor(HostAgentSettings currentValue) : IOptionsMonitor<HostAgentSettings>
    {
        public HostAgentSettings CurrentValue { get; } = currentValue;

        public HostAgentSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<HostAgentSettings, string?> listener) => null;
    }
}

/// <summary>
/// Local SQL Server test database provisioned with the real core setup script
/// (sql/1-setup-openmoduleplatform.sql) for the installer registration tests. It has its own
/// database so it can run beside the other Installation fixtures.
/// </summary>
public sealed class ArtifactRegistrationHashFixture : IAsyncLifetime
{
    public static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("InstallRegistrationHash");

    public static string ConnectionString { get; } = ForDatabase(DatabaseName);

    private int _moduleId;

    public async Task InitializeAsync()
    {
        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(
            ForDatabase("master"),
            $@"
IF DB_ID(N'{DatabaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{DatabaseName}];
END;
CREATE DATABASE [{DatabaseName}];");

        var setupSql = OmpRepositoryFiles.ReadRepositoryTextFile("sql", "1-setup-openmoduleplatform.sql");
        // Strip the historical local development database switch so the script runs against
        // the fixture database instead.
        var portableSql = Regex.Replace(
            setupSql,
            @"^\s*USE\s+\[OpenModulePlatform\]\s*;\s*\r?\n\s*GO\s*(?:--.*)?\s*(?:\r?\n)?",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        foreach (var batch in Regex.Split(portableSql, @"^\s*GO\s*$", RegexOptions.Multiline)
            .Where(batch => !string.IsNullOrWhiteSpace(batch)))
        {
            await ExecuteAsync(batch);
        }

        _moduleId = await ScalarAsync(
            @"
INSERT INTO omp.Modules (ModuleKey, DisplayName, ModuleType, SchemaName)
VALUES (N'installation-registration-hash', N'installation-registration-hash', N'core', N'omp');
SELECT CAST(SCOPE_IDENTITY() AS int);");
    }

    public async Task DisposeAsync()
    {
        await using var conn = new SqlConnection(ForDatabase("master"));
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
        catch (SqlException ex)
        {
            // A failed drop must not fail the run, but it must not be silent either.
            OmpTestCleanupLog.RecordFailure(
                nameof(ArtifactRegistrationHashFixture),
                $"Could not drop test database '{DatabaseName}': {ex.Message}");
        }
    }

    public Task<int> InsertAppAsync(string appKey)
        => ScalarAsync(
            @"
INSERT INTO omp.Apps (ModuleId, AppKey, DisplayName, AppType)
VALUES (@moduleId, @appKey, @appKey, N'web');
SELECT CAST(SCOPE_IDENTITY() AS int);",
            ("@moduleId", (object)_moduleId),
            ("@appKey", (object)appKey));

    private static string ForDatabase(string databaseName)
    {
        var overrideString = Environment.GetEnvironmentVariable("OMP_TEST_CONNECTION_STRING");
        var builder = string.IsNullOrWhiteSpace(overrideString)
            ? new SqlConnectionStringBuilder
            {
                DataSource = "localhost",
                IntegratedSecurity = true,
                TrustServerCertificate = true
            }
            : new SqlConnectionStringBuilder(overrideString);

        builder.InitialCatalog = databaseName;
        return builder.ConnectionString;
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
