using Xunit;

namespace OpenModulePlatform.Bootstrapper.Tests;

public sealed class ModuleDefinitionImportSelectionTests
{
    private static ModuleDefinitionDocument CreateDefinition(string moduleKey, string definitionVersion)
        => new(moduleKey, definitionVersion, 1, "{}", "0000", []);

    [Fact]
    public void IsSupersededByNewerPackageDefinition_SkipsOlderVersionWhenPackageHasNewerDefinition()
    {
        var older = CreateDefinition("ibs_packager", "0.3.159");
        var newer = CreateDefinition("ibs_packager", "0.3.160");
        var package = new[] { older, newer };

        Assert.True(Program.IsSupersededByNewerPackageDefinition(older, package));
        Assert.False(Program.IsSupersededByNewerPackageDefinition(newer, package));
    }

    [Fact]
    public void IsSupersededByNewerPackageDefinition_KeepsSameVersionSoFailedScriptsAreRetried()
    {
        var definition = CreateDefinition("ibs_packager", "0.3.159");
        var package = new[] { definition, CreateDefinition("ibs_packager", "0.3.159") };

        Assert.False(Program.IsSupersededByNewerPackageDefinition(definition, package));
    }

    [Fact]
    public void IsSupersededByNewerPackageDefinition_IgnoresNewerDefinitionsForOtherModules()
    {
        var definition = CreateDefinition("ibs_packager", "0.3.159");
        var package = new[] { definition, CreateDefinition("omp_core", "0.9.0") };

        Assert.False(Program.IsSupersededByNewerPackageDefinition(definition, package));
    }

    [Fact]
    public void IsSupersededByNewerPackageDefinition_MatchesModuleKeysCaseInsensitively()
    {
        var older = CreateDefinition("Ibs_Packager", "0.3.159");
        var package = new[] { older, CreateDefinition("ibs_packager", "0.3.160") };

        Assert.True(Program.IsSupersededByNewerPackageDefinition(older, package));
    }

    [Fact]
    public void IsSupersededByNewerPackageDefinition_ComparesVersionsNumericallyNotLexically()
    {
        var older = CreateDefinition("ibs_packager", "0.3.9");
        var package = new[] { older, CreateDefinition("ibs_packager", "0.3.10") };

        Assert.True(Program.IsSupersededByNewerPackageDefinition(older, package));
    }
}
