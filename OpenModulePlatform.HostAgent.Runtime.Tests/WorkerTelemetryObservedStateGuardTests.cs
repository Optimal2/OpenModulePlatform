// File: OpenModulePlatform.HostAgent.Runtime.Tests/WorkerTelemetryObservedStateGuardTests.cs
namespace OpenModulePlatform.HostAgent.Runtime.Tests;

/// <summary>
/// R7-F7 follow-through. The resource telemetry collector samples the live worker
/// processes listed in omp.WorkerInstanceRuntimeStates, keyed by ObservedState.
/// When WorkerManager gained the Draining (6) state, a draining worker still owns
/// a live process burning CPU and memory on its in-flight job -- leaving 6 out of
/// the sampling set would blind the collector exactly while the worker finishes
/// its heaviest work. This guard pins the fixed set.
/// </summary>
public sealed class WorkerTelemetryObservedStateGuardTests
{
    [Fact]
    public void The_telemetry_sampling_set_includes_Draining()
    {
        var source = Normalize(ReadRepositoryTextFile(
            "OpenModulePlatform.HostAgent.Runtime", "Services", "OmpHostArtifactRepository.ResourceTelemetry.cs"));

        Assert.Contains("rs.ObservedState IN (1, 2, 3, 6)", source);
        // The pre-fix set must be gone -- not just accompanied by the fixed one.
        Assert.DoesNotContain("rs.ObservedState IN (1, 2, 3);", source);
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
