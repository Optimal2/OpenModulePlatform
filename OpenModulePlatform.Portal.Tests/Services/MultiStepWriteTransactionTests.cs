using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class MultiStepWriteTransactionTests
{
    [Fact]
    public void TemplateArtifactUpgrade_CarriesConfigurationMovesPointerAndCarriesEditsInOneTransaction()
    {
        var source = OmpRepositoryFiles.ReadRepositoryTextFile(
            "OpenModulePlatform.Portal",
            "Services",
            "OmpAdminRepository.Editor.cs");
        var body = MethodBody(
            source,
            "public async Task<(string TargetVersion, ArtifactConfigurationCarryForwardResult CarryForward, string? ConfigurationCarryMessage)> UpgradeInstanceTemplateAppArtifactAsync",
            "public async Task<InstanceTemplateHostEditData?> GetInstanceTemplateHostAsync");

        Assert.Contains("BeginTransactionAsync", body, StringComparison.Ordinal);
        Assert.Contains("CarryForwardArtifactConfigurationFilesCoreAsync(conn, tx", body, StringComparison.Ordinal);
        Assert.Contains("CommitAsync", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileImageRemoval_UpdatesUserAndBinaryStorageInOneTransaction()
    {
        var source = OmpRepositoryFiles.ReadRepositoryTextFile(
            "OpenModulePlatform.Portal",
            "Services",
            "UserProfileImageService.cs");
        var body = MethodBody(
            source,
            "public async Task<bool> RemoveProfileImageAsync",
            "private static string NormalizeContentType");

        Assert.Contains("BeginTransactionAsync", body, StringComparison.Ordinal);
        Assert.Contains("DisableProfileImageBinaryDataAsync(conn, tx", body, StringComparison.Ordinal);
        Assert.Contains("CommitAsync", body, StringComparison.Ordinal);
    }

    private static string MethodBody(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, startMarker);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, endMarker);
        return source[start..end];
    }
}
