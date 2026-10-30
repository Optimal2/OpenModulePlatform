// File: OpenModulePlatform.WorkerManager.WindowsService.Tests/WorkerManagerRuntimeRobustnessTests.cs
using System.Diagnostics;
using OpenModulePlatform.TestSupport;
using OpenModulePlatform.WorkerManager.WindowsService.Models;
using OpenModulePlatform.WorkerManager.WindowsService.Runtime;
using OpenModulePlatform.WorkerManager.WindowsService.Services;

namespace OpenModulePlatform.WorkerManager.WindowsService.Tests;

/// <summary>
/// Campaign omp-workermanager-och-sql-robusthet, job 01. Four runtime defects in the
/// WorkerManager, each pinned by behaviour rather than by source text where that is
/// possible without a database or a HostAgent:
/// (1) a SQL 1205 deadlock on the idempotent observation publish is retried, bounded and
///     cancellation-aware, and nothing else is retried;
/// (2) worker processes are owned by a kill-on-close job object, so they die with the
///     manager instead of surviving it as orphans, while unrelated processes are untouched;
/// (3) a successful ensureArtifact result is memoized per artifact identity + version, so
///     steady-state polling issues no repeated identical HostAgent RPCs;
/// (4) the runtime repository binds every parameter with an explicit schema type.
/// </summary>
public sealed class WorkerManagerRuntimeRobustnessTests
{
    private sealed class DeadlockTestException : Exception
    {
    }

    private static bool IsTestDeadlock(Exception ex) => ex is DeadlockTestException;

    // ---- (1) SQL 1205 retry ------------------------------------------------------

