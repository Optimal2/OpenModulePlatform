using OpenModulePlatform.Installation;
using OpenModulePlatform.Installer.Core;

namespace OpenModulePlatform.Installer.Tests;

public class ProfileResolutionTests
{
    private static BootstrapConfigProfile Profile(string displayName, string configPath, params string[] machineNames)
        => new(displayName, configPath, machineNames);

    private static IReadOnlySet<string> Candidates(params string[] names)
        => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void No_profiles_on_disk_reports_NoProfilesFound()
    {
        var resolution = InstallerProfileResolver.Resolve([], Candidates("SERVER01"));
        Assert.Equal(ProfileMatchOutcome.NoProfilesFound, resolution.Outcome);
    }

    [Fact]
    public void No_matching_profile_reports_NoMatch()
    {
        var profiles = new[] { Profile("Prod", @"C:\pkg\hosts\prod\bootstrap.json", "OTHERSRV") };
        var resolution = InstallerProfileResolver.Resolve(profiles, Candidates("SERVER01"));
        Assert.Equal(ProfileMatchOutcome.NoMatch, resolution.Outcome);
        Assert.Null(resolution.Profile);
    }

    [Fact]
    public void Exactly_one_match_is_selected()
    {
        var wanted = Profile("Prod", @"C:\pkg\hosts\prod\bootstrap.json", "SERVER01");
        var other = Profile("Test", @"C:\pkg\hosts\test\bootstrap.json", "TESTSRV");
        var resolution = InstallerProfileResolver.Resolve([other, wanted], Candidates("SERVER01"));
        Assert.Equal(ProfileMatchOutcome.SingleMatch, resolution.Outcome);
        Assert.Equal(wanted.ConfigPath, resolution.Profile!.ConfigPath);
    }

    [Fact]
    public void Several_matches_are_named()
    {
        var first = Profile("Prod A", @"C:\pkg\hosts\a\bootstrap.json", "SERVER01");
        var second = Profile("Prod B", @"C:\pkg\hosts\b\bootstrap.json", "server01.example.com");
        var resolution = InstallerProfileResolver.Resolve([first, second], Candidates("SERVER01"));
        Assert.Equal(ProfileMatchOutcome.MultipleMatches, resolution.Outcome);
        Assert.Equal(2, resolution.MatchingProfiles.Count);
        Assert.Null(resolution.Profile);
    }

    [Fact]
    public void Domain_suffix_matches_both_ways()
    {
        var byShortName = Profile("A", @"C:\pkg\hosts\a\bootstrap.json", "server01.example.com");
        Assert.Equal(
            ProfileMatchOutcome.SingleMatch,
            InstallerProfileResolver.Resolve([byShortName], Candidates("SERVER01")).Outcome);

        var byLongName = Profile("B", @"C:\pkg\hosts\b\bootstrap.json", "SERVER01");
        Assert.Equal(
            ProfileMatchOutcome.SingleMatch,
            InstallerProfileResolver.Resolve([byLongName], Candidates("SERVER01.example.com", "SERVER01")).Outcome);
    }

    [Fact]
    public void Sample_configs_never_count_even_when_they_are_the_only_match()
    {
        var sample = Profile("Sample", @"C:\pkg\hosts\sample\bootstrap.sample.json", "SERVER01");
        var resolution = InstallerProfileResolver.Resolve([sample], Candidates("SERVER01"));
        Assert.Equal(ProfileMatchOutcome.NoMatch, resolution.Outcome);
    }

    [Fact]
    public void Sample_configs_do_not_shadow_or_extend_a_real_match()
    {
        var real = Profile("Prod", @"C:\pkg\hosts\prod\bootstrap.json", "SERVER01");
        var sample = Profile("Sample", @"C:\pkg\hosts\sample\bootstrap.sample.json", "SERVER01");
        var resolution = InstallerProfileResolver.Resolve([sample, real], Candidates("SERVER01"));
        Assert.Equal(ProfileMatchOutcome.SingleMatch, resolution.Outcome);
        Assert.Equal(real.ConfigPath, resolution.Profile!.ConfigPath);
    }
}
