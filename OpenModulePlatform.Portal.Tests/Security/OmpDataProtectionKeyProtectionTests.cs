using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.Options;

namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// Route-selection tests for the shared Data Protection key-ring at-rest
/// protection: the CNG DPAPI-NG descriptor mode, the legacy DPAPI scopes, the
/// explicit off switch, and the loud failure path for an invalid descriptor
/// (never a silent fallback to another scope). The concrete framework
/// encryptor types are asserted by name because not all of them are public.
/// </summary>
public sealed class OmpDataProtectionKeyProtectionTests
{
    private static string ValidDescriptor =>
        OperatingSystem.IsWindows()
            ? "SID=" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value
            : "SID=S-1-1-0"; // content is irrelevant off Windows: validation is skipped there

    [Fact]
    public void Apply_WhenDescriptorSet_UsesDpapiNG()
    {
        var encryptor = ResolveXmlEncryptor(new OmpAuthOptions
        {
            DpapiNgProtectionDescriptor = ValidDescriptor,
        });

        Assert.Equal("DpapiNGXmlEncryptor", encryptor?.GetType().Name);
    }

    [Fact]
    public void Apply_WhenDescriptorSetAndDpapiDisabled_DescriptorStillWins()
    {
        // Documented priority: a set descriptor takes precedence over
        // ProtectKeysWithDpapi=false.
        var encryptor = ResolveXmlEncryptor(new OmpAuthOptions
        {
            ProtectKeysWithDpapi = false,
            DpapiNgProtectionDescriptor = ValidDescriptor,
        });

        Assert.Equal("DpapiNGXmlEncryptor", encryptor?.GetType().Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Apply_WhenNoDescriptor_UsesLegacyDpapi(bool protectToLocalMachine)
    {
        var encryptor = ResolveXmlEncryptor(new OmpAuthOptions
        {
            DpapiProtectToLocalMachine = protectToLocalMachine,
        });

        Assert.Equal("DpapiXmlEncryptor", encryptor?.GetType().Name);
    }

    [Fact]
    public void Apply_WhenDpapiDisabled_LeavesKeyRingUnencrypted()
    {
        var encryptor = ResolveXmlEncryptor(new OmpAuthOptions
        {
            ProtectKeysWithDpapi = false,
        });

        Assert.Null(encryptor);
    }

    [Fact]
    public void Apply_WhenDescriptorInvalid_ThrowsInsteadOfFallingBack()
    {
        var options = new OmpAuthOptions
        {
            DpapiNgProtectionDescriptor = "SID=not-a-sid",
        };

        var services = new ServiceCollection();
        var builder = services.AddDataProtection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => OmpWebHostingExtensions.ApplyDataProtectionKeyProtection(builder, options));
        Assert.Contains(nameof(OmpAuthOptions.DpapiNgProtectionDescriptor), ex.Message);

        // No silent fallback: the failure left no key encryptor registered at all.
        Assert.Null(ResolveXmlEncryptor(services));
    }

    [Fact]
    public void Configure_WhenDescriptorSetButNoKeyPathResolved_ThrowsInsteadOfSilentlySkipping()
    {
        // The Auth app calls AddOmpCookieAuthentication with no content root,
        // so without an explicit OmpAuth:DataProtectionKeyPath the descriptor
        // used to be ignored silently: the login app ran an unprotected ring
        // while other apps on the host applied DPAPI-NG. A set protection
        // option that cannot take effect is a config error and must throw.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"OmpAuth:{nameof(OmpAuthOptions.DpapiNgProtectionDescriptor)}"] = ValidDescriptor,
            })
            .Build();
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddOmpCookieAuthentication(configuration));
        Assert.Contains(nameof(OmpAuthOptions.DpapiNgProtectionDescriptor), ex.Message);
        Assert.Contains(nameof(OmpAuthOptions.DataProtectionKeyPath), ex.Message);
    }

    [Fact]
    public void Guard_WhenDescriptorSetOnNonWindows_ThrowsInsteadOfSilentlySkipping()
    {
        var options = new OmpAuthOptions
        {
            DpapiNgProtectionDescriptor = ValidDescriptor,
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => OmpWebHostingExtensions.ThrowIfDescriptorCannotTakeEffect(
                options,
                dataProtectionKeyPath: "C:\\omp\\keys",
                isWindows: false));
        Assert.Contains(nameof(OmpAuthOptions.DpapiNgProtectionDescriptor), ex.Message);
        Assert.Contains("Windows", ex.Message);
    }

    [Fact]
    public void Guard_WhenDescriptorSetAndApplicable_DoesNotThrow()
    {
        var options = new OmpAuthOptions
        {
            DpapiNgProtectionDescriptor = ValidDescriptor,
        };

        OmpWebHostingExtensions.ThrowIfDescriptorCannotTakeEffect(
            options,
            dataProtectionKeyPath: "C:\\omp\\keys",
            isWindows: true);
    }

    [Fact]
    public void Guard_WhenNoDescriptor_NeverThrows()
    {
        // Without a descriptor the legacy behavior is unchanged: no key path
        // and/or a non-Windows host simply skips at-rest protection.
        OmpWebHostingExtensions.ThrowIfDescriptorCannotTakeEffect(
            new OmpAuthOptions(),
            dataProtectionKeyPath: "",
            isWindows: false);
    }

    private static IXmlEncryptor? ResolveXmlEncryptor(OmpAuthOptions options)
    {
        var services = new ServiceCollection();
        var builder = services.AddDataProtection();
        OmpWebHostingExtensions.ApplyDataProtectionKeyProtection(builder, options);
        return ResolveXmlEncryptor(services);
    }

    private static IXmlEncryptor? ResolveXmlEncryptor(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor;
    }
}
