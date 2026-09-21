using OpenModulePlatform.TestSupport;

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
        var repository = OmpRepositoryFiles.ReadRepositoryTextFile("OpenModulePlatform.Auth", "Services", "OmpAuthRepository.cs");

        Assert.Contains("AND u.account_status = @active_account_status", repository);
        Assert.DoesNotContain("ORDER BY CASE WHEN u.account_status", repository);
    }

    [Fact]
    public void LinkedUserResolution_HasADeterministicOrder()
    {
        var repository = OmpRepositoryFiles.ReadRepositoryTextFile("OpenModulePlatform.Auth", "Services", "OmpAuthRepository.cs");

        Assert.Contains("ORDER BY ua.user_auth_id;", repository);
    }
}
