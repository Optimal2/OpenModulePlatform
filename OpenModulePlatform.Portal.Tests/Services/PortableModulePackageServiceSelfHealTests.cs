using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Verifies the self-healing import rules for platform-core (omp_core) module definitions.
/// </summary>
public sealed class PortableModulePackageServiceSelfHealTests
{
    private static readonly Type ServiceType = typeof(PortableModulePackageService);
    private static readonly System.Reflection.MethodInfo RequiresPreApplySqlRepairsMethod = ServiceType.GetMethod(
        "RequiresPreApplySqlRepairs",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
        null,
        [typeof(ModuleDefinitionDocumentEditData)],
        null)!;

    [Theory]
    [InlineData("omp_core", true)]
    [InlineData("OMP_CORE", true)]
    [InlineData("omp_portal", false)]
    [InlineData("omp_auth", false)]
    [InlineData("content_webapp", false)]
    [InlineData("", false)]
    public void RequiresPreApplySqlRepairs_IdentifiesPlatformCoreOnly(string moduleKey, bool expected)
    {
        var definition = new ModuleDefinitionDocumentEditData { ModuleKey = moduleKey };

        var actual = (bool)RequiresPreApplySqlRepairsMethod.Invoke(null, [definition])!;

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("omp_core", true, false)]   // skip message present, but omp_core must never be skipped
    [InlineData("omp_core", false, false)]  // no skip message, not skipped
    [InlineData("omp_portal", true, true)]  // skip message present, non-core is skipped
    [InlineData("omp_portal", false, false)] // no skip message, not skipped
    public void SkipGate_CombinedCondition_SkipsOnlyNonPlatformCore(
        string moduleKey,
        bool hasSkipMessage,
        bool expectedSkip)
    {
        var definition = new ModuleDefinitionDocumentEditData { ModuleKey = moduleKey };
        var requiresPreApplySqlRepairs = (bool)RequiresPreApplySqlRepairsMethod.Invoke(null, [definition])!;

        // Mirror the exact condition used in PortableModulePackageService.
        var shouldSkip = hasSkipMessage && !requiresPreApplySqlRepairs;

        Assert.Equal(expectedSkip, shouldSkip);
    }
}
