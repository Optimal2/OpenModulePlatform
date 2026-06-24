using System.Globalization;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

internal static class ImportFileArchiveDestination
{
    private const int MaxCollisionAttempts = 100;

    public static string CreateUniquePath(string destinationRoot, string importPath, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            throw new ArgumentException("Destination root must be configured.", nameof(destinationRoot));
        }

        var fileName = Path.GetFileName(importPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Import path must include a file name.", nameof(importPath));
        }

        var timestamp = utcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        for (var attempt = 0; attempt < MaxCollisionAttempts; attempt++)
        {
            var candidateName = attempt == 0
                ? $"{timestamp}-{fileName}"
                : $"{timestamp}-{attempt:00}-{fileName}";
            var candidate = Path.Join(destinationRoot, candidateName);
            if (!File.Exists(candidate) && !File.Exists(candidate + ".error.txt"))
            {
                return candidate;
            }
        }

        throw new IOException(
            $"Could not allocate an import archive path for '{fileName}' under '{destinationRoot}'.");
    }
}
