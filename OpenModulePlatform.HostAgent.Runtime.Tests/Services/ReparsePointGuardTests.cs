// File: OpenModulePlatform.HostAgent.Runtime.Tests/Services/ReparsePointGuardTests.cs
using System.Runtime.Versioning;
using OpenModulePlatform.HostAgent.Runtime.Services;
using Xunit;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// Regression tests for the reparse-point guards, using REAL junctions and symlinks.
/// </summary>
/// <remarks>
/// Six hardening rounds went into this pattern (R2-S8, R3-A1, R5S-A2, R5S-D1, R7-S2 through
/// R7-S5, R7-S11, R7-A5) and R8's sweep found that a grep for Reparse/Junction/Symlink across
/// both repositories' test projects returned zero hits. Every guard could be deleted without a
/// single test failing -- which is the most likely reason R6-D7 discovered that one of them had
/// silently become a no-op. These tests plant the real thing rather than a mock so the guard is
/// exercised the way an attacker would trigger it (R8-P2).
///
/// Creating the link needs the symlink privilege (or Developer Mode) on Windows. A test that
/// cannot plant one reports itself as SKIPPED with the reason, so a machine without the
/// privilege shows the guard as unexercised instead of silently green.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ReparsePointGuardTests : IDisposable
{
    private const string SkipReason =
        "This process cannot create a directory link (symlink privilege or Developer Mode required); the guard was not exercised.";

    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "omp-reparse-tests-" + Guid.NewGuid().ToString("N"));

    public ReparsePointGuardTests() => Directory.CreateDirectory(_root);

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
            // Best effort: a leftover temp tree must never fail the suite.
        }
    }

    [SkippableFact]
    public void MirrorDirectory_DoesNotFollowJunctionInSource()
    {
        // A junction inside the provisioned artifact path is the R8-P2-7 scenario: the source
        // side of the mirror had no AttributesToSkip, so the junction's target was copied into
        // the web root where IIS serves it statically.
        var source = CreateDirectory("source");
        var target = CreateDirectory("target");
        var secret = CreateDirectory("secret");
        File.WriteAllText(Path.Join(secret, "credentials.txt"), "top secret");
        File.WriteAllText(Path.Join(source, "real.txt"), "payload");

        Skip.IfNot(TryCreateDirectoryJunction(Path.Join(source, "leak"), secret), SkipReason);

        ArtifactDirectoryMirror.MirrorDirectory(source, target, [], CancellationToken.None);

        Assert.True(File.Exists(Path.Join(target, "real.txt")));
        Assert.False(
            File.Exists(Path.Join(target, "leak", "credentials.txt")),
            "The mirror followed a junction in the source and copied the target's content.");
    }

    [SkippableFact]
    public void MirrorDirectory_DoesNotDeleteThroughJunctionInTarget()
    {
        // The mirror-side counterpart: R5S-D1's original scenario, where the stale-entry sweep
        // deleted through a planted junction as LocalSystem.
        var source = CreateDirectory("source");
        var target = CreateDirectory("target");
        var victim = CreateDirectory("victim");
        var victimFile = Path.Join(victim, "keep-me.txt");
        File.WriteAllText(victimFile, "must survive");
        File.WriteAllText(Path.Join(source, "real.txt"), "payload");

        Skip.IfNot(TryCreateDirectoryJunction(Path.Join(target, "stale"), victim), SkipReason);

        try
        {
            ArtifactDirectoryMirror.MirrorDirectory(source, target, [], CancellationToken.None);
        }
        catch (IOException)
        {
            // Refusing outright is an acceptable outcome; the invariant below is what matters.
        }

        Assert.True(File.Exists(victimFile), "The mirror deleted through a junction in the target.");
    }

    private string CreateDirectory(string name)
    {
        var path = Path.Join(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Creates a real directory link. Returns false when the platform refuses, so the test
    /// reports a skip (with <see cref="SkipReason"/>) instead of failing on an environment
    /// that cannot create one.
    /// </summary>
    private static bool TryCreateDirectoryJunction(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return Directory.Exists(linkPath) || new DirectoryInfo(linkPath).Exists;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
