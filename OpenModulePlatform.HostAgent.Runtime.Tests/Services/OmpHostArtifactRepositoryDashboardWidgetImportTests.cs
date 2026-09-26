using System.Text.Json;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// Proves that the HostAgent universal-package import path stores a <c>module-fragment</c>
/// widget with its normalized payload, so the Portal can resolve and fetch the fragment.
/// </summary>
public sealed class OmpHostArtifactRepositoryDashboardWidgetImportTests : IDisposable
{
    private readonly OmpHostArtifactRepositoryTestDatabase _database;
    private readonly OmpHostArtifactRepository _repository;

    public OmpHostArtifactRepositoryDashboardWidgetImportTests()
    {
        _database = new OmpHostArtifactRepositoryTestDatabase();
        try
        {
            _database.CreateDashboardWidgetTables();
            _repository = new OmpHostArtifactRepository(_database.CreateFactory());
        }
        catch
        {
            // A throwing constructor means xUnit never calls Dispose(); dispose the
            // fixture here or its database leaks on every failing run.
            _database.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task ImportModuleFragmentWidget_StoresNormalizedPayload()
    {
        var package = await DashboardWidgetPackageReaderModuleFragmentTests.ReadAsync(
            FragmentWidgetJson("/widgets/overview", "1.0.0"));

        var result = await _repository.SaveImportedDashboardWidgetsAsync(package, CancellationToken.None);

        Assert.Equal(1, result.CreatedCount);
        var row = Assert.Single(_database.GetDashboardWidgets());
        Assert.Equal("module-fragment", row.WidgetType);
        Assert.NotNull(row.Payload);
        AssertPayload(row.Payload!, "/widgets/overview");

        // The Portal's ModuleFragmentWidget.TryParsePayload delegates to this parser; a
        // non-null result is what lets the dashboard fetch the fragment instead of
        // showing the placeholder.
        var config = ModuleFragmentWidgetPayload.TryParse(row.Payload);
        Assert.NotNull(config);
        Assert.Equal("example_webapp_webapp", config.AppKey);
        Assert.Equal("/widgets/overview", config.FragmentPath);
    }

    [Fact]
    public async Task ReimportIdenticalModuleFragmentWidget_IsSkipped()
    {
        var package = await DashboardWidgetPackageReaderModuleFragmentTests.ReadAsync(
            FragmentWidgetJson("/widgets/overview", "1.0.0"));
        await _repository.SaveImportedDashboardWidgetsAsync(package, CancellationToken.None);
        var before = Assert.Single(_database.GetDashboardWidgets());

        // A fresh read of the same file must normalize to byte-identical payload JSON,
        // otherwise every unattended re-import would fail as "different content".
        var again = await DashboardWidgetPackageReaderModuleFragmentTests.ReadAsync(
            FragmentWidgetJson("/widgets/overview", "1.0.0"));
        var result = await _repository.SaveImportedDashboardWidgetsAsync(again, CancellationToken.None);

        Assert.Equal((0, 0, 1, 0), result);
        Assert.Equal(before, Assert.Single(_database.GetDashboardWidgets()));
    }

    [Fact]
    public async Task NewerVersionWithChangedFragmentPath_UpdatesThePayload()
    {
        await _repository.SaveImportedDashboardWidgetsAsync(
            await DashboardWidgetPackageReaderModuleFragmentTests.ReadAsync(FragmentWidgetJson("/widgets/overview", "1.0.0")),
            CancellationToken.None);

        var result = await _repository.SaveImportedDashboardWidgetsAsync(
            await DashboardWidgetPackageReaderModuleFragmentTests.ReadAsync(FragmentWidgetJson("/widgets/summary", "1.0.1")),
            CancellationToken.None);

        Assert.Equal(1, result.UpdatedCount);
        var row = Assert.Single(_database.GetDashboardWidgets());
        Assert.Equal("1.0.1", row.WidgetVersion);
        AssertPayload(row.Payload!, "/widgets/summary");
    }

    [Fact]
    public async Task SameVersionWithChangedFragmentPath_IsRefusedAndKeepsTheRow()
    {
        await _repository.SaveImportedDashboardWidgetsAsync(
            await DashboardWidgetPackageReaderModuleFragmentTests.ReadAsync(FragmentWidgetJson("/widgets/overview", "1.0.0")),
            CancellationToken.None);

        var changed = await DashboardWidgetPackageReaderModuleFragmentTests.ReadAsync(FragmentWidgetJson("/widgets/summary", "1.0.0"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.SaveImportedDashboardWidgetsAsync(changed, CancellationToken.None));

        Assert.Contains("imported content is different", ex.Message, StringComparison.Ordinal);
        AssertPayload(Assert.Single(_database.GetDashboardWidgets()).Payload!, "/widgets/overview");
    }

    [Fact]
    public async Task SameVersionReimport_RepairsARowStoredWithoutPayload()
    {
        // The state an import before the shared normalization left behind.
        _database.InsertDashboardWidget("example:overview", "Example overview", "module-fragment", "1.0.0", "example_webapp", payload: null);

        var result = await _repository.SaveImportedDashboardWidgetsAsync(
            await DashboardWidgetPackageReaderModuleFragmentTests.ReadAsync(FragmentWidgetJson("/widgets/overview", "1.0.0")),
            CancellationToken.None);

        Assert.Equal(1, result.UpdatedCount);
        var row = Assert.Single(_database.GetDashboardWidgets());
        Assert.Equal("1.0.0", row.WidgetVersion);
        AssertPayload(row.Payload!, "/widgets/overview");
    }

    [Fact]
    public async Task SameVersionReimport_DoesNotRepairWhenOtherFieldsDiffer()
    {
        _database.InsertDashboardWidget("example:overview", "Old title", "module-fragment", "1.0.0", "example_webapp", payload: null);

        var package = await DashboardWidgetPackageReaderModuleFragmentTests.ReadAsync(FragmentWidgetJson("/widgets/overview", "1.0.0"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.SaveImportedDashboardWidgetsAsync(package, CancellationToken.None));

        Assert.Contains("imported content is different", ex.Message, StringComparison.Ordinal);
        Assert.Null(Assert.Single(_database.GetDashboardWidgets()).Payload);
    }

    private static string FragmentWidgetJson(string fragmentPath, string widgetVersion)
        => DashboardWidgetPackageReaderModuleFragmentTests.ModuleFragmentWidgetJson(
            $$"""
            "appKey": "example_webapp_webapp",
            "fragmentPath": "{{fragmentPath}}",
            "defaultWidth": 416
            """,
            widgetVersion);

    private static void AssertPayload(string payload, string expectedFragmentPath)
    {
        using var document = JsonDocument.Parse(payload);
        Assert.Equal("example_webapp_webapp", document.RootElement.GetProperty("appKey").GetString());
        Assert.Equal(expectedFragmentPath, document.RootElement.GetProperty("fragmentPath").GetString());
        Assert.Equal(416, document.RootElement.GetProperty("defaultWidth").GetInt32());
    }
}
