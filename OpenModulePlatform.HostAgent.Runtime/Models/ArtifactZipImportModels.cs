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
    int WorkerInstanceRowsUpdated);

public sealed record ArtifactZipImportDuplicateInfo(
    int ArtifactId,
    string AppKey,
    string Version,
    string PackageType,
    string? TargetName);
