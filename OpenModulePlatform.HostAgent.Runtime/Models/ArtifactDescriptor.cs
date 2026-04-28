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
        return Path.Combine(package, target, version);
    }

    private static string Sanitize(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            normalized = normalized.Replace(invalid, '_');
        }

        return normalized.Replace(' ', '_');
    }
}
