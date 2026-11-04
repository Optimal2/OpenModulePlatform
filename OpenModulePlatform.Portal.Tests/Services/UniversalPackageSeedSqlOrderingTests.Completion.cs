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
        using var importCts = new CancellationTokenSource();
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
            var hash = (string)(await WithinBound(
                EntrySyncScalarAsync($"SELECT DefinitionSha256 FROM omp.ModuleDefinitionDocuments WHERE ModuleKey = N'{moduleKey}';"),
                DatabaseOperationBound,
                "Reading the imported definition hash"))!;
            var document = new ModuleDefinitionImportDocument(moduleKey, "1.0.1", 1, json, hash, "test", [], []);
            var host = new ArtifactZipImportService(new StaticOptionsMonitor<HostAgentSettings>(new()),
                _fixture.CreateHostAgentRepository(), NullLogger<ArtifactZipImportService>.Instance);

            Task<UniversalPackageImportResult> portalImport;
            Task<ModuleDefinitionImportResult> hostImport;
            Task? allImports = null;
            var waiters = 0;
            var contentionObserved = false;
            // Hold the real repair lock until both imports are observed waiting on it.
            // The imports still execute the production lock and recheck-under-lock paths.
            var repairScope = await WithinBound(
                _fixture.CreatePortalRepository().BeginModuleDefinitionSqlRepairAsync(importCts.Token),
                RepairLockAcquisitionBound,
                "Acquiring the test-held module-definition SQL repair lock");
            try
            {
                portalImport = ImportPortalPackageAsync(
                    service, package, quickImport: true, cancellationToken: importCts.Token);
                hostImport = host.ImportModuleDefinitionAsync(document, importCts.Token);
                allImports = Task.WhenAll(portalImport, hostImport);

                // Keep one dedicated observer session instead of creating a fresh pooled
                // session for every poll. Its open and every DMV command have their own bounds.
                using var observer = new SqlConnection(_fixture.ConnectionString);
                await WithinBound(
                    observer.OpenAsync(importCts.Token),
                    ObserverOpenBound,
                    "Opening the repair-lock observer connection");
                using var command = new SqlCommand(
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
                        "Observing repair-lock waiters in sys.dm_tran_locks"));
                    if (waiters < 2)
                        await Task.Delay(ObservationPollInterval);
                }

                contentionObserved = true;
            }
            catch
            {
                importCts.Cancel();
                throw;
            }
            finally
            {
                try
                {
                    await WithinBound(
                        repairScope.DisposeAsync().AsTask(),
                        RepairLockReleaseBound,
                        "Releasing the test-held module-definition SQL repair lock");
                }
                finally
                {
                    if (!contentionObserved && allImports is not null)
                        await ObserveImportCleanupAsync(allImports);
                }
            }

            var imports = allImports ?? throw new InvalidOperationException("The concurrent imports were never started.");
            try
            {
                await WithinBound(imports, ImportCompletionBound, "The concurrent Portal and HostAgent imports");
            }
            catch
            {
                importCts.Cancel();
                await ObserveImportCleanupAsync(imports);
                throw;
            }

            Assert.Equal(2, waiters);
            var portalResult = await WithinBound(portalImport, ImportCompletionBound, "Reading the completed Portal import");
            var hostResult = await WithinBound(hostImport, ImportCompletionBound, "Reading the completed HostAgent import");
            Assert.DoesNotContain(portalResult.Items, item => item.Status == "Failed");
            Assert.True(hostResult.Applied);
            Assert.Equal(2, await WithinBound(
                _fixture.CountSucceededDefinitionSqlExecutionsAsync(moduleKey, "1.0.1"),
                DatabaseOperationBound,
                "Counting successful definition SQL executions"));
            Assert.Contains(PackageArtifactVersion, await WithinBound(
                _fixture.GetSeededVersionsAsync(schemaName),
                DatabaseOperationBound,
                "Reading the seeded artifact versions"));
        }
        finally
        {
            TryDeleteDirectory(workRoot);
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

    private static async Task ObserveImportCleanupAsync(Task imports)
    {
        try
        {
            await WithinBound(imports, ImportCleanupBound, "Cancelled import cleanup");
        }
        catch
        {
            // Preserve the handshake or import failure that triggered cancellation.
            // The cleanup wait is still bounded, so failed cleanup cannot hang the suite.
        }
    }
}
