using OpenModulePlatform.Web.ExampleWebAppBlazorModule.Components;
using OpenModulePlatform.Web.ExampleWebAppBlazorModule.Localization;
using OpenModulePlatform.Web.ExampleWebAppBlazorModule.Services;
using OpenModulePlatform.Web.Shared.ActivityLog;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.OpenDocViewer;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults<ExampleWebAppBlazorModuleResource>(optionsSectionName: "Portal");
builder.Services.Configure<OpenDocViewerExampleOptions>(
    builder.Configuration.GetSection(OpenDocViewerExampleOptions.DefaultSectionName));
builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<ExampleWebAppBlazorModuleAdminRepository>();
// User log: a saved configuration goes to omp_example_webapp_blazor.ActivityLog
// in the shared envelope and shows up in the Portal user log. The example
// doubles as the reference for writing entries from a Blazor component.
builder.Services.AddOmpActivityLog(new ActivityLogOptions
{
    SchemaName = "omp_example_webapp_blazor",
    ModuleKey = "example_webapp_blazor",
    AppKey = "example-webapp-blazor"
});

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: false);
app.UseAntiforgery();
app.MapOpenDocViewerExampleBundleEndpoint("OpenModulePlatform.Web.ExampleWebAppBlazorModule");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
