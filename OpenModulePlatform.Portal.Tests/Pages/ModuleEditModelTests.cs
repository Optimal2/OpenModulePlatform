using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Pages.Admin;

namespace OpenModulePlatform.Portal.Tests.Pages;

public sealed class ModuleEditModelTests
{
    [Fact]
    public void ModuleTypeOptions_PreserveAnExistingTypeTheEditorDoesNotOffer()
    {
        var options = StandardModuleTypes();

        var result = ModuleEditModel.IncludeCurrentModuleTypeOption(options, "PlatformCore");

        Assert.Equal("PlatformCore", result[0].Value);
        Assert.Equal("PlatformCore", result[0].Label);
        Assert.Equal(4, result.Count);
    }

    [Theory]
    [InlineData("PortalModule")]
    [InlineData("webappmodule")]
    [InlineData("")]
    [InlineData(null)]
    public void ModuleTypeOptions_DoNotDuplicateOfferedOrEmptyTypes(string? currentType)
    {
        var options = StandardModuleTypes();

        var result = ModuleEditModel.IncludeCurrentModuleTypeOption(options, currentType);

        Assert.Equal(3, result.Count);
        Assert.Equal(options.Select(option => option.Value), result.Select(option => option.Value));
    }

    private static List<OptionItem> StandardModuleTypes() =>
    [
        new() { Value = "PortalModule", Label = "Portal module" },
        new() { Value = "WebAppModule", Label = "Web app module" },
        new() { Value = "HostAppModule", Label = "Host app module" }
    ];
}
