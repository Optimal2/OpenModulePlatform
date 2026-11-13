using OpenModulePlatform.Web.ContextGuardModule.Options;

namespace OpenModulePlatform.Web.ContextGuardModule.Tests;

public sealed class ContextGuardOptionsValidatorTests
{
    [Fact]
    public void Validate_WithValidProfile_Succeeds()
    {
        var result = new ContextGuardOptionsValidator().Validate(null, ValidOptions());

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Theory]
    [InlineData("user", true)]
    [InlineData("user+client", true)]
    [InlineData("client", false)]
    public void Validate_WithScope_EnforcesSupportedValues(string scope, bool expected)
    {
        var options = ValidOptions();
        options.Scope = scope;

        Assert.Equal(expected, new ContextGuardOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_WithTooShortHeartbeatTimeout_Fails()
    {
        var options = ValidOptions();
        options.HeartbeatIntervalSeconds = 10;
        options.HeartbeatTimeoutSeconds = 19;

        Assert.False(new ContextGuardOptionsValidator().Validate(null, options).Succeeded);
    }

    private static ContextGuardOptions ValidOptions() => new()
    {
        Profiles = new Dictionary<string, ContextGuardProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["profile"] = new()
            {
                TargetUrl = "https://target.example/result",
                AllowedFieldNames = ["correlation"],
                CorrelationFieldName = "correlation"
            }
        }
    };
}
