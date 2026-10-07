using OpenModulePlatform.Installation;

namespace OpenModulePlatform.Installation.Tests;

public sealed class HostProfileMatcherTests
{
    private static BootstrapConfigProfile Profile(
        string configPath,
        params string[] machineNames)
        => new(
            DisplayName: Path.GetFileNameWithoutExtension(configPath),
            ConfigPath: configPath,
            MachineNames: machineNames);

    private static IReadOnlySet<string> Candidates(params string[] names)
        => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void SelectConfigPath_NoProfiles_ThrowsFileNotFound()
    {
        var ex = Assert.Throws<FileNotFoundException>(
            () => HostProfileMatcher.SelectConfigPath([], Candidates("SERVER01")));
        Assert.Contains("No bootstrap configuration file was found", ex.Message);
    }

    [Fact]
    public void SelectConfigPath_ExactlyOneMatch_ReturnsConfigPath()
    {
        var profiles = new[]
        {
            Profile(@"C:\pkg\hosts\web01\bootstrap.json", "WEB01"),
            Profile(@"C:\pkg\hosts\db01\bootstrap.json", "DB01"),
        };

        var selected = HostProfileMatcher.SelectConfigPath(profiles, Candidates("WEB01"));

        Assert.Equal(@"C:\pkg\hosts\web01\bootstrap.json", selected);
    }

    [Fact]
    public void SelectConfigPath_NoMatch_ThrowsWithLocalNames()
    {
        var profiles = new[]
        {
            Profile(@"C:\pkg\hosts\web01\bootstrap.json", "WEB01"),
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => HostProfileMatcher.SelectConfigPath(profiles, Candidates("OTHERHOST")));

        Assert.Contains("No bootstrap configuration matches this computer", ex.Message);
        Assert.Contains("OTHERHOST", ex.Message);
    }

    [Fact]
    public void SelectConfigPath_MoreThanOneMatch_ThrowsWithMatchingConfigs()
    {
        var profiles = new[]
        {
            Profile(@"C:\pkg\hosts\web01\bootstrap.json", "WEB01"),
            Profile(@"C:\pkg\hosts\web01-alt\bootstrap.json", "WEB01"),
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => HostProfileMatcher.SelectConfigPath(profiles, Candidates("WEB01")));

        Assert.Contains("More than one bootstrap configuration matches this computer", ex.Message);
        Assert.Contains("web01-alt", ex.Message);
    }

    [Fact]
    public void SelectConfigPath_SampleLosesToRealProfile()
    {
        var profiles = new[]
        {
            Profile(@"C:\pkg\configs\bootstrap.local.sample.json", "WEB01"),
            Profile(@"C:\pkg\hosts\web01\bootstrap.json", "WEB01"),
        };

        var selected = HostProfileMatcher.SelectConfigPath(profiles, Candidates("WEB01"));

        Assert.Equal(@"C:\pkg\hosts\web01\bootstrap.json", selected);
    }

    [Fact]
    public void SelectConfigPath_SampleIsSelectedWhenItIsTheOnlyMatch()
    {
        var profiles = new[]
        {
            Profile(@"C:\pkg\configs\bootstrap.local.sample.json", "WEB01"),
            Profile(@"C:\pkg\hosts\db01\bootstrap.json", "DB01"),
        };

        var selected = HostProfileMatcher.SelectConfigPath(profiles, Candidates("WEB01"));

        Assert.Equal(@"C:\pkg\configs\bootstrap.local.sample.json", selected);
    }

    [Fact]
    public void SelectConfigPath_ConfiguredFullyQualifiedNameMatchesShortCandidate()
    {
        var profiles = new[]
        {
            Profile(@"C:\pkg\hosts\web01\bootstrap.json", "WEB01.example.test"),
        };

        var selected = HostProfileMatcher.SelectConfigPath(profiles, Candidates("WEB01"));

        Assert.Equal(@"C:\pkg\hosts\web01\bootstrap.json", selected);
    }

    [Fact]
    public void SelectConfigPath_ConfiguredShortNameMatchesFullyQualifiedCandidate()
    {
        var profiles = new[]
        {
            Profile(@"C:\pkg\hosts\web01\bootstrap.json", "WEB01"),
        };

        // GetLocalMachineNames stores both the full DNS name and its first label,
        // so the candidate set carries both forms.
        var selected = HostProfileMatcher.SelectConfigPath(profiles, Candidates("WEB01.example.test", "WEB01"));

        Assert.Equal(@"C:\pkg\hosts\web01\bootstrap.json", selected);
    }

    [Fact]
    public void SelectConfigPath_MatchingIsCaseInsensitive()
    {
        var profiles = new[]
        {
            Profile(@"C:\pkg\hosts\web01\bootstrap.json", "web01"),
        };

        var selected = HostProfileMatcher.SelectConfigPath(profiles, Candidates("WEB01"));

        Assert.Equal(@"C:\pkg\hosts\web01\bootstrap.json", selected);
    }

    [Fact]
    public void SelectConfigPath_BlankConfiguredNameNeverMatches()
    {
        var profiles = new[]
        {
            Profile(@"C:\pkg\hosts\web01\bootstrap.json", " ", ""),
        };

        Assert.Throws<InvalidOperationException>(
            () => HostProfileMatcher.SelectConfigPath(profiles, Candidates("WEB01")));
    }
}
