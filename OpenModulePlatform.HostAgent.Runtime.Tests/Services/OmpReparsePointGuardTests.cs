// File: OpenModulePlatform.HostAgent.Runtime.Tests/Services/OmpReparsePointGuardTests.cs
using System.Runtime.Versioning;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Services;
using Xunit;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// The shared reparse-point guard, exercised with real junctions.
/// </summary>
/// <remarks>
/// R8-P2. Eight rounds hardened this one call site at a time and the sweep still found a dozen
/// writers without it. The cause was structural: the guard was a private method inside
/// ArtifactZipImportService, so nobody else could call it even if they knew it existed. It now
/// lives in OpenModulePlatform.Artifacts, which HostAgent, the Portal and the Bootstrapper all
/// reference -- and these tests plant the real thing rather than a mock, because a guard nothing
/// exercises is how R6-D7 found one that had silently become a no-op.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class OmpReparsePointGuardTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "omp-guard-tests-" + Guid.NewGuid().ToString("N"));

    public OmpReparsePointGuardTests() => Directory.CreateDirectory(_root);

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
        }
    }

    [Fact]
    public void EnsureNotReparsePoint_accepts_an_ordinary_directory_and_a_missing_path()
    {
        var ordinary = Path.Join(_root, "ordinary");
        Directory.CreateDirectory(ordinary);

        OmpReparsePointGuard.EnsureNotReparsePoint(ordinary, "test");
        OmpReparsePointGuard.EnsureNotReparsePoint(Path.Join(_root, "missing"), "test");
    }

    [Fact]
    public void EnsureNotReparsePoint_refuses_a_junction()
    {
        var target = Path.Join(_root, "target");
        Directory.CreateDirectory(target);
        var link = Path.Join(_root, "link");
        if (!TryCreateJunction(link, target))
        {
            return;
        }

        Assert.Throws<IOException>(() => OmpReparsePointGuard.EnsureNotReparsePoint(link, "test"));
    }

    /// <summary>
    /// Checking only the leaf is what left several findings exploitable: the junction goes on a
    /// directory above, and the leaf below it looks perfectly ordinary.
    /// </summary>
    [Fact]
    public void EnsureNoReparsePointInPath_refuses_a_junction_above_the_leaf()
    {
        var target = Path.Join(_root, "target");
        Directory.CreateDirectory(target);
        var linkedParent = Path.Join(_root, "parent");
        if (!TryCreateJunction(linkedParent, target))
        {
            return;
        }

        var leaf = Path.Join(linkedParent, "child", "leaf.txt");

        // The leaf alone passes, which is exactly the trap.
        OmpReparsePointGuard.EnsureNotReparsePoint(leaf, "test");

        Assert.Throws<IOException>(
            () => OmpReparsePointGuard.EnsureNoReparsePointInPath(leaf, _root, "test"));
    }

    [Fact]
    public void RecursiveNoFollow_does_not_enumerate_through_a_junction()
    {
        var source = Path.Join(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Join(source, "real.txt"), "payload");

        var secret = Path.Join(_root, "secret");
        Directory.CreateDirectory(secret);
        File.WriteAllText(Path.Join(secret, "credentials.txt"), "top secret");

        if (!TryCreateJunction(Path.Join(source, "leak"), secret))
        {
            return;
        }

        var files = Directory.EnumerateFiles(source, "*", OmpReparsePointGuard.RecursiveNoFollow)
            .Select(Path.GetFileName)
            .ToList();

        Assert.Contains("real.txt", files);
        Assert.DoesNotContain("credentials.txt", files);
    }

    /// <summary>
    /// The artifact integrity hash must not be computable over content behind a link.
    /// </summary>
    [Fact]
    public async Task ComputeSha256Async_refuses_a_linked_artifact_root()
    {
        var target = Path.Join(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Join(target, "file.txt"), "content");

        var link = Path.Join(_root, "artifact");
        if (!TryCreateJunction(link, target))
        {
            return;
        }

        await Assert.ThrowsAsync<IOException>(
            () => ArtifactHash.ComputeSha256Async(link, CancellationToken.None));
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
