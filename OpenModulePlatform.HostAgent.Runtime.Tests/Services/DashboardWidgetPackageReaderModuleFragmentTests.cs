using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// The HostAgent imports universal packages through <see cref="DashboardWidgetPackageReader"/>,
/// so a <c>module-fragment</c> definition must be normalized into its stored payload JSON here,
/// exactly as the Portal import does, or the widget row is written with a NULL payload.
/// </summary>
public sealed class DashboardWidgetPackageReaderModuleFragmentTests
{
    [Fact]
    public async Task ModuleFragmentDefinition_IsNormalizedIntoPayloadJson()
    {
        var package = await ReadAsync(ModuleFragmentWidgetJson("""
            "appKey": "example_webapp_webapp",
            "fragmentPath": "/widgets/overview",
            "defaultWidth": 416,
            "defaultHeight": 320
            """));

        var widget = Assert.Single(package.Widgets);
        Assert.Equal("module-fragment", widget.WidgetType);
        Assert.NotNull(widget.Payload);

        using var payload = JsonDocument.Parse(widget.Payload!);
        Assert.Equal("example_webapp_webapp", payload.RootElement.GetProperty("appKey").GetString());
        Assert.Equal("/widgets/overview", payload.RootElement.GetProperty("fragmentPath").GetString());
        Assert.Equal(416, payload.RootElement.GetProperty("defaultWidth").GetInt32());
        Assert.Equal(320, payload.RootElement.GetProperty("defaultHeight").GetInt32());
    }

    [Fact]
    public async Task ModuleFragmentDefinition_WithoutDefaultSize_OmitsTheSizeFields()
    {
        var package = await ReadAsync(ModuleFragmentWidgetJson("""
            "appKey": "example_webapp_webapp",
            "fragmentPath": " /widgets/overview?compact=1 "
            """));

        var payload = Assert.Single(package.Widgets).Payload;
        Assert.Equal("{\"appKey\":\"example_webapp_webapp\",\"fragmentPath\":\"/widgets/overview?compact=1\"}", payload);
    }

    [Theory]
    [InlineData("//evil.example/widget")]
    [InlineData("https://evil.example/widget")]
    [InlineData("widgets/overview")]
    [InlineData("/widgets/../admin")]
    [InlineData("/widgets/%2e%2e/admin")]
    public async Task InvalidFragmentPath_SkipsTheWidgetAndLogsIt(string fragmentPath)
    {
        var json = ModuleFragmentWidgetJson($"""
            "appKey": "example_webapp_webapp",
            "fragmentPath": {JsonSerializer.Serialize(fragmentPath)}
            """);

        var logger = new RecordingLogger();
        var package = await ReadAsync(json, logger);

        Assert.Empty(package.Widgets);
        AssertWarning(logger, "example:overview", "fragmentPath");
    }

    [Fact]
    public async Task MissingFragmentPath_SkipsTheWidgetAndLogsIt()
    {
        var json = ModuleFragmentWidgetJson("""
            "appKey": "example_webapp_webapp"
            """);

        var logger = new RecordingLogger();
        var package = await ReadAsync(json, logger);

        Assert.Empty(package.Widgets);
        AssertWarning(logger, "example:overview", "fragmentPath is required");
    }

    [Fact]
    public async Task MissingAppKey_SkipsTheWidgetAndLogsIt()
    {
        var json = ModuleFragmentWidgetJson("""
            "fragmentPath": "/widgets/overview"
            """);

        var logger = new RecordingLogger();
        var package = await ReadAsync(json, logger);

        Assert.Empty(package.Widgets);
        AssertWarning(logger, "example:overview", "appKey");
    }

    [Fact]
    public async Task ModuleFragmentWithPayload_SkipsTheWidgetAndLogsIt()
    {
        var json = ModuleFragmentWidgetJson("""
            "payload": "admin-overview",
            "appKey": "example_webapp_webapp",
            "fragmentPath": "/widgets/overview"
            """);

        var logger = new RecordingLogger();
        var package = await ReadAsync(json, logger);

        Assert.Empty(package.Widgets);
        AssertWarning(logger, "example:overview", "instead of payload");
    }

    [Theory]
    [InlineData("\"defaultWidth\": 100", "defaultWidth")]
    [InlineData("\"defaultWidth\": 5000", "defaultWidth")]
    [InlineData("\"defaultHeight\": 10", "defaultHeight")]
    [InlineData("\"defaultHeight\": 9000", "defaultHeight")]
    public async Task DefaultSizeOutsideRange_SkipsTheWidgetAndLogsIt(string sizeProperty, string propertyName)
    {
        var json = ModuleFragmentWidgetJson($"""
            "appKey": "example_webapp_webapp",
            "fragmentPath": "/widgets/overview",
            {sizeProperty}
            """);

        var logger = new RecordingLogger();
        var package = await ReadAsync(json, logger);

        Assert.Empty(package.Widgets);
        AssertWarning(logger, "example:overview", $"{propertyName} must be between");
    }

