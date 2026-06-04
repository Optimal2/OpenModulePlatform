namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed record ArtifactZipImportAppDescriptor(
    int AppId,
    string ModuleKey,
    string AppKey);

public sealed record ArtifactCompatibilitySlot(
    string ModuleKey,
    string DefinitionVersion,
    string AppKey,
    string PackageType,
    string? TargetName,
    string? RelativePathTemplate,
    string? MinArtifactVersion,
    string? MaxArtifactVersion);

public sealed record ArtifactZipImportResult(
    int ArtifactId,
    string Version,
    string RelativePath,
    int CopiedConfigurationFileCount,
    int TemplateAppRowsUpdated,
    int AppInstanceRowsUpdated,
    int WorkerInstanceRowsUpdated,
    int HostAgentDesiredRowsUpdated,
    bool AdoptedExistingContent,
    string Status = "Imported",
    string? Message = null);

public sealed record ArtifactZipImportDuplicateInfo(
    int ArtifactId,
    string AppKey,
    string Version,
    string PackageType,
    string? TargetName,
    string? RelativePath,
    string? Sha256);

public sealed record ModuleDefinitionImportDocument(
    string ModuleKey,
    string DefinitionVersion,
    int FormatVersion,
    string DefinitionJson,
    string DefinitionSha256,
    string? SourceName,
    IReadOnlyList<ModuleDefinitionArtifactCompatibilityEntry> CompatibleArtifacts);

public sealed record ModuleDefinitionArtifactCompatibilityEntry(
    string AppKey,
    string PackageType,
    string? TargetName,
    string? RelativePathTemplate,
    string? MinArtifactVersion,
    string? MaxArtifactVersion);

public sealed record ModuleDefinitionSaveResult(
    int ModuleDefinitionDocumentId,
    bool Created,
    bool Replaced,
    bool WasIdentical);

public sealed record ModuleDefinitionImportResult(
    string ModuleKey,
    string DefinitionVersion,
    int ModuleDefinitionDocumentId,
    bool Applied,
    int SqlRepairCount);
