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

            var portalFactory = _fixture.CreatePortalConnectionFactory();
            var service = new PortableModulePackageService(
                _fixture.CreatePortalRepository(),
                Microsoft.Extensions.Options.Options.Create(new ArtifactUploadOptions
                {
                    ArtifactStoreRoot = storeRoot
                }),
                new PortalDashboardWidgetPackageService(portalFactory),
                new PortalWidgetRuntimeDataPackageService(portalFactory),
                NullLogger<PortableModulePackageService>.Instance);

            UniversalPackageImportResult result;
            await using (var stream = File.OpenRead(packagePath))
            {
                var upload = new FormFile(stream, 0, stream.Length, "packageFile", Path.GetFileName(packagePath));
                result = await service.ImportUniversalPackageUploadAsync(
                    upload,
                    new PortableModulePackageImportOptions(
                        ApplyModuleDefinition: true,
                        ExecuteSqlRepairs: true,
                        AllowTemporaryIncompatibleArtifacts: false,
                        ReplaceExistingModuleDefinition: false,
                        ReplaceExistingArtifacts: false,
                        ReplaceExistingDashboardWidgets: false,
                        CopyConfigurationFilesFromPreviousVersion: true,
                        UseArtifactsImmediately: true),
                    replaceExistingConfigObjects: false,
                    CancellationToken.None);
            }

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
    /// The state a previous import leaves behind: module and app rows, the
    /// previous artifact version in omp.Artifacts, and the module's version
    /// table seeded with that previous version.
    /// </summary>
    private async Task ArrangePreviousImportStateAsync(string moduleKey, string schemaName)
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
    private static string BuildUniversalPackageZip(string workRoot, string moduleKey, string schemaName)
    {
        var packagePath = Path.Join(workRoot, $"omp-universal__{moduleKey}__test.zip");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        WriteTextEntry(
            archive,
            UniversalModulePackageReaderManifestName,
            """{"formatVersion":1,"packageKey":"seed-order-test","packageVersion":"1.0.1"}""");
        WriteTextEntry(
            archive,
            $"module-definitions/{moduleKey}.module-definition.json",
            BuildDefinitionJson(moduleKey, schemaName));
        WriteArtifactZipEntry(
            archive,
            $"artifacts/{moduleKey}__{AppKey}__{PackageType}__{TargetName}__{PackageArtifactVersion}.zip",
            $"{moduleKey} plugin payload {PackageArtifactVersion}");
        return packagePath;
    }

    private const string UniversalModulePackageReaderManifestName = "omp-universal-package.json";

    private static string BuildDefinitionJson(string moduleKey, string schemaName)
    {
        var seedSql =
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
            $"INSERT INTO [{schemaName}].[SeededVersions] ([Version]) VALUES (@Version);";

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
            ["sqlScripts"] = new JsonArray(new JsonObject
            {
                ["key"] = "initialize-seed-order",
                ["phase"] = "setup",
                ["order"] = 10,
                ["execution"] = "idempotent",
                ["inlineSql"] = seedSql
            }),
            ["integrity"] = new JsonObject
            {
                ["requiredSchemas"] = new JsonArray(schemaName),
                ["requiredTables"] = new JsonArray(new JsonObject
                {
                    ["schema"] = schemaName,
                    ["name"] = "SeededVersions",
                    ["source"] = "initialize-seed-order"
                })
            }
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
