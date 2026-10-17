namespace OpenModulePlatform.Portal.Models;

/// <summary>
/// Phase B Portal API contract for overlay administration and the
/// effective-configuration view. Phase A (this change) defines these DTOs and
/// the repository methods behind them; the Razor pages in Phase B bind to
/// them without changing the persistence or resolution contract.
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
