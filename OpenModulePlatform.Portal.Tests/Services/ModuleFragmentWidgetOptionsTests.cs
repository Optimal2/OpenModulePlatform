using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Options;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class ModuleFragmentWidgetOptionsTests
{
    [Theory]
    [InlineData("http://user@module.example")]
    [InlineData("https://user:private-password@module.example")]
    [InlineData(" https://user%40name:private-password@module.example/path ")]
    public void UserInfo_FailsOptionsValidationWithoutExposingCredentials(string url)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IValidateOptions<ModuleFragmentWidgetOptions>, ModuleFragmentWidgetOptionsValidator>();
        services.AddOptions<ModuleFragmentWidgetOptions>().Configure(options =>
        {
            options.InternalBaseUrl = url;
            options.AllowInsecureInternalBaseUrl = true;
        }).ValidateOnStart();
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Equal("ModuleFragmentWidgets:InternalBaseUrl must not contain user information (username or password).",
            Assert.Single(error.Failures));
        Assert.DoesNotContain("private-password", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("http://127.0.0.1:5000/path")]
    [InlineData(" https://module.example/path ")]
    public void SafeOrUnsetInternalBaseUrl_IsAccepted(string? url)
    {
        var options = new ModuleFragmentWidgetOptions { InternalBaseUrl = url };

        Assert.False(options.AllowInsecureInternalBaseUrl);
        Assert.True(new ModuleFragmentWidgetOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("/relative")]
    [InlineData("ftp://module.example")]
    [InlineData("not a URL")]
    public void InvalidInternalBaseUrl_FailsWithConfigurationKey(string url)
    {
        var result = new ModuleFragmentWidgetOptionsValidator().Validate(null,
            new ModuleFragmentWidgetOptions { InternalBaseUrl = url });

        Assert.True(result.Failed);
        Assert.Equal("ModuleFragmentWidgets:InternalBaseUrl must be an absolute HTTP or HTTPS URL.",
            Assert.Single(result.Failures));
    }
}
