using Xunit;

namespace OpenModulePlatform.Bootstrapper.Tests;

public sealed class DeveloperSourceStatusComparisonTests
{
    private static Program.ManifestComponent CreateComponent(
        string version,
        string relativePathTemplate = "omp-portal/web/{version}")
        => new(
            SourceRoot: @"C:\src",
            RepositoryKey: "omp",
            ComponentKey: "omp-portal-web",
            ModuleKey: "omp_portal",
            AppKey: "portal",
            PackageType: "webapp",
            TargetName: "web",
            Version: version,
            RelativePathTemplate: relativePathTemplate,
            ProjectPath: "OpenModulePlatform.Portal/OpenModulePlatform.Portal.csproj",
            PackageFileTemplate: "{componentKey}-{version}.zip",
            MinModuleDefinitionVersion: "0.1.0");

    [Fact]
    public void StaleVersionSegmentInTarget_WhenZipCarriesSourceVersion_IsNotAnUpdate()
    {
        // Sabotage case from the linus_hemma dev installation: the config Target still
        // carries the stale 0.3.64 segment while the configured zip and the source
        // manifest both carry 0.3.563. A path-only mismatch must never be an UPDATE.
        var component = CreateComponent("0.3.563");
        var current = new ArtifactPayloadOptions
        {
            Target = "omp-portal/web/0.3.64",
            Source = @"artifacts\omp_portal__portal__webapp__web__0.3.563.zip"
        };

        var evaluation = Program.EvaluateComponentPackageStatus(component, current);

        Assert.NotEqual("UPDATE", evaluation.Status);
        Assert.Equal("NORMALIZED", evaluation.Status);
        Assert.False(Program.IsDeveloperUpdateStatus(evaluation.Status));
    }

    [Fact]
    public void OlderConfiguredZipVersion_StillYieldsUpdate()
    {
        var component = CreateComponent("0.3.114");
        var current = new ArtifactPayloadOptions
        {
            Target = "omp-portal/web/0.3.111",
            Source = @"artifacts\omp_portal__portal__webapp__web__0.3.111.zip"
        };

        var evaluation = Program.EvaluateComponentPackageStatus(component, current);

        Assert.Equal("UPDATE", evaluation.Status);
        Assert.True(Program.IsDeveloperUpdateStatus(evaluation.Status));
    }

    [Fact]
    public void OlderConfiguredTargetSegment_WhenZipNameHasNoIdentity_StillYieldsUpdate()
    {
        // Fallback: no parseable package identity in Source, so the version is taken
        // from the {version}-position segment of Target.
        var component = CreateComponent("0.3.114");
        var current = new ArtifactPayloadOptions
        {
            Target = "omp-portal/web/0.3.111",
            Source = @"artifacts\portal-package.zip"
        };

        var evaluation = Program.EvaluateComponentPackageStatus(component, current);

        Assert.Equal("UPDATE", evaluation.Status);
    }

    [Fact]
    public void NewerConfiguredZipVersion_YieldsDiff()
    {
        var component = CreateComponent("0.3.111");
        var current = new ArtifactPayloadOptions
        {
            Target = "omp-portal/web/0.3.114",
            Source = @"artifacts\omp_portal__portal__webapp__web__0.3.114.zip"
        };

        var evaluation = Program.EvaluateComponentPackageStatus(component, current);

        Assert.Equal("DIFF", evaluation.Status);
    }

    [Fact]
    public void EqualVersionWithDriftingTargetPath_YieldsNormalized()
    {
        var component = CreateComponent("0.3.563");
        var current = new ArtifactPayloadOptions
        {
            Target = "omp-portal/web/0.3.563/",
            Source = @"artifacts\omp_portal__portal__webapp__web__0.3.563.zip"
        };

        var evaluation = Program.EvaluateComponentPackageStatus(component, current);

        Assert.Equal("NORMALIZED", evaluation.Status);
        Assert.Contains("same version 0.3.563", evaluation.Line);
    }

    [Fact]
    public void EqualVersionAndMatchingTarget_YieldsOk()
    {
        var component = CreateComponent("0.3.563");
        var current = new ArtifactPayloadOptions
        {
            Target = "omp-portal/web/0.3.563",
            Source = @"artifacts\omp_portal__portal__webapp__web__0.3.563.zip"
        };

        var evaluation = Program.EvaluateComponentPackageStatus(component, current);

        Assert.Equal("OK", evaluation.Status);
    }

    [Fact]
    public void DatabaseSide_OlderInstalledVersion_StillYieldsUpdate()
    {
        Assert.Equal("UPDATE", Program.CompareInstalledVersion("0.3.111", "0.3.114"));
        Assert.Equal("OK", Program.CompareInstalledVersion("0.3.114", "0.3.114"));
        Assert.Equal("DIFF", Program.CompareInstalledVersion("0.3.115", "0.3.114"));
    }

    [Fact]
    public void CombinedStatus_NormalizedPackageSide_SurvivesOkDatabaseSide()
    {
        Assert.Equal("NORMALIZED", Program.CombineDeveloperSourceStatus("NORMALIZED", "OK"));
        Assert.Equal("NORMALIZED", Program.CombineDeveloperSourceStatus("NORMALIZED", null));
        Assert.Equal("UPDATE", Program.CombineDeveloperSourceStatus("NORMALIZED", "UPDATE"));
        Assert.Equal("UPDATE", Program.CombineDeveloperSourceStatus("UPDATE", "OK"));
        Assert.Equal("OK", Program.CombineDeveloperSourceStatus("OK", "OK"));
    }
}
