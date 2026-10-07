using OpenModulePlatform.Installer.Install;

namespace OpenModulePlatform.Installer.Tests;

public class ServiceLogonRightTests
{
    [Fact]
    public void Dot_slash_account_is_machine_qualified_before_the_lookup()
    {
        // LookupAccountName returns error 1332 for ".\name"; MACHINE\name names
        // the same account and resolves.
        Assert.Equal(
            @"SERVER01\svc-omp",
            ServiceLogonRight.NormalizeAccountNameForLookup(@".\svc-omp", "SERVER01"));
    }

    [Fact]
    public void Machine_qualified_and_domain_accounts_pass_through()
    {
        Assert.Equal(
            @"SERVER01\svc-omp",
            ServiceLogonRight.NormalizeAccountNameForLookup(@"SERVER01\svc-omp", "SERVER01"));
        Assert.Equal(
            @"CONTOSO\svc-omp",
            ServiceLogonRight.NormalizeAccountNameForLookup(@"CONTOSO\svc-omp", "SERVER01"));
        Assert.Equal(
            "svc-omp@contoso.example.com",
            ServiceLogonRight.NormalizeAccountNameForLookup("svc-omp@contoso.example.com", "SERVER01"));
    }
}
