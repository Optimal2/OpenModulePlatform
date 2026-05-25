namespace OpenModulePlatform.Artifacts;

public sealed record ArtifactPackageExtractionResult(
    string ArtifactContentPath,
    IReadOnlyList<ArtifactPackageConfigurationFile> ConfigurationFiles,
    string? MinModuleDefinitionVersion,
    bool UsesManifestEnvelope);
