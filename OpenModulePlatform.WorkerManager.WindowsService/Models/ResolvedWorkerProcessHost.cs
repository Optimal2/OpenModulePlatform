// File: OpenModulePlatform.WorkerManager.WindowsService/Models/ResolvedWorkerProcessHost.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Models;

/// <summary>
/// The WorkerProcessHost executable a worker process is launched with, together with the
/// artifact it came from (R12-F2).
/// </summary>
/// <remarks>
/// The path used to be all that was resolved, which is why omp_workerprocesshost was the
/// third of the three desired app instances no deployment check could see a running version
/// for: the worker-host build is not a process of its own, it is loaded by every worker, and
/// nothing recorded which build that was. The artifact identity is resolved in the same query
/// as the path so the two can never describe different artifacts.
///
/// ArtifactId and Version are nullable because the path can also come from the
/// WorkerManager:WorkerProcessPath setting, which names a file and nothing else. A configured
/// path is a real answer to "which executable" and no answer at all to "which build", and the
/// diagnostics say so rather than inventing one.
/// </remarks>
public sealed record ResolvedWorkerProcessHost(string Path, int? ArtifactId, string? Version);
