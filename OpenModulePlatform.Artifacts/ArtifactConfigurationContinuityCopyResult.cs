namespace OpenModulePlatform.Artifacts;

/// <summary>
/// Result of copying configuration rows onto a newly registered artifact from
/// its continuity source. <see cref="SourceArtifactId"/> is NULL when the
/// per-path operator-delta fallback supplied the rows (no single source
/// artifact exists to name).
/// </summary>
public sealed record ArtifactConfigurationContinuityCopyResult(
    int? SourceArtifactId,
    string? SourceVersion,
    int CopiedCount);
