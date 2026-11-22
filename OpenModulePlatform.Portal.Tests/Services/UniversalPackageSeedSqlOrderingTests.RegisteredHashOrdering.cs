using System.IO.Compression;
using System.Text;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Re-import of an artifact version whose omp.Artifacts.Sha256 was registered before the
/// directory hash ordered relative paths with the ordinal comparer.
/// </summary>
/// <remarks>
/// For a payload whose file names sort differently under the two comparers ("B.txt" and
/// "a.txt") the registered value no longer equals the hash an import computes for the very
/// same bytes. Both import paths compare a freshly computed hash with the registered one, so
/// both must read unchanged content as identical rather than as different content under the
/// same version. The registered value is computed by PreOrdinalDirectoryHash, an independent
/// copy of the old definition.
/// </remarks>
public sealed partial class UniversalPackageSeedSqlOrderingTests
{
    private static readonly (string Name, string Content)[] OrderSensitivePayload =
    [
        ("B.txt", "bravo"),
        ("a.txt", "alpha"),
        ("Zeta/x", "zeta-x"),
        ("alpha/y", "alpha-y")
    ];

    [Fact]
    public async Task PortalImport_WhenTheRegisteredHashUsesTheOldPathOrdering_UnchangedContentIsIdentical()
    {
        const string moduleKey = "legacyhashportal";
        const string schemaName = "omp_legacyhashportal";
        var workRoot = CreateWorkRoot();
        try
        {
            var storeRoot = Directory.CreateDirectory(Path.Join(workRoot, "store")).FullName;
            var (artifactId, registeredHash) = await ArrangeArtifactRegisteredWithOldPathOrderingAsync(workRoot, moduleKey, schemaName);
            var packagePath = BuildOrderSensitiveUniversalPackageZip(workRoot, moduleKey, schemaName, bravoContent: "bravo");

            var result = await ImportPortalPackageAsync(CreatePortalService(storeRoot), packagePath);

            var failed = result.Items.Where(static item => item.Status == "Failed").ToList();
            Assert.True(
                failed.Count == 0,
                "Import items failed: " + string.Join("; ", failed.Select(static item => $"{item.Kind} {item.Path}: {item.Message}")));
            Assert.Contains(
                result.Items,
                static item => (item.Message ?? string.Empty).Contains("same artifact identity and content already exists", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(registeredHash, await ReadArtifactSha256Async(artifactId));
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    [Fact]
    public async Task PortalImport_WhenTheRegisteredHashUsesTheOldPathOrdering_ChangedContentIsStillRefused()
    {
        const string moduleKey = "legacyhashportalchanged";
        const string schemaName = "omp_legacyhashportalchanged";
        var workRoot = CreateWorkRoot();
        try
        {
            var storeRoot = Directory.CreateDirectory(Path.Join(workRoot, "store")).FullName;
            var (artifactId, registeredHash) = await ArrangeArtifactRegisteredWithOldPathOrderingAsync(workRoot, moduleKey, schemaName);
            var packagePath = BuildOrderSensitiveUniversalPackageZip(workRoot, moduleKey, schemaName, bravoContent: "BRAVO");

            var result = await ImportPortalPackageAsync(CreatePortalService(storeRoot), packagePath);

            Assert.Contains(
                result.Items,
                static item => item.Status == "Failed"
                    && (item.Message ?? string.Empty).Contains("different content", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(registeredHash, await ReadArtifactSha256Async(artifactId));
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    [Fact]
    public async Task HostAgentImport_WhenTheRegisteredHashUsesTheOldPathOrdering_UnchangedContentIsIdentical()
    {
        const string moduleKey = "legacyhashhostagent";
        const string schemaName = "omp_legacyhashhostagent";
        var workRoot = CreateWorkRoot();
        try
        {
            var importRoot = Directory.CreateDirectory(Path.Join(workRoot, "import")).FullName;
            var storeRoot = Directory.CreateDirectory(Path.Join(workRoot, "store")).FullName;
            var (artifactId, registeredHash) = await ArrangeArtifactRegisteredWithOldPathOrderingAsync(workRoot, moduleKey, schemaName);
            var packagePath = BuildOrderSensitiveUniversalPackageZip(workRoot, moduleKey, schemaName, bravoContent: "bravo");
            File.Copy(packagePath, Path.Join(importRoot, Path.GetFileName(packagePath)));

            await RunHostAgentImportAsync(importRoot, storeRoot);

            var failedRoot = Path.Join(importRoot, "failed");
            Assert.True(
                !Directory.Exists(failedRoot) || Directory.GetFiles(failedRoot).Length == 0,
                "The package landed in failed\\: " + ReadFailedImportReasons(importRoot));
            Assert.Equal(registeredHash, await ReadArtifactSha256Async(artifactId));
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    /// <summary>
    /// Module and app rows plus the package's own artifact version, registered with the hash
    /// the old (case-insensitive) path ordering gives for the unchanged payload.
    /// </summary>
    private async Task<(int ArtifactId, string RegisteredHash)> ArrangeArtifactRegisteredWithOldPathOrderingAsync(
        string workRoot,
        string moduleKey,
        string schemaName)
    {
        var payloadRoot = Path.Join(workRoot, "registered-payload");
        foreach (var (name, content) in OrderSensitivePayload)
        {
            var path = Path.Join(payloadRoot, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        // From the independent copy of the old algorithm, never from the code under test.
        var registeredHash = PreOrdinalDirectoryHash.Compute(payloadRoot);
        Assert.NotEqual(registeredHash, await ArtifactHash.ComputeSha256Async(payloadRoot, CancellationToken.None));

        var moduleId = await _fixture.InsertModuleAsync(moduleKey, schemaName);
        var appId = await _fixture.InsertAppAsync(moduleId, AppKey);
        var artifactId = await _fixture.InsertArtifactAsync(
            appId,
            PackageArtifactVersion,
            PackageType,
            TargetName,
            $"{moduleKey}/{PackageType}/{PackageArtifactVersion}",
            registeredHash);
        return (artifactId, registeredHash);
    }

    private static string BuildOrderSensitiveUniversalPackageZip(
        string workRoot,
        string moduleKey,
        string schemaName,
        string bravoContent)
    {
        var packagePath = Path.Join(workRoot, $"omp-universal__{moduleKey}__{PackageArtifactVersion}.zip");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        WriteTextEntry(
            archive,
            UniversalModulePackageReaderManifestName,
            """{"formatVersion":1,"packageKey":"legacy-hash-test","packageVersion":"1.0.1"}""");
        WriteTextEntry(
            archive,
            $"module-definitions/{moduleKey}.module-definition.json",
            BuildDefinitionJson(moduleKey, schemaName, includeValidationProbe: false, seedSqlOverride: null));

        var entry = archive.CreateEntry($"artifacts/{moduleKey}__{AppKey}__{PackageType}__{TargetName}__{PackageArtifactVersion}.zip");
        using var entryStream = entry.Open();
        using var artifactZip = new ZipArchive(entryStream, ZipArchiveMode.Create);
        foreach (var (name, content) in OrderSensitivePayload)
        {
            WriteTextEntry(artifactZip, name, name == "B.txt" ? bravoContent : content);
        }

        return packagePath;
    }

    private async Task<string?> ReadArtifactSha256Async(int artifactId)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT Sha256 FROM omp.Artifacts WHERE ArtifactId = @artifactId;", conn);
        cmd.Parameters.AddWithValue("@artifactId", artifactId);
        return await cmd.ExecuteScalarAsync() as string;
    }
}
