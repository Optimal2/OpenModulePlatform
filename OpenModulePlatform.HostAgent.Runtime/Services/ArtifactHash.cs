using System.Security.Cryptography;
using System.Text;
using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public static class ArtifactHash
{
    // A missing desired hash means content-change detection is unavailable for the
    // artifact, so callers must fall back to identity-based (id/version/path) comparison
    // instead of treating every cycle as a content change.
    public static bool MatchesDeployedContent(string? desiredSha256, string? deployedSha256)
    {
        if (string.IsNullOrWhiteSpace(desiredSha256))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(deployedSha256)
            && string.Equals(desiredSha256.Trim(), deployedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// R8-P2-10. This is the only integrity check an artifact gets, and it enumerated with
    /// SearchOption.AllDirectories -- straight through any junction planted in the artifact tree,
    /// hashing whatever was on the other side and reporting it as the artifact's content. The
    /// enumeration now skips reparse points and the root itself is checked, so a link cannot be
    /// used to make foreign content hash as an approved artifact.
    ///
    /// A residual race remains between enumeration and File.OpenRead: a file could be replaced by
    /// a link in that window. Windows offers no portable way to open a handle that refuses to
    /// follow one, and the artifact tree is SYSTEM-owned, so this is left as the narrower risk
    /// rather than papered over with a check that cannot actually close it.
    /// </remarks>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        OmpReparsePointGuard.EnsureNotReparsePoint(path, "Artifact path");

        if (File.Exists(path))
        {
            await using var stream = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }

        if (!Directory.Exists(path))
        {
            throw new FileNotFoundException($"Artifact path does not exist: '{path}'.", path);
        }

        using var sha = SHA256.Create();
        var files = Directory.EnumerateFiles(path, "*", OmpReparsePointGuard.RecursiveNoFollow)
            .OrderBy(file => Path.GetRelativePath(path, file), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            var relativeBytes = Encoding.UTF8.GetBytes(relative);
            sha.TransformBlock(relativeBytes, 0, relativeBytes.Length, null, 0);

            var separator = new byte[] { 0 };
            sha.TransformBlock(separator, 0, separator.Length, null, 0);

            await using var stream = File.OpenRead(file);
            var buffer = new byte[1024 * 128];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}
