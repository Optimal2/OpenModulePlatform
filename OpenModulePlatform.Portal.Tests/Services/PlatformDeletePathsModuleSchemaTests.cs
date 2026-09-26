using System.Text.RegularExpressions;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// The platform's host, artifact and app-instance delete paths name no table of a module outside
/// this repository. A module whose rows reference platform rows declares runtimeMaintenance steps
/// (docs/MODULE_DEFINITIONS.md, "Runtime maintenance steps"); cleanup SQL for one module written
/// into the platform is what those steps replaced.
/// </summary>
public sealed partial class PlatformDeletePathsModuleSchemaTests
{
    // omp is the platform's own schema; omp_content belongs to the content web app module that
    // ships in this repository.
    private static readonly HashSet<string> SchemasOwnedByThisRepository = new(StringComparer.OrdinalIgnoreCase)
    {
        "omp",
        "omp_content",
    };

    [Theory]
    [InlineData("OpenModulePlatform.HostAgent.Runtime", "Services", "OmpHostArtifactRepository.Maintenance.cs")]
    [InlineData("OpenModulePlatform.Portal", "Services", "OmpAdminRepository.Editor.cs")]
    public void DeletePaths_ReferenceOnlySchemasOwnedByThisRepository(string project, string folder, string file)
    {
        var source = OmpRepositoryFiles.ReadRepositoryTextFile(project, folder, file);

        var foreignSchemas = QualifiedModuleSchemaPattern().Matches(source)
            .Select(static match => match.Groups["schema"].Value)
            .Where(static schema => !SchemasOwnedByThisRepository.Contains(schema))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Empty(foreignSchemas);
    }

    [GeneratedRegex(@"\b(?<schema>omp_[A-Za-z0-9_]+)\s*\.\s*\[?[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedModuleSchemaPattern();
}
