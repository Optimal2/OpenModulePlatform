using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed partial class UniversalPackageSeedSqlOrderingTests
{
    private static readonly TimeSpan ImportCompletionBound = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ImportCleanupBound = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RepairLockAcquisitionBound = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RepairLockReleaseBound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ContentionObservationBound = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ObserverOpenBound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ObserverCommandBound = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DatabaseOperationBound = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ObservationPollInterval = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task ConcurrentPortalAndHostAgentImports_RecheckEvidenceUnderLock_ExecuteEachHashOnce()
    {
        const string moduleKey = "completionconcurrent";
        const string schemaName = "omp_completionconcurrent";
        var workRoot = CreateWorkRoot();
        var importCts = new CancellationTokenSource();
        Task? allImports = null;
        try
        {
            var service = CreatePortalService(Path.Join(workRoot, "store"));
            var package = BuildUniversalPackageZip(workRoot, moduleKey, schemaName, splitSetupAndInitialize: true);
            var first = await WithinBound(
                ImportPortalPackageAsync(service, package, cancellationToken: importCts.Token),
                ImportCompletionBound,
                "The initial Portal import");
            Assert.DoesNotContain(first.Items, item => item.Status == "Failed");
            await WithinBound(
                _fixture.ExecuteAsync(
                    $"DELETE e FROM omp.ModuleDefinitionSqlExecutions e JOIN omp.ModuleDefinitionDocuments d ON d.ModuleDefinitionDocumentId = e.ModuleDefinitionDocumentId WHERE d.ModuleKey = N'{moduleKey}';",
                    $"DELETE FROM [{schemaName}].SeededVersions;"),
                DatabaseOperationBound,
                "Resetting the definition SQL execution evidence");

            var json = (string)(await WithinBound(
                EntrySyncScalarAsync($"SELECT DefinitionJson FROM omp.ModuleDefinitionDocuments WHERE ModuleKey = N'{moduleKey}';"),
                DatabaseOperationBound,
                "Reading the imported definition JSON"))!;
            var expectedExecutions = GetExpectedDefinitionSqlExecutions(json);
            Assert.Equal(2, expectedExecutions.Count);
            var hash = (string)(await WithinBound(
                EntrySyncScalarAsync($"SELECT DefinitionSha256 FROM omp.ModuleDefinitionDocuments WHERE ModuleKey = N'{moduleKey}';"),
                DatabaseOperationBound,
                "Reading the imported definition hash"))!;
            var document = new ModuleDefinitionImportDocument(moduleKey, "1.0.1", 1, json, hash, "test", [], []);
            var host = new ArtifactZipImportService(new StaticOptionsMonitor<HostAgentSettings>(new()),
                _fixture.CreateHostAgentRepository(), NullLogger<ArtifactZipImportService>.Instance);

            Task<UniversalPackageImportResult> portalImport = null!;
            Task<ModuleDefinitionImportResult> hostImport = null!;
            var waiters = 0;
            // Hold the real repair lock until both imports are observed waiting on its
            // measured resource identity. The production lock and recheck paths stay intact.
            var repairScope = await WithinBound(
                _fixture.CreatePortalRepository().BeginModuleDefinitionSqlRepairAsync(importCts.Token),
                RepairLockAcquisitionBound,
                "Acquiring the test-held module-definition SQL repair lock");
            Exception? handshakeFailure = null;
            try
            {
                // Keep one dedicated observer session instead of creating a fresh pooled
                // session for every poll. Its open and every DMV command have their own bounds.
                using var observer = new SqlConnection(_fixture.ConnectionString);
                await WithinBound(
                    observer.OpenAsync(importCts.Token),
                    ObserverOpenBound,
                    "Opening the repair-lock observer connection");
                await using var holderSpidCommand = new SqlCommand("SELECT @@SPID;", repairScope.Connection)
                {
                    CommandTimeout = (int)ObserverCommandBound.TotalSeconds
                };
                var holderSpid = Convert.ToInt32(await WithinBound(
                    holderSpidCommand.ExecuteScalarAsync(importCts.Token),
                    ObserverCommandBound,
                    "Reading the held repair-lock session id"));
                var repairLock = await ReadHeldRepairLockAsync(observer, holderSpid, importCts.Token);

                // Two deterministic waiters on an unrelated resource make the old database-wide
                // observation reach two before either importer starts. The scoped observation
                // must remain at zero, then observe the two genuine importer contenders below.
                await ProveUnrelatedApplicationLockExclusionAsync(observer, repairLock, importCts);

                portalImport = ImportPortalPackageAsync(
                    service, package, quickImport: true, cancellationToken: importCts.Token);
                allImports = portalImport;
                hostImport = host.ImportModuleDefinitionAsync(document, importCts.Token);
                allImports = Task.WhenAll(portalImport, hostImport);

                using var command = CreateRepairLockWaiterCommand(observer, repairLock);
                var contentionDeadline = DateTime.UtcNow.Add(ContentionObservationBound);
                while (waiters < 2)
                {
                    if (portalImport.IsCompleted || hostImport.IsCompleted)
                    {
                        Assert.Fail(
                            "An import completed while the test held the repair lock, so real lock contention was not observed. "
                            + $"Portal: {DescribeImport(portalImport)}; HostAgent: {DescribeImport(hostImport)}; waiters: {waiters}.");
                    }

                    if (DateTime.UtcNow >= contentionDeadline)
                    {
                        Assert.Fail(
                            $"Only {waiters} of 2 imports were waiting on the repair lock after "
                            + $"{ContentionObservationBound.TotalSeconds:0} seconds. "
                            + $"Portal: {DescribeImport(portalImport)}; HostAgent: {DescribeImport(hostImport)}.");
                    }

                    waiters = Convert.ToInt32(await WithinBound(
                        command.ExecuteScalarAsync(importCts.Token),
                        ObserverCommandBound,
                        "Observing waiters on the held repair-lock resource in sys.dm_tran_locks"));
                    if (waiters < 2)
                        await Task.Delay(ObservationPollInterval, importCts.Token);
                }
            }
            catch (Exception ex)
            {
                handshakeFailure = ex;
            }

            await CompleteConcurrentImportCleanupAsync(
                handshakeFailure,
                importCts,
                () => WithinBound(
                    repairScope.DisposeAsync().AsTask(),
                    RepairLockReleaseBound,
                    "Releasing the test-held module-definition SQL repair lock"),
                () => allImports is null ? Task.CompletedTask : ObserveImportCleanupAsync(allImports));

            var imports = allImports ?? throw new InvalidOperationException("The concurrent imports were never started.");
            try
            {
                await WithinBound(imports, ImportCompletionBound, "The concurrent Portal and HostAgent imports");
            }
            catch (Exception ex)
            {
                await CompleteConcurrentImportCleanupAsync(
                    ex,
                    importCts,
                    () => Task.CompletedTask,
                    () => ObserveImportCleanupAsync(imports));
                throw;
            }

            Assert.Equal(2, waiters);
            var portalResult = await WithinBound(portalImport, ImportCompletionBound, "Reading the completed Portal import");
            var hostResult = await WithinBound(hostImport, ImportCompletionBound, "Reading the completed HostAgent import");
            Assert.DoesNotContain(portalResult.Items, item => item.Status == "Failed");
            Assert.True(hostResult.Applied);
            var actualExecutions = await WithinBound(
                _fixture.GetSucceededDefinitionSqlExecutionCountsAsync(moduleKey, "1.0.1"),
                DatabaseOperationBound,
                "Reading successful definition SQL executions grouped by script key and hash");
            AssertExactlyOneSuccessfulExecutionPerExpectedScript(expectedExecutions, actualExecutions);
            Assert.Contains(PackageArtifactVersion, await WithinBound(
                _fixture.GetSeededVersionsAsync(schemaName),
                DatabaseOperationBound,
                "Reading the seeded artifact versions"));
        }
        finally
        {
            // If bounded cleanup expired, this continuation keeps the CTS alive and observes
            // late task failures. Completed imports are disposed synchronously here.
            var cancellationObservation = ObserveTasksAndDisposeCancellationSourceAsync(importCts, allImports);
            if (cancellationObservation.IsCompleted)
                await cancellationObservation;
            TryDeleteDirectory(workRoot);
        }
    }

    [Fact]
    public void SuccessfulDefinitionSqlExecutionAssertion_WhenWrongDistributionKeepsTotal_Fails()
    {
        var expected = new (string ScriptKey, string ScriptSha256, int ExecutionCount)[]
        {
            ("setup-seedorder-schema", new string('a', 64), 1),
            ("initialize-seedorder-rows", new string('b', 64), 1)
        };
        var wrongDistribution = new (string ScriptKey, string ScriptSha256, int ExecutionCount)[]
        {
            ("setup-seedorder-schema", new string('a', 64), 2)
        };

        Assert.Equal(2, wrongDistribution.Sum(row => row.ExecutionCount));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertExactlyOneSuccessfulExecutionPerExpectedScript(expected, wrongDistribution));
    }

    [Fact]
    public async Task ConcurrentImportCleanup_WhenPrimaryReleaseAndCleanupFail_PreservesEveryFailure()
    {
        var primary = new InvalidOperationException("Injected primary handshake failure.");
        var release = new TimeoutException("Injected repair-lock release failure.");
        var cleanup = new InvalidOperationException("Injected import cleanup failure.");
        using var cts = new CancellationTokenSource();

        var failure = await Assert.ThrowsAsync<AggregateException>(() => CompleteConcurrentImportCleanupAsync(
            primary,
            cts,
            () => Task.FromException(release),
            () => Task.FromException(cleanup)));

        Assert.Equal(3, failure.InnerExceptions.Count);
        Assert.Same(primary, failure.InnerExceptions[0]);
        Assert.Same(release, failure.InnerExceptions[1]);
        Assert.Same(cleanup, failure.InnerExceptions[2]);
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task ImportCancellationSource_WhenTasksAreNull_ReturnsNullAndDisposesSource()
    {
        var cts = new CancellationTokenSource();

        var observed = await ObserveTasksAndDisposeCancellationSourceAsync(cts, tasks: null);

        Assert.Null(observed);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            using var registration = cts.Token.Register(static () => { });
        });
    }

    [Fact]
    public async Task ImportCancellationSource_WhenCleanupObservationTimesOut_StaysAliveAndObservesLateFailures()
    {
        var cts = new CancellationTokenSource();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var imports = Task.WhenAll(first.Task, second.Task);
        var lateFailure = new InvalidOperationException("Injected late import failure.");
        var disposal = ObserveTasksAndDisposeCancellationSourceAsync(cts, imports);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => ObserveImportCleanupAsync(
                imports,
                TimeSpan.FromMilliseconds(25)));
            Assert.False(disposal.IsCompleted);
            using (var registration = cts.Token.Register(static () => { }))
            {
                // Registration succeeding proves the bounded observation did not dispose the CTS.
            }

            first.SetException(lateFailure);
            Assert.False(disposal.IsCompleted);
            second.SetResult();
            var observed = await WithinBound(
                disposal,
                TimeSpan.FromSeconds(5),
                "Observing the controlled late import failure and disposing its CTS");
            Assert.NotNull(observed);
            Assert.Contains(observed.Flatten().InnerExceptions, ex => ReferenceEquals(ex, lateFailure));
            Assert.Throws<ObjectDisposedException>(() =>
            {
                using var registration = cts.Token.Register(static () => { });
            });
        }
        finally
        {
            first.TrySetException(lateFailure);
            second.TrySetResult();
        }
    }

    private static IReadOnlyList<(string ScriptKey, string ScriptSha256, int ExecutionCount)>
        GetExpectedDefinitionSqlExecutions(string definitionJson)
    {
        var definition = JsonNode.Parse(definitionJson)?.AsObject()
            ?? throw new InvalidOperationException("The imported definition JSON is not an object.");
        var scripts = definition["sqlScripts"]?.AsArray()
            ?? throw new InvalidOperationException("The imported definition has no SQL script array.");
        var expected = new List<(string ScriptKey, string ScriptSha256, int ExecutionCount)>();
        foreach (var scriptNode in scripts)
        {
            var script = scriptNode?.AsObject()
                ?? throw new InvalidOperationException("The imported definition contains an invalid SQL script.");
            var text = (string?)script["inlineSql"];
            var execution = (string?)script["execution"];
            if (string.IsNullOrWhiteSpace(text)
                || string.Equals(execution, "validation", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = (string?)script["key"]
                ?? throw new InvalidOperationException("An executable SQL script has no key.");
            var scriptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            expected.Add((key, scriptHash, 1));
        }

        return expected;
    }

    private static void AssertExactlyOneSuccessfulExecutionPerExpectedScript(
        IReadOnlyCollection<(string ScriptKey, string ScriptSha256, int ExecutionCount)> expected,
        IReadOnlyCollection<(string ScriptKey, string ScriptSha256, int ExecutionCount)> actual)
    {
        Assert.Equal(expected.Sum(row => row.ExecutionCount), actual.Sum(row => row.ExecutionCount));
        Assert.Equal(expected.Count, actual.Count);
        foreach (var expectedScript in expected)
        {
            var actualScript = Assert.Single(actual, row =>
                string.Equals(row.ScriptKey, expectedScript.ScriptKey, StringComparison.Ordinal)
                && string.Equals(row.ScriptSha256, expectedScript.ScriptSha256, StringComparison.Ordinal));
            Assert.Equal(1, actualScript.ExecutionCount);
        }
    }

    private sealed record HeldRepairLock(
        int HolderSessionId,
        string ResourceDescription,
        string ResourceSubtype,
        long ResourceAssociatedEntityId);

    private static async Task<HeldRepairLock> ReadHeldRepairLockAsync(
        SqlConnection observer,
        int holderSpid,
        CancellationToken cancellationToken)
    {
        using var command = new SqlCommand(
            """
            SELECT request_session_id, resource_description, resource_subtype, resource_associated_entity_id
            FROM sys.dm_tran_locks
            WHERE request_session_id = @holderSpid
              AND resource_database_id = DB_ID()
              AND resource_type = 'APPLICATION'
              AND request_mode = 'X'
              AND request_status = 'GRANT'
              AND request_owner_type = 'SESSION';
            """,
            observer)
        {
            CommandTimeout = (int)ObserverCommandBound.TotalSeconds
        };
        command.Parameters.AddWithValue("@holderSpid", holderSpid);
        await using var reader = await WithinBound(
            command.ExecuteReaderAsync(cancellationToken),
            ObserverCommandBound,
            "Reading the held repair-lock resource identity");
        Assert.True(
            await WithinBound(
                reader.ReadAsync(cancellationToken),
                ObserverCommandBound,
                "Reading the held repair-lock row"),
            "The held repair lock was not visible in sys.dm_tran_locks.");
        var held = new HeldRepairLock(
            Convert.ToInt32(reader.GetValue(0)),
            reader.GetString(1),
            reader.GetString(2),
            Convert.ToInt64(reader.GetValue(3)));
        Assert.False(
            await WithinBound(
                reader.ReadAsync(cancellationToken),
                ObserverCommandBound,
                "Checking for additional held repair-lock rows"),
            "The repair-lock holder had more than one exclusive session application lock.");
        return held;
    }

    private static SqlCommand CreateRepairLockWaiterCommand(SqlConnection observer, HeldRepairLock held)
    {
        var command = new SqlCommand(
            """
            SELECT COUNT(*)
            FROM sys.dm_tran_locks
            WHERE request_session_id <> @holderSpid
              AND resource_database_id = DB_ID()
              AND resource_type = 'APPLICATION'
              AND resource_description = @resourceDescription
              AND resource_subtype = @resourceSubtype
              AND resource_associated_entity_id = @resourceAssociatedEntityId
              AND request_mode = 'X'
              AND request_status = 'WAIT'
              AND request_owner_type = 'SESSION';
            """,
            observer)
        {
            CommandTimeout = (int)ObserverCommandBound.TotalSeconds
        };
        command.Parameters.AddWithValue("@holderSpid", held.HolderSessionId);
        command.Parameters.AddWithValue("@resourceDescription", held.ResourceDescription);
        command.Parameters.AddWithValue("@resourceSubtype", held.ResourceSubtype);
        command.Parameters.AddWithValue("@resourceAssociatedEntityId", held.ResourceAssociatedEntityId);
        return command;
    }

    private static SqlCommand CreateUnscopedApplicationLockWaiterCommand(SqlConnection observer)
        => new(
            """
            SELECT COUNT(*)
            FROM sys.dm_tran_locks
            WHERE resource_database_id = DB_ID()
              AND resource_type = 'APPLICATION'
              AND request_status = 'WAIT'
              AND request_owner_type = 'SESSION';
            """,
            observer)
        {
            CommandTimeout = (int)ObserverCommandBound.TotalSeconds
        };

    private async Task ProveUnrelatedApplicationLockExclusionAsync(
        SqlConnection observer,
        HeldRepairLock repairLock,
        CancellationTokenSource importCts)
    {
        var unrelatedResource = $"omp.test.unrelated.{Guid.NewGuid():N}";
        await using var holder = new SqlConnection(CreateNonPoolingConnectionString());
        await WithinBound(
            holder.OpenAsync(importCts.Token),
            ObserverOpenBound,
            "Opening the unrelated application-lock holder");
        await AcquireSessionApplicationLockAsync(
            holder,
            unrelatedResource,
            lockTimeoutMilliseconds: 0,
            importCts.Token,
            "Acquiring the unrelated application lock");

        Task? allWaiters = null;
        Exception? observationFailure = null;
        try
        {
            allWaiters = Task.WhenAll(
                WaitForUnrelatedApplicationLockAsync(unrelatedResource, importCts.Token),
                WaitForUnrelatedApplicationLockAsync(unrelatedResource, importCts.Token));
            using var unscoped = CreateUnscopedApplicationLockWaiterCommand(observer);
            using var scoped = CreateRepairLockWaiterCommand(observer, repairLock);
            var unrelatedWaiters = 0;
            var deadline = DateTime.UtcNow.Add(ContentionObservationBound);
            while (unrelatedWaiters < 2)
            {
                if (allWaiters.IsCompleted)
                {
                    Assert.Fail(
                        "An unrelated application-lock waiter completed before its holder was released: "
                        + DescribeImport(allWaiters));
                }

                if (DateTime.UtcNow >= deadline)
                {
                    Assert.Fail(
                        $"Only {unrelatedWaiters} of 2 unrelated application-lock waiters were visible after "
                        + $"{ContentionObservationBound.TotalSeconds:0} seconds.");
                }

                unrelatedWaiters = Convert.ToInt32(await WithinBound(
                    unscoped.ExecuteScalarAsync(importCts.Token),
                    ObserverCommandBound,
                    "Proving that the old observation counts unrelated application-lock waiters"));
                if (unrelatedWaiters < 2)
                    await Task.Delay(ObservationPollInterval, importCts.Token);
            }

            Assert.Equal(2, unrelatedWaiters);
            var repairWaiters = Convert.ToInt32(await WithinBound(
                scoped.ExecuteScalarAsync(importCts.Token),
                ObserverCommandBound,
                "Proving that the scoped observation ignores unrelated application-lock waiters"));
            Assert.Equal(0, repairWaiters);
        }
        catch (Exception ex)
        {
            observationFailure = ex;
        }

        await CompleteConcurrentImportCleanupAsync(
            observationFailure,
            importCts,
            () => ReleaseSessionApplicationLockAsync(holder, unrelatedResource),
            () => allWaiters is null ? Task.CompletedTask : ObserveImportCleanupAsync(allWaiters),
            observeCleanupOnSuccess: true);
    }

    private string CreateNonPoolingConnectionString()
        => new SqlConnectionStringBuilder(_fixture.ConnectionString) { Pooling = false }.ConnectionString;

    private async Task WaitForUnrelatedApplicationLockAsync(string resource, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(CreateNonPoolingConnectionString());
        await WithinBound(
            connection.OpenAsync(cancellationToken),
            ObserverOpenBound,
            "Opening an unrelated application-lock waiter");
        await AcquireSessionApplicationLockAsync(
            connection,
            resource,
            lockTimeoutMilliseconds: 60000,
            cancellationToken,
            "Waiting on the unrelated application lock");
        await ReleaseSessionApplicationLockAsync(connection, resource);
    }

    private static async Task AcquireSessionApplicationLockAsync(
        SqlConnection connection,
        string resource,
        int lockTimeoutMilliseconds,
        CancellationToken cancellationToken,
        string description)
    {
        await using var command = new SqlCommand(
            """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource, @LockMode = N'Exclusive',
                @LockOwner = N'Session', @LockTimeout = @lockTimeout;
            SELECT @result;
            """,
            connection)
        {
            CommandTimeout = (int)DatabaseOperationBound.TotalSeconds + 5
        };
        command.Parameters.AddWithValue("@resource", resource);
        command.Parameters.AddWithValue("@lockTimeout", lockTimeoutMilliseconds);
        var result = Convert.ToInt32(await WithinBound(
            command.ExecuteScalarAsync(cancellationToken),
            DatabaseOperationBound,
            description));
        Assert.True(result >= 0, $"{description} was refused with sp_getapplock result {result}.");
    }

    private static Task ReleaseSessionApplicationLockAsync(SqlConnection connection, string resource)
        => WithinBound(
            new Func<Task>(async () =>
            {
                await using var command = new SqlCommand(
                    "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';",
                    connection)
                {
                    CommandTimeout = (int)ObserverCommandBound.TotalSeconds
                };
                command.Parameters.AddWithValue("@resource", resource);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            })(),
            RepairLockReleaseBound,
            "Releasing an unrelated application lock");

    private static async Task CompleteConcurrentImportCleanupAsync(
        Exception? primaryFailure,
        CancellationTokenSource importCts,
        Func<Task> releaseRepairLock,
        Func<Task> observeCancelledImports,
        bool observeCleanupOnSuccess = false)
    {
        var secondaryFailures = new List<Exception>();
        var cancellationAttempted = false;
        void CancelImports()
        {
            if (cancellationAttempted)
                return;
            cancellationAttempted = true;
            try
            {
                importCts.Cancel();
            }
            catch (Exception ex)
            {
                secondaryFailures.Add(ex);
            }
        }

        if (primaryFailure is not null)
            CancelImports();
        try
        {
            await releaseRepairLock();
        }
        catch (Exception ex)
        {
            secondaryFailures.Add(ex);
        }

        if (secondaryFailures.Count > 0)
            CancelImports();
        if (observeCleanupOnSuccess || primaryFailure is not null || secondaryFailures.Count > 0)
        {
            try
            {
                await observeCancelledImports();
            }
            catch (Exception ex)
            {
                secondaryFailures.Add(ex);
                CancelImports();
            }
        }

        if (primaryFailure is not null)
        {
            if (secondaryFailures.Count == 0)
            {
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                throw primaryFailure;
            }

            throw new AggregateException(
                "The concurrent import test failed and its bounded release/cleanup also failed.",
                new[] { primaryFailure }.Concat(secondaryFailures));
        }

        if (secondaryFailures.Count == 0)
            return;
        if (secondaryFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(secondaryFailures[0]).Throw();
            throw secondaryFailures[0];
        }

        throw new AggregateException("Concurrent import release/cleanup failed.", secondaryFailures);
    }

    private static async Task<AggregateException?> ObserveTasksAndDisposeCancellationSourceAsync(
        CancellationTokenSource cancellationSource,
        Task? tasks)
    {
        using (cancellationSource)
        {
            if (tasks is null)
                return null;

            try
            {
                await tasks.ConfigureAwait(false);
                return null;
            }
            catch
            {
                // Awaiting observes every task through WhenAll. Returning the aggregate makes
                // late-failure observation explicit and deterministically testable.
                return tasks.Exception;
            }
        }
    }

    private static async Task<T> WithinBound<T>(Task<T> operation, TimeSpan bound, string description)
    {
        try
        {
            return await operation.WaitAsync(bound);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{description} did not complete within {bound.TotalSeconds:0} seconds.", ex);
        }
    }

    private static async Task WithinBound(Task operation, TimeSpan bound, string description)
    {
        try
        {
            await operation.WaitAsync(bound);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{description} did not complete within {bound.TotalSeconds:0} seconds.", ex);
        }
    }

    private static string DescribeImport(Task import)
        => import.Exception is null
            ? import.Status.ToString()
            : $"{import.Status}: {import.Exception.GetBaseException()}";

    private static Task ObserveImportCleanupAsync(Task imports, TimeSpan? bound = null)
        => WithinBound(imports, bound ?? ImportCleanupBound, "Cancelled import cleanup");
}
