using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class ArtifactDescriptor
{
    public Guid HostId { get; init; }

    public int ArtifactId { get; init; }

    public string Version { get; init; } = string.Empty;

    public string PackageType { get; init; } = string.Empty;

    public string? TargetName { get; init; }

    public string? RelativePath { get; init; }

    public string? Sha256 { get; init; }

    public string RequirementKey { get; init; } = string.Empty;

    public string? DesiredLocalPath { get; init; }

    public string GetCacheRelativePath()
    {
        var package = Sanitize(PackageType, "package");
        var target = Sanitize(TargetName, $"artifact-{ArtifactId}");
        var version = Sanitize(Version, "version");
        return Path.Join(package, target, version);
    }

    // R10-T3. This builds the artifact relative path, so the '_' replacement stays: it is
    // baked into every path already on disk and in the catalog. What it lacked was the
    // traversal handling -- GetInvalidFileNameChars never covers '..', and the values come
    // from catalog rows, so a version of '..' walked the artifact out of its own root.
    private static string Sanitize(string? value, string fallback)
        => OmpArtifactNaming.SanitizePathSegment(
            string.IsNullOrWhiteSpace(value) ? fallback : value,
            '_');
}
