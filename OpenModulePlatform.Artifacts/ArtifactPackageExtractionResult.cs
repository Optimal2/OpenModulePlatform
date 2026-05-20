namespace OpenModulePlatform.Artifacts;

public sealed record ArtifactPackageExtractionResult(
    string ArtifactContentPath,
    IReadOnlyList<ArtifactPackageConfigurationFile> ConfigurationFiles,
    bool UsesManifestEnvelope);
