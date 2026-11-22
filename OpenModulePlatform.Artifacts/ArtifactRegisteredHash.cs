// File: OpenModulePlatform.Artifacts/ArtifactRegisteredHash.cs
using System.Security.Cryptography;
using System.Text;

namespace OpenModulePlatform.Artifacts;

/// <summary>How freshly measured artifact content relates to a registered SHA-256.</summary>
public enum ArtifactRegisteredHashMatch
{
    /// <summary>Neither the canonical nor the legacy directory hash equals the registered value.</summary>
    Mismatch = 0,

    /// <summary>The canonical (ordinal path ordering) hash equals the registered value.</summary>
    Canonical = 1,

    /// <summary>
    /// Only the legacy (case-insensitive path ordering) directory hash equals the registered
    /// value: the content is unchanged, the row was registered before the ordering changed.
    /// </summary>
    LegacyPathOrdering = 2
}

/// <summary>
/// Compares freshly measured artifact content with a SHA-256 that was registered earlier
/// (omp.Artifacts.Sha256), accepting the directory hash definition that was in use when
/// older rows were written.
/// </summary>
/// <remarks>
/// The directory hash feeds every file as "relative path with '/', UTF-8, one zero byte, file
/// content" in sorted relative-path order. The sort used StringComparer.OrdinalIgnoreCase until
/// the ordering was aligned on StringComparer.Ordinal, which is the canonical definition for
/// everything registered from then on. Both comparers give the same order for most trees, but
/// not for one that mixes upper- and lower-case leading characters ("B.txt" and "a.txt"), so a
/// row registered with the old ordering no longer equals a fresh canonical hash of the very
/// same bytes. Without this comparison every such artifact is reported as corrupt on the first
/// run after an upgrade, and nothing recomputes the stored value.
///
/// The rule is deliberately narrow so it cannot weaken the integrity check: the legacy hash
/// covers exactly the same files and bytes as the canonical one (same reparse-point guard,
/// same framing), only in the other order, and it is consulted only when the canonical hash
/// does not equal the registered value. Content that really changed matches neither.
///
/// It lives in this project for the same reason OmpReparsePointGuard does: HostAgent.Runtime,
/// the Portal, the Installation engine and the Bootstrapper all reference it, so there is one
/// definition of the legacy hash instead of one per caller.
/// </remarks>
public static class ArtifactRegisteredHash
{
    /// <summary>
    /// Classifies <paramref name="canonicalSha256"/> (already computed for
    /// <paramref name="contentPath"/>) against <paramref name="registeredSha256"/>.
    /// </summary>
    /// <remarks>
    /// The legacy hash is computed only when the canonical one does not match, only for a
    /// directory (a single file hashes identically under both definitions), and only when the
    /// two orderings actually differ for the tree. A missing registered value is a mismatch
    /// here; callers decide separately what an unregistered hash means.
    /// </remarks>
    public static async Task<ArtifactRegisteredHashMatch> MatchAsync(
        string contentPath,
        string canonicalSha256,
        string? registeredSha256,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSha256);

        if (string.IsNullOrWhiteSpace(registeredSha256))
        {
            return ArtifactRegisteredHashMatch.Mismatch;
        }

        var registered = registeredSha256.Trim();
        if (string.Equals(canonicalSha256.Trim(), registered, StringComparison.OrdinalIgnoreCase))
        {
            return ArtifactRegisteredHashMatch.Canonical;
        }

        return await MatchesLegacyPathOrderingAsync(contentPath, registered, cancellationToken)
            ? ArtifactRegisteredHashMatch.LegacyPathOrdering
            : ArtifactRegisteredHashMatch.Mismatch;
    }

    /// <summary>
    /// True when <paramref name="contentPath"/> is a directory whose legacy hash equals
    /// <paramref name="registeredSha256"/> and differs from the canonical hash of the same tree.
    /// </summary>
    /// <remarks>
    /// For callers that already know the canonical hash does not match, or that remember an
    /// artifact was last accepted through the legacy ordering and want to re-verify it without
    /// hashing the tree twice.
    /// </remarks>
    public static async Task<bool> MatchesLegacyPathOrderingAsync(
        string contentPath,
        string? registeredSha256,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentPath);

        if (string.IsNullOrWhiteSpace(registeredSha256))
        {
            return false;
        }

        OmpReparsePointGuard.EnsureNotReparsePoint(contentPath, "Artifact path");
        if (!Directory.Exists(contentPath))
        {
            // A single file, or nothing at all: the two definitions cannot differ.
            return false;
        }

        var (legacyOrder, sameAsCanonical) = EnumerateLegacyOrder(contentPath);
        if (sameAsCanonical)
        {
            // Same order means the same hash, which the caller has already compared.
            return false;
        }

        var legacySha256 = await ComputeSha256Async(contentPath, legacyOrder, cancellationToken);
        return string.Equals(legacySha256, registeredSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The directory hash as it was computed before the path ordering changed: relative paths
    /// sorted with StringComparer.OrdinalIgnoreCase, otherwise identical to the canonical hash.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The path is not an existing directory.</exception>
    public static async Task<string> ComputeLegacyDirectorySha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        OmpReparsePointGuard.EnsureNotReparsePoint(path, "Artifact path");
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Artifact directory does not exist: '{path}'.");
        }

        var (legacyOrder, _) = EnumerateLegacyOrder(path);
        return await ComputeSha256Async(path, legacyOrder, cancellationToken);
    }

    private static (List<string> LegacyOrder, bool SameAsCanonical) EnumerateLegacyOrder(string path)
    {
        var files = Directory.EnumerateFiles(path, "*", OmpReparsePointGuard.RecursiveNoFollow).ToList();
        var legacyOrder = files
            .OrderBy(file => Path.GetRelativePath(path, file), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var canonicalOrder = files
            .OrderBy(file => Path.GetRelativePath(path, file), StringComparer.Ordinal);

        return (legacyOrder, legacyOrder.SequenceEqual(canonicalOrder, StringComparer.Ordinal));
    }

    private static async Task<string> ComputeSha256Async(
        string root,
        IReadOnlyList<string> orderedFiles,
        CancellationToken cancellationToken)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var separator = new byte[] { 0 };
        var buffer = new byte[1024 * 128];

        foreach (var file in orderedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            sha.AppendData(Encoding.UTF8.GetBytes(relative));
            sha.AppendData(separator);

            await using var stream = File.OpenRead(file);
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                sha.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}
