// File: OpenModulePlatform.Web.ExampleWorkerAppModule/Program.cs
using OpenModulePlatform.Web.ExampleWorkerAppModule.Localization;
using OpenModulePlatform.Web.ExampleWorkerAppModule.Services;
using OpenModulePlatform.Web.Shared.ActivityLog;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.OpenDocViewer;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults<ExampleWorkerAppModuleResource>(optionsSectionName: "Portal");
builder.Services.Configure<OpenDocViewerExampleOptions>(
    builder.Configuration.GetSection(OpenDocViewerExampleOptions.DefaultSectionName));
builder.Services.AddScoped<ExampleWorkerAppModuleAdminRepository>();
// User log: what an admin did in the web app (saved a configuration or an
// app instance, queued a job) goes to omp_example_workerapp.ActivityLog in the
// shared envelope and shows up in the Portal user log. The service or
// worker itself is not a person and writes nothing here.
builder.Services.AddOmpActivityLog(new ActivityLogOptions
{
    SchemaName = "omp_example_workerapp",
    ModuleKey = "example_workerapp",
    AppKey = "example-workerapp-web"
});

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);
app.MapOpenDocViewerExampleBundleEndpoint("OpenModulePlatform.Web.ExampleWorkerAppModule");

app.Run();
