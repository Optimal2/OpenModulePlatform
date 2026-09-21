using System.Runtime.Versioning;
using System.Text.Json;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using Xunit;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// Direct tests for the credential store: the file it writes, the DPAPI round trip and
/// the guards around a disabled store. Until 2026-09-21 the service was exercised only
/// as a dependency of the service-app deployment tests, so a change to its file format
/// or its refusal rules had no test of its own to fail.
/// </summary>
/// <remarks>
/// DPAPI is Windows-only; the whole class is skipped elsewhere. CurrentUser scope is
/// used so the tests need no machine-wide key and leave nothing behind but the temp
/// file, which the class deletes.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HostAgentCredentialStoreServiceTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "omp-credential-store-tests-" + Guid.NewGuid().ToString("N"));

    public HostAgentCredentialStoreServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp-directory cleanup is advisory: a leftover file must not fail a passing test.
        }
    }

    [SkippableFact]
    public async Task Upsert_then_read_round_trips_the_password_without_storing_it_in_clear()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is Windows-only.");
        var (service, path) = CreateEnabledStore();

        await service.UpsertCredentialAsync(" svc-account ", "DOMAIN\\svc", "p@ss w0rd", " runs the backend ");

        var raw = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("p@ss w0rd", raw, StringComparison.Ordinal);
        Assert.Contains("\"svc-account\"", raw, StringComparison.Ordinal);

        var credential = await service.TryReadCredentialAsync("svc-account");
        Assert.NotNull(credential);
        Assert.Equal("svc-account", credential.Key);
        Assert.Equal("DOMAIN\\svc", credential.UserName);
        Assert.Equal("p@ss w0rd", credential.Password);

        var document = await service.LoadAsync();
        var entry = Assert.Single(document.Credentials).Value;
        Assert.Equal("WindowsDpapi", entry.ProtectionProvider);
        Assert.Equal(HostAgentCredentialProtectionScopes.CurrentUser, entry.ProtectionScope);
        Assert.Equal("runs the backend", entry.Description);
    }

    [SkippableFact]
    public async Task Credential_keys_are_case_insensitive()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is Windows-only.");
        var (service, _) = CreateEnabledStore();

        await service.UpsertCredentialAsync("Backend", "user", "secret");

        var credential = await service.TryReadCredentialAsync("BACKEND");
        Assert.NotNull(credential);
        Assert.Equal("user", credential.UserName);
        Assert.True(await service.RemoveCredentialAsync("backend"));
        Assert.Null(await service.TryReadCredentialAsync("Backend"));
    }

    [SkippableFact]
    public async Task Remove_reports_false_for_an_unknown_key_and_leaves_the_others()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is Windows-only.");
        var (service, _) = CreateEnabledStore();
        await service.UpsertCredentialAsync("keep", "user", "secret");

        Assert.False(await service.RemoveCredentialAsync("missing"));

        Assert.NotNull(await service.TryReadCredentialAsync("keep"));
    }

    [Fact]
    public async Task A_disabled_store_reads_as_empty_and_refuses_writes()
    {
        var settings = new HostAgentSettings
        {
            CredentialStore = new HostAgentCredentialStoreSettings
            {
                AutomationMode = HostAgentCredentialAutomationModes.Disabled,
                FilePath = Path.Join(_root, "disabled.json")
            }
        };
        var service = new HostAgentCredentialStoreService(
            new FakeOptionsMonitor<HostAgentSettings> { CurrentValue = settings });

        Assert.Empty((await service.LoadAsync()).Credentials);
        Assert.Null(await service.TryReadCredentialAsync("anything"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpsertCredentialAsync("key", "user", "secret"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RemoveCredentialAsync("key"));
        Assert.False(File.Exists(settings.CredentialStore.FilePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_key_is_rejected_before_the_store_is_touched(string key)
    {
        var (service, path) = CreateEnabledStore();

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpsertCredentialAsync(key, "user", "secret"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.TryReadCredentialAsync(key));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RemoveCredentialAsync(key));

        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// A store written by another tool (or a tampered file) naming a provider this build
    /// cannot decrypt must fail loudly rather than hand back garbage as a password.
    /// </summary>
    [Fact]
    public async Task An_unsupported_protection_provider_is_refused_on_read()
    {
        var (service, path) = CreateEnabledStore();
        var document = new HostAgentCredentialStoreDocument();
        document.Credentials["foreign"] = new HostAgentStoredCredentialEntry
        {
            UserName = "user",
            EncryptedPassword = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            ProtectionProvider = "SomethingElse"
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TryReadCredentialAsync("foreign"));

        Assert.Contains("SomethingElse", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The PowerShell helper that seeds the store wrote timestamps in the legacy
    /// <c>/Date(ms)/</c> form; a store it produced must still load.
    /// </summary>
    [Fact]
    public async Task A_legacy_microsoft_json_date_is_accepted()
    {
        var (service, path) = CreateEnabledStore();
        const string json = """
            {
              "formatVersion": 1,
              "updatedUtc": "/Date(1700000000000)/",
              "credentials": {
                "legacy": {
                  "userName": "user",
                  "encryptedPassword": "AQID",
                  "protectionProvider": "WindowsDpapi",
                  "protectionScope": "CurrentUser",
                  "description": "",
                  "updatedUtc": "/Date(1700000000000+0100)/"
                }
              }
            }
            """;
        await File.WriteAllTextAsync(path, json);

        var document = await service.LoadAsync();

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), document.UpdatedUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), document.Credentials["legacy"].UpdatedUtc);
    }

    [SkippableFact]
    public void Static_protect_and_unprotect_round_trip_with_the_configured_entropy()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is Windows-only.");
        var settings = new HostAgentCredentialStoreSettings
        {
            AutomationMode = HostAgentCredentialAutomationModes.Full,
            ProtectionScope = HostAgentCredentialProtectionScopes.CurrentUser,
            EntropyPurpose = "test-purpose"
        };
        var otherEntropy = new HostAgentCredentialStoreSettings
        {
            AutomationMode = HostAgentCredentialAutomationModes.Full,
            ProtectionScope = HostAgentCredentialProtectionScopes.CurrentUser,
            EntropyPurpose = "another-purpose"
        };

        var encrypted = HostAgentCredentialStoreService.ProtectPassword("secret", settings);

        Assert.NotEqual("secret", encrypted);
        Assert.Equal(
            "secret",
            HostAgentCredentialStoreService.UnprotectPassword(encrypted, settings, HostAgentCredentialProtectionScopes.CurrentUser));
        Assert.ThrowsAny<Exception>(
            () => HostAgentCredentialStoreService.UnprotectPassword(encrypted, otherEntropy, HostAgentCredentialProtectionScopes.CurrentUser));
    }

    private (HostAgentCredentialStoreService Service, string Path) CreateEnabledStore()
    {
        var path = Path.Join(_root, "store", "hostagent.credentials.json");
        Directory.CreateDirectory(Path.Join(_root, "store"));
        var settings = new HostAgentSettings
        {
            CredentialStore = new HostAgentCredentialStoreSettings
            {
                AutomationMode = HostAgentCredentialAutomationModes.Full,
                FilePath = path,
                ProtectionScope = HostAgentCredentialProtectionScopes.CurrentUser
            }
        };
        var service = new HostAgentCredentialStoreService(
            new FakeOptionsMonitor<HostAgentSettings> { CurrentValue = settings });
        return (service, path);
    }
}
