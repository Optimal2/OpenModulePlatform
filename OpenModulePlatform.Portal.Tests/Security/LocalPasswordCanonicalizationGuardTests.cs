namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// R7-F12. The local password user name had its normalization rule split
/// between application code (trim + invariant lowercase) and the database's
/// default collation (whatever the installation happened to use), so a stored
/// row could become unreachable -- or a case-insensitive lookup could match a
/// differently-cased row, including the wrong one. These guards pin the fixed
/// shape: every omp.auth_provider_lpwd comparison is pinned to the one shared
/// binary collation constant, the old unpinned form is gone, and the core
/// setup script ships the idempotent canonicalization migration for legacy
/// rows.
/// </summary>
public sealed class LocalPasswordCanonicalizationGuardTests
{
    [Fact]
    public void LpwdComparisons_ArePinnedToTheSharedBinaryCollation()
    {
        var authRepository = ReadRepositoryTextFile("OpenModulePlatform.Auth", "Services", "OmpAuthRepository.cs");
        var adminRepository = ReadRepositoryTextFile("OpenModulePlatform.Portal", "Services", "OmpUserAdminRepository.cs");

        // The old, unpinned comparison must be gone -- not just accompanied by
        // a pinned one.
        Assert.DoesNotContain("WHERE user_name = @user_name", authRepository);
        Assert.DoesNotContain("WHERE user_name = @user_name", adminRepository);

        // Both repositories use the one shared collation constant rather than
        // a hard-coded copy each.
        Assert.Contains(
            "user_name COLLATE \" + LocalPasswordIdentity.UserNameBinaryCollation",
            authRepository);
        Assert.Contains(
            "user_name COLLATE \" + LocalPasswordIdentity.UserNameBinaryCollation",
            adminRepository);
    }

    [Fact]
    public void AdminWritePaths_ApplyTheSharedNormalizationRule()
    {
        var adminRepository = ReadRepositoryTextFile("OpenModulePlatform.Portal", "Services", "OmpUserAdminRepository.cs");

        // Create, add-login, reset, and removal all key the hash row by the
        // canonical form produced by the one shared rule.
        Assert.Contains("LocalPasswordIdentity.NormalizeUserName", adminRepository);
        Assert.Contains(
            "LocalPasswordIdentity.NormalizeUserName(providerUserKey)",
            adminRepository);
        Assert.Contains(
            "LocalPasswordIdentity.NormalizeUserName(link.Value.ProviderUserKey)",
            adminRepository);
    }

    [Fact]
    public void CoreSetup_ShipsTheCanonicalizationMigration()
    {
        var setupScript = ReadRepositoryTextFile("sql", "1-setup-openmoduleplatform.sql");

        Assert.Contains("-- R7-F12: local password user-name canonicalization (begin)", setupScript);
        Assert.Contains("-- R7-F12: local password user-name canonicalization (end)", setupScript);
        Assert.Contains("FROM omp.auth_provider_lpwd target", setupScript);
        Assert.Contains("LOWER(LTRIM(RTRIM(target.user_name)))", setupScript);
        Assert.Contains("FROM omp.user_auth target", setupScript);
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
            throw new ArgumentException("Repository test paths must be relative.", nameof(rootedSegment));
        }

        var segments = new string[relativePathSegments.Length + 1];
        segments[0] = FindRepositoryRoot();
        Array.Copy(relativePathSegments, 0, segments, 1, relativePathSegments.Length);
        return File.ReadAllText(Path.Join(segments));
    }
}
