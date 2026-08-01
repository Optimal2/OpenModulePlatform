using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Models;
using OpenModulePlatform.Web.ContentWebAppModule.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Services;
using OpenModulePlatform.Web.Shared.Services;

var arguments = ParseArguments(args);
var runtimeRoot = RequireArgument(arguments, "runtime-root");
var sqlServer = RequireArgument(arguments, "sql-server");
var database = RequireArgument(arguments, "database");
var appInstanceId = Guid.Parse(RequireArgument(arguments, "app-instance-id"));

var contentRoot = Path.GetFullPath(Path.Join(runtimeRoot, "WebApps", "content"));
if (!Directory.Exists(contentRoot))
{
    throw new DirectoryNotFoundException($"Content runtime was not found: '{contentRoot}'.");
}

var environment = new RuntimeWebHostEnvironment(contentRoot);
var contentOptions = Options.Create(new ContentWebAppModuleOptions
{
    AppInstanceId = appInstanceId,
    ServerReportsPath = "App_Data/ContentReports",
    HtmlFilesPath = "App_Data/ContentPages",
    AllowedServerReportDatabases = [database],
    ServerReportDefaultMaxRows = 100,
    ServerReportMaxRowsLimit = 1000,
    ServerReportQueryTimeoutSeconds = 30
});
var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:OmpDb"] =
            $"Data Source={sqlServer};Initial Catalog={database};Integrated Security=True;Trust Server Certificate=True"
    })
    .Build();
var connectionFactory = new SqlConnectionFactory(configuration);
var htmlLoader = new HtmlContentFileLoader(environment, contentOptions);
var reportLoader = new ServerReportDefinitionLoader(
    environment,
    contentOptions,
    NullLogger<ServerReportDefinitionLoader>.Instance);
var queryRunner = new ServerReportQueryRunner(
    connectionFactory,
    contentOptions,
    NullLogger<ServerReportQueryRunner>.Instance);
var reportRenderer = new ServerReportRenderer(
    reportLoader,
    queryRunner,
    NullLogger<ServerReportRenderer>.Instance);
var contentRenderer = new ContentRenderer(htmlLoader, reportRenderer);
var repository = new ContentPageRepository(connectionFactory);

const string fileKey = "content-test-file";
const string reportKey = "content-test-status";
Require(htmlLoader.ListHtmlFileKeys().Contains(fileKey, StringComparer.OrdinalIgnoreCase),
    $"HTML file key '{fileKey}' was not discovered in the installed runtime.");
Require(reportLoader.ListReportKeys().Contains(reportKey, StringComparer.OrdinalIgnoreCase),
    $"Server report key '{reportKey}' was not discovered in the installed runtime.");

var reportDefinition = await reportLoader.LoadAsync(reportKey, CancellationToken.None);
Require(reportDefinition.Queries.Count == 2,
    $"Expected two server report queries, found {reportDefinition.Queries.Count}.");

var expectations = new[]
{
    new PageExpectation("test-markdown-shortcodes", ContentTypes.Markdown, "Markdown shortcode test"),
    new PageExpectation("test-html-shortcodes", ContentTypes.Html, "window.contentTestRows"),
    new PageExpectation("test-html-file", ContentTypes.HtmlFile, "HTML file content test"),
    new PageExpectation("test-server-report", ContentTypes.ServerReport, "Content Web App test report")
};
var renderedPages = new List<object>();

foreach (var expectation in expectations)
{
    var page = await repository.GetReadablePageBySlugAsync(
        appInstanceId,
        expectation.Slug,
        [],
        canManageAll: true,
        CancellationToken.None);
    Require(page is not null, $"Content page '{expectation.Slug}' was not found in the database.");
    Require(
        string.Equals(page!.ContentType, expectation.ContentType, StringComparison.OrdinalIgnoreCase),
        $"Content page '{expectation.Slug}' had type '{page.ContentType}', expected '{expectation.ContentType}'.");

    var html = await contentRenderer.RenderToHtmlAsync(
        page.Body,
        page.ContentType,
        page.ServerReportKey,
        CancellationToken.None);
    Require(html.Contains(expectation.ExpectedText, StringComparison.Ordinal),
        $"Rendered page '{expectation.Slug}' did not contain '{expectation.ExpectedText}'.");
    Require(!html.Contains("server-report--error", StringComparison.Ordinal),
        $"Rendered page '{expectation.Slug}' contained a server report availability error.");
    Require(!html.Contains("server-report__error", StringComparison.Ordinal),
        $"Rendered page '{expectation.Slug}' contained a server report query error.");
    Require(html.Contains("<table", StringComparison.OrdinalIgnoreCase),
        $"Rendered page '{expectation.Slug}' did not contain the expected report table.");

    renderedPages.Add(new
    {
        expectation.Slug,
        page.ContentType,
        page.ServerReportKey,
        RenderedLength = html.Length
    });
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    Status = "PASS",
    ContentRoot = contentRoot,
    HtmlFileKey = fileKey,
    ServerReportKey = reportKey,
    ServerReportQueryCount = reportDefinition.Queries.Count,
    Pages = renderedPages
}, new JsonSerializerOptions { WriteIndented = true }));

return;

static Dictionary<string, string> ParseArguments(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Arguments must use --name value pairs.");
        }

        result[values[index][2..]] = values[index + 1];
    }

    return result;
}

static string RequireArgument(IReadOnlyDictionary<string, string> arguments, string name)
{
    if (!arguments.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
    {
        throw new ArgumentException($"Missing required argument --{name}.");
    }

    return value;
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed record PageExpectation(string Slug, string ContentType, string ExpectedText);

internal sealed class RuntimeWebHostEnvironment : IWebHostEnvironment
{
    public RuntimeWebHostEnvironment(string contentRootPath)
    {
        ContentRootPath = contentRootPath;
        ContentRootFileProvider = new PhysicalFileProvider(contentRootPath);
        WebRootPath = Path.Join(contentRootPath, "wwwroot");
        WebRootFileProvider = Directory.Exists(WebRootPath)
            ? new PhysicalFileProvider(WebRootPath)
            : new NullFileProvider();
    }

    public string ApplicationName { get; set; } = "ContentWebAppRuntimeProbe";

    public IFileProvider WebRootFileProvider { get; set; }

    public string WebRootPath { get; set; }

    public string EnvironmentName { get; set; } = "Production";

    public string ContentRootPath { get; set; }

    public IFileProvider ContentRootFileProvider { get; set; }
}
