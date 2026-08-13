// File: OpenModulePlatform.Artifacts/OmpArtifactNaming.cs
namespace OpenModulePlatform.Artifacts;

/// <summary>
/// Builds the file and directory names that artifact identity fields turn into on disk.
/// </summary>
/// <remarks>
/// R8-P2-16..23. The values behind these names -- module key, app key, package type,
/// target name, version -- come from database rows and package manifests, not from the
/// code. SanitizePathSegment existed as a private copy in the HostAgent importer and
/// another in the Portal upload page, while CreateArtifactPackageFileName existed twice
/// more and interpolated the same values into a file name with no sanitizing at all. A
/// version or target name carrying a separator or a '..' segment therefore escaped its
/// intended directory in the two places that skipped the helper -- the same structural
/// shape as the rest of P2: the guard was private, so half the callers did without it.
/// </remarks>
public static class OmpArtifactNaming
{
    /// <summary>
    /// Reduces a value to something safe to use as one path segment: no separators, no
    /// characters the filesystem rejects, no spaces.
    /// </summary>
    public static string SanitizePathSegment(string? value)
    {
        var sanitized = (value ?? string.Empty).Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalid, '-');
        }

        // GetInvalidFileNameChars covers the separators on Windows but not on every
        // platform, and '..' survives it everywhere: both would turn one segment into a
        // path. Neither is a legitimate part of an artifact identity field.
        sanitized = sanitized
            .Replace('/', '-')
            .Replace('\\', '-')
            .Replace(' ', '-');

        while (sanitized.Contains("..", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("..", "-", StringComparison.Ordinal);
        }

        return sanitized.Length == 0 ? "-" : sanitized;
    }

    /// <summary>
    /// The canonical file name for an exported artifact package. Every part is sanitized,
    /// because the result is used both as a file name on disk and as a zip entry name.
    /// </summary>
    public static string CreateArtifactPackageFileName(
        string moduleKey,
        string appKey,
        string packageType,
        string? targetName,
        string version)
        => string.Join(
            "__",
            SanitizePathSegment(moduleKey),
            SanitizePathSegment(appKey),
            SanitizePathSegment(packageType),
            SanitizePathSegment(targetName),
            SanitizePathSegment(version)) + ".zip";
}
