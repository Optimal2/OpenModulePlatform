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

        Assert.Empty(ForeignSchemas(source));
    }

    [Theory]
    [InlineData("DELETE FROM omp_x.T WHERE HostId = @HostId;")]
    [InlineData("DELETE FROM [omp_x].[T] WHERE HostId = @HostId;")]
    [InlineData("DELETE FROM [ omp_x ].T WHERE HostId = @HostId;")]
    [InlineData("DELETE FROM omp_x.[T] WHERE HostId = @HostId;")]
    [InlineData("SELECT COUNT(*) FROM [omp_x] . [T];")]
    [InlineData("DELETE FROM \"omp_x\".\"T\" WHERE HostId = @HostId;")]
    [InlineData("DELETE FROM \"omp_x\".T WHERE HostId = @HostId;")]
    [InlineData("DELETE FROM omp_x.\"T\" WHERE HostId = @HostId;")]
    public void Pattern_FindsModuleSchemaInEveryQuotingForm(string sql)
    {
        Assert.Equal(["omp_x"], ForeignSchemas(sql));
    }

    [Theory]
    [InlineData("DELETE FROM omp.Hosts WHERE HostId = @HostId;")]
    [InlineData("DELETE FROM [omp].[Hosts] WHERE HostId = @HostId;")]
    [InlineData("SELECT COUNT(*) FROM [omp_content].[contents];")]
    [InlineData("DELETE FROM \"omp\".\"T\" WHERE HostId = @HostId;")]
    [InlineData("-- the omp_x module releases its own rows")]
    public void Pattern_AllowsSchemasOwnedByThisRepositoryAndPlainText(string sql)
    {
        Assert.Empty(ForeignSchemas(sql));
    }

    private static string[] ForeignSchemas(string source)
        => QualifiedModuleSchemaPattern().Matches(source)
            .Select(static match => match.Groups["schema"].Value)
            .Where(static schema => !SchemasOwnedByThisRepository.Contains(schema))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // A schema-qualified name in any quoting form: omp_x.T, [omp_x].[T], [ omp_x ].T, omp_x.[T],
    // "omp_x"."T" (QUOTED_IDENTIFIER), and mixes of them.
    [GeneratedRegex(@"[""\[]?\s*\b(?<schema>omp_[A-Za-z0-9_]+)\s*[""\]]?\s*\.\s*[""\[]?\s*[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedModuleSchemaPattern();
}
