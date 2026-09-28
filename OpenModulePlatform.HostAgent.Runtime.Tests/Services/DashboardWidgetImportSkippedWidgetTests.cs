using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// A widget the HostAgent import skips must be visible outside the log: counted with its key
/// and reason in the import item result, and recorded as an open maintenance finding that the
/// Portal Maintenance page lists under dashboard widget readiness.
/// </summary>
public sealed class DashboardWidgetImportSkippedWidgetTests : IDisposable
{
    private readonly OmpHostArtifactRepositoryTestDatabase _database;
    private readonly OmpHostArtifactRepository _repository;
    private readonly ArtifactZipImportService _service;
    private readonly string _tempDirectory;

    public DashboardWidgetImportSkippedWidgetTests()
    {
        _database = new OmpHostArtifactRepositoryTestDatabase();
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"omp-widget-skip-{Guid.NewGuid():N}");
        try
        {
            _database.CreateDashboardWidgetTables();
            _database.CreateMaintenanceFindingsTable();
            _repository = new OmpHostArtifactRepository(_database.CreateFactory());
            _service = new ArtifactZipImportService(
                new FakeOptionsMonitor<HostAgentSettings>(),
                _repository,
                NullLogger<ArtifactZipImportService>.Instance);
            Directory.CreateDirectory(_tempDirectory);
        }
        catch
        {
            _database.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _database.Dispose();
        try { Directory.Delete(_tempDirectory, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task PackageWithValidAndUnknownWidgetType_StoresValidAndReportsSkippedWidget()
    {
        var result = await ImportAsync(MixedDocument(futureWidgetType: "future-widget"));

        Assert.Equal("Imported", result.Status);
        Assert.Equal(1, result.SkippedInvalidWidgetCount);
        Assert.Contains("skipped (invalid): 1", result.Message, StringComparison.Ordinal);
        Assert.Contains("example:future", result.Message, StringComparison.Ordinal);
        Assert.Contains("unsupported widgetType", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-private-payload", result.Message, StringComparison.Ordinal);

        var stored = Assert.Single(_database.GetDashboardWidgets());
        Assert.Equal("example:valid", stored.WidgetKey);

        var finding = Assert.Single(_database.GetMaintenanceFindings());
        Assert.Equal(OmpHostArtifactRepository.DashboardWidgetImportSkippedCategory, finding.Category);
        Assert.Equal("example:future", finding.TargetIdentifier);
        Assert.Equal(MaintenanceFindingStatuses.Open, finding.Status);
        Assert.Contains("unsupported widgetType", finding.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-private-payload", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedSkip_KeepsOneFinding_AndLaterSuccessfulImportClosesIt()
    {
        await ImportAsync(MixedDocument(futureWidgetType: "future-widget"));
        await ImportAsync(MixedDocument(futureWidgetType: "future-widget"));
        Assert.Single(_database.GetMaintenanceFindings());

        // The upgraded importer understands the widget: it is stored and the finding closes.
        var result = await ImportAsync(MixedDocument(futureWidgetType: "portal"));

        Assert.Equal(0, result.SkippedInvalidWidgetCount);
        Assert.Contains("skipped (invalid): 0", result.Message, StringComparison.Ordinal);
        Assert.Equal(2, _database.GetDashboardWidgets().Count);
        Assert.Equal(MaintenanceFindingStatuses.Cleaned, Assert.Single(_database.GetMaintenanceFindings()).Status);
    }

    [Fact]
    public void ImportMessage_NamesEachSkippedWidgetWithItsReason()
    {
        var message = ArtifactZipImportService.BuildDashboardWidgetImportMessage(
            (1, 0, 0, 0),
            [new PortableDashboardWidgetSkip("example:a", "reason a"), new PortableDashboardWidgetSkip("", "reason b")]);

        Assert.Equal(
            "Created: 1; updated: 0; skipped: 0; skipped (invalid): 2; permission rows: 0. " +
            "Skipped widgets: example:a: reason a | (missing widgetKey): reason b",
            message);
    }

    private async Task<ArtifactZipImportService.UniversalHostAgentImportItemResult> ImportAsync(string json)
    {
        var path = Path.Combine(_tempDirectory, $"{Guid.NewGuid():N}.widgets.json");
        await File.WriteAllTextAsync(path, json);
        return await _service.ImportUniversalDashboardWidgetItemAsync(
            new PortableUniversalModulePackageItem(
                UniversalModulePackageItemKind.DashboardWidget,
                "widgets/example.widgets.json",
                path,
                "example.widgets.json",
                "1.0.0"),
            CancellationToken.None);
    }

    private static string MixedDocument(string futureWidgetType)
        => $$"""
            {
              "format": "omp.portal.dashboard.widgets",
              "formatVersion": 1,
              "packageVersion": "1.0.0",
              "widgets": [
                {
                  "widgetKey": "example:valid",
                  "title": "Valid widget",
                  "widgetType": "portal",
                  "payload": "admin-overview",
                  "permissionNames": [],
                  "roleNames": []
                },
                {
                  "widgetKey": "example:future",
                  "title": "Future widget",
                  "widgetType": "{{futureWidgetType}}",
                  "payload": "opaque-private-payload",
                  "permissionNames": [],
                  "roleNames": []
                }
              ]
            }
            """;
}