    [Fact]
    public async Task A_deadlocked_publish_is_retried_until_it_succeeds()
    {
        var attempts = 0;

        await OmpWorkerRuntimeRepository.ExecuteWithDeadlockRetryAsync(
            _ =>
            {
                attempts++;
                return attempts < 3 ? throw new DeadlockTestException() : Task.CompletedTask;
            },
            IsTestDeadlock,
            maxAttempts: 3,
            retryDelay: TimeSpan.Zero,
            CancellationToken.None);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task The_deadlock_retry_is_bounded_and_rethrows_the_last_deadlock()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<DeadlockTestException>(() =>
            OmpWorkerRuntimeRepository.ExecuteWithDeadlockRetryAsync(
                _ =>
                {
                    attempts++;
                    throw new DeadlockTestException();
                },
                IsTestDeadlock,
                maxAttempts: 3,
                retryDelay: TimeSpan.Zero,
                CancellationToken.None));

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task A_non_deadlock_failure_is_never_retried()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OmpWorkerRuntimeRepository.ExecuteWithDeadlockRetryAsync(
                _ =>
                {
                    attempts++;
                    throw new InvalidOperationException("not a deadlock");
                },
                IsTestDeadlock,
                maxAttempts: 3,
                retryDelay: TimeSpan.Zero,
                CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task The_deadlock_retry_stops_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OmpWorkerRuntimeRepository.ExecuteWithDeadlockRetryAsync(
                _ =>
                {
                    attempts++;
                    cts.Cancel();
                    throw new DeadlockTestException();
                },
                IsTestDeadlock,
                maxAttempts: 3,
                retryDelay: TimeSpan.FromSeconds(30),
                cts.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Only_sql_error_1205_counts_as_a_deadlock_victim()
    {
        Assert.Equal(1205, OmpWorkerRuntimeRepository.DeadlockVictimErrorNumber);
        Assert.False(OmpWorkerRuntimeRepository.IsDeadlockVictim(new DeadlockTestException()));
        Assert.False(OmpWorkerRuntimeRepository.IsDeadlockVictim(new TimeoutException()));
    }

    [Fact]
    public void Only_the_observation_publish_goes_through_the_deadlock_retry()
    {
        var repository = OmpRepositoryFiles.ReadRepositoryTextFile(
            "OpenModulePlatform.WorkerManager.WindowsService", "Services", "OmpWorkerRuntimeRepository.cs");

        var occurrences = repository.Split("ExecuteWithDeadlockRetryAsync(", StringSplitOptions.None).Length - 1;
        // One declaration plus exactly one call site: PublishObservationAsync.
        Assert.Equal(2, occurrences);
        Assert.Contains("attemptCt => PublishObservationCoreAsync(", repository);
    }

    // ---- (2) Worker processes die with the manager -------------------------------

    [Fact]
    public void A_worker_in_the_job_dies_when_the_job_owner_goes_away_and_a_bystander_survives()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var job = WorkerProcessJobObject.TryCreate();
        Assert.NotNull(job);

        using var worker = CreateLongRunningChild();
        using var bystander = CreateLongRunningChild();
        try
        {
            var assigned = WorkerManagerHostedService.StartWorkerProcess(
                worker,
                Guid.NewGuid(),
                worker.StartInfo.FileName,
                onStandardError: null,
                jobObject: job);
            Assert.True(assigned);

            Assert.True(bystander.Start());
            Assert.False(worker.HasExited);

            // Closing the last handle to the job is exactly what the kernel does when the
            // owning WorkerManager process dies, however it dies.
            job!.Dispose();

            Assert.True(worker.WaitForExit(TimeSpan.FromSeconds(10)), "The worker survived its job owner.");
            Assert.False(bystander.HasExited, "A process outside the job was killed.");
        }
        finally
        {
            job?.Dispose();
            KillQuietly(worker);
            KillQuietly(bystander);
        }
    }

    [Fact]
    public void The_startup_orphan_cleanup_is_kept_as_the_second_line_of_defence()
    {
        var service = OmpRepositoryFiles.ReadRepositoryTextFile(
            "OpenModulePlatform.WorkerManager.WindowsService", "Services", "WorkerManagerHostedService.cs");

        Assert.Contains("await CleanupOrphanedWorkerProcessesOnStartupAsync(stoppingToken);", service);
        Assert.Contains("jobObject: _workerJobObject", service);
    }

    private static Process CreateLongRunningChild()
    {
        // ping.exe is a plain console process that ships with every Windows edition and
        // waits about a second per echo, so it outlives the assertion without any script.
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "PING.EXE"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("120");
        startInfo.ArgumentList.Add("127.0.0.1");
        return new Process { StartInfo = startInfo };
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(10));
            }
        }
        catch (InvalidOperationException)
        {
            // Never started, or already gone.
        }
    }

    // ---- (3) ensureArtifact memoization -----------------------------------------

    private static HostAgentEnsureArtifactResponse Success(string localPath) => new()
    {
        Success = true,
        LocalPath = localPath
    };

    [Fact]
    public async Task Steady_state_polling_issues_one_ensureArtifact_rpc_per_artifact_version()
    {
        var cache = new HostAgentEnsureArtifactCache();
        var rpcCount = 0;

        Task<HostAgentEnsureArtifactResponse?> Rpc(CancellationToken _)
        {
            Interlocked.Increment(ref rpcCount);
            return Task.FromResult<HostAgentEnsureArtifactResponse?>(Success(@"C:\omp\artifacts\302"));
        }

        // Ten reconcile cycles, three workers sharing one artifact, resolved concurrently
        // exactly like ResolveDesiredWorkerArtifactsAsync does.
        for (var cycle = 0; cycle < 10; cycle++)
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
                cache.GetOrEnsureAsync(302, "0.3.128", Rpc, _ => true, CancellationToken.None)));
            Assert.All(responses, response => Assert.Equal(@"C:\omp\artifacts\302", response?.LocalPath));
        }

        Assert.Equal(1, rpcCount);
    }

    [Fact]
    public async Task A_new_artifact_version_is_asked_for_again()
    {
        var cache = new HostAgentEnsureArtifactCache();
        var rpcCount = 0;

        Task<HostAgentEnsureArtifactResponse?> Rpc(CancellationToken _)
        {
            rpcCount++;
            return Task.FromResult<HostAgentEnsureArtifactResponse?>(Success(@"C:\omp\artifacts\302"));
        }

        await cache.GetOrEnsureAsync(302, "0.3.128", Rpc, _ => true, CancellationToken.None);
        await cache.GetOrEnsureAsync(302, "0.3.129", Rpc, _ => true, CancellationToken.None);
        await cache.GetOrEnsureAsync(303, "0.3.129", Rpc, _ => true, CancellationToken.None);
        await cache.GetOrEnsureAsync(302, "0.3.129", Rpc, _ => true, CancellationToken.None);

        Assert.Equal(3, rpcCount);
    }

    [Fact]
    public async Task A_failed_ensureArtifact_result_is_never_memoized()
    {
        var cache = new HostAgentEnsureArtifactCache();
        var rpcCount = 0;

        Task<HostAgentEnsureArtifactResponse?> FailingRpc(CancellationToken _)
        {
            rpcCount++;
            return Task.FromResult<HostAgentEnsureArtifactResponse?>(new HostAgentEnsureArtifactResponse
            {
                Success = false,
                ErrorMessage = "HostAgent is down."
            });
        }

        await cache.GetOrEnsureAsync(302, "0.3.128", FailingRpc, _ => true, CancellationToken.None);
        await cache.GetOrEnsureAsync(302, "0.3.128", FailingRpc, _ => true, CancellationToken.None);

        Task<HostAgentEnsureArtifactResponse?> ThrowingRpc(CancellationToken _)
        {
            rpcCount++;
            throw new IOException("pipe broke");
        }

        await Assert.ThrowsAsync<IOException>(() =>
            cache.GetOrEnsureAsync(303, "0.3.128", ThrowingRpc, _ => true, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() =>
            cache.GetOrEnsureAsync(303, "0.3.128", ThrowingRpc, _ => true, CancellationToken.None));

        Assert.Equal(4, rpcCount);
    }

    [Fact]
    public async Task A_memoized_result_whose_files_are_gone_is_invalidated()
    {
        var cache = new HostAgentEnsureArtifactCache();
        var rpcCount = 0;
        var stillValid = true;

        Task<HostAgentEnsureArtifactResponse?> Rpc(CancellationToken _)
        {
            rpcCount++;
            return Task.FromResult<HostAgentEnsureArtifactResponse?>(Success(@"C:\omp\artifacts\302"));
        }

        await cache.GetOrEnsureAsync(302, "0.3.128", Rpc, _ => stillValid, CancellationToken.None);
        await cache.GetOrEnsureAsync(302, "0.3.128", Rpc, _ => stillValid, CancellationToken.None);
        Assert.Equal(1, rpcCount);

        stillValid = false;
        await cache.GetOrEnsureAsync(302, "0.3.128", Rpc, _ => stillValid, CancellationToken.None);
        Assert.Equal(2, rpcCount);
    }

    [Fact]
    public async Task Artifacts_no_longer_in_the_catalog_are_dropped()
    {
        var cache = new HostAgentEnsureArtifactCache();
        var rpcCount = 0;

        Task<HostAgentEnsureArtifactResponse?> Rpc(CancellationToken _)
        {
            rpcCount++;
            return Task.FromResult<HostAgentEnsureArtifactResponse?>(Success(@"C:\omp\artifacts\x"));
        }

        await cache.GetOrEnsureAsync(302, "0.3.128", Rpc, _ => true, CancellationToken.None);
        await cache.GetOrEnsureAsync(303, "0.3.128", Rpc, _ => true, CancellationToken.None);

        cache.RetainOnly([(302, "0.3.128")]);

        await cache.GetOrEnsureAsync(302, "0.3.128", Rpc, _ => true, CancellationToken.None);
        await cache.GetOrEnsureAsync(303, "0.3.128", Rpc, _ => true, CancellationToken.None);

        Assert.Equal(3, rpcCount);
    }

    // ---- (4) Explicit parameter types --------------------------------------------

    [Fact]
    public void The_runtime_repository_binds_no_parameter_with_an_inferred_type()
    {
        var repository = OmpRepositoryFiles.ReadRepositoryTextFile(
            "OpenModulePlatform.WorkerManager.WindowsService", "Services", "OmpWorkerRuntimeRepository.cs");

        Assert.DoesNotContain(".AddWithValue(", repository);
        Assert.Contains("\"@workerInstanceKey\", SqlDbType.NVarChar, 150", repository);
        Assert.Contains("\"@statusMessage\", SqlDbType.NVarChar, 500", repository);
        Assert.Contains("\"@observedState\", SqlDbType.TinyInt", repository);
        Assert.Contains("\"@hostKey\", SqlDbType.NVarChar, 128", repository);
    }

    [Fact]
    public void AddParameter_rejects_a_string_longer_than_the_column_instead_of_truncating()
    {
        using var cmd = new Microsoft.Data.SqlClient.SqlCommand();
        var error = Assert.Throws<ArgumentException>(() => OmpWorkerRuntimeRepository.AddParameter(
            cmd, "@runtimeKind", System.Data.SqlDbType.NVarChar, 5, "abcdefghij"));
        Assert.Contains("@runtimeKind", error.Message);
        Assert.Empty(cmd.Parameters);

        OmpWorkerRuntimeRepository.AddParameter(cmd, "@runtimeKind", System.Data.SqlDbType.NVarChar, 5, "abcde");
        Assert.Equal("abcde", cmd.Parameters["@runtimeKind"].Value);
    }
}
