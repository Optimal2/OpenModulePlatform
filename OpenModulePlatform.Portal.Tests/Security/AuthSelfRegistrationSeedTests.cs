namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// R7-F17. The "self-registration is opt-in" promise (R3-F2) was inert: the
/// omp_auth seed wrote auth/selfRegistrationEnabled = true, so every seeded
/// installation had registration switched on no matter what the documentation
/// said, and the repository/portal readers treated an absent value as enabled.
/// These guards pin the seeded default and every code default to disabled, so
/// disconnecting the flag (or flipping the default back) fails the build.
/// </summary>
public sealed class AuthSelfRegistrationSeedTests
{
    [Fact]
    public void AuthSeed_SeedsSelfRegistrationDisabled()
    {
        var sql = ReadRepositoryTextFile("OpenModulePlatform.Auth", "sql", "2-initialize-omp-auth.sql");

        Assert.Contains("(N'auth', N'selfRegistrationEnabled', N'false')", sql);
        Assert.DoesNotContain("(N'auth', N'selfRegistrationEnabled', N'true')", sql);
    }

    [Fact]
    public void SelfRegistrationReaders_TreatAbsentValueAsDisabled()
    {
        var repository = ReadRepositoryTextFile("OpenModulePlatform.Auth", "Services", "OmpAuthRepository.cs");
        var portalSettings = ReadRepositoryTextFile("OpenModulePlatform.Portal", "Pages", "Account", "Settings.cshtml.cs");
        var loginPage = ReadRepositoryTextFile("OpenModulePlatform.Auth", "Pages", "Login.cshtml.cs");

        // Every reader of auth/selfRegistrationEnabled must fail closed when the
        // value is absent: an installation without the seeded row is an
        // installation with self-registration off (opt-in, R3-F2/R7-F17).
        Assert.Contains(
            "OmpAuthDefaults.ParseEnabledConfigValue(read.Value, defaultValue: false)",
            repository);
        Assert.Contains(
            "OmpAuthDefaults.ParseEnabledConfigValue(read.Value, defaultValue: false)",
            portalSettings);
        Assert.Contains(
            "OmpAuthDefaults.ParseEnabledConfigValue(value, defaultValue: false)",
            loginPage);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "OpenModulePlatform.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate OpenModulePlatform repository root.");
    }

    private static string ReadRepositoryTextFile(params string[] relativePathSegments)
    {
        var rootedSegment = relativePathSegments.FirstOrDefault(Path.IsPathRooted);
        if (rootedSegment is not null)
        {
            throw new ArgumentException("Repository test paths must be relative.", nameof(relativePathSegments));
        }

        var segments = new string[relativePathSegments.Length + 1];
        segments[0] = FindRepositoryRoot();
        Array.Copy(relativePathSegments, 0, segments, 1, relativePathSegments.Length);
        return File.ReadAllText(Path.Join(segments));
    }
}
