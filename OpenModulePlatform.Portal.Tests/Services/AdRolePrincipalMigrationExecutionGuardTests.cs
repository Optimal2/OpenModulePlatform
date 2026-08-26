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
        var repository = ReadRepositoryTextFile(
            "OpenModulePlatform.Portal", "Services", "AdRolePrincipalMigrationRepository.cs");

        Assert.Contains("catch (SqlException ex) when (ex.Number == DeadlockVictimErrorNumber)", repository);
        Assert.Contains("DeadlockVictimErrorNumber = 1205", repository);
    }

    [Fact]
    public void Execute_RollbackDoesNotReuseTheFailingCancellationToken()
    {
        var repository = ReadRepositoryTextFile(
            "OpenModulePlatform.Portal", "Services", "AdRolePrincipalMigrationRepository.cs");

        Assert.Contains("RollbackAsync(CancellationToken.None)", repository);
        Assert.DoesNotContain("RollbackAsync(ct)", repository);
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
