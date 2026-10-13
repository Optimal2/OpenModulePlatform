using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed partial class UniversalPackageSeedSqlOrderingTests
{
    [Fact]
    public async Task ConcurrentPortalAndHostAgentImports_RecheckEvidenceUnderLock_ExecuteEachHashOnce()
    {
        const string moduleKey = "completionconcurrent";
        const string schemaName = "omp_completionconcurrent";
        var workRoot = CreateWorkRoot();
        try
        {
            var service = CreatePortalService(Path.Join(workRoot, "store"));
            var package = BuildUniversalPackageZip(workRoot, moduleKey, schemaName, splitSetupAndInitialize: true);
            var first = await ImportPortalPackageAsync(service, package);
            Assert.DoesNotContain(first.Items, item => item.Status == "Failed");
            await _fixture.ExecuteAsync(
                $"DELETE e FROM omp.ModuleDefinitionSqlExecutions e JOIN omp.ModuleDefinitionDocuments d ON d.ModuleDefinitionDocumentId = e.ModuleDefinitionDocumentId WHERE d.ModuleKey = N'{moduleKey}';",
                $"DELETE FROM [{schemaName}].SeededVersions;");

            var json = (string)(await EntrySyncScalarAsync($"SELECT DefinitionJson FROM omp.ModuleDefinitionDocuments WHERE ModuleKey = N'{moduleKey}';"))!;
            var hash = (string)(await EntrySyncScalarAsync($"SELECT DefinitionSha256 FROM omp.ModuleDefinitionDocuments WHERE ModuleKey = N'{moduleKey}';"))!;
            var document = new ModuleDefinitionImportDocument(moduleKey, "1.0.1", 1, json, hash, "test", [], []);
            var host = new ArtifactZipImportService(new StaticOptionsMonitor<HostAgentSettings>(new()),
                _fixture.CreateHostAgentRepository(), NullLogger<ArtifactZipImportService>.Instance);

            Task<UniversalPackageImportResult> portalImport;
            Task<ModuleDefinitionImportResult> hostImport;
            var waiters = 0;
            // Hold the real repair lock until both imports are queued on it. This
            // tests contention deterministically without timing the DDL executor.
            await using (var scope = await _fixture.CreatePortalRepository().BeginModuleDefinitionSqlRepairAsync(CancellationToken.None))
            {
                portalImport = ImportPortalPackageAsync(service, package, quickImport: true);
                hostImport = host.ImportModuleDefinitionAsync(document, CancellationToken.None);
                for (var lap = 0; lap < 100 && waiters < 2; lap++)
                {
                    await Task.Delay(100);
                    waiters = Convert.ToInt32(await EntrySyncScalarAsync(
                        "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_database_id = DB_ID() AND resource_type = 'APPLICATION' AND request_status = 'WAIT';"));
                    if (portalImport.IsCompleted || hostImport.IsCompleted)
                        break;
                }
            }

            await Task.WhenAll(portalImport, hostImport);
            Assert.Equal(2, waiters);
            Assert.DoesNotContain((await portalImport).Items, item => item.Status == "Failed");
            Assert.True((await hostImport).Applied);
            Assert.Equal(2, await _fixture.CountSucceededDefinitionSqlExecutionsAsync(moduleKey, "1.0.1"));
            Assert.Contains(PackageArtifactVersion, await _fixture.GetSeededVersionsAsync(schemaName));
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }
}
