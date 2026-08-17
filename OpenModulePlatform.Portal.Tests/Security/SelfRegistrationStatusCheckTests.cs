using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// R7-F17 follow-up: the operator-facing self-registration status behind the
/// startup warning and the /runtime-versions row. An installation running
/// with self-registration on must surface a warning; a failed read must say
/// "unknown" rather than silently report disabled.
/// </summary>
public sealed class SelfRegistrationStatusCheckTests
{
    [Fact]
    public void Evaluate_EnabledValue_ReportsEnabledWithWarning()
    {
        var status = OmpSelfRegistrationStatusCheck.Evaluate(
            new OmpConfigurationRead("true", Failed: false));

        Assert.True(status.Enabled);
        Assert.NotNull(status.Warning);
        Assert.Contains("selfRegistrationEnabled", status.Warning);
    }

    [Theory]
    [InlineData("false")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-bool")]
    public void Evaluate_DisabledOrAbsentValue_ReportsDisabledWithoutWarning(string? value)
    {
        var status = OmpSelfRegistrationStatusCheck.Evaluate(
            new OmpConfigurationRead(value, Failed: false));

        Assert.False(status.Enabled);
        Assert.Null(status.Warning);
    }

    [Fact]
    public void Evaluate_FailedRead_ReportsUnknownWithWarning()
    {
        var status = OmpSelfRegistrationStatusCheck.Evaluate(
            new OmpConfigurationRead(null, Failed: true));

        Assert.Null(status.Enabled);
        Assert.NotNull(status.Warning);
        Assert.Contains("could not be read", status.Warning);
    }
}
