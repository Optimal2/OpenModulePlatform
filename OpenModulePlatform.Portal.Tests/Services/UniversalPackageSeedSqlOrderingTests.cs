using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.Portal.Options;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// A module definition's embedded SQL must run AFTER the same package's artifact
/// rows are registered in omp.Artifacts.
/// </summary>
/// <remarks>
/// Measured on linus_hemma 2026-08-25 (HostAgent 0.3.221, import of
/// omp-universal__global__20260825-2202.zip): IbsPackager's seed script
/// discovers "the latest channel-type artifact version" with a
/// SELECT TOP 1 ... FROM omp.Artifacts ... ORDER BY ArtifactId DESC. Because
/// the import applied the definition SQL before registering the package's
/// artifact rows, the seed saw 0.3.134 while the package carried 0.3.135, and
/// omp_ibs_packager.ChannelTypeVersions was left one import behind the plugin
/// the worker actually ran. Script execution is gated per definition version,
/// so an unchanged definition does not re-run on the next import -- the lag
/// never self-heals.
///
/// These tests rebuild that exact shape against the real core schema: a
/// previous import left artifact 0.3.134 and a seeded version table, and the
/// universal package under test carries a bumped definition plus artifact
/// 0.3.135. After the import, the seeded table must contain 0.3.135.
/// </remarks>
public sealed class UniversalPackageSeedSqlOrderingTests : IClassFixture<SeedSqlOrderingTestFixture>
{
    private const string PreviousArtifactVersion = "0.3.134";
    private const string PackageArtifactVersion = "0.3.135";
    private const string FixedArtifactVersion = "0.3.136";
    private const string AppKey = "worker";
    private const string PackageType = "channel-type";
    private const string TargetName = "filedrop";

    private readonly SeedSqlOrderingTestFixture _fixture;

