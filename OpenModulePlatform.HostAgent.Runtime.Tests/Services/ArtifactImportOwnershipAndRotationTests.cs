using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.ModuleDefinitions;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// Scratch directories for the tests in this file. OMP_TEST_TEMP_ROOT, when set, keeps
/// extraction and junction fixtures out of the system TEMP that real-time scanners watch;
/// unset, the system TEMP is used as before.
/// </summary>
internal static class ArtifactImportTestScratch
{
    public static string Create(string prefix)
    {
        var configured = Environment.GetEnvironmentVariable("OMP_TEST_TEMP_ROOT");
        var root = string.IsNullOrWhiteSpace(configured) ? Path.GetTempPath() : configured.Trim();
        var path = Path.Join(root, $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Scratch cleanup is advisory; a handle the OS still holds must not fail a passed test.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}

/// <summary>
/// Repository-level proof against a throwaway SQL Server database: operator-owned
/// configuration rows survive a same-identity replacement, a failed registration leaves
/// nothing behind so the retry carries operator edits forward, duplicate-content lookup
/// prefers the importing component, and the capped desired-artifact page rotates.
/// </summary>
public sealed class ArtifactImportOwnershipRepositoryTests : IDisposable
{
    private const string ConfigPath = "odv.site.config.js";
    private const string PackagedContent = "window.odv = { theme: 'default' };";
    private const string OperatorEditedContent = "window.odv = { theme: 'customer-blue' };";

    private readonly OmpHostArtifactRepositoryTestDatabase _database;
    private readonly OmpHostArtifactRepository _repository;

    public ArtifactImportOwnershipRepositoryTests()
    {
        _database = new OmpHostArtifactRepositoryTestDatabase();
        try
        {
            _database.CreateConfigurationFileResolutionTables();
            // Registration inserts identity rows with a content hash, which the shared
            // fixture's minimal omp.Artifacts table cannot hold; recreate it with the
            // production shape these queries read.
            Execute(@"
DROP TABLE omp.Artifacts;
CREATE TABLE omp.Artifacts
(
    ArtifactId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    AppId int NULL,
    Version nvarchar(50) NULL,
    PackageType nvarchar(50) NOT NULL,
    TargetName nvarchar(200) NULL,
    RelativePath nvarchar(400) NULL,
    Sha256 nvarchar(128) NULL,
    IsEnabled bit NOT NULL DEFAULT(1),
    CreatedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
);
ALTER TABLE omp.AppInstances ADD TargetHostTemplateId int NULL;
ALTER TABLE omp.WorkerInstances ADD IsAllowed bit NOT NULL DEFAULT(1);
CREATE TABLE omp.HostDeploymentAssignments
(
    HostId uniqueidentifier NOT NULL,
    HostTemplateId int NOT NULL,
    IsActive bit NOT NULL DEFAULT(1)
);
INSERT INTO omp.Modules(ModuleId, ModuleKey) VALUES(1, N'test-module');
INSERT INTO omp.Apps(AppId, ModuleId, AppKey) VALUES(1, 1, N'test-app');
INSERT INTO omp.Apps(AppId, ModuleId, AppKey) VALUES(2, 1, N'other-app');");
            _repository = new OmpHostArtifactRepository(_database.CreateFactory());
        }
        catch
        {
            _database.Dispose();
            throw;
        }
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Same_identity_replacement_keeps_an_operator_created_row_the_package_does_not_ship()
    {
        var artifactId = InsertArtifact(1, "web-app", "1.0.0", sha256: "aa");
        await _repository.ReplaceArtifactConfigurationFilesAsync(artifactId, [new(ConfigPath, PackagedContent)], CancellationToken.None);
        // A row created in the Portal editor carries no package baseline.
        _database.InsertArtifactConfigurationFile(artifactId, "operator.extra.json", "{ \"proxy\": true }", packageFileContent: null);

        await _repository.ReplaceArtifactConfigurationFilesAsync(artifactId, [new(ConfigPath, PackagedContent)], CancellationToken.None);

        var rows = _database.GetArtifactConfigurationFiles(artifactId);
        Assert.Contains(rows, row => row.RelativePath == "operator.extra.json" && row.FileContent == "{ \"proxy\": true }");
        Assert.Contains(rows, row => row.RelativePath == ConfigPath);
    }

    [Fact]
    public async Task Same_identity_replacement_still_removes_a_package_row_the_package_dropped()
    {
        var artifactId = InsertArtifact(1, "web-app", "1.0.0", sha256: "aa");
        await _repository.ReplaceArtifactConfigurationFilesAsync(
            artifactId,
            [new(ConfigPath, PackagedContent), new("legacy.settings.json", "{ \"old\": true }")],
            CancellationToken.None);

        await _repository.ReplaceArtifactConfigurationFilesAsync(artifactId, [new(ConfigPath, PackagedContent)], CancellationToken.None);

        var row = Assert.Single(_database.GetArtifactConfigurationFiles(artifactId));
        Assert.Equal(ConfigPath, row.RelativePath);
    }

    [Fact]
    public async Task Interrupted_registration_leaves_no_identity_and_the_retry_carries_operator_edits_forward()
    {
        var previousId = InsertArtifact(1, "web-app", "1.0.0", sha256: "aa");
        await _repository.ReplaceArtifactConfigurationFilesAsync(previousId, [new(ConfigPath, PackagedContent)], CancellationToken.None);
        _database.SetArtifactConfigurationFileContent(previousId, ConfigPath, OperatorEditedContent);

        // The second row overflows the column, so the transaction fails after the identity
        // row and the first configuration row were written -- the same state a process
        // death at that point leaves behind.
        var failing = new ArtifactPackageConfigurationFile[]
        {
            new(ConfigPath, PackagedContent),
            new(new string('x', 600) + ".json", "{}")
        };
        await Assert.ThrowsAsync<SqlException>(() => _repository.RegisterImportedArtifactWithConfigurationAsync(
            1, "1.1.0", "web-app", "", "web-app/1.1.0", "bb", failing, copyFromContinuitySource: true, CancellationToken.None));
        Assert.Null(FindArtifactId(1, "1.1.0", "web-app"));

        var registration = await _repository.RegisterImportedArtifactWithConfigurationAsync(
            1, "1.1.0", "web-app", "", "web-app/1.1.0", "bb", [new(ConfigPath, PackagedContent)], copyFromContinuitySource: true, CancellationToken.None);

        Assert.NotNull(registration);
        var item = Assert.Single(registration.CarryForward.Items);
        Assert.Equal(ArtifactConfigurationCarryForwardOutcome.Preserved, item.Outcome);
        var row = Assert.Single(_database.GetArtifactConfigurationFiles(registration.ArtifactId));
        Assert.Equal(OperatorEditedContent, row.FileContent);
        Assert.Equal(PackagedContent, row.PackageFileContent);
    }

    [Fact]
    public async Task Same_identity_replacement_resumes_the_carry_forward_for_an_identity_left_without_rows()
    {
        var previousId = InsertArtifact(1, "web-app", "1.0.0", sha256: "aa");
        await _repository.ReplaceArtifactConfigurationFilesAsync(previousId, [new(ConfigPath, PackagedContent)], CancellationToken.None);
        _database.SetArtifactConfigurationFileContent(previousId, ConfigPath, OperatorEditedContent);
        // An older import committed the identity and died before writing any rows.
        var interruptedId = InsertArtifact(1, "web-app", "1.1.0", sha256: "bb");

        var (_, carryForward) = await _repository.ReplaceArtifactConfigurationFilesResumingCarryForwardAsync(
            interruptedId,
            [new(ConfigPath, PackagedContent)],
            CancellationToken.None);

        Assert.Equal(ArtifactConfigurationCarryForwardOutcome.Preserved, Assert.Single(carryForward.Items).Outcome);
        Assert.Equal(OperatorEditedContent, Assert.Single(_database.GetArtifactConfigurationFiles(interruptedId)).FileContent);
    }

    [Fact]
    public async Task Same_identity_replacement_does_not_reapply_carry_forward_over_existing_rows()
    {
        var previousId = InsertArtifact(1, "web-app", "1.0.0", sha256: "aa");
        await _repository.ReplaceArtifactConfigurationFilesAsync(previousId, [new(ConfigPath, PackagedContent)], CancellationToken.None);
        _database.SetArtifactConfigurationFileContent(previousId, ConfigPath, OperatorEditedContent);
        // The new version already has its rows; the operator deliberately kept the package default.
        var currentId = InsertArtifact(1, "web-app", "1.1.0", sha256: "bb");
        _database.InsertArtifactConfigurationFile(currentId, ConfigPath, PackagedContent, PackagedContent);

        var (_, carryForward) = await _repository.ReplaceArtifactConfigurationFilesResumingCarryForwardAsync(
            currentId,
            [new(ConfigPath, PackagedContent)],
            CancellationToken.None);

        Assert.Empty(carryForward.Items);
        Assert.Equal(PackagedContent, Assert.Single(_database.GetArtifactConfigurationFiles(currentId)).FileContent);
    }

    [Fact]
    public async Task Registration_of_an_existing_identity_returns_null_and_writes_no_rows()
    {
        var existingId = InsertArtifact(1, "web-app", "1.1.0", sha256: "bb", targetName: "");

        var registration = await _repository.RegisterImportedArtifactWithConfigurationAsync(
            1, "1.1.0", "web-app", "", "web-app/1.1.0", "bb", [new(ConfigPath, PackagedContent)], copyFromContinuitySource: true, CancellationToken.None);

        Assert.Null(registration);
        Assert.Empty(_database.GetArtifactConfigurationFiles(existingId));
    }

    [Fact]
    public async Task Duplicate_content_lookup_prefers_the_importing_component_over_an_older_foreign_row()
    {
        var foreignId = InsertArtifact(2, "worker", "3.0.0", sha256: "same");
        var ownId = InsertArtifact(1, "web-app", "1.0.0", sha256: "same");

        var forOwnComponent = await _repository.FindImportedArtifactBySha256Async("same", 1, "web-app", CancellationToken.None);
        var forUnrelatedComponent = await _repository.FindImportedArtifactBySha256Async("same", 1, "service-app", CancellationToken.None);

        Assert.Equal(ownId, forOwnComponent!.ArtifactId);
        Assert.Equal(foreignId, forUnrelatedComponent!.ArtifactId);
    }

    [Fact]
    public async Task Capped_desired_artifact_page_rotates_so_the_tail_is_provisioned()
    {
        const string hostKey = "rotation-host";
        var hostId = _database.InsertHost(hostKey);
        var artifactIds = new List<int>();
        for (var index = 1; index <= 5; index++)
        {
            var artifactId = InsertArtifact(1, "web-app", $"1.0.{index}", sha256: $"s{index}", relativePath: $"web-app/1.0.{index}");
            artifactIds.Add(artifactId);
            Execute(
                "INSERT INTO omp.HostArtifactRequirements(HostId, ArtifactId, RequirementKey) VALUES(@hostId, @artifactId, @key);",
                new SqlParameter("@hostId", hostId),
                new SqlParameter("@artifactId", artifactId),
                new SqlParameter("@key", $"req-{index}"));
        }

        var seen = new HashSet<int>();
        var pages = new List<IReadOnlyList<int>>();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var page = await _repository.GetDesiredArtifactsAsync(hostKey, false, true, 2, CancellationToken.None);
            Assert.Equal(2, page.Count);
            pages.Add(page.Select(static artifact => artifact.ArtifactId).ToList());
            seen.UnionWith(page.Select(static artifact => artifact.ArtifactId));
        }

        Assert.Equal(artifactIds.ToHashSet(), seen);
        Assert.Equal(new[] { artifactIds[0], artifactIds[1] }, pages[0].Order());
        Assert.Equal(new[] { artifactIds[2], artifactIds[3] }, pages[1].Order());
        Assert.Equal(new[] { artifactIds[0], artifactIds[4] }, pages[2].Order());
    }

    [Fact]
    public async Task Uncapped_desired_artifact_page_returns_everything_and_restarts_from_the_head()
    {
        const string hostKey = "rotation-reset-host";
        var hostId = _database.InsertHost(hostKey);
        var artifactIds = new List<int>();
        for (var index = 1; index <= 3; index++)
        {
            var artifactId = InsertArtifact(1, "web-app", $"2.0.{index}", sha256: $"r{index}", relativePath: $"web-app/2.0.{index}");
            artifactIds.Add(artifactId);
            Execute(
                "INSERT INTO omp.HostArtifactRequirements(HostId, ArtifactId, RequirementKey) VALUES(@hostId, @artifactId, @key);",
                new SqlParameter("@hostId", hostId),
                new SqlParameter("@artifactId", artifactId),
                new SqlParameter("@key", $"req-{index}"));
        }

        await _repository.GetDesiredArtifactsAsync(hostKey, false, true, 2, CancellationToken.None);
        var all = await _repository.GetDesiredArtifactsAsync(hostKey, false, true, 10, CancellationToken.None);
        var afterReset = await _repository.GetDesiredArtifactsAsync(hostKey, false, true, 2, CancellationToken.None);

        Assert.Equal(artifactIds, all.Select(static artifact => artifact.ArtifactId).Order());
        Assert.Equal(new[] { artifactIds[0], artifactIds[1] }, afterReset.Select(static artifact => artifact.ArtifactId).Order());
    }

    private int InsertArtifact(
        int appId,
        string packageType,
        string version,
        string sha256,
        string? targetName = null,
        string? relativePath = null)
    {
        using var conn = new SqlConnection(_database.ConnectionString);
        conn.Open();
        using var cmd = new SqlCommand(@"
INSERT INTO omp.Artifacts(AppId, Version, PackageType, TargetName, RelativePath, Sha256, IsEnabled)
VALUES(@appId, @version, @packageType, @targetName, @relativePath, @sha256, 1);
SELECT CAST(SCOPE_IDENTITY() AS int);", conn);
        cmd.Parameters.AddWithValue("@appId", appId);
        cmd.Parameters.AddWithValue("@version", version);
        cmd.Parameters.AddWithValue("@packageType", packageType);
        cmd.Parameters.AddWithValue("@targetName", (object?)targetName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@relativePath", (object?)relativePath ?? $"{packageType}/{version}");
        cmd.Parameters.AddWithValue("@sha256", sha256);
        return (int)cmd.ExecuteScalar()!;
    }

    private int? FindArtifactId(int appId, string version, string packageType)
    {
        using var conn = new SqlConnection(_database.ConnectionString);
        conn.Open();
        using var cmd = new SqlCommand(
            "SELECT ArtifactId FROM omp.Artifacts WHERE AppId = @appId AND Version = @version AND PackageType = @packageType;",
            conn);
        cmd.Parameters.AddWithValue("@appId", appId);
        cmd.Parameters.AddWithValue("@version", version);
        cmd.Parameters.AddWithValue("@packageType", packageType);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : (int)result;
    }

    private void Execute(string sql, params SqlParameter[] parameters)
    {
        using var conn = new SqlConnection(_database.ConnectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddRange(parameters);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Service-level gates of <see cref="ArtifactZipImportService" /> against a deterministic
/// <see cref="IArtifactZipImportRepository" />, plus the file-system guards of the restore
/// and staging-cleanup paths.
/// </summary>
public sealed class ArtifactImportServiceGateTests : IDisposable
{
    private const string ConfigPath = "odv.site.config.js";

    private readonly string _root = ArtifactImportTestScratch.Create("omp-import-gates");
    private readonly FakeImportRepository _repository = new();
    private readonly CapturingLogger _logger = new();
    private readonly HostAgentSettings _settings;
    private readonly ArtifactZipImportService _service;

    public ArtifactImportServiceGateTests()
    {
        _settings = new HostAgentSettings
        {
            HostKey = "gate-host",
            CentralArtifactRoot = Path.Join(_root, "store"),
            ArtifactZipImport = new HostAgentArtifactZipImportSettings
            {
                ImportPath = Path.Join(_root, "import")
            }
        };
        _service = new ArtifactZipImportService(
            new FakeOptionsMonitor<HostAgentSettings> { CurrentValue = _settings },
            _repository,
            _logger);
    }

    public void Dispose() => ArtifactImportTestScratch.Delete(_root);

    [Fact]
    public async Task Retry_after_an_interrupted_registration_registers_again_and_carries_forward()
    {
        var package = CreateArtifactPackage("1.1.0", "<html>v</html>");
        _repository.RegistrationFailuresRemaining = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync("1.1.0", package));
        var result = await ImportAsync("1.1.0", package);

        Assert.Equal("Imported", result.Status);
        Assert.Equal(2, _repository.RegistrationCalls);
        Assert.Equal(0, _repository.SameIdentityReplaceCalls);
        Assert.Contains("Preserved 1 operator-edited", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Identical_content_of_the_same_component_under_another_target_is_an_accepted_empty_diff()
    {
        // Contract kept on purpose: the duplicate gate compares app and package type, not
        // TargetName -- the same bytes delivered to two targets of one component are not a
        // repackaging mistake.
        var package = CreateArtifactPackage("2.0.0", "<html>same</html>");
        var hash = await HashOfPackagePayloadAsync(package);
        _repository.Artifacts.Add(new FakeArtifact(50, 1, "1.9.0", "web-app", "other-target", "web-app/other-target/1.9.0", hash));

        var result = await ImportAsync("2.0.0", package);

        Assert.Equal("Imported", result.Status);
        Assert.Contains("empty-diff", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((1, "web-app"), _repository.LastDuplicateLookupComponent);
    }

    [Fact]
    public async Task Identical_content_of_another_component_is_still_rejected()
    {
        var package = CreateArtifactPackage("2.0.0", "<html>foreign</html>");
        var hash = await HashOfPackagePayloadAsync(package);
        _repository.Artifacts.Add(new FakeArtifact(60, 2, "4.0.0", "worker", "", "worker/4.0.0", hash));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync("2.0.0", package));

        Assert.Contains("identical extracted content", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _repository.RegistrationCalls);
    }

    [SupportedOSPlatform("windows")]
    [SkippableFact]
    public async Task Restore_rejects_a_reparse_point_parent_and_writes_nothing_through_it()
    {
        var store = Path.Join(_root, "restore-store");
        var outside = Path.Join(_root, "outside");
        Directory.CreateDirectory(store);
        Directory.CreateDirectory(outside);
        var linkedParent = Path.Join(store, "web-app");
        Skip.IfNot(TryCreateJunction(linkedParent, outside), "This process cannot create a directory link; the guard was not exercised.");
        var content = Path.Join(_root, "content");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Join(content, "index.html"), "<html/>");

        await Assert.ThrowsAsync<IOException>(() => ArtifactZipImportService.RestoreMissingImportedArtifactContentAsync(
            content,
            Path.Join(linkedParent, "1.0.0"),
            "web-app/1.0.0",
            "hash",
            existingMetadataHashMissing: false,
            "test-app",
            "1.0.0",
            "web-app",
            "",
            CancellationToken.None));

        Assert.False(Directory.Exists(Path.Join(outside, "1.0.0")));
        Assert.True(File.Exists(Path.Join(content, "index.html")));
    }

    [Fact]
    public void Staging_cleanup_failure_is_logged_with_the_path_and_error()
    {
        var staging = Path.Join(_root, ".artifact-staging", "held");
        Directory.CreateDirectory(staging);
        var held = Path.Join(staging, "payload.bin");
        File.WriteAllText(held, "x");

        bool removed;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            removed = _service.TryDeleteImportScratch(staging);
        }

        Assert.False(removed);
        var warning = Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(staging, warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(warning.Exception);
        Assert.True(_service.TryDeleteImportScratch(staging));
    }

    private Task<ArtifactZipImportResult> ImportAsync(string version, string packagePath)
        => _service.ImportArtifactPackageAsync(
            _settings,
            _settings.ArtifactZipImport,
            new ArtifactZipImportService.FilenameMetadata("test-module", "test-app", "web-app", "", version),
            packagePath,
            Path.Join(_root, "staging", Guid.NewGuid().ToString("N")),
            CancellationToken.None);

    private string CreateArtifactPackage(string version, string payload)
    {
        var path = Path.Join(_root, $"package-{version}-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, ArtifactPackageExtractor.ManifestEntryName, $$"""
            {
              "formatVersion": 1,
              "payload": { "path": "payload" },
              "configurationFiles": [ { "relativePath": "{{ConfigPath}}", "source": "config/{{ConfigPath}}" } ]
            }
            """);
        Write(archive, "payload/index.html", payload);
        Write(archive, $"config/{ConfigPath}", "window.odv = {};");
        return path;
    }

    private async Task<string> HashOfPackagePayloadAsync(string packagePath)
    {
        var staging = Path.Join(_root, "hash", Guid.NewGuid().ToString("N"));
        var extracted = new ArtifactPackageExtractor().Extract(packagePath, staging);
        return await ArtifactHash.ComputeSha256Async(extracted.ArtifactContentPath, CancellationToken.None);
    }

    private static void Write(ZipArchive archive, string name, string text)
    {
        using var stream = archive.CreateEntry(name).Open();
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return Directory.Exists(linkPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private sealed record FakeArtifact(
        int ArtifactId,
        int AppId,
        string Version,
        string PackageType,
        string TargetName,
        string RelativePath,
        string Sha256);

    /// <summary>
    /// Models the registration contract: a failed registration stores nothing, as the
    /// single transaction guarantees in SQL.
    /// </summary>
    private sealed class FakeImportRepository : IArtifactZipImportRepository
    {
        public List<FakeArtifact> Artifacts { get; } = [];
        public int RegistrationFailuresRemaining { get; set; }
        public int RegistrationCalls { get; private set; }
        public int SameIdentityReplaceCalls { get; private set; }
        public (int AppId, string PackageType)? LastDuplicateLookupComponent { get; private set; }

        public Task<ArtifactZipImportAppDescriptor?> ResolveArtifactZipImportAppAsync(string moduleKey, string appKey, CancellationToken ct)
            => Task.FromResult<ArtifactZipImportAppDescriptor?>(new ArtifactZipImportAppDescriptor(1, moduleKey, appKey));

        public Task<ArtifactCompatibilitySlot> RequireCompatibleArtifactSlotAsync(int appId, string version, string packageType, string targetName, CancellationToken ct)
            => Task.FromResult(new ArtifactCompatibilitySlot("test-module", "1.0.0", "test-app", packageType, targetName, null, null, null));

        public Task<ArtifactZipImportDuplicateInfo?> FindImportedArtifactByIdentityAsync(int appId, string version, string packageType, string targetName, CancellationToken ct)
            => Task.FromResult(Artifacts
                .Where(a => a.AppId == appId && a.Version == version && a.PackageType == packageType && a.TargetName == targetName)
                .Select(ToInfo)
                .FirstOrDefault());

        public Task<ArtifactZipImportDuplicateInfo?> FindImportedArtifactBySha256Async(string sha256, int appId, string packageType, CancellationToken ct)
        {
            LastDuplicateLookupComponent = (appId, packageType);
            return Task.FromResult(Artifacts
                .Where(a => a.Sha256 == sha256)
                .OrderBy(a => a.AppId == appId && a.PackageType == packageType ? 0 : 1)
                .ThenBy(a => a.ArtifactId)
                .Select(ToInfo)
                .FirstOrDefault());
        }

        public Task<ImportedArtifactRegistration?> RegisterImportedArtifactWithConfigurationAsync(
            int appId,
            string version,
            string packageType,
            string targetName,
            string relativePath,
            string sha256,
            IReadOnlyList<ArtifactPackageConfigurationFile> configurationFiles,
            bool copyFromContinuitySource,
            CancellationToken ct)
        {
            RegistrationCalls++;
            if (RegistrationFailuresRemaining > 0)
            {
                RegistrationFailuresRemaining--;
                throw new InvalidOperationException("Simulated process death inside the registration transaction.");
            }

            if (Artifacts.Any(a => a.AppId == appId && a.Version == version && a.PackageType == packageType && a.TargetName == targetName))
            {
                return Task.FromResult<ImportedArtifactRegistration?>(null);
            }

            var artifactId = Artifacts.Count == 0 ? 1 : Artifacts.Max(a => a.ArtifactId) + 1;
            Artifacts.Add(new FakeArtifact(artifactId, appId, version, packageType, targetName, relativePath, sha256));
            var carryForward = new ArtifactConfigurationCarryForwardResult(
                "1.0.0",
                [new ArtifactConfigurationCarryForwardItem(ConfigPath, ArtifactConfigurationCarryForwardOutcome.Preserved)]);
            return Task.FromResult<ImportedArtifactRegistration?>(
                new ImportedArtifactRegistration(artifactId, configurationFiles.Count, carryForward, null));
        }

        public Task<(int ReplacedCount, ArtifactConfigurationCarryForwardResult CarryForward)> ReplaceArtifactConfigurationFilesResumingCarryForwardAsync(
            int artifactId,
            IReadOnlyList<ArtifactPackageConfigurationFile> configurationFiles,
            CancellationToken ct)
        {
            SameIdentityReplaceCalls++;
            return Task.FromResult((configurationFiles.Count, ArtifactConfigurationCarryForwardResult.Empty));
        }

        public Task<(int TemplateAppRowsUpdated, int AppInstanceRowsUpdated, int WorkerInstanceRowsUpdated, int ConfigurationRowsCarried, string? ConfigurationCarryMessage)> ApplyImportedArtifactToMatchingApplicationsAsync(int artifactId, CancellationToken ct)
            => Task.FromResult((0, 0, 0, 0, (string?)null));

        public Task UpdateImportedArtifactMetadataAsync(int artifactId, string relativePath, string sha256, CancellationToken ct)
            => Task.CompletedTask;

        public Task<int> ApplyImportedHostAgentArtifactToCurrentHostAsync(int artifactId, string hostKey, string? serviceNamePrefix, string? installRoot, CancellationToken ct)
            => Task.FromResult(0);

        public Task<WorkerHostVersionState> GetSelectedAndProvisionedWorkerHostVersionsAsync(string hostKey, string componentKey, CancellationToken cancellationToken)
            => Task.FromResult(new WorkerHostVersionState(null, null));

        public Task<ArtifactContentHashGap> GetArtifactContentHashGapAsync(CancellationToken ct) => throw Unused();

        public Task<(int TemplateAppRowsUpdated, int AppInstanceRowsUpdated, int WorkerInstanceRowsUpdated, int ConfigurationRowsCarried, string? ConfigurationCarryMessage)> ApplyLatestModuleArtifactsAsync(string moduleKey, CancellationToken ct)
            => throw Unused();

        public Task<(int DocumentId, bool Created, bool Replaced, bool WasIdentical)> SaveImportedHostConfigurationAsync(PortableHostConfigurationDocument input, bool replaceExisting, CancellationToken ct)
            => throw Unused();

        public Task<(int DocumentId, bool Created, bool Replaced, bool WasIdentical)> SaveImportedConfigOverlayAsync(PortableConfigOverlayDocument input, bool replaceExisting, CancellationToken ct)
            => throw Unused();

        public Task<(int CreatedCount, int UpdatedCount, int SkippedCount, int PermissionRowCount)> SaveImportedDashboardWidgetsAsync(PortableDashboardWidgetPackage input, CancellationToken ct)
            => throw Unused();

        public Task RecordDashboardWidgetImportFindingsAsync(IReadOnlyCollection<string> importedWidgetKeys, IReadOnlyCollection<PortableDashboardWidgetSkip> skippedWidgets, string sourceName, CancellationToken ct)
            => throw Unused();

        public Task<(int DataDocumentCount, int InsertedBinaryDataCount, int ReusedBinaryDataCount)> SaveImportedWidgetRuntimeDataAsync(PortableWidgetRuntimeDataPackage input, CancellationToken ct)
            => throw Unused();

        public Task<ModuleDefinitionSaveResult> SaveImportedModuleDefinitionAsync(ModuleDefinitionImportDocument input, bool replaceExisting, CancellationToken ct)
            => throw Unused();

        public Task<string?> GetAppliedModuleDefinitionVersionAsync(string moduleKey, CancellationToken ct) => throw Unused();

        public Task<int> ExecuteImportedModuleDefinitionSqlRepairsAsync(int moduleDefinitionDocumentId, CancellationToken ct) => throw Unused();

        public Task<int> ExecuteImportedModuleDefinitionSqlRepairsAsync(int moduleDefinitionDocumentId, IReadOnlySet<string>? scriptKeysToRepair, CancellationToken ct, SqlConnection conn)
            => throw Unused();

        public Task<bool> ApplyImportedModuleDefinitionAsync(int moduleDefinitionDocumentId, CancellationToken ct) => throw Unused();

        public Task<ModuleDefinitionSqlRepairScope> BeginModuleDefinitionSqlRepairAsync(CancellationToken ct) => throw Unused();

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetMissingRequiredObjectsByScriptKeyAsync(string definitionJson, CancellationToken ct)
            => throw Unused();

        public Task<IReadOnlyList<string>> GetPendingDefinitionScriptsAsync(int documentId, SqlConnection conn, CancellationToken ct) => throw Unused();

        public Task<int> InsertMissingPortalAppEntriesAsync(CancellationToken ct, Action<int>? onAttempt = null) => throw Unused();

        private static ArtifactZipImportDuplicateInfo ToInfo(FakeArtifact artifact)
            => new(artifact.ArtifactId, artifact.AppId, artifact.AppId == 1 ? "test-app" : "other-app", artifact.Version, artifact.PackageType, artifact.TargetName, artifact.RelativePath, artifact.Sha256);

        private static NotSupportedException Unused() => new("Not used by the artifact-package gates under test.");
    }

    private sealed class CapturingLogger : ILogger<ArtifactZipImportService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
