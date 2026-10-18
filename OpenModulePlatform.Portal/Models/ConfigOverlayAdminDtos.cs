namespace OpenModulePlatform.Portal.Models;

/// <summary>
/// Portal metadata contracts for overlay administration and effective sources.
/// All content fields are only populated for authorized admin callers; list
/// DTOs never carry file content or secrets.
/// </summary>
public sealed record ConfigOverlayListItemDto(
    int ConfigOverlayDocumentId,
    string OverlayKey,
    string OverlayVersion,
    string HostKey,
    string HostScope,
    bool IsAllHosts,
    string? ModuleKey,
    string? AppKey,
    string? PackageType,
    string? TargetName,
    string? ArtifactVersion,
    int ConfigurationFileCount,
    bool IsEnabled,
    string? SourceName,
    DateTime UpdatedUtc);

public sealed record ConfigOverlayFileDto(
    string RelativePath,
    string? MergeMode,
    string EffectiveMergeMode,
    bool IsEnabled);

public sealed record ConfigOverlayDetailDto(
    ConfigOverlayListItemDto Summary,
    IReadOnlyList<ConfigOverlayFileDto> Files,
    string OverlayJson);

/// <summary>One source row in the per-file effective-configuration view.</summary>
public sealed record EffectiveConfigurationSourceDto(
    string RelativePath,
    int SourcePriority,
    string SourceKind,
    string? OverlayKey,
    string? OverlayVersion,
    string? HostScope,
    string? MergeMode,
    string EffectiveMergeMode,
    bool IsWinner,
    bool IsShadowed,
    // Stable source identities for editor links. Artifact sources carry the
    // artifact configuration file id; overlay sources carry the overlay
    // document id and configuration file row id. Null when not applicable.
    int? ArtifactConfigurationFileId = null,
    int? ConfigOverlayDocumentId = null,
    int? ConfigOverlayConfigurationFileId = null);

public sealed record EffectiveConfigurationFileDto(
    string RelativePath,
    EffectiveConfigurationSourceDto Winner,
    IReadOnlyList<EffectiveConfigurationSourceDto> Sources);

/// <summary>
/// One configured deployment context in the installation-wide overlay impact
/// view. A context is one <see cref="AppInstanceRow"/>: a concrete host plus
/// the exact artifact version it is configured to run. ContextState is
/// "Applicable" (the selected overlay applies here), "NotApplicable" (a
/// saved selector or state excludes it) or "Unavailable" (the host or
/// artifact row is absent, so nothing can be resolved).
/// </summary>
public sealed record OverlayInstallationContextDto(
    string HostKey,
    string? AppKey,
    string? AppInstanceKey,
    int? ArtifactId,
    string? ArtifactVersion,
    string ContextState,
    IReadOnlyList<OverlayInstallationFileDto> Files,
    IReadOnlyList<string> Notes);

/// <summary>
/// Per-file removal outcome inside one installation-impact context. Same
/// outcome vocabulary as the single host/artifact removal preview:
/// "Unchanged", "ChangesEffectiveContent", "FallsBack", "NoEffectiveFile" or
/// "Unknown". Winner metadata only; file content is never included.
/// </summary>
public sealed record OverlayInstallationFileDto(
    string RelativePath,
    EffectiveConfigurationSourceDto? CurrentWinner,
    EffectiveConfigurationSourceDto? WinnerAfterRemoval,
    string Outcome,
    IReadOnlyList<string> Notes);

/// <summary>
/// An artifact whose selectors match the selected overlay but which no
/// configured app context in this installation references. Listed as a
/// possible match only, never as a deployment.
/// </summary>
public sealed record OverlayPossibleArtifactDto(
    int ArtifactId,
    string ModuleKey,
    string AppKey,
    string Version,
    string PackageType,
    string? TargetName);

/// <summary>
/// Read-only base-vs-effective content comparison for one host and artifact.
/// Content is carried as plain text and must be HTML-encoded (inert) by the
/// view; it is never stored, cached or written to logs.
/// </summary>
public sealed record OverlayContentComparison(
    string HostKey,
    int ArtifactId,
    string ArtifactVersion,
    IReadOnlyList<OverlayContentComparisonFile> Files,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Per-path comparison between the artifact-owned base content and the
/// resolved effective content. <see cref="Sources"/> lists every matching
/// candidate in deterministic precedence order; <see cref="Diff"/> is a
/// bounded line diff between base and effective.
/// </summary>
public sealed record OverlayContentComparisonFile(
    string RelativePath,
    bool BaseAvailable,
    bool EffectiveAvailable,
    bool EffectiveDiffers,
    string? BaseContent,
    string? EffectiveContent,
    bool ContentTruncated,
    IReadOnlyList<EffectiveConfigurationSourceDto> Sources,
    IReadOnlyList<OverlayDiffLine> Diff,
    IReadOnlyList<string> Notes);

/// <summary>
/// One line of a bounded base/effective diff. <see cref="Kind"/> is "context",
/// "removed" (present in the base only) or "added" (present in the effective
/// content only). <see cref="Text"/> is inert plain text; the view encodes it.
/// </summary>
public sealed record OverlayDiffLine(string Kind, int? BaseLine, int? EffectiveLine, string Text);
