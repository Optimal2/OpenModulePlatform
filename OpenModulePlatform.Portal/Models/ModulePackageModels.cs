// File: OpenModulePlatform.Portal/Models/ModulePackageModels.cs
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Models;

/// <summary>
/// Installation status for one available portable module package.
/// </summary>
public enum AvailablePackageInstallState
{
    NotInstalled,
    SameVersion,
    NewerInstalled,
    OtherVersion,
    Partial
}

/// <summary>
/// Portal row model that compares an available module package with the current
/// database state. It is shared by the module package page and the module
/// definition overview so both places explain the same import semantics.
/// </summary>
public sealed class AvailablePackageRowModel
{
    public required string ModuleKey { get; init; }

    public required string DefinitionVersion { get; init; }

    public required string ModuleDefinitionFileName { get; init; }

    public required IReadOnlyList<ArtifactPackageFile> ArtifactFiles { get; init; }

    public string? InstalledDefinitionVersion { get; init; }

    public int SameArtifactVersionCount { get; init; }

    public int NewerArtifactVersionCount { get; init; }

    public int OlderArtifactVersionCount { get; init; }

    public int OtherArtifactVersionCount { get; init; }

    public int MissingArtifactCount { get; init; }

    public AvailablePackageInstallState InstallState { get; init; }

    public string StatusLabel => InstallState switch
    {
        AvailablePackageInstallState.SameVersion => "Installed same version",
        AvailablePackageInstallState.NewerInstalled => "Newer version installed",
        AvailablePackageInstallState.OtherVersion => "Installed other version",
        AvailablePackageInstallState.Partial => "Partially installed",
        _ => "Not installed"
    };

    public string StatusClass => InstallState switch
    {
        AvailablePackageInstallState.SameVersion => "integrity-pill--ok",
        AvailablePackageInstallState.NewerInstalled => "integrity-pill--ok",
        AvailablePackageInstallState.OtherVersion => "integrity-pill--warning",
        AvailablePackageInstallState.Partial => "integrity-pill--warning",
        _ => "integrity-pill--neutral"
    };

    public string ImportGuidance => InstallState switch
    {
        AvailablePackageInstallState.SameVersion =>
            "Importing again keeps unchanged content, skips identical artifacts, and can still execute missing SQL repairs.",
        AvailablePackageInstallState.NewerInstalled =>
            "Importing keeps the newer installed selections and only fills in missing compatible package objects.",
        AvailablePackageInstallState.OtherVersion =>
            "Importing applies this definition version and can move matching app instances to the package artifact versions.",
        AvailablePackageInstallState.Partial =>
            "Importing fills in missing pieces and may update desired artifact versions for matching app instances.",
        _ =>
            "Importing adds this module definition and registers the package artifacts so HostAgent can deploy them."
    };

    public string ActionLabel => InstallState switch
    {
        AvailablePackageInstallState.NotInstalled => "Install in OMP",
        AvailablePackageInstallState.SameVersion => "Run import/SQL check",
        AvailablePackageInstallState.NewerInstalled => "Run import/SQL check",
        _ => "Update/complete"
    };

    public static AvailablePackageRowModel Create(
        AvailablePortableModulePackage package,
        ModuleDefinitionDocumentRow? appliedDefinition,
        IReadOnlyList<ArtifactRow> installedArtifacts)
    {
        var installedDefinitionVersion = appliedDefinition?.DefinitionVersion;
        var definitionSame = string.Equals(
            installedDefinitionVersion,
            package.DefinitionVersion,
            StringComparison.OrdinalIgnoreCase);
        var definitionInstalled = !string.IsNullOrWhiteSpace(installedDefinitionVersion);
        var definitionNewer = definitionInstalled
            && CompareArtifactVersions(installedDefinitionVersion!, package.DefinitionVersion) > 0;
        var installedArtifactsBySlot = installedArtifacts.ToLookup(
            artifact => BuildArtifactSlotKey(
                artifact.ModuleKey,
                artifact.AppKey,
                artifact.PackageType,
                artifact.TargetName),
            StringComparer.OrdinalIgnoreCase);

        var sameArtifacts = 0;
        var newerArtifacts = 0;
        var olderArtifacts = 0;
        var otherArtifacts = 0;
        var missingArtifacts = 0;
        foreach (var file in package.ArtifactFiles)
        {
            var matches = installedArtifactsBySlot[
                BuildArtifactSlotKey(file.ModuleKey, file.AppKey, file.PackageType, file.TargetName)];

            if (matches.Any(artifact => artifact.Version.Equals(file.Version, StringComparison.OrdinalIgnoreCase)))
            {
                sameArtifacts++;
            }
            else if (matches.Any(artifact => CompareArtifactVersions(artifact.Version, file.Version) > 0))
            {
                newerArtifacts++;
            }
            else if (matches.Any(artifact => CompareArtifactVersions(artifact.Version, file.Version) < 0))
            {
                olderArtifacts++;
            }
            else if (matches.Any())
            {
                otherArtifacts++;
            }
            else
            {
                missingArtifacts++;
            }
        }

        var allArtifactsSame = package.ArtifactFiles.Count == sameArtifacts;
        var allArtifactsCoveredBySameOrNewer = package.ArtifactFiles.Count == sameArtifacts + newerArtifacts;
        var noArtifactsInstalled = sameArtifacts == 0 && newerArtifacts == 0 && olderArtifacts == 0 && otherArtifacts == 0;
        var installState = (definitionInstalled, definitionSame, definitionNewer, allArtifactsSame, allArtifactsCoveredBySameOrNewer, noArtifactsInstalled) switch
        {
            (true, true, _, true, _, _) => AvailablePackageInstallState.SameVersion,
            (true, true, _, false, true, _) => AvailablePackageInstallState.NewerInstalled,
            (true, _, true, _, true, _) => AvailablePackageInstallState.NewerInstalled,
            (false, _, _, _, _, true) => AvailablePackageInstallState.NotInstalled,
            (true, false, _, _, _, _) => AvailablePackageInstallState.OtherVersion,
            _ => AvailablePackageInstallState.Partial
        };

        return new AvailablePackageRowModel
        {
            ModuleKey = package.ModuleKey,
            DefinitionVersion = package.DefinitionVersion,
            ModuleDefinitionFileName = package.ModuleDefinitionFileName,
            ArtifactFiles = package.ArtifactFiles,
            InstalledDefinitionVersion = installedDefinitionVersion,
            SameArtifactVersionCount = sameArtifacts,
            NewerArtifactVersionCount = newerArtifacts,
            OlderArtifactVersionCount = olderArtifacts,
            OtherArtifactVersionCount = otherArtifacts,
            MissingArtifactCount = missingArtifacts,
            InstallState = installState
        };
    }

    private static string BuildArtifactSlotKey(
        string moduleKey,
        string appKey,
        string packageType,
        string? targetName)
        => string.Join('\u001f', moduleKey, appKey, packageType, targetName ?? string.Empty);

    private static int CompareArtifactVersions(string left, string right)
    {
        if (TryParseComparableVersion(left, out var leftVersion)
            && TryParseComparableVersion(right, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.Compare(
            left?.Trim(),
            right?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseComparableVersion(string? value, out Version version)
    {
        var text = value?.Trim() ?? string.Empty;
        var suffixIndex = text.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            text = text[..suffixIndex];
        }

        return Version.TryParse(text, out version!);
    }
}
