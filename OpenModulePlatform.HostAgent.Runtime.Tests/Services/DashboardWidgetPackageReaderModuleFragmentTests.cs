using System.Text;
using System.Text.Json;
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
    public async Task InvalidFragmentPath_IsRejected(string fragmentPath)
    {
        var json = ModuleFragmentWidgetJson($"""
            "appKey": "example_webapp_webapp",
            "fragmentPath": {JsonSerializer.Serialize(fragmentPath)}
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(json));
        Assert.Contains("fragmentPath", ex.Message, StringComparison.Ordinal);
        Assert.Contains("example:overview", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingFragmentPath_IsRejected()
    {
        var json = ModuleFragmentWidgetJson("""
            "appKey": "example_webapp_webapp"
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(json));
        Assert.Contains("fragmentPath is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingAppKey_IsRejected()
    {
        var json = ModuleFragmentWidgetJson("""
            "fragmentPath": "/widgets/overview"
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(json));
        Assert.Contains("appKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModuleFragmentWithPayload_IsRejected()
    {
        var json = ModuleFragmentWidgetJson("""
            "payload": "admin-overview",
            "appKey": "example_webapp_webapp",
            "fragmentPath": "/widgets/overview"
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(json));
        Assert.Contains("instead of payload", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"defaultWidth\": 100", "defaultWidth")]
    [InlineData("\"defaultWidth\": 5000", "defaultWidth")]
    [InlineData("\"defaultHeight\": 10", "defaultHeight")]
    [InlineData("\"defaultHeight\": 9000", "defaultHeight")]
    public async Task DefaultSizeOutsideRange_IsRejected(string sizeProperty, string propertyName)
    {
        var json = ModuleFragmentWidgetJson($"""
            "appKey": "example_webapp_webapp",
            "fragmentPath": "/widgets/overview",
            {sizeProperty}
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(json));
        Assert.Contains($"{propertyName} must be between", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FragmentFieldsOnOtherWidgetType_AreRejected()
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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(json));
        Assert.Contains("only valid for widgetType 'module-fragment'", ex.Message, StringComparison.Ordinal);
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

    internal static async Task<PortableDashboardWidgetPackage> ReadAsync(string json)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await new DashboardWidgetPackageReader().ReadAsync(stream, "widgets.json", CancellationToken.None);
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
