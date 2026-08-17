namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// R7-F11. The linked-user lookup used to keep the disabled-account guard in
/// an active-first ORDER BY under TOP (1) instead of in the selection. These
/// guards pin the fixed shape: active accounts are filtered in the WHERE
/// clause and the remaining TOP (1) choice is ordered by the unique
/// user_auth_id, so the result is deterministic.
/// </summary>
public sealed class AuthLinkedUserResolutionGuardTests
{
    [Fact]
    public void LinkedUserResolution_FiltersActiveAccountsInTheSelection()
    {
        var repository = ReadRepositoryTextFile("OpenModulePlatform.Auth", "Services", "OmpAuthRepository.cs");

        Assert.Contains("AND u.account_status = @active_account_status", repository);
        Assert.DoesNotContain("ORDER BY CASE WHEN u.account_status", repository);
    }

    [Fact]
    public void LinkedUserResolution_HasADeterministicOrder()
    {
        var repository = ReadRepositoryTextFile("OpenModulePlatform.Auth", "Services", "OmpAuthRepository.cs");

        Assert.Contains("ORDER BY ua.user_auth_id;", repository);
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
