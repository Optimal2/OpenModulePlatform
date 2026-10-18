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

    private static string WriteRaw(string target, string json)
    {
        var path = AppliedOverlayFileInventory.GetPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void TryRead_NullFiles_ReturnsActionableDiagnosticWithoutThrowing()
    {
        var target = NewTarget();
        WriteRaw(target, """
            { "SchemaName": "OpenModulePlatform.AppliedOverlayFiles.v1", "Files": null }
            """);

        var inventory = AppliedOverlayFileInventory.TryRead(target, out var diagnostic);

        Assert.Null(inventory);
        Assert.NotNull(diagnostic);
        Assert.Contains("Delete the inventory file", diagnostic, StringComparison.Ordinal);
        Assert.Contains("No file was removed", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_NullEntry_ReturnsActionableDiagnosticWithoutThrowing()
    {
        var target = NewTarget();
        WriteRaw(target, """
            { "SchemaName": "OpenModulePlatform.AppliedOverlayFiles.v1", "Files": [null] }
            """);

        var inventory = AppliedOverlayFileInventory.TryRead(target, out var diagnostic);

        Assert.Null(inventory);
        Assert.NotNull(diagnostic);
    }

    [Fact]
    public void TryRead_UnsupportedSchema_DoesNotEchoUntrustedValue()
    {
        var target = NewTarget();
        WriteRaw(target, """
            { "SchemaName": "SECRET-SCHEMA-VALUE", "Files": [] }
            """);

        Assert.Null(AppliedOverlayFileInventory.TryRead(target, out var diagnostic));
        Assert.NotNull(diagnostic);
        Assert.DoesNotContain("SECRET-SCHEMA-VALUE", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_OversizedInventory_IsRejectedBeforeDeserialize()
    {
        var target = NewTarget();
        var path = AppliedOverlayFileInventory.GetPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string(' ', AppliedOverlayFileInventory.MaxInventoryBytes + 1));

        Assert.Null(AppliedOverlayFileInventory.TryRead(target, out var diagnostic));
        Assert.Contains("exceeds", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_TornInventory_ReportsRecoverableDiagnostic()
    {
        var target = NewTarget();
        WriteRaw(target, "{ \"SchemaName\": \"OpenModulePlatform.AppliedOverlayFiles.v1\", \"Files\": [");

        Assert.Null(AppliedOverlayFileInventory.TryRead(target, out var diagnostic));
        Assert.NotNull(diagnostic);
        Assert.Contains("Delete the inventory file", diagnostic, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void TryRead_JunctionedAppData_IsNotFollowed()
    {
        var target = NewTarget();
        var elsewhere = Path.Join(_root, "elsewhere-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Join(elsewhere, AppliedOverlayFileInventory.FileName), """
            { "SchemaName": "OpenModulePlatform.AppliedOverlayFiles.v1", "Files": [] }
            """);
        Skip.IfNot(TryCreateJunction(Path.Join(target, "App_Data"), elsewhere), SkipReason);

        var inventory = AppliedOverlayFileInventory.TryRead(target, out var diagnostic);

        Assert.Null(inventory);
        Assert.NotNull(diagnostic);
        Assert.Contains("reparse point", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_ThenTryRead_RoundTripsAndLeavesNoTempFile()
    {
        var target = NewTarget();
        var entry = new AppliedOverlayFileEntry
        {
            RelativePath = "odv.site.config.js",
            ContentLength = 3,
            ContentSha256 = new string('a', 64)
        };

        AppliedOverlayFileInventory.Write(target, Guid.NewGuid(), "app", "host", [entry]);

        var inventory = AppliedOverlayFileInventory.TryRead(target, out var diagnostic);
        Assert.Null(diagnostic);
        Assert.NotNull(inventory);
        Assert.Single(inventory.Files);
        Assert.Empty(Directory.GetFiles(Path.Join(target, "App_Data"), "*.tmp"));
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
}
