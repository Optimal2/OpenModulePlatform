using OpenModulePlatform.Installer.Core;
using OpenModulePlatform.Installer.Ui;

namespace OpenModulePlatform.Installer.Tests;

// Pure fabricated state only: these tests create no Form and call no account API.
public sealed class AccountValidationInvalidationTests
{
    [Fact]
    public void EditingInputInvalidatesVerificationEvenWhenTheOldEvaluationIsGreen()
    {
        var state = Verified();
        Assert.True(state.CanInstall);

        state.InvalidatePassword();

        Assert.False(state.PasswordValidated);
        Assert.False(state.CanInstall);
        state.SetPasswordValidation(true);
        Assert.True(state.CanInstall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefreshDropsBothVerificationAndTheOldEvaluation(bool differentAccount)
    {
        var state = Verified();
        state.BeginRefresh();
        Assert.Null(state.Evaluation);
        Assert.False(state.PasswordValidated);
        Assert.False(state.CanInstall);

        state.CompleteRefresh(Evaluation(differentAccount ? "other-service" : "service"));

        Assert.False(state.CanInstall);
        state.SetPasswordValidation(true);
        Assert.True(state.CanInstall);
    }

    [Fact]
    public void FailedRefreshCannotReuseThePreviousGate()
    {
        var state = Verified();
        state.BeginRefresh(); // No completion: the environment check failed.
        state.SetPasswordValidation(true); // Even an obsolete callback cannot enable install.
        Assert.False(state.CanInstall);
    }

    [Fact]
    public void FailedVerificationAndBlockingPrerequisitesKeepTheGateClosed()
    {
        var state = Verified();
        state.SetPasswordValidation(false);
        Assert.False(state.CanInstall);
        state.CompleteRefresh(Evaluation() with
        {
            Checks = [new(PrerequisiteCheckId.SqlDatabase, false, "Database", "Synthetic blocker")]
        });
        state.SetPasswordValidation(true);
        Assert.False(state.CanInstall);
    }

    [Fact]
    public void BuiltInAccountDoesNotRequirePasswordVerification()
    {
        var state = new AccountValidationState();
        state.CompleteRefresh(Evaluation() with
        {
            ServiceAccount = new(ServiceAccountKind.BuiltIn, "Synthetic built-in", "", "")
        });
        state.InvalidatePassword();
        Assert.True(state.CanInstall);
    }

    private static AccountValidationState Verified()
    {
        var state = new AccountValidationState();
        state.CompleteRefresh(Evaluation());
        state.SetPasswordValidation(true);
        return state;
    }

    private static PrerequisiteEvaluation Evaluation(string user = "service") => new(
        [new(PrerequisiteCheckId.ServiceAccount, true, "Account", "Synthetic green result")],
        IsServerOs: true, MissingIisFeatures: [], HostingBundleRequired: false,
        HostingBundleRepairRequired: false, HostingBundleInstallerPath: null,
        RequiredRuntimeMajor: 10,
        new ServiceAccountInfo(ServiceAccountKind.Domain, "EXAMPLE\\" + user, user, "EXAMPLE"));
}
