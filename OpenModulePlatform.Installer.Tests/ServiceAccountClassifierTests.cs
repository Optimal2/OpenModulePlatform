using OpenModulePlatform.Installer.Core;

namespace OpenModulePlatform.Installer.Tests;

public class ServiceAccountClassifierTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_account_means_not_configured_and_no_password(string? account)
    {
        var info = ServiceAccountClassifier.Classify(account, "SERVER01");
        Assert.Equal(ServiceAccountKind.NotConfigured, info.Kind);
        Assert.False(info.RequiresPassword);
    }

    [Theory]
    [InlineData("LocalSystem")]
    [InlineData("NETWORKSERVICE")]
    [InlineData(@"NT AUTHORITY\SYSTEM")]
    [InlineData(@"NT AUTHORITY\NETWORK SERVICE")]
    [InlineData(@"NT SERVICE\some-service")]
    public void Built_in_accounts_need_no_password(string account)
    {
        var info = ServiceAccountClassifier.Classify(account, "SERVER01");
        Assert.Equal(ServiceAccountKind.BuiltIn, info.Kind);
        Assert.False(info.RequiresPassword);
    }

    [Theory]
    [InlineData(@"CONTOSO\svc-omp")]
    [InlineData("svc-omp@contoso.example.com")]
    public void Domain_accounts_are_detected_and_need_a_password(string account)
    {
        var info = ServiceAccountClassifier.Classify(account, "SERVER01");
        Assert.Equal(ServiceAccountKind.Domain, info.Kind);
        Assert.True(info.RequiresPassword);
    }

    [Fact]
    public void Domain_backslash_account_splits_for_logon()
    {
        var info = ServiceAccountClassifier.Classify(@"CONTOSO\svc-omp", "SERVER01");
        Assert.Equal("svc-omp", info.LogonUserName);
        Assert.Equal("CONTOSO", info.LogonDomain);
    }

    [Fact]
    public void Upn_account_logs_on_with_the_full_name()
    {
        var info = ServiceAccountClassifier.Classify("svc-omp@contoso.example.com", "SERVER01");
        Assert.Equal("svc-omp@contoso.example.com", info.LogonUserName);
        Assert.Equal(string.Empty, info.LogonDomain);
    }

    [Theory]
    [InlineData(@".\svc-omp")]
    [InlineData(@"SERVER01\svc-omp")]
    [InlineData("svc-omp")]
    public void Local_accounts_need_a_password_and_log_on_locally(string account)
    {
        var info = ServiceAccountClassifier.Classify(account, "SERVER01");
        Assert.Equal(ServiceAccountKind.LocalMachine, info.Kind);
        Assert.True(info.RequiresPassword);
        Assert.Equal("svc-omp", info.LogonUserName);
        Assert.Equal(".", info.LogonDomain);
    }
}
