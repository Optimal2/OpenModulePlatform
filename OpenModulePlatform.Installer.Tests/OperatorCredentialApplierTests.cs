using OpenModulePlatform.Installation;
using OpenModulePlatform.Installer.Core;

namespace OpenModulePlatform.Installer.Tests;

public class OperatorCredentialApplierTests
{
    private static BootstrapConfig ConfigWithServiceAccount(string serviceAccount)
        => new()
        {
            HostAgent = new HostAgentInstallOptions
            {
                ServiceAccountName = serviceAccount,
                IisAppPoolUserName = serviceAccount
            }
        };

    [Fact]
    public void Typed_password_is_written_to_the_service_account()
    {
        var config = ConfigWithServiceAccount(@"CONTOSO\svc-omp");
        OperatorCredentialApplier.Apply(config, "s3cret");
        Assert.Equal("s3cret", config.HostAgent.ServiceAccountPassword);
    }

    [Fact]
    public void App_pool_with_same_account_and_no_password_reuses_the_service_password()
    {
        var config = ConfigWithServiceAccount(@"CONTOSO\svc-omp");
        OperatorCredentialApplier.Apply(config, "s3cret");
        Assert.Equal("s3cret", config.HostAgent.IisAppPoolPassword);
    }

    [Fact]
    public void App_pool_with_same_account_in_upn_form_reuses_the_service_password()
    {
        var config = new BootstrapConfig
        {
            HostAgent = new HostAgentInstallOptions
            {
                ServiceAccountName = "svc-omp@contoso.example.com",
                IisAppPoolUserName = @"CONTOSO\svc-omp"
            }
        };
        OperatorCredentialApplier.Apply(config, "s3cret");
        Assert.Equal("s3cret", config.HostAgent.IisAppPoolPassword);
    }

    [Fact]
    public void App_pool_with_a_different_account_is_left_alone()
    {
        var config = new BootstrapConfig
        {
            HostAgent = new HostAgentInstallOptions
            {
                ServiceAccountName = @"CONTOSO\svc-omp",
                IisAppPoolUserName = @"CONTOSO\svc-web"
            }
        };
        OperatorCredentialApplier.Apply(config, "s3cret");
        Assert.Equal(string.Empty, config.HostAgent.IisAppPoolPassword);
    }

    [Fact]
    public void App_pool_with_its_own_password_keeps_it()
    {
        var config = new BootstrapConfig
        {
            HostAgent = new HostAgentInstallOptions
            {
                ServiceAccountName = @"CONTOSO\svc-omp",
                IisAppPoolUserName = @"CONTOSO\svc-omp",
                IisAppPoolPassword = "enc:aesgcm:v1:whatever"
            }
        };
        OperatorCredentialApplier.Apply(config, "s3cret");
        Assert.Equal("enc:aesgcm:v1:whatever", config.HostAgent.IisAppPoolPassword);
    }

    [Fact]
    public void App_pool_with_a_credential_key_is_not_given_a_plaintext_password()
    {
        var config = new BootstrapConfig
        {
            HostAgent = new HostAgentInstallOptions
            {
                ServiceAccountName = @"CONTOSO\svc-omp",
                IisAppPoolUserName = @"CONTOSO\svc-omp",
                IisAppPoolPasswordCredentialKey = "iis:pool"
            }
        };
        OperatorCredentialApplier.Apply(config, "s3cret");
        Assert.Equal(string.Empty, config.HostAgent.IisAppPoolPassword);
    }

    [Fact]
    public void Pool_override_with_same_account_and_no_password_reuses_the_service_password()
    {
        var config = new BootstrapConfig
        {
            HostAgent = new HostAgentInstallOptions
            {
                ServiceAccountName = @"CONTOSO\svc-omp",
                IisAppPoolOverrides = new Dictionary<string, IisAppPoolIdentityOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["portal"] = new() { UserName = @"CONTOSO\svc-omp" }
                }
            }
        };
        OperatorCredentialApplier.Apply(config, "s3cret");
        Assert.Equal("s3cret", config.HostAgent.IisAppPoolOverrides["portal"].Password);
    }

    [Fact]
    public void Empty_typed_password_never_creates_pool_passwords()
    {
        var config = ConfigWithServiceAccount(@"CONTOSO\svc-omp");
        OperatorCredentialApplier.Apply(config, string.Empty);
        Assert.Equal(string.Empty, config.HostAgent.ServiceAccountPassword);
        Assert.Equal(string.Empty, config.HostAgent.IisAppPoolPassword);
    }

    [Fact]
    public void Passwords_are_never_trimmed()
    {
        // LogonUser validated the exact typed value; leading/trailing spaces
        // are legal password characters and must survive.
        var config = ConfigWithServiceAccount(@"CONTOSO\svc-omp");
        OperatorCredentialApplier.Apply(config, "  padded secret  ");
        Assert.Equal("  padded secret  ", config.HostAgent.ServiceAccountPassword);
        Assert.Equal("  padded secret  ", config.HostAgent.IisAppPoolPassword);
    }

    [Fact]
    public void A_configured_password_with_spaces_is_reused_verbatim_for_the_pool()
    {
        var config = ConfigWithServiceAccount(@"CONTOSO\svc-omp");
        config.HostAgent.ServiceAccountPassword = " padded ";
        OperatorCredentialApplier.Apply(config, string.Empty);
        Assert.Equal(" padded ", config.HostAgent.ServiceAccountPassword);
        Assert.Equal(" padded ", config.HostAgent.IisAppPoolPassword);
    }
}
