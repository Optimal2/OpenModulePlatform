using OpenModulePlatform.Worker.Abstractions.Models;

namespace OpenModulePlatform.Artifacts;

public sealed record ArtifactPackageExtractionResult(
    string ArtifactContentPath,
    IReadOnlyList<ArtifactPackageConfigurationFile> ConfigurationFiles,
    string? MinModuleDefinitionVersion,
    WorkerHostCompatibilityRequirement? WorkerHostRequirement,
    bool UsesManifestEnvelope);