    public UniversalPackageSeedSqlOrderingTests(SeedSqlOrderingTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task HostAgentImport_WhenPackageCarriesDefinitionAndArtifact_SeedSqlSeesThePackageArtifactVersion()
    {
        const string moduleKey = "seedorderhostagent";
        const string schemaName = "omp_seedorderhostagent";
        await ArrangePreviousImportStateAsync(moduleKey, schemaName);

        var workRoot = CreateWorkRoot();
        try
        {
            var importRoot = Directory.CreateDirectory(Path.Join(workRoot, "import")).FullName;
            var storeRoot = Directory.CreateDirectory(Path.Join(workRoot, "store")).FullName;
            var packagePath = BuildUniversalPackageZip(workRoot, moduleKey, schemaName);
            File.Copy(packagePath, Path.Join(importRoot, Path.GetFileName(packagePath)));

            await RunHostAgentImportAsync(importRoot, storeRoot);

            Assert.True(
                Directory.GetFiles(Path.Join(importRoot, "failed")).Length == 0,
                "The package landed in failed\\: " + ReadFailedImportReasons(importRoot));
            Assert.True(
                await _fixture.ArtifactExistsAsync(
                    await GetAppIdAsync(moduleKey),
                    PackageArtifactVersion,
                    PackageType,
                    TargetName),
                $"The package artifact {PackageArtifactVersion} was not registered in omp.Artifacts.");

            var seeded = await _fixture.GetSeededVersionsAsync(schemaName);
            Assert.Contains(PackageArtifactVersion, seeded);
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    [Fact]
    public async Task PortalImport_WhenPackageCarriesDefinitionAndArtifact_SeedSqlSeesThePackageArtifactVersion()
    {
        const string moduleKey = "seedorderportal";
        const string schemaName = "omp_seedorderportal";
        await ArrangePreviousImportStateAsync(moduleKey, schemaName);

        var workRoot = CreateWorkRoot();
        try
        {
            var storeRoot = Directory.CreateDirectory(Path.Join(workRoot, "store")).FullName;
            var packagePath = BuildUniversalPackageZip(workRoot, moduleKey, schemaName);
            var service = CreatePortalService(storeRoot);

            var result = await ImportPortalPackageAsync(service, packagePath);

            var failed = result.Items.Where(static item => item.Status == "Failed").ToList();
            Assert.True(
                failed.Count == 0,
                "Import items failed: " + string.Join("; ", failed.Select(static item => $"{item.Kind} {item.Path}: {item.Message}")));
            Assert.True(
                await _fixture.ArtifactExistsAsync(
                    await GetAppIdAsync(moduleKey),
                    PackageArtifactVersion,
                    PackageType,
                    TargetName),
                $"The package artifact {PackageArtifactVersion} was not registered in omp.Artifacts.");

            var seeded = await _fixture.GetSeededVersionsAsync(schemaName);
            Assert.Contains(PackageArtifactVersion, seeded);
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    /// <summary>
    /// Campaign importvagen-kor-inte-seed-skripten: the incident shape, end to end.
    /// A NEW definition version (new document, new script content hash for that
    /// document) is imported through the Portal universal package path while the
    /// module's validation probe reports HEALTHY -- the probe only checks that the
    /// seed table exists, which the previous import's state already satisfies.
    /// The Portal script gate used to read "probe healthy" as "nothing needs to
    /// run", so the definition was Applied with ZERO rows in
    /// omp.ModuleDefinitionSqlExecutions and the seed never recorded the new
    /// artifact version (measured in the customer environments over five
    /// ibs_packager versions). HostAgent and the bootstrapper run the scripts in
    /// exactly this state
    /// (OmpHostArtifactRepository.AnyModuleDefinitionScriptWithoutSucceededExecutionAsync);
    /// Portal must too.
    /// </summary>
    [Fact]
    public async Task PortalImport_WhenProbeIsHealthyAndDefinitionVersionIsNew_SqlScriptsStillRun()
    {
        const string moduleKey = "seedorderprobepo";
        const string schemaName = "omp_seedorderprobepo";
        const string definitionVersion = "1.0.1";
        await ArrangePreviousImportStateAsync(moduleKey, schemaName);

        var workRoot = CreateWorkRoot();
        try
        {
            var storeRoot = Directory.CreateDirectory(Path.Join(workRoot, "store")).FullName;
            var packagePath = BuildUniversalPackageZip(
                workRoot, moduleKey, schemaName, PackageArtifactVersion, includeValidationProbe: true);
            var service = CreatePortalService(storeRoot);

            var result = await ImportPortalPackageAsync(service, packagePath);

            var definitionItem = Assert.Single(
                result.Items,
                static item => item.Kind == "module-definition");
            Assert.Equal("Applied", definitionItem.Status);
            Assert.Equal(
                1,
                await _fixture.CountSucceededDefinitionSqlExecutionsAsync(moduleKey, definitionVersion));
            Assert.Contains(
                "Definition SQL scripts executed: 1.",
                definitionItem.Message,
                StringComparison.Ordinal);
            var seeded = await _fixture.GetSeededVersionsAsync(schemaName);
            Assert.Contains(PackageArtifactVersion, seeded);
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    /// <summary>
    /// The other half of the version gate: an unchanged definition re-imported after a
    /// successful run must NOT re-execute its scripts (executions are recorded per
    /// document + script + content hash), and the outcome line must say the skip is
    /// backed by an execution record -- not left silent.
    /// </summary>
    [Fact]
    public async Task PortalImport_WhenScriptsAlreadyHaveSucceededRecords_ReimportDoesNotReExecute()
    {
        const string moduleKey = "seedorderreimport";
        const string schemaName = "omp_seedorderreimport";
        const string definitionVersion = "1.0.1";
        await ArrangePreviousImportStateAsync(moduleKey, schemaName);

        var workRoot = CreateWorkRoot();
        try
        {
            var storeRoot = Directory.CreateDirectory(Path.Join(workRoot, "store")).FullName;
            var packagePath = BuildUniversalPackageZip(workRoot, moduleKey, schemaName);
            var service = CreatePortalService(storeRoot);

            await ImportPortalPackageAsync(service, packagePath);
            Assert.Equal(
                1,
                await _fixture.CountSucceededDefinitionSqlExecutionsAsync(moduleKey, definitionVersion));

            var second = await ImportPortalPackageAsync(service, packagePath);

            var definitionItem = Assert.Single(
                second.Items,
                static item => item.Kind == "module-definition");
            Assert.Equal("Applied", definitionItem.Status);
            Assert.Contains(
                "every declared script already has a successful execution record",
                definitionItem.Message,
                StringComparison.Ordinal);
            Assert.Equal(
                1,
                await _fixture.CountSucceededDefinitionSqlExecutionsAsync(moduleKey, definitionVersion));
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    /// <summary>
    /// Silence is not an acceptable outcome: when the definition declares SQL scripts
    /// but the import options disable the SQL phase, the result line must SAY the
    /// scripts did not run and why -- not render the same bare "Applied" as a run.
    /// </summary>
    [Fact]
    public async Task PortalImport_WhenSqlRepairsDisabled_MessageSaysScriptsWereNotExecuted()
    {
        const string moduleKey = "seedorderdisabled";
        const string schemaName = "omp_seedorderdisabled";
        const string definitionVersion = "1.0.1";
        await ArrangePreviousImportStateAsync(moduleKey, schemaName);

        var workRoot = CreateWorkRoot();
        try
        {
            var storeRoot = Directory.CreateDirectory(Path.Join(workRoot, "store")).FullName;
            var packagePath = BuildUniversalPackageZip(workRoot, moduleKey, schemaName);
            var service = CreatePortalService(storeRoot);

            var result = await ImportPortalPackageAsync(service, packagePath, executeSqlRepairs: false);

            var definitionItem = Assert.Single(
                result.Items,
                static item => item.Kind == "module-definition");
            Assert.Equal("Applied", definitionItem.Status);
            Assert.Contains(
                "none executed: SQL repairs were disabled for this import.",
                definitionItem.Message,
                StringComparison.Ordinal);
            Assert.Equal(
                0,
                await _fixture.CountSucceededDefinitionSqlExecutionsAsync(moduleKey, definitionVersion));
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    /// <summary>
    /// The remaining zero-execution shape: the definition is applied and the SQL phase
    /// is enabled, but the declared script can never run through Portal (execution mode
    /// "once" is Blocked by the script gate). The result line must list the script and
    /// its state instead of rendering a silent "Applied".
    /// </summary>
    [Fact]
    public async Task PortalImport_WhenDeclaredScriptIsBlocked_MessageListsTheScriptState()
    {
        const string moduleKey = "seedorderblocked";
        const string schemaName = "omp_seedorderblocked";
        const string definitionVersion = "1.0.1";
        await ArrangePreviousImportStateAsync(moduleKey, schemaName);

        var workRoot = CreateWorkRoot();
        try
        {
            var storeRoot = Directory.CreateDirectory(Path.Join(workRoot, "store")).FullName;
            var packagePath = BuildUniversalPackageZip(
                workRoot, moduleKey, schemaName, seedExecution: "once");
            var service = CreatePortalService(storeRoot);

            var result = await ImportPortalPackageAsync(service, packagePath);

            var definitionItem = Assert.Single(
                result.Items,
                static item => item.Kind == "module-definition");
            Assert.Equal("Applied", definitionItem.Status);
            Assert.Contains(
                "NONE were executed",
                definitionItem.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                "initialize-seed-order: Blocked",
                definitionItem.Message,
                StringComparison.Ordinal);
            Assert.Equal(
                0,
                await _fixture.CountSucceededDefinitionSqlExecutionsAsync(moduleKey, definitionVersion));
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    /// <summary>
    /// The residual incident path: the package's artifact FAILS to register (here: a
    /// version conflict), yet the definition's seed SQL used to run anyway and record a
    /// Succeeded execution. Because execution is version-gated, repairing the artifact
    /// and re-importing the unchanged definition never re-ran the seed -- the stale
    /// version was permanent. The SQL must be deferred while any artifact item failed,
    /// so the next clean import (no succeeded execution recorded) runs it.
    /// </summary>
    [Fact]
    public async Task HostAgentImport_WhenAnArtifactFails_SeedSqlIsDeferredUntilACleanImport()
    {
        const string moduleKey = "seedorderdeferha";
        const string schemaName = "omp_seedorderdeferha";
        var (_, appId) = await ArrangePreviousImportStateAsync(moduleKey, schemaName);
        await _fixture.InsertArtifactAsync(
            appId,
            PackageArtifactVersion,
            PackageType,
            TargetName,
            $"{moduleKey}/{PackageType}/{PackageArtifactVersion}",
            new string('b', 64));

        var workRoot = CreateWorkRoot();
        try
        {
            var importRoot = Directory.CreateDirectory(Path.Join(workRoot, "import")).FullName;
            var storeRoot = Directory.CreateDirectory(Path.Join(workRoot, "store")).FullName;

            var conflictedPackage = BuildUniversalPackageZip(
                workRoot, moduleKey, schemaName, PackageArtifactVersion, includeValidationProbe: true);
            File.Copy(conflictedPackage, Path.Join(importRoot, Path.GetFileName(conflictedPackage)));
            await RunHostAgentImportAsync(importRoot, storeRoot);

            Assert.NotEmpty(Directory.GetFiles(Path.Join(importRoot, "failed")));
            var seededAfterConflict = await _fixture.GetSeededVersionsAsync(schemaName);
            Assert.DoesNotContain(PackageArtifactVersion, seededAfterConflict);

            var fixedPackage = BuildUniversalPackageZip(
                workRoot, moduleKey, schemaName, FixedArtifactVersion, includeValidationProbe: true);
            File.Copy(fixedPackage, Path.Join(importRoot, Path.GetFileName(fixedPackage)));
            await RunHostAgentImportAsync(importRoot, storeRoot);

            var seeded = await _fixture.GetSeededVersionsAsync(schemaName);
            Assert.Contains(FixedArtifactVersion, seeded);
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    /// <summary>
    /// Same residual incident path as the HostAgent variant, through the Portal. The
    /// definition carries no validation probe; the probe-carrying shape is covered by
    /// PortalImport_WhenProbeIsHealthyAndDefinitionVersionIsNew_SqlScriptsStillRun.
    /// </summary>
    [Fact]
    public async Task PortalImport_WhenAnArtifactFails_SeedSqlIsDeferredUntilACleanImport()
    {
        const string moduleKey = "seedorderdeferpo";
        const string schemaName = "omp_seedorderdeferpo";
        var (_, appId) = await ArrangePreviousImportStateAsync(moduleKey, schemaName);
        await _fixture.InsertArtifactAsync(
            appId,
            PackageArtifactVersion,
            PackageType,
            TargetName,
            $"{moduleKey}/{PackageType}/{PackageArtifactVersion}",
            new string('b', 64));

        var workRoot = CreateWorkRoot();
        try
        {
            var storeRoot = Directory.CreateDirectory(Path.Join(workRoot, "store")).FullName;
            var service = CreatePortalService(storeRoot);

            var conflictedPackage = BuildUniversalPackageZip(
                workRoot, moduleKey, schemaName, PackageArtifactVersion);
            var conflictedResult = await ImportPortalPackageAsync(service, conflictedPackage);

            Assert.Contains(
                conflictedResult.Items,
                static item => item.Kind == "artifact-package" && item.Status == "Failed");
            var seededAfterConflict = await _fixture.GetSeededVersionsAsync(schemaName);
            Assert.DoesNotContain(PackageArtifactVersion, seededAfterConflict);

            var fixedPackage = BuildUniversalPackageZip(
                workRoot, moduleKey, schemaName, FixedArtifactVersion);
            var fixedResult = await ImportPortalPackageAsync(service, fixedPackage);

            var failed = fixedResult.Items.Where(static item => item.Status == "Failed").ToList();
            Assert.True(
                failed.Count == 0,
                "Import items failed: " + string.Join("; ", failed.Select(static item => $"{item.Kind} {item.Path}: {item.Message}")));
            var seeded = await _fixture.GetSeededVersionsAsync(schemaName);
            Assert.Contains(FixedArtifactVersion, seeded);
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    /// <summary>
    /// When the definition's SQL fails AFTER the artifacts imported, the artifacts must
    /// keep their real import result and must not be imported a second time through the
    /// standalone artifact fall-through (which would rewrite configuration rows and
    /// report the fresh import as an identical skip).
    /// </summary>
    [Fact]
    public async Task PortalImport_WhenDefinitionSqlFails_ArtifactsAreImportedExactlyOnce()
    {
        const string moduleKey = "seedordersqlpo";
        const string schemaName = "omp_seedordersqlpo";
        const string brokenSeedSql = "SET NOCOUNT ON; THROW 51000, N'seed intentionally broken for the ordering test', 1;";

        var workRoot = CreateWorkRoot();
        try
        {
            var storeRoot = Directory.CreateDirectory(Path.Join(workRoot, "store")).FullName;
            var service = CreatePortalService(storeRoot);
            var packagePath = BuildUniversalPackageZip(
                workRoot, moduleKey, schemaName, PackageArtifactVersion, includeValidationProbe: false, seedSqlOverride: brokenSeedSql);

            var result = await ImportPortalPackageAsync(service, packagePath);

            Assert.Contains(
                result.Items,
                static item => item.Kind == "module-definition"
                    && item.Status == "Failed"
                    && item.Message is not null
                    && item.Message.Contains("seed intentionally broken", StringComparison.OrdinalIgnoreCase));
            var artifactItem = Assert.Single(result.Items.Where(static item => item.Kind == "artifact-package"));
            Assert.True(
                artifactItem.Status is "Imported" or "Replaced",
                $"The artifact must keep its original import result; got '{artifactItem.Status}': {artifactItem.Message}");
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    /// <summary>HostAgent sibling of the SQL-failure case: the package routes to failed\
    /// with the SQL error, and the artifact registration made before the failure stays
    /// recorded.</summary>
    [Fact]
    public async Task HostAgentImport_WhenDefinitionSqlFails_ArtifactRegistrationSurvives()
    {
        const string moduleKey = "seedordersqlha";
        const string schemaName = "omp_seedordersqlha";
        const string brokenSeedSql = "SET NOCOUNT ON; THROW 51000, N'seed intentionally broken for the ordering test', 1;";
        var moduleId = await _fixture.InsertModuleAsync(moduleKey, schemaName);
        var appId = await _fixture.InsertAppAsync(moduleId, AppKey);

        var workRoot = CreateWorkRoot();
        try
        {
            var importRoot = Directory.CreateDirectory(Path.Join(workRoot, "import")).FullName;
            var storeRoot = Directory.CreateDirectory(Path.Join(workRoot, "store")).FullName;
            var packagePath = BuildUniversalPackageZip(
                workRoot, moduleKey, schemaName, PackageArtifactVersion, includeValidationProbe: false, seedSqlOverride: brokenSeedSql);
            File.Copy(packagePath, Path.Join(importRoot, Path.GetFileName(packagePath)));

            await RunHostAgentImportAsync(importRoot, storeRoot);

            Assert.Contains("seed intentionally broken", ReadFailedImportReasons(importRoot), StringComparison.OrdinalIgnoreCase);
            Assert.True(
                await _fixture.ArtifactExistsAsync(appId, PackageArtifactVersion, PackageType, TargetName),
                $"The artifact {PackageArtifactVersion} registered before the SQL failure must stay recorded.");
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    /// <summary>
    /// The state a previous import leaves behind: module and app rows, the
    /// previous artifact version in omp.Artifacts, and the module's version
    /// table seeded with that previous version.
    /// </summary>
    private async Task<(int ModuleId, int AppId)> ArrangePreviousImportStateAsync(string moduleKey, string schemaName)
    {
        var moduleId = await _fixture.InsertModuleAsync(moduleKey, schemaName);
        var appId = await _fixture.InsertAppAsync(moduleId, AppKey);
        await _fixture.InsertArtifactAsync(
            appId,
            PreviousArtifactVersion,
            PackageType,
            TargetName,
            $"{moduleKey}/{PackageType}/{PreviousArtifactVersion}",
            new string('a', 64));
        await _fixture.ExecuteAsync(
            $"IF SCHEMA_ID(N'{schemaName}') IS NULL EXEC(N'CREATE SCHEMA [{schemaName}]');",
            $"IF OBJECT_ID(N'{schemaName}.SeededVersions', N'U') IS NULL CREATE TABLE [{schemaName}].[SeededVersions] ([Version] nvarchar(50) NOT NULL PRIMARY KEY);",
            $"IF NOT EXISTS (SELECT 1 FROM [{schemaName}].[SeededVersions] WHERE [Version] = N'{PreviousArtifactVersion}') INSERT INTO [{schemaName}].[SeededVersions] ([Version]) VALUES (N'{PreviousArtifactVersion}');");
        return (moduleId, appId);
    }

    private async Task RunHostAgentImportAsync(string importRoot, string storeRoot)
    {
        var settings = new HostAgentSettings
        {
            CentralArtifactRoot = storeRoot,
            ArtifactZipImport = new HostAgentArtifactZipImportSettings
            {
                IsEnabled = true,
                ImportPath = importRoot
            }
        };
        var service = new ArtifactZipImportService(
            new StaticOptionsMonitor<HostAgentSettings>(settings),
            _fixture.CreateHostAgentRepository(),
            NullLogger<ArtifactZipImportService>.Instance);
        await service.ImportPendingAsync(CancellationToken.None);
    }

    private PortableModulePackageService CreatePortalService(string storeRoot)
    {
        var portalFactory = _fixture.CreatePortalConnectionFactory();
        return new PortableModulePackageService(
            _fixture.CreatePortalRepository(),
            Microsoft.Extensions.Options.Options.Create(new ArtifactUploadOptions
            {
                ArtifactStoreRoot = storeRoot
            }),
            new PortalDashboardWidgetPackageService(portalFactory),
            new PortalWidgetRuntimeDataPackageService(portalFactory),
            NullLogger<PortableModulePackageService>.Instance);
    }

    private static async Task<UniversalPackageImportResult> ImportPortalPackageAsync(
        PortableModulePackageService service,
        string packagePath,
        bool executeSqlRepairs = true)
    {
        await using var stream = File.OpenRead(packagePath);
        var upload = new FormFile(stream, 0, stream.Length, "packageFile", Path.GetFileName(packagePath));
        return await service.ImportUniversalPackageUploadAsync(
            upload,
            new PortableModulePackageImportOptions(
                ApplyModuleDefinition: true,
                ExecuteSqlRepairs: executeSqlRepairs,
                AllowTemporaryIncompatibleArtifacts: false,
                ReplaceExistingModuleDefinition: false,
                ReplaceExistingArtifacts: false,
                ReplaceExistingDashboardWidgets: false,
                CopyConfigurationFilesFromPreviousVersion: true,
                UseArtifactsImmediately: true),
            replaceExistingConfigObjects: false,
            CancellationToken.None);
    }

    private async Task<int> GetAppIdAsync(string moduleKey)
    {
        var repository = _fixture.CreateHostAgentRepository();
        var app = await repository.ResolveArtifactZipImportAppAsync(moduleKey, AppKey, CancellationToken.None);
        Assert.NotNull(app);
        return app.AppId;
    }

    /// <summary>
    /// Builds a universal module package zip whose definition seeds a version
    /// table from the latest omp.Artifacts row, exactly like IbsPackager's
    /// 2-initialize-ibspackager.sql @Version discovery, and whose artifact
    /// carries a newer version than the database currently holds.
    /// </summary>
    private static string BuildUniversalPackageZip(
        string workRoot,
        string moduleKey,
        string schemaName,
        string artifactVersion = PackageArtifactVersion,
        bool includeValidationProbe = false,
        string? seedSqlOverride = null,
        string seedExecution = "idempotent")
    {
        var packagePath = Path.Join(workRoot, $"omp-universal__{moduleKey}__{artifactVersion}.zip");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        WriteTextEntry(
            archive,
            UniversalModulePackageReaderManifestName,
            """{"formatVersion":1,"packageKey":"seed-order-test","packageVersion":"1.0.1"}""");
        WriteTextEntry(
            archive,
            $"module-definitions/{moduleKey}.module-definition.json",
            BuildDefinitionJson(moduleKey, schemaName, includeValidationProbe, seedSqlOverride, seedExecution));
        WriteArtifactZipEntry(
            archive,
            $"artifacts/{moduleKey}__{AppKey}__{PackageType}__{TargetName}__{artifactVersion}.zip",
            $"{moduleKey} plugin payload {artifactVersion}");
        return packagePath;
    }

    private const string UniversalModulePackageReaderManifestName = "omp-universal-package.json";

    private static string BuildDefinitionJson(
        string moduleKey,
        string schemaName,
        bool includeValidationProbe,
        string? seedSqlOverride,
        string seedExecution = "idempotent")
    {
        var seedSql = seedSqlOverride ?? (
            "DECLARE @Version nvarchar(50); " +
            "SELECT TOP (1) @Version = a.[Version] " +
            "FROM omp.Artifacts a " +
            "INNER JOIN omp.Apps ap ON ap.AppId = a.AppId " +
            "INNER JOIN omp.Modules m ON m.ModuleId = ap.ModuleId " +
            $"WHERE m.ModuleKey = N'{moduleKey}' AND a.PackageType = N'{PackageType}' AND a.IsEnabled = 1 " +
            "ORDER BY a.ArtifactId DESC; " +
            $"IF SCHEMA_ID(N'{schemaName}') IS NULL EXEC(N'CREATE SCHEMA [{schemaName}]'); " +
            $"IF OBJECT_ID(N'{schemaName}.SeededVersions', N'U') IS NULL CREATE TABLE [{schemaName}].[SeededVersions] ([Version] nvarchar(50) NOT NULL PRIMARY KEY); " +
            $"IF @Version IS NOT NULL AND NOT EXISTS (SELECT 1 FROM [{schemaName}].[SeededVersions] WHERE [Version] = @Version) " +
            $"INSERT INTO [{schemaName}].[SeededVersions] ([Version]) VALUES (@Version);");

        var sqlScripts = new JsonArray(new JsonObject
        {
            ["key"] = "initialize-seed-order",
            ["phase"] = "setup",
            ["order"] = 10,
            ["execution"] = seedExecution,
            ["inlineSql"] = seedSql
        });
        if (includeValidationProbe)
        {
            // Shaped like IbsPackager's 0-validate script: a healthy probe makes the
            // repair decision fall to "has every script a succeeded execution?", which
            // is the version gate that made the incident's seed lag permanent.
            var validateSql =
                "SET NOCOUNT ON; SELECT CAST(CASE WHEN OBJECT_ID(N'" + schemaName + ".SeededVersions', N'U') IS NOT NULL " +
                "THEN 1 ELSE 0 END AS bit) AS IsHealthy, N'seed order probe' AS Message;";
            sqlScripts.Add(new JsonObject
            {
                ["key"] = "validate-seed-order",
                ["phase"] = "validate",
                ["order"] = 0,
                ["execution"] = "validation",
                ["inlineSql"] = validateSql
            });
        }

        var integrity = seedSqlOverride is null
            ? new JsonObject
            {
                ["requiredSchemas"] = new JsonArray(schemaName),
                ["requiredTables"] = new JsonArray(new JsonObject
                {
                    ["schema"] = schemaName,
                    ["name"] = "SeededVersions",
                    ["source"] = "initialize-seed-order"
                })
            }
            : new JsonObject();

        var definition = new JsonObject
        {
            ["moduleKey"] = moduleKey,
            ["definitionVersion"] = "1.0.1",
            ["formatVersion"] = 1,
            ["module"] = new JsonObject
            {
                ["displayName"] = "Seed order test module",
                ["moduleType"] = "WorkerModule",
                ["schemaName"] = schemaName
            },
            ["apps"] = new JsonArray(new JsonObject
            {
                ["appKey"] = AppKey,
                ["displayName"] = "Seed order worker",
                ["appType"] = "Worker"
            }),
            ["compatibleArtifacts"] = new JsonArray(new JsonObject
            {
                ["appKey"] = AppKey,
                ["packageType"] = PackageType,
                ["targetName"] = TargetName
            }),
            ["sqlScripts"] = sqlScripts,
            ["integrity"] = integrity
        };
        return definition.ToJsonString();
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static void WriteArtifactZipEntry(ZipArchive archive, string entryName, string payloadContent)
    {
        var entry = archive.CreateEntry(entryName);
        using var entryStream = entry.Open();
        using var artifactZip = new ZipArchive(entryStream, ZipArchiveMode.Create);
        var payload = artifactZip.CreateEntry("plugin.txt");
        using var writer = new StreamWriter(payload.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(payloadContent);
    }

    private static string CreateWorkRoot()
    {
        var root = Path.Join(Path.GetTempPath(), "OmpSeedSqlOrderingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ReadFailedImportReasons(string importRoot)
    {
        var failedRoot = Path.Join(importRoot, "failed");
        if (!Directory.Exists(failedRoot))
        {
            return "(no failed folder)";
        }

        var reasons = Directory.GetFiles(failedRoot, "*.error.txt")
            .Select(static path => $"{Path.GetFileName(path)}: {File.ReadAllText(path)}")
            .ToList();
        return reasons.Count > 0 ? string.Join("; ", reasons) : "(no error sidecars)";
    }

    private static void TryDeleteDirectory(string path)
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
            // Best-effort cleanup of the temp work root.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of the temp work root.
        }
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
        where T : class
    {
        public T CurrentValue => currentValue;

        public T Get(string? name) => currentValue;

        public IDisposable OnChange(Action<T, string?> listener) => new NullDisposable();

        private sealed class NullDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
