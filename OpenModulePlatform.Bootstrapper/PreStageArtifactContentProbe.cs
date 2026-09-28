using System.IO.Compression;
using System.Text.RegularExpressions;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.Bootstrapper;

/// <summary>
/// The artifact identity an artifact package file name declares:
/// <c>moduleKey__appKey__packageType__targetName__version.zip</c>, the same
/// identity the host agent import resolves against omp.Artifacts.
/// </summary>
internal sealed record PreStageArtifactIdentity(
    string PackageName,
    string ModuleKey,
    string AppKey,
    string PackageType,
    string TargetName,
    string Version)
{
    public string Describe()
        => $"{AppKey} {Version} ({PackageType}, {TargetName})";
}

/// <summary>The artifact row already registered for an identity.</summary>
/// <param name="Version">The registered version.</param>
/// <param name="Sha256">omp.Artifacts.Sha256, or null when the row carries no hash.</param>
internal sealed record PreStageRegisteredArtifact(string Version, string? Sha256);

/// <summary>
/// Measures the artifact packages inside a universal package the way the host
/// agent import will: extract each artifact package with the same extractor,
/// hash the artifact content with <see cref="ArtifactHash"/>, and set that
/// against omp.Artifacts.Sha256 for the same app, version, package type and
/// target.
/// </summary>
/// <remarks>
/// The pre-stage gate used to be fed a version comparison only, so a package
/// whose content differed under an unchanged version passed the gate and was
/// then rejected by the import with "The artifact content has changed under the
/// same version". Measuring with the import's own yardstick is the only way the
/// gate can refuse exactly what the import would refuse.
/// </remarks>
internal static partial class PreStageArtifactContentProbe
{
    private const string ArtifactsFolder = "artifacts/";

    // The same token rule as the import's file name parser
    // (ArtifactZipImportService.MetadataTokenPattern): a leading letter or digit,
    // then '+' allowed for build metadata such as 1.2.3+abc. A looser or stricter
    // rule here lets the gate skip a package the import measures, or measure one
    // the import refuses by name.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex MetadataTokenPattern();

    public static PreStageArtifactIdentity? TryParseArtifactPackageName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || !Path.GetExtension(fileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = Path.GetFileNameWithoutExtension(fileName).Split("__", StringSplitOptions.None);
        if (parts.Length != 5 || parts.Any(part => part.Length == 0 || !MetadataTokenPattern().IsMatch(part)))
        {
            return null;
        }

        return new PreStageArtifactIdentity(fileName, parts[0], parts[1], parts[2], parts[3], parts[4]);
    }

    /// <summary>
    /// Returns one <see cref="PreStageComponent"/> per artifact package in the
    /// universal package. Content is only extracted and hashed when a row with
    /// the same identity and version is registered and carries a hash, because
    /// that is the only case in which the import compares content.
    /// </summary>
    public static async Task<IReadOnlyList<PreStageComponent>> MeasureAsync(
        string universalPackagePath,
        Func<PreStageArtifactIdentity, CancellationToken, Task<PreStageRegisteredArtifact?>> findRegistered,
        string workRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(universalPackagePath);
        ArgumentNullException.ThrowIfNull(findRegistered);
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);

        var components = new List<PreStageComponent>();
        using var archive = ZipFile.OpenRead(universalPackagePath);
        foreach (var entry in archive.Entries.OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase))
        {
            // Every package anywhere under artifacts/, as the import finds them:
            // UniversalModulePackageReader enumerates artifacts/ with
            // SearchOption.AllDirectories, and the identity comes from the file
            // name alone.
            var entryName = entry.FullName.Replace('\\', '/');
            if (!entryName.StartsWith(ArtifactsFolder, StringComparison.OrdinalIgnoreCase)
                || entryName.EndsWith('/'))
            {
                continue;
            }

            var identity = TryParseArtifactPackageName(entryName[(entryName.LastIndexOf('/') + 1)..]);
            if (identity is null)
            {
                continue;
            }

            var registered = await findRegistered(identity, cancellationToken);
            if (registered is null || string.IsNullOrWhiteSpace(registered.Sha256))
            {
                // Not registered at this version, or registered without a hash:
                // the import has nothing to contradict and adopts or imports it.
                components.Add(new PreStageComponent(
                    identity.TargetName,
                    identity.Version,
                    registered?.Version,
                    PackageSha256: null,
                    RegisteredSha256: registered?.Sha256,
                    identity.Describe()));
                continue;
            }

            string? packageSha256;
            string? measurementFailure = null;
            try
            {
                packageSha256 = await ComputeArtifactContentSha256Async(entry, workRoot, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
            {
                packageSha256 = null;
                measurementFailure = ex.Message;
            }

            components.Add(new PreStageComponent(
                identity.TargetName,
                identity.Version,
                registered.Version,
                packageSha256,
                registered.Sha256,
                identity.Describe(),
                measurementFailure));
        }

        return components;
    }

    private static async Task<string> ComputeArtifactContentSha256Async(
        ZipArchiveEntry entry,
        string workRoot,
        CancellationToken cancellationToken)
    {
        var workPath = Path.Join(workRoot, "prestage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workPath);
        try
        {
            var packagePath = Path.Join(workPath, "package.zip");
            entry.ExtractToFile(packagePath, overwrite: true);
            var package = new ArtifactPackageExtractor().Extract(packagePath, Path.Join(workPath, "content"));
            return await ArtifactHash.ComputeSha256Async(package.ArtifactContentPath, cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(workPath, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leftover work folder must not turn a measurement
                // into a failure.
            }
        }
    }
}
