// File: OpenModulePlatform.WorkerManager.WindowsService.Tests/ObservationSqlGuardTests.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Tests;

/// <summary>
/// Source guards for the observation SQL, which is embedded in the repository and
/// has no local database to run against in unit tests. These pin the fixed shape of
/// R7-F4 (both foreign keys guarded, on both write paths) and the Draining half of
/// R7-F7 that lives in SQL (summary severity order and the staleness downgrade).
/// Reverting the fix makes the matching guard fail; that is the sabotage check.
/// </summary>
public sealed class ObservationSqlGuardTests
{
    [Fact]
    public void The_observation_upsert_guards_both_foreign_keys()
    {
        // R7-F4. omp.WorkerInstanceRuntimeStates has TWO foreign keys --
        // WorkerInstanceId and AppInstanceId -- and the upsert guarded only the one
        // the MERGE matches on. An observation arriving after its app instance was
        // deleted died on the unguarded FK and failed the whole publish.
        var repository = ReadRepositoryTextFile(
            "OpenModulePlatform.WorkerManager.WindowsService", "Services", "OmpWorkerRuntimeRepository.cs");

        Assert.Contains(
            "IF EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = @appInstanceId)\n"
            + "   AND EXISTS (SELECT 1 FROM omp.WorkerInstances WHERE WorkerInstanceId = @workerInstanceId)",
            Normalize(repository));
    }

    [Fact]
    public void The_app_instance_fallback_write_is_guarded_too()
    {
        // The direct AppInstanceRuntimeStates fallback write has the same FK and
        // must carry the same guard -- guarding only the MERGE path would re-open
        // the finding on the fallback path.
        var repository = Normalize(ReadRepositoryTextFile(
            "OpenModulePlatform.WorkerManager.WindowsService", "Services", "OmpWorkerRuntimeRepository.cs"));

        const string guard = "IF EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = @appInstanceId)";
        var occurrences = repository.Split(guard, StringSplitOptions.None).Length - 1;
        Assert.Equal(2, occurrences);
    }

    [Fact]
    public void The_summary_severity_order_ranks_Draining_deliberately()
    {
        // R7-F7. An unlisted state falls into ELSE and sorts below Running, so a
        // draining sibling would be averaged away behind a healthy one. Draining
        // ranks between Stopping and Starting by name, not by accident.
        var repository = Normalize(ReadRepositoryTextFile(
            "OpenModulePlatform.WorkerManager.WindowsService", "Services", "OmpWorkerRuntimeRepository.cs"));

        Assert.Contains("WHEN 6 THEN 4", repository);
        Assert.Contains("Draining(6)", repository);
    }

    [Fact]
    public void The_staleness_downgrade_covers_Draining()
    {
        // A manager that dies mid-drain leaves a Draining row behind; that claim is
        // exactly as alive -- and exactly as stale-prone -- as Running. Leaving it
        // out of the downgrade would pin "Draining" in the Portal forever.
        var repository = Normalize(ReadRepositoryTextFile(
            "OpenModulePlatform.WorkerManager.WindowsService", "Services", "OmpWorkerRuntimeRepository.cs"));

        Assert.Contains("s.ObservedState IN (1, 2, 6)", repository);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

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
