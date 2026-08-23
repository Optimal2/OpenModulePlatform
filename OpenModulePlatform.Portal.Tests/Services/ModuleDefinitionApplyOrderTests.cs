// File: OpenModulePlatform.Portal.Tests/Services/ModuleDefinitionApplyOrderTests.cs
using OpenModulePlatform.Artifacts;
using Xunit;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Platform core must be applied before the modules whose scripts depend on its schema.
/// </summary>
/// <remarks>
/// Module definitions carry no dependency declaration, so both import paths applied them
/// in arrival order - alphabetically by module key on the portal path, package order on
/// the HostAgent path. Both put omp_auth before omp_core.
///
/// Measured in customer production 2026-08-23: omp_auth failed with "Invalid column name
/// 'ValidationRegex'. Invalid column name 'ExampleValues'." while omp_core - the
/// definition that adds exactly those two columns - was applied successfully immediately
/// afterwards. The same package imported cleanly in test, where core had been applied
/// long ago, and cleanly on a second production run for the same reason. An import that
/// succeeds on the second attempt is not a fixed import; it is the same bug with the
/// evidence erased.
/// </remarks>
public sealed class ModuleDefinitionApplyOrderTests
{
    [Fact]
    public void PlatformCore_RanksBeforeEverythingElse()
    {
        Assert.Equal(0, ModuleDefinitionApplyOrder.GetApplyRank("omp_core"));
        Assert.Equal(0, ModuleDefinitionApplyOrder.GetApplyRank("OMP_CORE"));

        foreach (var other in new[] { "omp_auth", "omp_portal", "ibs_packager", "log_search" })
        {
            Assert.Equal(1, ModuleDefinitionApplyOrder.GetApplyRank(other));
        }
    }

    /// <summary>
    /// The real ordering, on the real module keys from the failing production import.
    /// </summary>
    [Fact]
    public void SortingByRank_PutsCoreFirst_AndKeepsTheRestAlphabetical()
    {
        // Alphabetical order, which is what the portal path produced and what broke.
        string[] asArrived = ["omp_auth", "omp_core", "omp_portal"];

        var applied = asArrived
            .OrderBy(ModuleDefinitionApplyOrder.GetApplyRank)
            .ThenBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(["omp_core", "omp_auth", "omp_portal"], applied);
    }

    [Fact]
    public void PathVariant_DerivesTheModuleKeyFromTheFileName()
    {
        Assert.Equal(0, ModuleDefinitionApplyOrder.GetApplyRankForPath("module-definitions/omp_core.module-definition.json"));
        Assert.Equal(0, ModuleDefinitionApplyOrder.GetApplyRankForPath(@"module-definitions\omp_core.module-definition.json"));
        Assert.Equal(1, ModuleDefinitionApplyOrder.GetApplyRankForPath("module-definitions/omp_auth.module-definition.json"));
    }

    /// <summary>
    /// A substring match on "omp_core" would rank these as core. Deriving the key does not.
    /// </summary>
    [Fact]
    public void PathVariant_DoesNotMatchOnSubstrings()
    {
        Assert.Equal(1, ModuleDefinitionApplyOrder.GetApplyRankForPath("module-definitions/omp_core_extra.module-definition.json"));
        Assert.Equal(1, ModuleDefinitionApplyOrder.GetApplyRankForPath("omp_core/module-definitions/omp_auth.module-definition.json"));
    }

    [Fact]
    public void PathVariant_IsSafeOnInputItCannotInterpret()
    {
        Assert.Equal(1, ModuleDefinitionApplyOrder.GetApplyRankForPath(null));
        Assert.Equal(1, ModuleDefinitionApplyOrder.GetApplyRankForPath(""));
        Assert.Equal(1, ModuleDefinitionApplyOrder.GetApplyRankForPath("artifacts/omp_core__something__0.1.0.zip"));
    }
}
