using OpenModulePlatform.Installer.Core;

namespace OpenModulePlatform.Installer.Tests;

public class AlreadyInstalledTests
{
    [Fact]
    public void Existing_service_means_already_installed()
        => Assert.True(AlreadyInstalledDetector.IsAlreadyInstalled(hostAgentServiceExists: true, installRootContainsHostAgent: false));

    [Fact]
    public void HostAgent_in_install_root_means_already_installed()
        => Assert.True(AlreadyInstalledDetector.IsAlreadyInstalled(hostAgentServiceExists: false, installRootContainsHostAgent: true));

    [Fact]
    public void Neither_fact_means_first_install()
        => Assert.False(AlreadyInstalledDetector.IsAlreadyInstalled(hostAgentServiceExists: false, installRootContainsHostAgent: false));
}

public class ProfileTargetResolverTests
{
    [Fact]
    public void Portal_url_uses_binding_protocol_host_header_and_port()
    {
        var config = new OpenModulePlatform.Installation.BootstrapConfig
        {
            HostAgent = new OpenModulePlatform.Installation.HostAgentInstallOptions
            {
                IisBindingProtocol = "https",
                IisBindingHostHeader = "portal.example.com",
                IisBindingPort = 443
            }
        };
        Assert.Equal("https://portal.example.com/", ProfileTargetResolver.ResolvePortalUrl(config));
    }

    [Fact]
    public void Portal_url_falls_back_to_localhost_and_keeps_non_default_port()
    {
        var config = new OpenModulePlatform.Installation.BootstrapConfig
        {
            HostAgent = new OpenModulePlatform.Installation.HostAgentInstallOptions
            {
                IisBindingProtocol = "http",
                IisBindingPort = 8088
            }
        };
        Assert.Equal("http://localhost:8088/", ProfileTargetResolver.ResolvePortalUrl(config));
    }

    [Fact]
    public void Auth_app_artifact_triggers_windows_authentication_requirement()
    {
        var config = new OpenModulePlatform.Installation.BootstrapConfig();
        config.Artifacts.Add(new OpenModulePlatform.Installation.ArtifactPayloadOptions
        {
            Source = @"data\global\artifacts\omp-auth-web-0.3.354.zip",
            Target = @"C:\OMP\WebApps\auth"
        });
        Assert.True(ProfileTargetResolver.ProfileUsesWindowsAuthentication(config));
        Assert.False(ProfileTargetResolver.ProfileUsesBlazor(config));
    }

    [Fact]
    public void Synced_package_target_for_the_auth_artifact_triggers_windows_authentication()
    {
        // A profile refreshed by the payload sync carries versioned
        // artifact-store targets, not the deployed-app folder: the auth app
        // must still be recognised or the Web-Windows-Auth feature check
        // would silently never fire.
        var config = new OpenModulePlatform.Installation.BootstrapConfig();
        config.Artifacts.Add(new OpenModulePlatform.Installation.ArtifactPayloadOptions
        {
            Source = @"data\global\artifacts\payload\OpenModulePlatform.Auth.zip",
            Target = @"omp-auth/web/0.3.873/payload/OpenModulePlatform.Auth.zip"
        });
        Assert.True(ProfileTargetResolver.ProfileUsesWindowsAuthentication(config));
    }

    [Fact]
    public void Portal_artifact_does_not_trigger_windows_authentication()
    {
        var config = new OpenModulePlatform.Installation.BootstrapConfig();
        config.Artifacts.Add(new OpenModulePlatform.Installation.ArtifactPayloadOptions
        {
            Source = @"data\global\artifacts\payload\OpenModulePlatform.Portal.zip",
            Target = @"omp-portal/web/0.3.873/payload/OpenModulePlatform.Portal.zip"
        });
        Assert.False(ProfileTargetResolver.ProfileUsesWindowsAuthentication(config));
    }

    [Fact]
    public void Disabled_auth_artifact_does_not_trigger_windows_authentication()
    {
        var config = new OpenModulePlatform.Installation.BootstrapConfig();
        config.Artifacts.Add(new OpenModulePlatform.Installation.ArtifactPayloadOptions
        {
            Enabled = false,
            Source = @"data\global\artifacts\omp-auth-web-0.3.354.zip",
            Target = @"C:\OMP\WebApps\auth"
        });
        Assert.False(ProfileTargetResolver.ProfileUsesWindowsAuthentication(config));
    }

    [Fact]
    public void Blazor_artifact_triggers_websocket_requirement()
    {
        var config = new OpenModulePlatform.Installation.BootstrapConfig();
        config.Artifacts.Add(new OpenModulePlatform.Installation.ArtifactPayloadOptions
        {
            Source = @"data\global\artifacts\example-webapp-blazor-0.3.342.zip",
            Target = @"C:\OMP\WebApps\blazorapp"
        });
        Assert.True(ProfileTargetResolver.ProfileUsesBlazor(config));
    }
}
