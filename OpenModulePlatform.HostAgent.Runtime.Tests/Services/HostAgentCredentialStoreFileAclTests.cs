using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using Xunit;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// The credential store is only as private as its file ACL (DPAPI LocalMachine with a
/// well-known entropy string decrypts for any reader on the host), so the ACL the C#
/// writers leave behind is asserted here, not just the file content (B65).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HostAgentCredentialStoreFileAclTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "omp-credential-store-acl-tests-" + Guid.NewGuid().ToString("N"));

    public HostAgentCredentialStoreFileAclTests() => Directory.CreateDirectory(_root);

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
    public void Apply_cuts_inheritance_and_grants_only_administrators_system_and_the_writer()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "File ACLs are Windows-only.");
        var path = Path.Join(_root, "hostagent.credentials.json");
        File.WriteAllText(path, "{}");

        HostAgentCredentialStoreFileAcl.Apply(path);

        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        Assert.Empty(security.GetAccessRules(includeExplicit: false, includeInherited: true, typeof(SecurityIdentifier)));
        Assert.Equal(
            ExpectedBaseGrantees(),
            ExplicitAllowGrantees(security));
    }

    [SkippableFact]
    public void Apply_adds_the_service_account_and_understands_sc_exe_spellings()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "File ACLs are Windows-only.");
        var path = Path.Join(_root, "hostagent.credentials.json");
        File.WriteAllText(path, "{}");

        // "LocalSystem" is sc.exe's name for SYSTEM and must not produce a duplicate rule;
        // a blank name is ignored; NetworkService is a built-in the resolver maps itself.
        HostAgentCredentialStoreFileAcl.Apply(path, "LocalSystem", " ", "NetworkService");

        var expected = ExpectedBaseGrantees();
        expected.Add(new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null));
        Assert.Equal(expected, ExplicitAllowGrantees(new FileInfo(path).GetAccessControl(AccessControlSections.Access)));
    }

    [SkippableFact]
    public void Apply_replaces_rules_an_earlier_writer_left_on_the_file()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "File ACLs are Windows-only.");
        var path = Path.Join(_root, "hostagent.credentials.json");
        File.WriteAllText(path, "{}");
        HostAgentCredentialStoreFileAcl.Apply(path, "LocalService");

        HostAgentCredentialStoreFileAcl.Apply(path);

        Assert.Equal(
            ExpectedBaseGrantees(),
            ExplicitAllowGrantees(new FileInfo(path).GetAccessControl(AccessControlSections.Access)));
    }

    [SkippableFact]
    public void Apply_refuses_an_account_that_does_not_exist()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "File ACLs are Windows-only.");
        var path = Path.Join(_root, "hostagent.credentials.json");
        File.WriteAllText(path, "{}");

        var ex = Assert.Throws<InvalidOperationException>(
            () => HostAgentCredentialStoreFileAcl.Apply(path, ".\\omp-no-such-account-" + Guid.NewGuid().ToString("N")));

        Assert.Contains("could not be resolved", ex.Message, StringComparison.Ordinal);
        Assert.IsType<IdentityNotMappedException>(ex.InnerException);
    }

    [SkippableFact]
    public async Task The_store_service_leaves_a_protected_acl_after_upsert()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI and file ACLs are Windows-only.");
        var path = Path.Join(_root, "hostagent.credentials.json");
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

        await service.UpsertCredentialAsync("svc", "DOMAIN\\svc", "secret");
        // A second save replaces the file through a temp-and-move; the ACL must survive that.
        await service.UpsertCredentialAsync("svc2", "DOMAIN\\svc2", "secret");

        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(ExpectedBaseGrantees(), ExplicitAllowGrantees(security));
        Assert.Equal("DOMAIN\\svc", (await service.TryReadCredentialAsync("svc"))?.UserName);
    }

    private static HashSet<SecurityIdentifier> ExpectedBaseGrantees()
    {
        var expected = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            new(WellKnownSidType.LocalSystemSid, null)
        };
        using var current = WindowsIdentity.GetCurrent();
        expected.Add(current.User!);
        return expected;
    }

    private static HashSet<SecurityIdentifier> ExplicitAllowGrantees(FileSecurity security)
    {
        var grantees = new HashSet<SecurityIdentifier>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier)))
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            grantees.Add((SecurityIdentifier)rule.IdentityReference);
        }

        return grantees;
    }
}
