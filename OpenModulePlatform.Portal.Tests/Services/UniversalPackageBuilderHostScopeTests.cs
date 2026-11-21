using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Source-level guards for the two browser builders. They run without a browser
/// in the ordinary unit suite; the existing Playwright command covers zip creation.
/// </summary>
public sealed class UniversalPackageBuilderHostScopeTests
{
    [Theory]
    [InlineData("OpenModulePlatform.Portal", "wwwroot/js/admin-universal-package-builder.js")]
    [InlineData("tools/universal-package-builder", "index.html")]
    public void GlobalPackageCreation_RejectsHostConfigurationsAndOverlays(string root, string relativePath)
    {
        var source = File.ReadAllText(OmpRepositoryFiles.GetRepositoryPath(root, relativePath));
        var guardStart = source.IndexOf(
            "if (!manifest.targetHostProfile && packageItems.some(item =>",
            StringComparison.Ordinal);
        var entriesStart = source.IndexOf("const entries = [];", StringComparison.Ordinal);

        Assert.True(guardStart >= 0, "The global-package host-scope guard is missing.");
        Assert.True(guardStart < entriesStart, "The host-scope guard must run before zip entries are created.");
        var guard = source[guardStart..entriesStart];
        Assert.Contains("host-config", guard, StringComparison.Ordinal);
        Assert.Contains("config-overlay", guard, StringComparison.Ordinal);
        Assert.Contains("return;", guard, StringComparison.Ordinal);
    }
}