    [Fact]
    public async Task FragmentFieldsOnOtherWidgetType_AreSkippedAndLogged()
    {
        var json = WidgetDocument("""
            "widgetKey": "portal:admin",
            "title": "Admin",
            "widgetType": "portal",
            "payload": "admin-overview",
            "fragmentPath": "/widgets/overview",
            "permissionNames": [],
            "roleNames": []
            """);

        var logger = new RecordingLogger();
        var package = await ReadAsync(json, logger);

        Assert.Empty(package.Widgets);
        AssertWarning(logger, "portal:admin", "only valid for widgetType 'module-fragment'");
    }

    [Fact]
    public async Task PortalWidget_KeepsItsPayloadUnchanged()
    {
        var json = WidgetDocument("""
            "widgetKey": "portal:admin",
            "title": "Admin",
            "widgetType": "portal",
            "payload": " admin-overview ",
            "permissionNames": [],
            "roleNames": []
            """);

        var package = await ReadAsync(json);

        Assert.Equal("admin-overview", Assert.Single(package.Widgets).Payload);
    }

    [Fact]
    public async Task InvalidWidget_DoesNotFailTheWholeFile_ValidWidgetsAreImported()
    {
        // One broken widget among valid ones must not fail the whole file: the valid
        // widget is imported and only the broken one is dropped and logged.
        var json = """
            {
              "format": "omp.portal.dashboard.widgets",
              "formatVersion": 1,
              "packageVersion": "1.0.0",
              "widgets": [
                {
                  "widgetKey": "example:overview",
                  "widgetVersion": "1.0.0",
                  "title": "Example overview",
                  "widgetType": "module-fragment",
                  "moduleKey": "example_webapp",
                  "appKey": "example_webapp_webapp",
                  "fragmentPath": "/widgets/overview",
                  "permissionNames": [],
                  "roleNames": []
                },
                {
                  "widgetKey": "example:broken",
                  "widgetVersion": "1.0.0",
                  "title": "Broken overview",
                  "widgetType": "module-fragment",
                  "moduleKey": "example_webapp",
                  "appKey": "example_webapp_webapp",
                  "fragmentPath": "https://evil.example/widget",
                  "permissionNames": [],
                  "roleNames": []
                }
              ]
            }
            """;

        var logger = new RecordingLogger();
        var package = await ReadAsync(json, logger);

        var widget = Assert.Single(package.Widgets);
        Assert.Equal("example:overview", widget.WidgetKey);

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("example:broken", warning.Message, StringComparison.Ordinal);
        Assert.Contains("fragmentPath", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExampleWebAppModuleWidgetFile_CarriesAModuleFragmentPayload()
    {
        // The example module's universal package ships this file (omp-components.json
        // widgetFiles); it is the reference for module-owned widgets.
        var json = OmpRepositoryFiles.ReadRepositoryTextFile(
            "examples", "WebAppModule", "widgets", "example_webapp-widgets.json");

        var package = await ReadAsync(json);

        var widget = Assert.Single(package.Widgets);
        Assert.Equal(ModuleFragmentWidgetPayload.WidgetType, widget.WidgetType);
        var config = ModuleFragmentWidgetPayload.TryParse(widget.Payload);
        Assert.NotNull(config);
        Assert.Equal("example_webapp_webapp", config.AppKey);
        Assert.Equal("/widgets/overview", config.FragmentPath);
    }

    internal static string ModuleFragmentWidgetJson(string fragmentProperties, string widgetVersion = "1.0.0")
        => WidgetDocument($$"""
            "widgetKey": "example:overview",
            "widgetVersion": "{{widgetVersion}}",
            "title": "Example overview",
            "widgetType": "module-fragment",
            "moduleKey": "example_webapp",
            {{fragmentProperties}},
            "permissionNames": [],
            "roleNames": []
            """);

    internal static async Task<PortableDashboardWidgetPackage> ReadAsync(string json, ILogger? logger = null)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await new DashboardWidgetPackageReader(logger).ReadAsync(stream, "widgets.json", CancellationToken.None);
    }

    private static void AssertWarning(RecordingLogger logger, string widgetKey, string reason)
    {
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(widgetKey, warning.Message, StringComparison.Ordinal);
        Assert.Contains(reason, warning.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static string WidgetDocument(string widgetProperties)
        => $$"""
            {
              "format": "omp.portal.dashboard.widgets",
              "formatVersion": 1,
              "packageVersion": "1.0.0",
              "widgets": [
                {
                  {{widgetProperties}}
                }
              ]
            }
            """;
}
