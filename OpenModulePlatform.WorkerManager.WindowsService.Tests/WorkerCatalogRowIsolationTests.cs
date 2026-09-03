// File: OpenModulePlatform.WorkerManager.WindowsService.Tests/WorkerCatalogRowIsolationTests.cs
using OpenModulePlatform.WorkerManager.WindowsService.Models;
using OpenModulePlatform.WorkerManager.WindowsService.Services;

namespace OpenModulePlatform.WorkerManager.WindowsService.Tests;

/// <summary>
/// R7-F6. One broken worker catalog row used to throw its way out of
/// GetDesiredWorkersAsync and fail reconciliation for EVERY worker on the host,
/// retried and refailed every cycle for as long as the row stayed broken. The
/// contract under test: a broken row is reported and skipped, never thrown, and
/// the healthy rows around it are still mapped.
/// </summary>
public sealed class WorkerCatalogRowIsolationTests
{
    private const string RuntimeKind = "windows-worker-plugin";

    private static WorkerCatalogRow CreateRow(
        Guid? workerInstanceId = null,
        string? packageType = "worker",
        string? installPath = @"C:\workers\test-worker",
        string pluginRelativePath = "Test.Worker.dll") => new()
    {
        AppInstanceId = new Guid("11111111-1111-1111-1111-111111111111"),
        WorkerInstanceId = workerInstanceId ?? Guid.NewGuid(),
        WorkerInstanceKey = "test-worker",
        WorkerTypeKey = "test.worker",
        ArtifactId = 302,
        PackageType = packageType,
        InstallPath = installPath,
        IsProvisionedFromHostArtifactCache = false,
        PluginRelativePath = pluginRelativePath,
        ConfigurationJson = null,
        ArtifactVersion = "0.3.128"
    };

    [Fact]
    public void A_valid_row_maps_to_a_desired_worker()
    {
        var row = CreateRow();
        var seen = new HashSet<Guid>();

        var created = OmpDatabaseWorkerInstanceCatalog.TryCreateDesiredWorker(
            row, RuntimeKind, seen, out var worker, out var problem);

        Assert.True(created);
        Assert.Null(problem);
        Assert.NotNull(worker);
        Assert.Equal(row.WorkerInstanceId, worker.WorkerInstanceId);
        Assert.Equal(row.AppInstanceId, worker.AppInstanceId);
        Assert.EndsWith("Test.Worker.dll", worker.PluginAssemblyPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_row_with_a_rooted_plugin_path_is_skipped_not_thrown()
    {
        // The pre-fix shape of this row threw InvalidOperationException out of the
        // row loop and took the host's whole reconciliation with it.
        var row = CreateRow(pluginRelativePath: @"C:\Windows\System32\evil.dll");
        var seen = new HashSet<Guid>();

        var created = OmpDatabaseWorkerInstanceCatalog.TryCreateDesiredWorker(
            row, RuntimeKind, seen, out var worker, out var problem);

        Assert.False(created);
        Assert.Null(worker);
        Assert.NotNull(problem);
        Assert.Contains("rooted", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_row_with_a_path_escaping_the_install_root_is_skipped_not_thrown()
    {
        var row = CreateRow(pluginRelativePath: @"..\..\outside.dll");
        var seen = new HashSet<Guid>();

        var created = OmpDatabaseWorkerInstanceCatalog.TryCreateDesiredWorker(
            row, RuntimeKind, seen, out var worker, out var problem);

        Assert.False(created);
        Assert.Null(worker);
        Assert.NotNull(problem);
    }

    [Fact]
    public void A_duplicate_worker_instance_id_keeps_the_first_row_and_skips_the_second()
    {
        // The duplicate used to throw and fail the whole batch. Now the first row
        // wins and the duplicate is reported.
        var workerInstanceId = Guid.NewGuid();
        var seen = new HashSet<Guid>();

        Assert.True(OmpDatabaseWorkerInstanceCatalog.TryCreateDesiredWorker(
            CreateRow(workerInstanceId), RuntimeKind, seen, out _, out _));
        Assert.False(OmpDatabaseWorkerInstanceCatalog.TryCreateDesiredWorker(
            CreateRow(workerInstanceId), RuntimeKind, seen, out var worker, out var problem));

        Assert.Null(worker);
        Assert.NotNull(problem);
        Assert.Contains("duplicate", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_incompatible_package_type_is_skipped_not_thrown()
    {
        var row = CreateRow(packageType: "web");
        var seen = new HashSet<Guid>();

        var created = OmpDatabaseWorkerInstanceCatalog.TryCreateDesiredWorker(
            row, RuntimeKind, seen, out var worker, out var problem);

        Assert.False(created);
        Assert.Null(worker);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Broken_rows_never_steal_the_healthy_rows_around_them()
    {
        // The finding itself, replayed over the mapper the row loop uses: a broken
        // row between healthy ones leaves the healthy ones mapped.
        var seen = new HashSet<Guid>();
        var desired = new List<DesiredWorkerInstance>();
        var rows = new[]
        {
            CreateRow(),
            CreateRow(pluginRelativePath: @"C:\rooted\bad.dll"),
            CreateRow(),
            CreateRow(packageType: "web"),
            CreateRow()
        };

        foreach (var row in rows)
        {
            if (!OmpDatabaseWorkerInstanceCatalog.TryCreateDesiredWorker(
                    row, RuntimeKind, seen, out var worker, out _))
            {
                continue;
            }

            desired.Add(worker);
        }

        Assert.Equal(3, desired.Count);
    }
}
