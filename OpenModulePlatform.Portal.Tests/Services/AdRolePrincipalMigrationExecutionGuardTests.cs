using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Campaign ad-principalformen-hela-vagen-adfs-till-rbac, follow-up phase 2,
/// finding 2. Two operators running the bulk move concurrently take shared
/// serializable range locks and then need exclusive locks for their INSERTs,
/// which is the classic deadlock shape; SQL Server kills one side with error
/// 1205. The run is idempotent, so the execute path must retry once instead of
/// crashing the page. The rollback must also not reuse the cancellation token
/// that caused the failure, or the rollback itself can be aborted before it
/// runs. These guards pin that shape.
/// </summary>
public sealed class AdRolePrincipalMigrationExecutionGuardTests
{
    [Fact]
    public void Execute_RetriesOnceOnDeadlockVictim()
    {
        var repository = OmpRepositoryFiles.ReadRepositoryTextFile(
            "OpenModulePlatform.Portal", "Services", "AdRolePrincipalMigrationRepository.cs");

        Assert.Contains("catch (SqlException ex) when (ex.Number == DeadlockVictimErrorNumber)", repository);
        Assert.Contains("DeadlockVictimErrorNumber = 1205", repository);
    }

    [Fact]
    public void Execute_RollbackDoesNotReuseTheFailingCancellationToken()
    {
        var repository = OmpRepositoryFiles.ReadRepositoryTextFile(
            "OpenModulePlatform.Portal", "Services", "AdRolePrincipalMigrationRepository.cs");

        Assert.Contains("RollbackAsync(CancellationToken.None)", repository);
        Assert.DoesNotContain("RollbackAsync(ct)", repository);
    }
}
