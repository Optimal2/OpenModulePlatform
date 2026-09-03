using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.Options;
using Xunit.Abstractions;

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
    private readonly ITestOutputHelper _output;

    public OmpDataProtectionKeyProtectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

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
            // Explicit: legacy DPAPI is no longer the default, so relying on it
            // here would silently turn this into a test of the default instead.
            ProtectKeysWithDpapi = true,
            DpapiProtectToLocalMachine = protectToLocalMachine,
        });

        Assert.Equal("DpapiXmlEncryptor", encryptor?.GetType().Name);
    }

    /// <summary>
    /// Locks the 2026-08-23 operator decision: an out-of-the-box key ring is NOT
    /// encrypted at rest.
    /// </summary>
    /// <remarks>
    /// Machine-scoped DPAPI ties the ring to the host that wrote it and repeatedly
    /// cost working installations their sign-in. Until the AD security group exists,
    /// NTFS permissions on the key directory are the control; after that the answer is
    /// <see cref="OmpAuthOptions.DpapiNgProtectionDescriptor"/>, not this flag.
    /// If this test goes red because someone restored the old default, that is a
    /// security-relevant change of behaviour for every existing installation and needs
    /// the operator's decision — not a test edit.
    /// </remarks>
    [Fact]
    public void Apply_WithDefaultOptions_LeavesKeyRingUnencrypted()
    {
        var encryptor = ResolveXmlEncryptor(new OmpAuthOptions());

        Assert.False(new OmpAuthOptions().ProtectKeysWithDpapi);
        Assert.Null(encryptor);
    }

    /// <summary>
    /// The AD-backed path must still work untouched — it is how encryption comes back
    /// once the security group is populated.
    /// </summary>
    [Fact]
    public void Apply_WithDefaultOptionsPlusDescriptor_StillUsesDpapiNG()
    {
        var encryptor = ResolveXmlEncryptor(new OmpAuthOptions
        {
            DpapiNgProtectionDescriptor = "SID=S-1-5-21-1111111111-2222222222-3333333333-4444",
        });

        Assert.Equal("DpapiNGXmlEncryptor", encryptor?.GetType().Name);
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

    // ----- X.509 certificate mode (the no-AD web-farm path) -----

    [Fact]
    public void Apply_WhenCertificateThumbprintSet_UsesCertificateEncryptor()
    {
        using var certificate = CreateSelfSignedCertificate();

        var encryptor = ResolveXmlEncryptor(
            new OmpAuthOptions
            {
                DataProtectionCertificateThumbprint = certificate.Thumbprint!,
            },
            ResolverFor(certificate));

        Assert.Equal("CertificateXmlEncryptor", encryptor?.GetType().Name);
    }

    [Fact]
    public void Apply_WhenCertificateSetAndDpapiDisabled_CertificateStillWins()
    {
        // Documented priority: a set certificate thumbprint takes precedence
        // over ProtectKeysWithDpapi=false, same as the descriptor does.
        using var certificate = CreateSelfSignedCertificate();

        var encryptor = ResolveXmlEncryptor(
            new OmpAuthOptions
            {
                ProtectKeysWithDpapi = false,
                DataProtectionCertificateThumbprint = certificate.Thumbprint!,
            },
            ResolverFor(certificate));

        Assert.Equal("CertificateXmlEncryptor", encryptor?.GetType().Name);
    }

    [Fact]
    public void Apply_WhenDescriptorAndCertificateBothSet_ThrowsInsteadOfGuessing()
    {
        // Two different at-rest modes configured at once: refuse rather than
        // silently pick one.
        using var certificate = CreateSelfSignedCertificate();
        var options = new OmpAuthOptions
        {
            DpapiNgProtectionDescriptor = ValidDescriptor,
            DataProtectionCertificateThumbprint = certificate.Thumbprint!,
        };

        var services = new ServiceCollection();
        var builder = services.AddDataProtection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => OmpWebHostingExtensions.ApplyDataProtectionKeyProtection(builder, options));
        Assert.Contains(nameof(OmpAuthOptions.DpapiNgProtectionDescriptor), ex.Message);
        Assert.Contains(nameof(OmpAuthOptions.DataProtectionCertificateThumbprint), ex.Message);

        // No silent fallback: the conflict left no key encryptor registered at all.
        Assert.Null(ResolveXmlEncryptor(services));
    }

    /// <summary>
    /// Functional rotation proof: a key ring written while certificate A was
    /// active must remain fully readable after the active certificate moves to
    /// B with A listed as retired (UnprotectKeysWithAnyCertificate). This is
    /// stronger than asserting a decryptor type name: it exercises the real
    /// decrypt path for an old key file.
    /// </summary>
    [Fact]
    public void EndToEnd_AfterCertificateRotation_OldKeysStayReadableThroughRetiredCertificate()
    {
        using var oldCertificate = CreateSelfSignedCertificate("CN=OMP-Test-DP-Old");
        using var newCertificate = CreateSelfSignedCertificate("CN=OMP-Test-DP-New");
        var keyDirectory = Path.Join(
            Path.GetTempPath(),
            "omp-dp-cert-rotation-" + Guid.NewGuid().ToString("N"));

        try
        {
            // Phase 1: ring protected by the OLD certificate.
            string protectedPayload;
            var phaseOneServices = new ServiceCollection();
            var phaseOneBuilder = phaseOneServices.AddDataProtection();
            phaseOneBuilder.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
            phaseOneBuilder.SetApplicationName("omp-dp-cert-rotation");
            OmpWebHostingExtensions.ApplyDataProtectionKeyProtection(
                phaseOneBuilder,
                new OmpAuthOptions
                {
                    DataProtectionCertificateThumbprint = oldCertificate.Thumbprint!,
                },
                ResolverFor(oldCertificate));

            using (var provider = phaseOneServices.BuildServiceProvider())
            {
                protectedPayload = provider
                    .GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("rotation-check")
                    .Protect("payload");
            }

            // Phase 2: NEW certificate active, OLD one retired.
            var phaseTwoServices = new ServiceCollection();
            var phaseTwoBuilder = phaseTwoServices.AddDataProtection();
            phaseTwoBuilder.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
            phaseTwoBuilder.SetApplicationName("omp-dp-cert-rotation");
            OmpWebHostingExtensions.ApplyDataProtectionKeyProtection(
                phaseTwoBuilder,
                new OmpAuthOptions
                {
                    DataProtectionCertificateThumbprint = newCertificate.Thumbprint!,
                    DataProtectionRetiredCertificateThumbprints = { oldCertificate.Thumbprint! },
                },
                ResolverFor(newCertificate, oldCertificate));

            using (var provider = phaseTwoServices.BuildServiceProvider())
            {
                var protector = provider
                    .GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("rotation-check");
                Assert.Equal("payload", protector.Unprotect(protectedPayload));
            }
        }
        finally
        {
            if (Directory.Exists(keyDirectory))
            {
                Directory.Delete(keyDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Apply_WhenCertificateMissing_ThrowsInsteadOfFallingBack()
    {
        var options = new OmpAuthOptions
        {
            DataProtectionCertificateThumbprint = UnknownThumbprint,
        };

        var services = new ServiceCollection();
        var builder = services.AddDataProtection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => OmpWebHostingExtensions.ApplyDataProtectionKeyProtection(builder, options));
        Assert.Contains(nameof(OmpAuthOptions.DataProtectionCertificateThumbprint), ex.Message);

        Assert.Null(ResolveXmlEncryptor(services));
    }

    [Fact]
    public void Apply_WhenRetiredCertificateMissing_ThrowsInsteadOfSilentlyStrandingKeys()
    {
        using var activeCertificate = CreateSelfSignedCertificate("CN=OMP-Test-DP-Active");
        var options = new OmpAuthOptions
        {
            DataProtectionCertificateThumbprint = activeCertificate.Thumbprint!,
            DataProtectionRetiredCertificateThumbprints = { UnknownThumbprint },
        };

        var services = new ServiceCollection();
        var builder = services.AddDataProtection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => OmpWebHostingExtensions.ApplyDataProtectionKeyProtection(
                builder, options, ResolverFor(activeCertificate)));
        Assert.Contains("retired", ex.Message);
        Assert.Contains(UnknownThumbprint, ex.Message);
    }

    [Fact]
    public void Guard_WhenCertificateHasNoPrivateKey_ThrowsInsteadOfFallingBack()
    {
        using var withPrivateKey = CreateSelfSignedCertificate();
        using var publicOnly = X509CertificateLoader.LoadCertificate(
            withPrivateKey.Export(X509ContentType.Cert));

        var ex = Assert.Throws<InvalidOperationException>(
            () => OmpWebHostingExtensions.ThrowIfCertificateCannotProtectKeys(
                publicOnly,
                publicOnly.Thumbprint!,
                isRetiredCertificate: false));
        Assert.Contains("private key", ex.Message);
    }

    [Fact]
    public void Guard_WhenThumbprintMalformed_ThrowsInsteadOfFallingBack()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => OmpWebHostingExtensions.LoadKeyProtectionCertificate(
                "not-a-thumbprint",
                ResolverFor(),
                isRetiredCertificate: false));
        Assert.Contains("40 hexadecimal", ex.Message);
    }

    [Fact]
    public void Guard_WhenCertificateIsNotRsa_ThrowsInsteadOfFailingAtFirstKeyWrite()
    {
        // The framework encrypts the ring with RSA key transport; an EC
        // certificate would only fail when the first key is written.
        using var ecdsa = ECDsa.Create();
        var request = new CertificateRequest(
            "CN=OMP-Test-DP-EC",
            ecdsa,
            HashAlgorithmName.SHA256);
        using var ecCertificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        var ex = Assert.Throws<InvalidOperationException>(
            () => OmpWebHostingExtensions.ThrowIfCertificateCannotProtectKeys(
                ecCertificate,
                ecCertificate.Thumbprint!,
                isRetiredCertificate: false));
        Assert.Contains("RSA", ex.Message);
    }

    [Fact]
    public void Guard_WhenCertificateExpired_ThrowsInsteadOfFallingBack()
    {
        using var expired = CreateSelfSignedCertificate(
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        var ex = Assert.Throws<InvalidOperationException>(
            () => OmpWebHostingExtensions.ThrowIfCertificateCannotProtectKeys(
                expired,
                expired.Thumbprint!,
                isRetiredCertificate: false));
        Assert.Contains("EXPIRED", ex.Message);
        Assert.Contains(nameof(OmpAuthOptions.DataProtectionRetiredCertificateThumbprints), ex.Message);
    }

    [Fact]
    public void Guard_WhenRetiredCertificateExpired_AcceptedByDesign()
    {
        // Rotation often happens BECAUSE the old certificate expired; its
        // private key still decrypts the old key files.
        using var expired = CreateSelfSignedCertificate(
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        OmpWebHostingExtensions.ThrowIfCertificateCannotProtectKeys(
            expired,
            expired.Thumbprint!,
            isRetiredCertificate: true);
    }

    [Fact]
    public void Guard_WhenRetiredThumbprintsWithoutActiveThumbprint_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => OmpWebHostingExtensions.ThrowIfKeyProtectionModesConflict(new OmpAuthOptions
            {
                DataProtectionRetiredCertificateThumbprints = { UnknownThumbprint },
            }));
        Assert.Contains(nameof(OmpAuthOptions.DataProtectionRetiredCertificateThumbprints), ex.Message);
        Assert.Contains(nameof(OmpAuthOptions.DataProtectionCertificateThumbprint), ex.Message);
    }

    [Fact]
    public void Guard_WhenCertificateSetButNoKeyPathResolved_ThrowsInsteadOfSilentlySkipping()
    {
        var options = new OmpAuthOptions
        {
            DataProtectionCertificateThumbprint = UnknownThumbprint,
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => OmpWebHostingExtensions.ThrowIfCertificateProtectionCannotTakeEffect(
                options,
                dataProtectionKeyPath: "",
                isWindows: true));
        Assert.Contains(nameof(OmpAuthOptions.DataProtectionCertificateThumbprint), ex.Message);
        Assert.Contains(nameof(OmpAuthOptions.DataProtectionKeyPath), ex.Message);
    }

    [Fact]
    public void Guard_WhenCertificateSetOnNonWindows_ThrowsInsteadOfSilentlySkipping()
    {
        var options = new OmpAuthOptions
        {
            DataProtectionCertificateThumbprint = UnknownThumbprint,
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => OmpWebHostingExtensions.ThrowIfCertificateProtectionCannotTakeEffect(
                options,
                dataProtectionKeyPath: "C:\\omp\\keys",
                isWindows: false));
        Assert.Contains(nameof(OmpAuthOptions.DataProtectionCertificateThumbprint), ex.Message);
        Assert.Contains("Windows", ex.Message);
    }

    [Fact]
    public void Guard_WhenCertificateSetAndApplicable_DoesNotThrow()
    {
        OmpWebHostingExtensions.ThrowIfCertificateProtectionCannotTakeEffect(
            new OmpAuthOptions { DataProtectionCertificateThumbprint = UnknownThumbprint },
            dataProtectionKeyPath: "C:\\omp\\keys",
            isWindows: true);
    }

    [Fact]
    public void StoreLookup_WhenThumbprintUnknown_ThrowsInsteadOfFallingBack()
    {
        // Real LocalMachine\My read (read-only): an unknown thumbprint must
        // throw with a message naming the store, never return null.
        if (!OperatingSystem.IsWindows())
        {
            return; // the store layout is a Windows concept in this test
        }

        var ex = Assert.Throws<InvalidOperationException>(
            () => OmpWebHostingExtensions.LoadKeyProtectionCertificate(
                UnknownThumbprint,
                certificateResolver: null,
                isRetiredCertificate: false));
        Assert.Contains("LocalMachine\\My", ex.Message);
        Assert.Contains(UnknownThumbprint, ex.Message);
    }

    /// <summary>
    /// Empirical marker check: run a REAL key creation through certificate
    /// protection and read the persisted key file, so docs/HOST_AGENT.md
    /// post-deploy verification states the observed element form — not an
    /// assumed one (the 27d5eb98 lesson). Observed on .NET 10 (2026-08-26):
    /// certificate-encrypted keys carry
    /// decryptorType="...XmlEncryption.EncryptedXmlDecryptor" (NOT
    /// CertificateXmlDecryptor), an inner XML-Encryption EncryptedData block
    /// whose EncryptedKey uses rsa-1_5 key transport and embeds the encrypting
    /// certificate as &lt;X509Certificate&gt;.
    /// </summary>
    [Fact]
    public void EndToEnd_CertificateMode_PersistsKeyFileWithEncryptedXmlDecryptor()
    {
        using var certificate = CreateSelfSignedCertificate();
        var keyDirectory = Path.Join(
            Path.GetTempPath(),
            "omp-dp-cert-test-" + Guid.NewGuid().ToString("N"));

        try
        {
            var services = new ServiceCollection();
            var builder = services.AddDataProtection();
            builder.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
            builder.SetApplicationName("omp-dp-cert-test");
            OmpWebHostingExtensions.ApplyDataProtectionKeyProtection(
                builder,
                new OmpAuthOptions
                {
                    DataProtectionCertificateThumbprint = certificate.Thumbprint!,
                },
                ResolverFor(certificate));

            using (var provider = services.BuildServiceProvider())
            {
                var protector = provider
                    .GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("marker-check");
                _ = protector.Protect("payload");
            } // disposing flushes the key ring to disk

            var keyFile = Assert.Single(Directory.GetFiles(keyDirectory, "key-*.xml"));
            var xml = File.ReadAllText(keyFile);
            _output.WriteLine(xml);

            Assert.Contains("EncryptedXmlDecryptor", xml);
            Assert.Contains("rsa-1_5", xml);
            Assert.Contains("<X509Certificate>", xml);
            Assert.DoesNotContain("DpapiXmlDecryptor", xml);
            Assert.DoesNotContain("DpapiNGXmlDecryptor", xml);
        }
        finally
        {
            if (Directory.Exists(keyDirectory))
            {
                Directory.Delete(keyDirectory, recursive: true);
            }
        }
    }

    private static IXmlEncryptor? ResolveXmlEncryptor(
        OmpAuthOptions options,
        Func<string, X509Certificate2?>? certificateResolver = null)
    {
        var services = new ServiceCollection();
        var builder = services.AddDataProtection();
        OmpWebHostingExtensions.ApplyDataProtectionKeyProtection(builder, options, certificateResolver);
        return ResolveXmlEncryptor(services);
    }

    private static IXmlEncryptor? ResolveXmlEncryptor(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor;
    }

    /// <summary>
    /// A syntactically valid SHA-1 thumbprint that is not installed in any
    /// store on the test machine, used to prove the not-found failure path.
    /// </summary>
    private const string UnknownThumbprint = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

    private static X509Certificate2 CreateSelfSignedCertificate(
        string subjectName = "CN=OMP-Test-DP",
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        var effectiveNotAfter = notAfter ?? DateTimeOffset.UtcNow.AddYears(1);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            subjectName,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        // CreateSelfSigned returns a certificate holding its own copy of the
        // private key, so disposing the RSA above is safe.
        return request.CreateSelfSigned(
            notBefore ?? effectiveNotAfter.AddYears(-2),
            effectiveNotAfter);
    }

    /// <summary>
    /// Test resolver standing in for the LocalMachine\My store lookup: maps
    /// normalized thumbprints to in-memory certificates and returns null for
    /// an unknown thumbprint, exactly like the store lookup.
    /// </summary>
    private static Func<string, X509Certificate2?> ResolverFor(params X509Certificate2[] certificates)
    {
        return thumbprint =>
            certificates.FirstOrDefault(certificate =>
                string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
    }
}
