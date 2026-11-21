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
}
