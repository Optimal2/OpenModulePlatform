using OpenModulePlatform.Installation;

namespace OpenModulePlatform.Installation.Tests;

public sealed class CoreMaintenanceVerificationTests
{
    private const string VerificationError = "Core maintenance could not verify the applied version and hash. Do not stage artifacts.";

    private static ModuleDefinitionDocument Definition(string version = "0.3.290", string hash = "abcdef")
        => new("omp_core", version, 1, "{}", hash, []);

    [Fact]
    public void ValidateAppliedCoreDefinition_MissingCurrent_ThrowsExactVerificationError()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => InstallationEngine.ValidateAppliedCoreDefinition(Definition(), current: null));

        Assert.Equal(VerificationError, ex.Message);
    }

    [Fact]
    public void ValidateAppliedCoreDefinition_DifferentVersion_ThrowsExactVerificationError()
    {
        var current = new AppliedModuleDefinition(1, "0.3.289", "abcdef");

        var ex = Assert.Throws<InvalidOperationException>(
            () => InstallationEngine.ValidateAppliedCoreDefinition(Definition(), current));

        Assert.Equal(VerificationError, ex.Message);
    }

    [Fact]
    public void ValidateAppliedCoreDefinition_DifferentHash_ThrowsExactVerificationError()
    {
        var current = new AppliedModuleDefinition(1, "0.3.290", "123456");

        var ex = Assert.Throws<InvalidOperationException>(
            () => InstallationEngine.ValidateAppliedCoreDefinition(Definition(), current));

        Assert.Equal(VerificationError, ex.Message);
    }

    [Fact]
    public void ValidateAppliedCoreDefinition_MatchingVersionAndHash_Succeeds()
    {
        var current = new AppliedModuleDefinition(1, "0.3.290", "abcdef");

        InstallationEngine.ValidateAppliedCoreDefinition(Definition(), current);
    }

    [Fact]
    public void ValidateAppliedCoreDefinition_HashCaseDifference_Succeeds()
    {
        var current = new AppliedModuleDefinition(1, "0.3.290", "ABCDEF");

        InstallationEngine.ValidateAppliedCoreDefinition(Definition(), current);
    }
}
