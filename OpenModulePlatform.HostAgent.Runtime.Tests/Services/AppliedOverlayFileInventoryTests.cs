using System.Runtime.Versioning;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using Xunit;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

[SupportedOSPlatform("windows")]
public sealed class AppliedOverlayFileInventoryTests : IDisposable
{
    private const string SkipReason =
        "This process cannot create a directory link (symlink privilege or Developer Mode required); the guard was not exercised.";

    private readonly string _root = Path.Join(
        Path.GetTempPath(), "omp-overlay-inventory-" + Guid.NewGuid().ToString("N"));

    private readonly Guid _appInstanceId = Guid.NewGuid();

    public AppliedOverlayFileInventoryTests() => Directory.CreateDirectory(_root);

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
            // Best-effort temp cleanup.
        }
    }

    private string NewTarget()
    {
        var target = Path.Join(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        return target;
    }

    private string NewStateRoot()
    {
        var stateRoot = Path.Join(_root, "state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateRoot);
        return stateRoot;
    }

    private string WriteRawTrusted(string stateRoot, string target, string json)
    {
        var path = AppliedOverlayFileInventory.GetTrustedPath(stateRoot, target, _appInstanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    private string ValidJson(string target, string filesJson)
        => $$"""
            { "SchemaName": "OpenModulePlatform.AppliedOverlayFiles.v1", "AppInstanceId": "{{_appInstanceId}}", "TargetPath": {{System.Text.Json.JsonSerializer.Serialize(AppliedOverlayFileInventory.NormalizeTargetPath(target))}}, "Files": {{filesJson}} }
            """;

    [Fact]
    public void TryRead_NullFiles_ReturnsActionableDiagnosticWithoutThrowing()
    {
        var target = NewTarget();
        var stateRoot = NewStateRoot();
        WriteRawTrusted(stateRoot, target, ValidJson(target, "null"));

        var inventory = AppliedOverlayFileInventory.TryRead(stateRoot, target, _appInstanceId, out var diagnostic);

        Assert.Null(inventory);
        Assert.NotNull(diagnostic);
        Assert.Contains("Delete the inventory file", diagnostic, StringComparison.Ordinal);
        Assert.Contains("No file was removed", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_NullEntry_ReturnsActionableDiagnosticWithoutThrowing()
    {
        var target = NewTarget();
        var stateRoot = NewStateRoot();
        WriteRawTrusted(stateRoot, target, ValidJson(target, "[null]"));

        var inventory = AppliedOverlayFileInventory.TryRead(stateRoot, target, _appInstanceId, out var diagnostic);

        Assert.Null(inventory);
        Assert.NotNull(diagnostic);
    }

    [Fact]
    public void TryRead_UnsupportedSchema_DoesNotEchoUntrustedValue()
    {
        var target = NewTarget();
        var stateRoot = NewStateRoot();
        WriteRawTrusted(stateRoot, target, """
            { "SchemaName": "SECRET-SCHEMA-VALUE", "AppInstanceId": "00000000-0000-0000-0000-000000000000", "Files": [] }
            """);

        Assert.Null(AppliedOverlayFileInventory.TryRead(stateRoot, target, _appInstanceId, out var diagnostic));
        Assert.NotNull(diagnostic);
        Assert.DoesNotContain("SECRET-SCHEMA-VALUE", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_CaseVariantSchemaName_IsRejectedNotDefaulted()
    {
        var target = NewTarget();
        var stateRoot = NewStateRoot();
        // "schemaName" (wrong case) must not silently keep a default schema value (F8).
        WriteRawTrusted(stateRoot, target, $$"""
            { "schemaName": "OpenModulePlatform.AppliedOverlayFiles.v1", "AppInstanceId": "{{_appInstanceId}}", "TargetPath": {{System.Text.Json.JsonSerializer.Serialize(AppliedOverlayFileInventory.NormalizeTargetPath(target))}}, "Files": [] }
            """);

        Assert.Null(AppliedOverlayFileInventory.TryRead(stateRoot, target, _appInstanceId, out var diagnostic));
        Assert.NotNull(diagnostic);
        Assert.Contains("No file was removed", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_EntryMissingContentLength_IsRejected()
    {
        var target = NewTarget();
        var stateRoot = NewStateRoot();
        // A missing ContentLength must not silently default to 0 (which would accept
        // any file the length check is meant to gate).
        WriteRawTrusted(stateRoot, target, ValidJson(target, $$"""[{ "RelativePath": "odv.site.config.js", "ContentSha256": "{{new string('a', 64)}}" }]"""));

        Assert.Null(AppliedOverlayFileInventory.TryRead(stateRoot, target, _appInstanceId, out var diagnostic));
        Assert.NotNull(diagnostic);
        Assert.Contains("No file was removed", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_EntryCaseVariantContentLength_IsRejected()
    {
        var target = NewTarget();
        var stateRoot = NewStateRoot();
        // "contentlength" (wrong case) must not stand in for the exact property.
        WriteRawTrusted(stateRoot, target, ValidJson(target, $$"""[{ "RelativePath": "odv.site.config.js", "contentlength": 3, "ContentSha256": "{{new string('a', 64)}}" }]"""));

        Assert.Null(AppliedOverlayFileInventory.TryRead(stateRoot, target, _appInstanceId, out var diagnostic));
        Assert.NotNull(diagnostic);
        Assert.Contains("No file was removed", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_ExplicitZeroContentLength_IsAccepted()
    {
        var target = NewTarget();
        var stateRoot = NewStateRoot();
        // An explicitly recorded zero length (a genuinely empty file) stays valid.
        WriteRawTrusted(stateRoot, target, ValidJson(target, """[{ "RelativePath": "empty.js", "ContentLength": 0, "ContentSha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" }]"""));

        var inventory = AppliedOverlayFileInventory.TryRead(stateRoot, target, _appInstanceId, out var diagnostic);

        Assert.Null(diagnostic);
        Assert.NotNull(inventory);
        var entry = Assert.Single(inventory.Files);
        Assert.Equal(0, entry.ContentLength);
    }

    [Fact]
    public void TryRead_MissingSchemaName_IsRejected()
    {
        var target = NewTarget();
        var stateRoot = NewStateRoot();
        WriteRawTrusted(stateRoot, target, $$"""
            { "AppInstanceId": "{{_appInstanceId}}", "TargetPath": {{System.Text.Json.JsonSerializer.Serialize(AppliedOverlayFileInventory.NormalizeTargetPath(target))}}, "Files": [] }
            """);

        Assert.Null(AppliedOverlayFileInventory.TryRead(stateRoot, target, _appInstanceId, out var diagnostic));
        Assert.NotNull(diagnostic);
    }

    [Fact]
    public void TryRead_ForeignTargetBinding_IsRejected()
    {
        var target = NewTarget();
        var otherTarget = NewTarget();
        var stateRoot = NewStateRoot();
        WriteRawTrusted(stateRoot, target, ValidJson(otherTarget, "[]"));

        Assert.Null(AppliedOverlayFileInventory.TryRead(stateRoot, target, _appInstanceId, out var diagnostic));
        Assert.NotNull(diagnostic);
    }

    [Fact]
    public void TryRead_OversizedInventory_IsRejectedBeforeDeserialize()
    {
        var target = NewTarget();
        var stateRoot = NewStateRoot();
        var path = AppliedOverlayFileInventory.GetTrustedPath(stateRoot, target, _appInstanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string(' ', AppliedOverlayFileInventory.MaxInventoryBytes + 1));

        Assert.Null(AppliedOverlayFileInventory.TryRead(stateRoot, target, _appInstanceId, out var diagnostic));
        Assert.Contains("exceeds", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_TornInventory_ReportsRecoverableDiagnostic()
    {
        var target = NewTarget();
        var stateRoot = NewStateRoot();
        WriteRawTrusted(stateRoot, target, "{ \"SchemaName\": \"OpenModulePlatform.AppliedOverlayFiles.v1\", \"Files\": [");

        Assert.Null(AppliedOverlayFileInventory.TryRead(stateRoot, target, _appInstanceId, out var diagnostic));
        Assert.NotNull(diagnostic);
        Assert.Contains("Delete the inventory file", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_LegacyAppDataInventory_IsUnknownAndNeverTrusted()
    {
        var target = NewTarget();
        var stateRoot = NewStateRoot();
        // Well-formed forged inventory in the app-pool-writable legacy location (F1).
        var legacyPath = AppliedOverlayFileInventory.GetPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, ValidJson(target, "[]"));

        var inventory = AppliedOverlayFileInventory.TryRead(stateRoot, target, _appInstanceId, out var diagnostic);

        Assert.Null(inventory);
        Assert.NotNull(diagnostic);
        Assert.Contains("not trusted", diagnostic, StringComparison.Ordinal);
        Assert.Contains("No file was removed", diagnostic, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void TryRead_SymlinkedTrustedInventory_IsNotFollowed()
    {
        var target = NewTarget();
        var stateRoot = NewStateRoot();
        var elsewhere = Path.Join(_root, "elsewhere-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        var victim = Path.Join(elsewhere, "victim.json");
        File.WriteAllText(victim, ValidJson(target, "[]"));
        var path = AppliedOverlayFileInventory.GetTrustedPath(stateRoot, target, _appInstanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Skip.IfNot(TryCreateFileSymlink(path, victim), SkipReason);

        var inventory = AppliedOverlayFileInventory.TryRead(stateRoot, target, _appInstanceId, out var diagnostic);

        Assert.Null(inventory);
        Assert.NotNull(diagnostic);
        Assert.Contains("reparse point", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryWrite_ThenTryRead_RoundTripsAndLeavesNoTempFile()
    {
        var target = NewTarget();
        var stateRoot = NewStateRoot();
        var entry = new AppliedOverlayFileEntry
        {
            RelativePath = "odv.site.config.js",
            ContentLength = 3,
            ContentSha256 = new string('a', 64)
        };

        var writeDiagnostic = AppliedOverlayFileInventory.TryWrite(stateRoot, target, _appInstanceId, "app", "host", [entry]);

        Assert.Null(writeDiagnostic);
        var inventory = AppliedOverlayFileInventory.TryRead(stateRoot, target, _appInstanceId, out var diagnostic);
        Assert.Null(diagnostic);
        Assert.NotNull(inventory);
        Assert.Single(inventory.Files);
        Assert.Equal(AppliedOverlayFileInventory.NormalizeTargetPath(target), inventory.TargetPath);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(AppliedOverlayFileInventory.GetTrustedPath(stateRoot, target, _appInstanceId))!, "*.tmp"));
    }

    [Fact]
    public void TryWrite_TooManyEntries_IsRejectedAndWritesNothing()
    {
        var target = NewTarget();
        var stateRoot = NewStateRoot();
        var entries = Enumerable.Range(0, AppliedOverlayFileInventory.MaxEntries + 1)
            .Select(i => new AppliedOverlayFileEntry
            {
                RelativePath = $"file-{i}.js",
                ContentLength = 1,
                ContentSha256 = new string('a', 64)
            })
            .ToList();

        var diagnostic = AppliedOverlayFileInventory.TryWrite(stateRoot, target, _appInstanceId, "app", "host", entries);

        Assert.NotNull(diagnostic);
        Assert.Contains("No file was removed", diagnostic, StringComparison.Ordinal);
        Assert.False(File.Exists(AppliedOverlayFileInventory.GetTrustedPath(stateRoot, target, _appInstanceId)));
    }

    [Fact]
    public void TryWrite_OverlongRelativePath_IsRejectedAndWritesNothing()
    {
        var target = NewTarget();
        var stateRoot = NewStateRoot();
        var entry = new AppliedOverlayFileEntry
        {
            RelativePath = new string('a', AppliedOverlayFileInventory.MaxRelativePathLength + 1),
            ContentLength = 1,
            ContentSha256 = new string('a', 64)
        };

        var diagnostic = AppliedOverlayFileInventory.TryWrite(stateRoot, target, _appInstanceId, "app", "host", [entry]);

        Assert.NotNull(diagnostic);
        Assert.False(File.Exists(AppliedOverlayFileInventory.GetTrustedPath(stateRoot, target, _appInstanceId)));
    }

    [Fact]
    public void TryWrite_DirectoryAtInventoryLeaf_DegradesToWarningWithoutThrowing()
    {
        var target = NewTarget();
        var stateRoot = NewStateRoot();
        var path = AppliedOverlayFileInventory.GetTrustedPath(stateRoot, target, _appInstanceId);
        Directory.CreateDirectory(path); // planted directory at the leaf (F3)

        var entry = new AppliedOverlayFileEntry
        {
            RelativePath = "odv.site.config.js",
            ContentLength = 3,
            ContentSha256 = new string('a', 64)
        };
        var diagnostic = AppliedOverlayFileInventory.TryWrite(stateRoot, target, _appInstanceId, "app", "host", [entry]);

        Assert.NotNull(diagnostic);
        Assert.Contains("No file was removed", diagnostic, StringComparison.Ordinal);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void ResolveConfigurationFiles_ClassifiesRowsFromFixtureDataWithoutLiveSql()
    {
        var rows = new[]
        {
            Row(1, "appsettings.json", "{ }", sourcePriority: 0),
            Row(2, "appsettings.json", "{ \"x\": 1 }", sourcePriority: 1),
            Row(3, "odv.site.config.js", "// overlay", sourcePriority: 2),
            Row(4, "artifact.config.js", "// artifact", sourcePriority: 0)
        };

        var resolved = OmpHostArtifactRepository.ResolveConfigurationFiles(rows, "1.0.0");

        Assert.False(resolved.Single(file => file.RelativePath == "appsettings.json").IsOverlayOwnedWholeFile);
        Assert.True(resolved.Single(file => file.RelativePath == "odv.site.config.js").IsOverlayOwnedWholeFile);
        Assert.False(resolved.Single(file => file.RelativePath == "artifact.config.js").IsOverlayOwnedWholeFile);
    }

    [Fact]
    public void ResolveConfigurationFiles_VersionSkippedOverlay_IsNotResolvedOrOwned()
    {
        var rows = new[]
        {
            Row(1, "odv.site.config.js", "// overlay", sourcePriority: 1, overlayArtifactVersion: "2.0.0")
        };

        var resolved = OmpHostArtifactRepository.ResolveConfigurationFiles(rows, "1.0.0");

        Assert.Empty(resolved);
    }

    private static OmpHostArtifactRepository.ResolvedConfigurationFile Row(
        int id, string relativePath, string content, int sourcePriority, string? overlayArtifactVersion = null)
        => new(
            new ArtifactConfigurationFileDescriptor
            {
                ArtifactConfigurationFileId = id,
                ArtifactId = 42,
                RelativePath = relativePath,
                FileContent = content
            },
            sourcePriority,
            DateTime.UtcNow,
            OverlayVersion: null,
            MergeMode: null,
            OverlayArtifactVersion: overlayArtifactVersion);

    private static bool TryCreateFileSymlink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return File.Exists(linkPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
