using OpenModulePlatform.Web.Shared.Options;

namespace OpenModulePlatform.Portal.Tests.Options;

public sealed class WebAppOptionsValidatorTests
{
    private readonly WebAppOptionsValidator _validator = new();

    [Fact]
    public void Validate_InvalidSupportedCulture_FailsStartupValidation()
    {
        var result = _validator.Validate(
            null,
            new WebAppOptions { SupportedCultures = ["sv-SE", "not_a_culture"] });

        Assert.True(result.Failed);
        Assert.Contains("not_a_culture", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ValidAndBlankSupportedCultures_Succeeds()
    {
        var result = _validator.Validate(
            null,
            new WebAppOptions { SupportedCultures = ["sv-SE", "en-US", "", " "] });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_InvalidPortalBaseUrl_NamesTheBoundSection()
    {
        // The Portal and the example apps bind WebAppOptions from "Portal", not "WebApp";
        // the failure must name the key the operator actually has to change.
        var validator = new WebAppOptionsValidator("Portal");

        var result = validator.Validate(
            null,
            new WebAppOptions { PortalTopBar = new PortalTopBarOptions { PortalBaseUrl = "portal" } });

        Assert.True(result.Failed);
        Assert.Contains("Portal:PortalTopBar:PortalBaseUrl", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("WebApp:", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_InvalidSupportedCulture_NamesTheBoundSection()
    {
        var validator = new WebAppOptionsValidator("Portal");

        var result = validator.Validate(
            null,
            new WebAppOptions { SupportedCultures = ["not_a_culture"] });

        Assert.True(result.Failed);
        Assert.Contains("Portal:SupportedCultures", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_DefaultConstructor_NamesTheWebAppSection()
    {
        var result = _validator.Validate(
            null,
            new WebAppOptions { PortalTopBar = new PortalTopBarOptions { PortalBaseUrl = "relative" } });

        Assert.True(result.Failed);
        Assert.Contains("WebApp:PortalTopBar:PortalBaseUrl", result.FailureMessage, StringComparison.Ordinal);
    }
}
