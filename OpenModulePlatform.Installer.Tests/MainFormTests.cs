using OpenModulePlatform.Installer.Core;
using OpenModulePlatform.Installer.Ui;

namespace OpenModulePlatform.Installer.Tests;

public sealed class MainFormTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Password_validation_is_required_before_install(bool passwordValidated, bool expected)
    {
        var evaluation = Evaluation(new PrerequisiteCheckResult(
            PrerequisiteCheckId.ServiceAccount,
            IsSatisfied: true,
            "Service account",
            "Ready"));

        Assert.Equal(expected, MainForm.CanInstallAfterPasswordValidation(passwordValidated, evaluation));
    }

    [Fact]
    public void Validated_password_allows_auto_fixes_and_the_service_account_check()
    {
        var evaluation = Evaluation(
            new PrerequisiteCheckResult(PrerequisiteCheckId.IisWebServer, false, "IIS", "Missing")
            {
                CanAutoFix = true
            },
            new PrerequisiteCheckResult(PrerequisiteCheckId.ServiceAccount, false, "Service account", "Password required"));

        Assert.True(MainForm.CanInstallAfterPasswordValidation(true, evaluation));
    }

    [Fact]
    public void Validated_password_does_not_bypass_a_blocking_check()
    {
        var evaluation = Evaluation(
            new PrerequisiteCheckResult(PrerequisiteCheckId.SqlDatabase, false, "Database", "Unavailable"),
            new PrerequisiteCheckResult(PrerequisiteCheckId.ServiceAccount, false, "Service account", "Password required"));

        Assert.False(MainForm.CanInstallAfterPasswordValidation(true, evaluation));
    }

    private static PrerequisiteEvaluation Evaluation(params PrerequisiteCheckResult[] checks)
        => new(
            checks,
            IsServerOs: true,
            MissingIisFeatures: [],
            HostingBundleRequired: false,
            HostingBundleRepairRequired: false,
            HostingBundleInstallerPath: null,
            RequiredRuntimeMajor: 10,
            new ServiceAccountInfo(ServiceAccountKind.Domain, "CONTOSO\\service", "service", "CONTOSO"));
}
