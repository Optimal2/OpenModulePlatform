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
    bool IsShadowed);

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
