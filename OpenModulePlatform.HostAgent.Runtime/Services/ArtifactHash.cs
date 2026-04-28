using System.Security.Cryptography;
using System.Text;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public static class ArtifactHash
{
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
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
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
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
