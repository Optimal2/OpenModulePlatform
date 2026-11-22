using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// The shared comparison of freshly measured content with a registered hash. The value
/// "registered before the path ordering became ordinal" always comes from
/// <see cref="PreOrdinalDirectoryHash"/>, an independent copy of the old algorithm.
/// </summary>
public sealed class ArtifactRegisteredHashTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "omp-registered-hash-" + Guid.NewGuid().ToString("N"));

    public ArtifactRegisteredHashTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task HashRegisteredWithTheOldPathOrdering_IsAcceptedForUnchangedContent()
    {
        var artifact = Path.Join(_root, "artifact");
        PreOrdinalDirectoryHash.WriteOrderSensitiveTree(artifact);
        var registered = PreOrdinalDirectoryHash.Compute(artifact);
        var canonical = await ArtifactHash.ComputeSha256Async(artifact, CancellationToken.None);

        // The premise of the whole fix: for this tree the two definitions really differ.
        Assert.NotEqual(registered, canonical);
        Assert.Equal(registered, await ArtifactRegisteredHash.ComputeLegacyDirectorySha256Async(artifact, CancellationToken.None));
        Assert.Equal(
            ArtifactRegisteredHashMatch.LegacyPathOrdering,
            await ArtifactRegisteredHash.MatchAsync(artifact, canonical, registered, CancellationToken.None));
        Assert.Equal(
            ArtifactRegisteredHashMatch.LegacyPathOrdering,
            await ArtifactRegisteredHash.MatchAsync(artifact, canonical, "  " + registered.ToUpperInvariant() + " ", CancellationToken.None));
        Assert.True(await ArtifactRegisteredHash.MatchesLegacyPathOrderingAsync(artifact, registered, CancellationToken.None));
    }

    [Fact]
    public async Task HashRegisteredWithTheCanonicalOrdering_IsCanonical()
    {
        var artifact = Path.Join(_root, "artifact");
        PreOrdinalDirectoryHash.WriteOrderSensitiveTree(artifact);
        var canonical = await ArtifactHash.ComputeSha256Async(artifact, CancellationToken.None);

        Assert.Equal(
            ArtifactRegisteredHashMatch.Canonical,
            await ArtifactRegisteredHash.MatchAsync(artifact, canonical, canonical, CancellationToken.None));
        Assert.False(await ArtifactRegisteredHash.MatchesLegacyPathOrderingAsync(artifact, canonical, CancellationToken.None));
    }

    [Theory]
    [InlineData("B.txt", "bravo changed")]
    [InlineData("a.txt", "alpha changed")]
    public async Task ChangedContent_MatchesNeitherDefinition(string changedFile, string newContent)
    {
        var artifact = Path.Join(_root, "artifact");
        PreOrdinalDirectoryHash.WriteOrderSensitiveTree(artifact);
        var registered = PreOrdinalDirectoryHash.Compute(artifact);

        await File.WriteAllTextAsync(Path.Join(artifact, changedFile), newContent);
        var canonical = await ArtifactHash.ComputeSha256Async(artifact, CancellationToken.None);

        Assert.Equal(
            ArtifactRegisteredHashMatch.Mismatch,
            await ArtifactRegisteredHash.MatchAsync(artifact, canonical, registered, CancellationToken.None));
        Assert.False(await ArtifactRegisteredHash.MatchesLegacyPathOrderingAsync(artifact, registered, CancellationToken.None));
    }

    [Fact]
    public async Task AddedOrRemovedFile_MatchesNeitherDefinition()
    {
        var artifact = Path.Join(_root, "artifact");
        PreOrdinalDirectoryHash.WriteOrderSensitiveTree(artifact);
        var registered = PreOrdinalDirectoryHash.Compute(artifact);

        await File.WriteAllTextAsync(Path.Join(artifact, "C.txt"), "added");
        var withAddedFile = await ArtifactHash.ComputeSha256Async(artifact, CancellationToken.None);
        Assert.Equal(
            ArtifactRegisteredHashMatch.Mismatch,
            await ArtifactRegisteredHash.MatchAsync(artifact, withAddedFile, registered, CancellationToken.None));

        File.Delete(Path.Join(artifact, "C.txt"));
        File.Delete(Path.Join(artifact, "a.txt"));
        var withRemovedFile = await ArtifactHash.ComputeSha256Async(artifact, CancellationToken.None);
        Assert.Equal(
            ArtifactRegisteredHashMatch.Mismatch,
            await ArtifactRegisteredHash.MatchAsync(artifact, withRemovedFile, registered, CancellationToken.None));
    }

    [Fact]
    public async Task TreeWhereBothOrderingsCoincide_HashesIdenticallyUnderBothDefinitions()
    {
        var artifact = Path.Join(_root, "artifact");
        Directory.CreateDirectory(Path.Join(artifact, "sub"));
        await File.WriteAllTextAsync(Path.Join(artifact, "a.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Join(artifact, "b.txt"), "bravo");
        await File.WriteAllTextAsync(Path.Join(artifact, "sub", "c.txt"), "charlie");

        var canonical = await ArtifactHash.ComputeSha256Async(artifact, CancellationToken.None);

        Assert.Equal(canonical, PreOrdinalDirectoryHash.Compute(artifact));
        Assert.Equal(canonical, await ArtifactRegisteredHash.ComputeLegacyDirectorySha256Async(artifact, CancellationToken.None));
        Assert.Equal(
            ArtifactRegisteredHashMatch.Canonical,
            await ArtifactRegisteredHash.MatchAsync(artifact, canonical, canonical, CancellationToken.None));
        // Nothing to gain from a second pass: the legacy check reports no separate match.
        Assert.False(await ArtifactRegisteredHash.MatchesLegacyPathOrderingAsync(artifact, canonical, CancellationToken.None));
        Assert.Equal(
            ArtifactRegisteredHashMatch.Mismatch,
            await ArtifactRegisteredHash.MatchAsync(artifact, canonical, new string('0', 64), CancellationToken.None));
    }

    [Fact]
    public async Task SingleFileArtifact_HasNoLegacyDefinition()
    {
        var file = Path.Join(_root, "artifact.bin");
        await File.WriteAllTextAsync(file, "payload");
        var canonical = await ArtifactHash.ComputeSha256Async(file, CancellationToken.None);

        Assert.Equal(
            ArtifactRegisteredHashMatch.Canonical,
            await ArtifactRegisteredHash.MatchAsync(file, canonical, canonical, CancellationToken.None));
        Assert.Equal(
            ArtifactRegisteredHashMatch.Mismatch,
            await ArtifactRegisteredHash.MatchAsync(file, canonical, new string('0', 64), CancellationToken.None));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => ArtifactRegisteredHash.ComputeLegacyDirectorySha256Async(file, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingRegisteredHash_IsNeverAMatch(string? registered)
    {
        var artifact = Path.Join(_root, "artifact");
        PreOrdinalDirectoryHash.WriteOrderSensitiveTree(artifact);
        var canonical = await ArtifactHash.ComputeSha256Async(artifact, CancellationToken.None);

        Assert.Equal(
            ArtifactRegisteredHashMatch.Mismatch,
            await ArtifactRegisteredHash.MatchAsync(artifact, canonical, registered, CancellationToken.None));
        Assert.False(await ArtifactRegisteredHash.MatchesLegacyPathOrderingAsync(artifact, registered, CancellationToken.None));
    }

    [SkippableFact]
    public async Task LegacyHash_KeepsTheReparsePointGuardOfTheCanonicalHash()
    {
        var outside = Path.Join(_root, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Join(outside, "Secret.txt"), "not part of the artifact");
        var artifact = Path.Join(_root, "artifact");
        PreOrdinalDirectoryHash.WriteOrderSensitiveTree(artifact);
        var withoutLink = PreOrdinalDirectoryHash.Compute(artifact);
        Skip.IfNot(
            TryCreateDirectoryLink(Path.Join(artifact, "linked"), outside),
            "This process cannot create a directory link; the guard was not exercised.");

        // A linked descendant is skipped, so the hash is that of the real files only.
        Assert.Equal(withoutLink, await ArtifactRegisteredHash.ComputeLegacyDirectorySha256Async(artifact, CancellationToken.None));

        // A linked root is refused outright, exactly as ArtifactHash refuses it.
        var linkedRoot = Path.Join(_root, "linked-root");
        Skip.IfNot(
            TryCreateDirectoryLink(linkedRoot, artifact),
            "This process cannot create a directory link; the guard was not exercised.");
        await Assert.ThrowsAsync<IOException>(
            () => ArtifactRegisteredHash.ComputeLegacyDirectorySha256Async(linkedRoot, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(
            () => ArtifactRegisteredHash.MatchesLegacyPathOrderingAsync(linkedRoot, withoutLink, CancellationToken.None));
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
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
