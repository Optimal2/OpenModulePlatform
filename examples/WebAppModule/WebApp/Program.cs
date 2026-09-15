// File: OpenModulePlatform.Web.ExampleWebAppModule/Program.cs
using OpenModulePlatform.Web.ExampleWebAppModule.Localization;
using OpenModulePlatform.Web.ExampleWebAppModule.Services;
using OpenModulePlatform.Web.Shared.ActivityLog;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.OpenDocViewer;

var builder = WebApplication.CreateBuilder(args);

// Shared defaults include the common web logging setup (NLog),
// so new modules can focus on services/pages and use ILogger<T> where needed.
builder.AddOmpWebDefaults<ExampleWebAppModuleResource>(optionsSectionName: "Portal");
builder.Services.Configure<OpenDocViewerExampleOptions>(
    builder.Configuration.GetSection(OpenDocViewerExampleOptions.DefaultSectionName));
builder.Services.AddScoped<ExampleWebAppModuleAdminRepository>();
// User log: what an admin did here (saved the configuration, sent a test
// notification or banner) goes to omp_example_webapp.ActivityLog in the
// shared envelope and shows up in the Portal user log. The example doubles
// as the reference for wiring a module into the user log.
builder.Services.AddOmpActivityLog(new ActivityLogOptions
{
    SchemaName = "omp_example_webapp",
    ModuleKey = "example_webapp",
    AppKey = "example-webapp"
});

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);
app.MapOpenDocViewerExampleBundleEndpoint("OpenModulePlatform.Web.ExampleWebAppModule");

app.Run();
