// File: OpenModulePlatform.HostAgent.Runtime.Tests/Services/DeploymentControlFileGuardTests.cs
using System.Runtime.Versioning;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;
using Xunit;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// R8-P2-8: the deployment control files live in a web root that application-pool
/// identities can write, and HostAgent writes them as LocalSystem.
/// </summary>
/// <remarks>
/// Planting the real symlink rather than mocking the check, for the reason the whole
/// P2 cluster exists: an unexercised guard is how R6-D7 found one that had quietly
/// become a no-op. Remove the PrepareOwnedFileForWrite call from any of the three
/// writers and the matching test here fails.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DeploymentControlFileGuardTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "omp-deployment-control-" + Guid.NewGuid().ToString("N"));

    public DeploymentControlFileGuardTests() => Directory.CreateDirectory(_root);

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
            // Temp-directory cleanup is advisory: a file still held open by the OS
            // must not fail the test that already passed (cs/empty-catch-block).
        }
    }

    [Fact]
    public async Task Deployment_lock_write_replaces_a_planted_symlink_instead_of_writing_through_it()
    {
        var applicationRoot = Path.Join(_root, "webroot");
        Directory.CreateDirectory(Path.Join(applicationRoot, "App_Data"));

        var victim = Path.Join(_root, "web.config");
        const string victimContent = "<configuration><!-- must survive --></configuration>";
        await File.WriteAllTextAsync(victim, victimContent);

        var lockPath = DeploymentLockFile.GetPath(applicationRoot);
        if (!TryCreateFileSymlink(lockPath, victim))
        {
            return;
        }

        var document = DeploymentLockFile.Create(
            "lock-1",
            "app",
            "test",
            "guard test",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5));

        await DeploymentLockFile.WriteAsync(applicationRoot, document, CancellationToken.None);

        Assert.Equal(victimContent, await File.ReadAllTextAsync(victim));
        Assert.False(OmpReparsePointGuard.IsReparsePoint(lockPath));
        Assert.Contains("lock-1", await File.ReadAllTextAsync(lockPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deployment_lock_write_refuses_a_junctioned_App_Data_directory()
    {
        // The leaf of the lock write was already safe: it writes a GUID-named temp file
        // and File.Move(overwrite: true) replaces a symlink rather than writing through
        // it. This is the case the guard actually adds -- a junction on App_Data sends
        // the whole write somewhere else, and the leaf still looks ordinary.
        var applicationRoot = Path.Join(_root, "webroot-junction");
        Directory.CreateDirectory(applicationRoot);
        var elsewhere = Path.Join(_root, "lock-elsewhere");
        Directory.CreateDirectory(elsewhere);

        if (!TryCreateJunction(Path.Join(applicationRoot, "App_Data"), elsewhere))
        {
            return;
        }

        var document = DeploymentLockFile.Create(
            "lock-2",
            "app",
            "test",
            "guard test",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5));

        await Assert.ThrowsAsync<IOException>(() =>
            DeploymentLockFile.WriteAsync(applicationRoot, document, CancellationToken.None));

        Assert.False(File.Exists(Path.Join(elsewhere, "omp-deployment.lock.json")));
    }

    [Fact]
    public async Task Deployment_lock_read_refuses_a_symlink_rather_than_reporting_its_target()
    {
        var applicationRoot = Path.Join(_root, "webroot-read");
        Directory.CreateDirectory(Path.Join(applicationRoot, "App_Data"));

        var secret = Path.Join(_root, "hostagent-settings.json");
        await File.WriteAllTextAsync(secret, "{\"ConnectionString\":\"secret\"}");

        var lockPath = DeploymentLockFile.GetPath(applicationRoot);
        if (!TryCreateFileSymlink(lockPath, secret))
        {
            return;
        }

        var status = DeploymentLockFile.ReadStatus(applicationRoot, DateTimeOffset.UtcNow);

        Assert.DoesNotContain("secret", status.Diagnostic ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reparse point", status.Diagnostic ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Runtime_stop_marker_write_replaces_a_planted_symlink()
    {
        var targetPath = Path.Join(_root, "service-root");
        Directory.CreateDirectory(Path.Join(targetPath, "App_Data"));

        var victim = Path.Join(_root, "appsettings.json");
        const string victimContent = "{\"keep\":true}";
        await File.WriteAllTextAsync(victim, victimContent);

        var markerPath = DeploymentRuntimeStopMarker.GetPath(targetPath);
        if (!TryCreateFileSymlink(markerPath, victim))
        {
            return;
        }

        DeploymentRuntimeStopMarker.Write(
            targetPath,
            "service",
            "test-service",
            Guid.NewGuid(),
            "app-instance",
            "host");

        Assert.Equal(victimContent, await File.ReadAllTextAsync(victim));
        Assert.False(OmpReparsePointGuard.IsReparsePoint(markerPath));
        Assert.Contains("test-service", await File.ReadAllTextAsync(markerPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_stop_marker_read_refuses_a_symlink()
    {
        var targetPath = Path.Join(_root, "service-root-read");
        Directory.CreateDirectory(Path.Join(targetPath, "App_Data"));

        // Valid marker JSON behind the link, so a loader that follows it succeeds and
        // the test fails for the right reason rather than on a parse error.
        var planted = Path.Join(_root, "planted-marker.json");
        await File.WriteAllTextAsync(
            planted,
            "{\"RuntimeKind\":\"service\",\"RuntimeName\":\"planted\",\"ExpiresUtc\":\"2099-01-01T00:00:00+00:00\"}");

        var markerPath = DeploymentRuntimeStopMarker.GetPath(targetPath);
        if (!TryCreateFileSymlink(markerPath, planted))
        {
            return;
        }

        Assert.Null(DeploymentRuntimeStopMarker.TryRead(targetPath));
    }

    [Fact]
    public void PrepareOwnedFileForWrite_still_throws_when_a_parent_directory_is_a_junction()
    {
        // The leaf is repaired, the tree is not: a junction above means the deployment
        // target is not what the caller believes and must not be papered over.
        var applicationRoot = Path.Join(_root, "junction-parent");
        Directory.CreateDirectory(applicationRoot);
        var elsewhere = Path.Join(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);

        var appData = Path.Join(applicationRoot, "App_Data");
        if (!TryCreateJunction(appData, elsewhere))
        {
            return;
        }

        Assert.Throws<IOException>(() => OmpReparsePointGuard.PrepareOwnedFileForWrite(
            Path.Join(appData, "omp-deployment.lock.json"),
            applicationRoot,
            "test"));
    }

    [Fact]
    public void PrepareOwnedFileForWrite_leaves_an_ordinary_file_untouched()
    {
        var applicationRoot = Path.Join(_root, "ordinary");
        Directory.CreateDirectory(applicationRoot);
        var file = Path.Join(applicationRoot, "control.json");
        File.WriteAllText(file, "existing");

        OmpReparsePointGuard.PrepareOwnedFileForWrite(file, applicationRoot, "test");

        Assert.Equal("existing", File.ReadAllText(file));
    }

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
